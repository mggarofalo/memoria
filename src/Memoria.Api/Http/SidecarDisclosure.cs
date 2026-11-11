using System.Net;
using System.Text.Json;
using Memoria.Core.Sidecar;

namespace Memoria.Api.Http;

/// <summary>What a sidecar failure may tell this caller. Shared by REST and MCP so neither can say more than the other.</summary>
public sealed record SidecarFailureView(string Code, string Message, int? UpstreamStatus, JsonElement? Details);

/// <summary>
/// Sidecar errors can carry paths from any collection: <c>similarFiles</c> on a
/// miss, the collection and path on <c>excluded_by_ignore</c>, the sidecar's LAN
/// address on a transport failure. Two independent rules decide what passes:
/// a collection-scoped caller sees no sidecar detail about documents at all, and
/// only a caller with <see cref="ICallerScope.SeesBackendDetails"/> sees how the
/// backend failed.
/// </summary>
public static class SidecarDisclosure
{
	public const string NotFoundMessage = "No document matches that reference.";

	public static SidecarFailureView For(SidecarException exception, ICallerScope scope)
	{
		ArgumentNullException.ThrowIfNull(exception);
		ArgumentNullException.ThrowIfNull(scope);

		bool restricted = scope.Collections is not null;
		bool backendFault = MemoriaExceptionHandler.Map(exception).Status >= StatusCodes.Status500InternalServerError;
		int? upstream = scope.SeesBackendDetails && exception.StatusCode is { } status ? (int)status : null;

		if (backendFault)
		{
			return scope.SeesBackendDetails
				? new SidecarFailureView(exception.Code, exception.Message, upstream, restricted ? null : exception.Details)
				: new SidecarFailureView(exception.Code, "The search backend could not complete this request. Quote the trace id to the operator.", null, null);
		}

		if (exception.StatusCode == HttpStatusCode.NotFound)
		{
			// No caller gets suggestions: the API does not ask for them, because producing
			// them scans every path in the index. Details are dropped in case a sidecar sends them.
			return restricted
				? new SidecarFailureView("not_found", NotFoundMessage, null, null)
				: new SidecarFailureView(exception.Code, exception.Message, upstream, null);
		}

		// A validation failure names only what the caller sent, which is in scope by now.
		return new SidecarFailureView(exception.Code, exception.Message, upstream, restricted ? null : exception.Details);
	}
}
