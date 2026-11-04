using Memoria.Core.Sidecar;

namespace Memoria.Core.Contracts;

/// <summary>
/// Body of <c>GET /ready</c>. <see cref="Ready"/> mirrors the HTTP status
/// (200 when true, 503 otherwise); <see cref="Status"/> is one of
/// <see cref="ReadinessStatus"/>. Only an admin key (or disabled-auth mode) gets
/// <see cref="Reason"/> and <see cref="Sidecar"/>; every other caller gets
/// <c>ready</c> or <c>not_ready</c> and nothing else.
/// </summary>
public sealed record ReadinessReport(
	string Status,
	bool Ready,
	string? Reason,
	SidecarReadiness? Sidecar);

public static class ReadinessStatus
{
	/// <summary>The sidecar answered and its index and models are ready.</summary>
	public const string Ready = "ready";

	/// <summary>The sidecar answered but is still loading models or embedding.</summary>
	public const string Warming = "warming";

	/// <summary>The sidecar did not answer, timed out, or rejected our credentials.</summary>
	public const string Unavailable = "unavailable";

	/// <summary>Not ready, as told to a caller who may not see why.</summary>
	public const string NotReady = "not_ready";
}
