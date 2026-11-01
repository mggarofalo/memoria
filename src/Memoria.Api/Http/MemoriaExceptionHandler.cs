using System.Net;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Memoria.Api.Http;

/// <summary>
/// Turns sidecar failures and scope violations into RFC 9457 problem details.
/// The sidecar's stable error code travels in the <c>code</c> extension and
/// its HTTP status in <c>upstreamStatus</c>, so a client can tell "you asked
/// for a document that does not exist" from "Memoria cannot reach its index".
/// </summary>
public sealed partial class MemoriaExceptionHandler(IProblemDetailsService problemDetails, ILogger<MemoriaExceptionHandler> logger) : IExceptionHandler
{
	public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
	{
		ProblemDetails? problem = exception switch
		{
			SidecarException sidecar => FromSidecar(sidecar),
			CollectionForbiddenException forbidden => new ProblemDetails
			{
				Status = StatusCodes.Status403Forbidden,
				Title = "Collection outside caller scope",
				Detail = forbidden.Message,
				Extensions = { ["code"] = "collection_forbidden", ["collection"] = forbidden.Collection },
			},
			_ => null,
		};

		if (problem is null)
		{
			return false;
		}

		httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
		return await problemDetails.TryWriteAsync(new ProblemDetailsContext
		{
			HttpContext = httpContext,
			Exception = exception,
			ProblemDetails = problem,
		});
	}

	private ProblemDetails FromSidecar(SidecarException exception)
	{
		(int status, string title) = Map(exception);

		if (status >= 500)
		{
			LogSidecarFailure(logger, exception, exception.Code, exception.StatusCode is null ? null : (int)exception.StatusCode.Value);
		}
		else
		{
			LogSidecarRejection(logger, exception.Code, (int)exception.StatusCode!.Value, exception.Message);
		}

		ProblemDetails problem = new()
		{
			Status = status,
			Title = title,
			Detail = exception.Message,
			Extensions = { ["code"] = exception.Code },
		};

		if (exception.StatusCode is HttpStatusCode upstream)
		{
			problem.Extensions["upstreamStatus"] = (int)upstream;
		}

		if (exception.Details is { } details)
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
			HttpStatusCode.ServiceUnavailable => (StatusCodes.Status503ServiceUnavailable, "Search backend not ready"),
			_ => (StatusCodes.Status502BadGateway, "Search backend error"),
		};
	}

	[LoggerMessage(EventId = 2000, Level = LogLevel.Error, Message = "Sidecar call failed with {Code} (upstream status {UpstreamStatus})")]
	private static partial void LogSidecarFailure(ILogger logger, Exception exception, string code, int? upstreamStatus);

	[LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Sidecar rejected request with {Code} ({UpstreamStatus}): {Message}")]
	private static partial void LogSidecarRejection(ILogger logger, string code, int upstreamStatus, string message);
}
