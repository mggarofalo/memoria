using Memoria.Api.Configuration;
using Memoria.Api.Sidecar;
using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Http.HttpResults;
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
			.WithDescription("The API process is up. Does not contact the search backend; use `/ready` for that.")
			.WithTags("Health");

		app.MapGet("/ready", ReadyAsync)
			.WithName("ready")
			.WithSummary("Readiness")
			.WithDescription(
				"Probes the memoria-qmd sidecar. 200 when its index is open and, by default, its models are resident; "
				+ "503 with the same body shape while it warms up or when it cannot be reached.")
			.WithTags("Health")
			.Produces<ReadinessReport>(StatusCodes.Status503ServiceUnavailable);

		return app;
	}

	private static async Task<Results<Ok<ReadinessReport>, JsonHttpResult<ReadinessReport>>> ReadyAsync(
		IMemoriaQmdClient sidecar,
		IOptions<MemoriaOptions> options,
		HttpContext context)
	{
		SidecarOptions settings = options.Value.Sidecar;
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
		timeout.CancelAfter(settings.ReadinessTimeout);

		ReadinessReport report;
		try
		{
			SidecarReadinessResult result = await sidecar.GetReadinessAsync(settings.ReadyRequiresModels, timeout.Token);
			report = new ReadinessReport(
				result.Ready ? ReadinessStatus.Ready : ReadinessStatus.Warming,
				result.Ready,
				result.Ready ? null : DescribeWarming(result.Details),
				result.Details);
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

		return report.Ready
			? TypedResults.Ok(report)
			: TypedResults.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
	}

	private static string DescribeWarming(SidecarReadiness details)
	{
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
