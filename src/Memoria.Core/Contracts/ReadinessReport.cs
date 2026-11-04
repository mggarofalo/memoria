using Memoria.Core.Sidecar;

namespace Memoria.Core.Contracts;

/// <summary>
/// Body of <c>GET /ready</c>. <see cref="Ready"/> mirrors the HTTP status
/// (200 when true, 503 otherwise); <see cref="Status"/> is one of
/// <see cref="ReadinessStatus"/>. Only an admin key (or disabled-auth mode) gets
/// <see cref="Reason"/> and <see cref="Sidecar"/>; every other caller gets the
/// status word and nothing else.
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

	/// <summary>The sidecar answered but is still loading models. Search works, more slowly on first use.</summary>
	public const string Warming = "warming";

	/// <summary>The sidecar answered, but some model role has no worker, or a job is waiting to retry. Search still works, possibly ranked worse.</summary>
	public const string Degraded = "degraded";

	/// <summary>The sidecar did not answer, timed out, or rejected our credentials.</summary>
	public const string Unavailable = "unavailable";

	/// <summary>Not ready, as told to a caller who may not see why.</summary>
	public const string NotReady = "not_ready";
}
