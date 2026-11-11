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
/// <remarks>
/// Ages are measured on the monotonic clock, so a wall clock stepped back cannot keep a stale answer, a failure included, for the
/// size of the step. The probe starts on its own, outside the lock and without the first caller's execution context, so no caller
/// waits on a lock while it starts, and its trace is not credited to whichever request happened to start it.
/// </remarks>
public sealed class ReadinessCache(TimeProvider time, IOptions<MemoriaOptions> options)
{
	private readonly object _gate = new();
	private Task<SidecarReadinessResult>? _current;
	private long? _finishedAt;

	/// <summary>The probe in flight or still fresh, or a new one through <paramref name="probe"/>.</summary>
	public Task<SidecarReadinessResult> GetAsync(Func<CancellationToken, Task<SidecarReadinessResult>> probe)
	{
		ArgumentNullException.ThrowIfNull(probe);
		TaskCompletionSource<SidecarReadinessResult> started;
		lock (_gate)
		{
			if (_current is { } current && (_finishedAt is not { } finished || time.GetElapsedTime(finished) < options.Value.Sidecar.ReadinessCacheTime))
			{
				return current;
			}

			started = new TaskCompletionSource<SidecarReadinessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
			_current = started.Task;
			_finishedAt = null;
		}

		using (ExecutionContext.SuppressFlow())
		{
			_ = Task.Run(() => RunAsync(probe, started));
		}

		return started.Task;
	}

	/// <summary>
	/// Runs the probe, then marks when it finished before anyone sees the outcome. The other way round, a caller resuming on another
	/// thread could ask again in between, find no finish time, and be handed the finished task as if it were still in flight, however
	/// old it later grows.
	/// </summary>
	private async Task RunAsync(Func<CancellationToken, Task<SidecarReadinessResult>> probe, TaskCompletionSource<SidecarReadinessResult> started)
	{
		SidecarReadinessResult? result = null;
		Exception? failure = null;
		try
		{
			using CancellationTokenSource timeout = new(options.Value.Sidecar.ReadinessTimeout, time);
			result = await probe(timeout.Token);
		}
		catch (Exception ex)
		{
			failure = ex;
		}

		lock (_gate)
		{
			if (_current == started.Task)
			{
				_finishedAt = time.GetTimestamp();
			}
		}

		switch (failure)
		{
			case null:
				started.SetResult(result!);
				break;
			case OperationCanceledException canceled:
				started.SetCanceled(canceled.CancellationToken);
				break;
			default:
				started.SetException(failure);
				break;
		}
	}
}
