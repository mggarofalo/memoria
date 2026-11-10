using Memoria.Api.Configuration;
using Memoria.Core.Sidecar;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Sidecar;

/// <summary>
/// The sidecar's answer to <c>/ready</c>, shared. <c>/ready</c> is anonymous, so anyone who can reach the API can ask it, and each
/// ask costs the sidecar scans of the index on its one event loop. So every caller in the same moment waits on one probe, and its
/// answer, or its failure, serves them all for <see cref="SidecarOptions.ReadinessCacheTime"/>. A caller who gives up does not stop
/// the probe the others wait on: it has its own timeout, <see cref="SidecarOptions.ReadinessTimeout"/>.
/// </summary>
public sealed class ReadinessCache(TimeProvider time, IOptions<MemoriaOptions> options)
{
	private readonly object _gate = new();
	private Task<SidecarReadinessResult>? _current;
	private DateTimeOffset _expires = DateTimeOffset.MinValue;

	/// <summary>The probe in flight or still fresh, or a new one through <paramref name="probe"/>.</summary>
	public Task<SidecarReadinessResult> GetAsync(Func<CancellationToken, Task<SidecarReadinessResult>> probe)
	{
		ArgumentNullException.ThrowIfNull(probe);
		lock (_gate)
		{
			if (_current is { } current && (!current.IsCompleted || time.GetUtcNow() < _expires))
			{
				return current;
			}

			// Marked unexpired until it ends, so it is shared while in flight; RunAsync sets when it goes stale.
			_expires = DateTimeOffset.MaxValue;
			_current = RunAsync(probe);
			return _current;
		}
	}

	private async Task<SidecarReadinessResult> RunAsync(Func<CancellationToken, Task<SidecarReadinessResult>> probe)
	{
		SidecarOptions settings = options.Value.Sidecar;
		try
		{
			using CancellationTokenSource timeout = new(settings.ReadinessTimeout, time);
			return await probe(timeout.Token);
		}
		finally
		{
			lock (_gate)
			{
				_expires = time.GetUtcNow() + settings.ReadinessCacheTime;
			}
		}
	}
}
