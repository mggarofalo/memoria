using System.ComponentModel;
using Memoria.Api.Auth;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Api.Sidecar;
using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Endpoints;

/// <summary>Body of <c>GET /health</c>.</summary>
public sealed record HealthReport(string Status, string Version);

public static class HealthEndpoints
{
	public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
	{
		app.MapGet("/health", () => TypedResults.Ok(new HealthReport("ok", SidecarServiceCollectionExtensions.ApiVersion)))
			.WithName("health")
			.WithSummary("Liveness")
			.WithDescription("The API process is up. Anonymous. Does not contact the search backend; use `/ready` for that.")
			.WithTags("Health")
			.AllowAnonymous();

		app.MapGet("/ready", ReadyAsync)
			.WithName("ready")
			.WithSummary("Readiness")
			.WithDescription(
				"Probes the memoria-qmd sidecar, at most once per `Memoria:Sidecar:ReadinessCacheTime` (2 s by default): callers in that "
				+ "time share one answer. A caller with an admin key probes on its own, so the sidecar logs the call under that request's trace id. 200 whenever it answers, with `status` of `ready`, `warming` or `degraded`: "
				+ "losing GPU workers makes search rank worse, and must not take the API out of rotation. "
				+ "503 only when the sidecar cannot be reached, unless `strict=true`, which also answers 503 for anything but `ready`. "
				+ "Anonymous for orchestrators: without an admin key the body carries only the status word, and `not_ready` for an "
				+ "unreachable sidecar, with `reason` and `sidecar` null. An admin key gets the full report, including stuck jobs "
				+ "and documents set aside.")
			.WithTags("Health")
			.AllowAnonymous()
			.Produces<ReadinessReport>(StatusCodes.Status503ServiceUnavailable);

		return app;
	}

	private static async Task<Results<Ok<ReadinessReport>, JsonHttpResult<ReadinessReport>>> ReadyAsync(
		[FromQuery(Name = "strict")][Description("Answer 503 unless the sidecar is fully ready, with no degradation.")] bool? strict,
		IMemoriaQmdClient sidecar,
		ReadinessCache readiness,
		ICallerScope scope,
		IOptions<MemoriaOptions> options,
		HttpContext context)
	{
		SidecarOptions settings = options.Value.Sidecar;

		ReadinessReport report;
		try
		{
			SidecarReadinessResult result = await ProbeAsync(sidecar, readiness, settings, context);
			report = Answered(result.Details, strict == true);
		}
		catch (SidecarException ex)
		{
			report = new ReadinessReport(ReadinessStatus.Unavailable, false, $"{ex.Code}: {ex.Message}", null);
		}
		catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
		{
			report = new ReadinessReport(
				ReadinessStatus.Unavailable,
				false,
				$"{SidecarException.Timeout}: the sidecar at {settings.BaseUrl} did not answer the readiness probe within {settings.ReadinessTimeout.TotalSeconds:0.#} s.",
				null);
		}

		if (!scope.SeesBackendDetails)
		{
			report = new ReadinessReport(report.Status == ReadinessStatus.Unavailable ? ReadinessStatus.NotReady : report.Status, report.Ready, null, null);
		}

		return report.Ready
			? TypedResults.Ok(report)
			: TypedResults.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
	}

	/// <summary>
	/// A caller with an admin key probes on its own, so the sidecar logs the call under the request's trace id, which
	/// <c>memoria ready</c> prints. Such a caller has a key, and so a rate limit. Everyone else, anonymous callers above all, shares one
	/// probe: see <see cref="ReadinessCache"/>. Disabled mode shares it too, as anyone who can reach the API is a caller there.
	/// </summary>
	private static async Task<SidecarReadinessResult> ProbeAsync(IMemoriaQmdClient sidecar, ReadinessCache readiness, SidecarOptions settings, HttpContext context)
	{
		if (context.User.HasClaim(MemoriaClaims.Scope, MemoriaScopes.Admin) && !context.User.HasClaim(static c => c.Type == MemoriaClaims.AuthDisabled))
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
			timeout.CancelAfter(settings.ReadinessTimeout);
			return await sidecar.GetReadinessAsync(requireModels: false, timeout.Token);
		}

		return await readiness.GetAsync(token => sidecar.GetReadinessAsync(requireModels: false, token)).WaitAsync(context.RequestAborted);
	}

	/// <summary>The sidecar answered, so the API stays in rotation unless the caller asked to be strict.</summary>
	private static ReadinessReport Answered(SidecarReadiness details, bool strict)
	{
		string status = details.Status switch
		{
			ReadinessStatus.Ready => ReadinessStatus.Ready,
			ReadinessStatus.Degraded => ReadinessStatus.Degraded,
			_ => ReadinessStatus.Warming,
		};
		bool fullyReady = status == ReadinessStatus.Ready;
		return new ReadinessReport(status, fullyReady || !strict, fullyReady ? null : DescribeNotReady(details), details);
	}

	private static string DescribeNotReady(SidecarReadiness details)
	{
		if (details.Jobs.Stuck is { } stuck)
		{
			return $"a job is waiting to retry after {stuck.Attempts} attempts: {stuck.Reason}";
		}

		if (details.Warmup.Error is { Length: > 0 } error)
		{
			return $"warmup failed: {error}";
		}

		if (!details.Models.Resident)
		{
			return "models are still loading";
		}

		if (details.Index.NeedsEmbedding > 0)
		{
			return $"{details.Index.NeedsEmbedding} documents await embedding";
		}

		return $"sidecar reports status '{details.Status}'";
	}
}
