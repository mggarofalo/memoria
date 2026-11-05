using System.Net;
using System.Text.Json;

namespace Memoria.Core.Sidecar;

/// <summary>
/// The sidecar answered with an error envelope, or could not be reached.
/// <see cref="StatusCode"/> is null when no HTTP response was received; then
/// <see cref="Code"/> is one of the <c>sidecar_*</c> constants below.
/// </summary>
public sealed class SidecarException(
	string code,
	string message,
	HttpStatusCode? statusCode = null,
	JsonElement? details = null,
	Exception? innerException = null) : Exception(message, innerException)
{
	/// <summary>The sidecar's stable machine-readable code (e.g. <c>validation</c>, <c>not_found</c>), or a transport code.</summary>
	public string Code { get; } = code;

	/// <summary>HTTP status from the sidecar, or null when the request never completed.</summary>
	public HttpStatusCode? StatusCode { get; } = statusCode;

	/// <summary>How long the sidecar asked callers to wait, when it said.</summary>
	public TimeSpan? RetryAfter { get; init; }

	/// <summary>Code-specific extra data from the sidecar's envelope.</summary>
	public JsonElement? Details { get; } = details;

	/// <summary>No TCP connection, DNS failure, or connection reset.</summary>
	public const string Unreachable = "sidecar_unreachable";

	/// <summary>The configured timeout elapsed before the sidecar answered.</summary>
	public const string Timeout = "sidecar_timeout";

	/// <summary>The sidecar returned a non-success status without a parseable error envelope.</summary>
	public const string Malformed = "sidecar_malformed_response";

	/// <summary>The sidecar sent more than the request's budget allows. Reading stopped.</summary>
	public const string ResponseTooLarge = "sidecar_response_too_large";

	/// <summary>True for failures where retrying later may succeed (unreachable, timeout, 5xx).</summary>
	public bool IsTransient =>
		StatusCode is null || (int)StatusCode.Value >= 500;
}
