using System.Net;
using Memoria.Api.Ingest;
using Memoria.Core.Redaction;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Memoria.Api.Http;

/// <summary>
/// Turns sidecar failures and scope violations into RFC 9457 problem details.
/// The sidecar's stable error code travels in the <c>code</c> extension and
/// its HTTP status in <c>upstreamStatus</c>, so a client can tell "you asked
/// for a document that does not exist" from "Memoria cannot reach its index".
/// <see cref="SidecarDisclosure"/> decides how much of that this caller may see;
/// the full error always goes to the log.
/// </summary>
public sealed partial class MemoriaExceptionHandler(IProblemDetailsService problemDetails, ILogger<MemoriaExceptionHandler> logger) : IExceptionHandler
{
	private const int DefaultRetryAfterSeconds = 5;

	public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
	{
		ProblemDetails? problem = exception switch
		{
			SidecarException sidecar => FromSidecar(sidecar, httpContext.RequestServices.GetRequiredService<ICallerScope>()),
			CollectionForbiddenException forbidden => new ProblemDetails
			{
				Status = StatusCodes.Status403Forbidden,
				Title = "Collection outside caller scope",
				Detail = forbidden.Message,
				Extensions = { ["code"] = "collection_forbidden", ["collection"] = forbidden.Collection },
			},
			IngestRefusedException refused => FromRefusal(refused),
			RedactionException redaction => FromRedaction(redaction),
			BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => FromRefusal(new IngestRefusedException(StatusCodes.Status413PayloadTooLarge, "payload_too_large", "The request body is too large.")),
			BadHttpRequestException bad => FromRefusal(new IngestRefusedException(bad.StatusCode, "bad_request_body", "The request body could not be read.")),
			_ => null,
		};

		if (problem is null)
		{
			return false;
		}

		if (RetryAfterSeconds(exception) is int retryAfter)
		{
			httpContext.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		// A refusal under load leaves a body unread. Draining it costs memory for each one, and that is what runs a burst of retries out of memory.
		if (exception is IngestRefusedException { Code: "ingest_busy" })
		{
			httpContext.Response.Headers.Connection = "close";
		}

		// The caller may still be sending the body that was too large. Reading the rest first lets it read this answer.
		if (exception is IngestRefusedException { Code: "payload_too_large" })
		{
			await RequestBody.DiscardRestAsync(httpContext, RequestBody.DiscardDeadline, httpContext.RequestAborted);
		}

		httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
		return await problemDetails.TryWriteAsync(new ProblemDetailsContext
		{
			HttpContext = httpContext,
			Exception = exception,
			ProblemDetails = problem,
		});
	}

	/// <summary>What a busy backend or a busy ingest asks of the caller. The sidecar's own value wins, and a 503 without one gets a short default.</summary>
	internal static int? RetryAfterSeconds(Exception exception)
	{
		return exception switch
		{
			IngestRefusedException refused => refused.RetryAfterSeconds,
			SidecarException { RetryAfter: { } after } => Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)),
			SidecarException sidecar when Map(sidecar).Status == StatusCodes.Status503ServiceUnavailable => DefaultRetryAfterSeconds,
			_ => null,
		};
	}

	private static ProblemDetails FromRefusal(IngestRefusedException refused)
	{
		return new ProblemDetails
		{
			Status = refused.Status,
			Title = "Ingest refused",
			Detail = refused.Message,
			Extensions = { ["code"] = refused.Code },
		};
	}

	/// <summary>Names the rule and the part and nothing from the text: <see cref="RedactionException"/> carries no more than that.</summary>
	private static ProblemDetails FromRedaction(RedactionException redaction)
	{
		return new ProblemDetails
		{
			Status = StatusCodes.Status422UnprocessableEntity,
			Title = "Redaction could not finish",
			Detail = "The document was not stored, because it could not be confirmed free of credentials.",
			Extensions =
			{
				["code"] = "redaction_failed",
				["rule"] = redaction.RuleName,
				["failure"] = redaction.Failure.ToString(),
				["part"] = redaction.Part,
			},
		};
	}

	private ProblemDetails FromSidecar(SidecarException exception, ICallerScope scope)
	{
		(int status, string title) = Map(exception);

		// Stryker disable once Equality : Map answers 400, 404, 409, 413, 502, 503 or 504 and never exactly 500, so > and >= agree on every status it can return.
		if (status >= 500)
		{
			LogSidecarFailure(logger, exception, exception.Code, exception.StatusCode is null ? null : (int)exception.StatusCode.Value);
		}
		else
		{
			LogSidecarRejection(logger, exception.Code, (int)exception.StatusCode!.Value);
		}

		SidecarFailureView view = SidecarDisclosure.For(exception, scope);
		ProblemDetails problem = new()
		{
			Status = status,
			Title = title,
			Detail = view.Message,
			Extensions = { ["code"] = view.Code },
		};

		if (view.UpstreamStatus is int upstream)
		{
			problem.Extensions["upstreamStatus"] = upstream;
		}

		if (view.Details is { } details)
		{
			problem.Extensions["details"] = details;
		}

		return problem;
	}

	/// <summary>
	/// Client mistakes the sidecar caught (400, 404, 409, 413) pass through with
	/// their status. A 401 or 403 means Memoria's own shared secret is wrong,
	/// which is a deployment fault, not the caller's: 502. Transport failures
	/// are 503 (unreachable) or 504 (timed out). Anything else from the sidecar
	/// is 502.
	/// </summary>
	internal static (int Status, string Title) Map(SidecarException exception)
	{
		return exception.StatusCode switch
		{
			null when exception.Code == SidecarException.Timeout => (StatusCodes.Status504GatewayTimeout, "Search backend timed out"),
			null => (StatusCodes.Status503ServiceUnavailable, "Search backend unavailable"),
			HttpStatusCode.BadRequest => (StatusCodes.Status400BadRequest, "Invalid request"),
			HttpStatusCode.NotFound => (StatusCodes.Status404NotFound, "Not found"),
			HttpStatusCode.Conflict => (StatusCodes.Status409Conflict, "Conflict"),
			HttpStatusCode.RequestEntityTooLarge => (StatusCodes.Status413PayloadTooLarge, "Payload too large"),
			HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => (StatusCodes.Status502BadGateway, "Search backend rejected Memoria's credentials"),
			HttpStatusCode.ServiceUnavailable when exception.RetryAfter is not null => (StatusCodes.Status503ServiceUnavailable, "Search backend busy"),
			HttpStatusCode.ServiceUnavailable => (StatusCodes.Status503ServiceUnavailable, "Search backend not ready"),
			_ => (StatusCodes.Status502BadGateway, "Search backend error"),
		};
	}

	[LoggerMessage(EventId = 2000, Level = LogLevel.Error, Message = "Sidecar call failed with {Code} (upstream status {UpstreamStatus})")]
	private static partial void LogSidecarFailure(ILogger logger, Exception exception, string code, int? upstreamStatus);

	/// <remarks>Not the sidecar's message, which can quote a document reference.</remarks>
	[LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Sidecar rejected request with {Code} ({UpstreamStatus})")]
	private static partial void LogSidecarRejection(ILogger logger, string code, int upstreamStatus);
}
