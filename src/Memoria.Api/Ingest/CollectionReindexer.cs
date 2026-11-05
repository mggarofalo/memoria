using Memoria.Api.Configuration;
using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Ingest;

/// <summary>The index update a delete asked for. <see cref="ReindexState"/> names the states.</summary>
public sealed record ReindexOutcome(string State, string? JobId = null, int? StartsInSeconds = null);

/// <summary>
/// Starts the index updates deletes ask for, at most one per collection per interval, because each is a scan that blocks the
/// sidecar. A delete with none recently gets its update at once, so removing a leaked secret stays immediate. A burst
/// shares one more update when the interval ends, so no delete waits longer than the interval, whoever else is deleting. The sidecar's
/// own debounced job stands behind all of this if this process dies first.
/// </summary>
public sealed partial class CollectionReindexer(
	IServiceScopeFactory scopes,
	IOptions<MemoriaOptions> options,
	TimeProvider timeProvider,
	ILogger<CollectionReindexer> logger)
{
	private readonly Lock _lock = new();
	private readonly Dictionary<string, CollectionState> _collections = new(StringComparer.Ordinal);

	/// <summary>
	/// Asks for an update when something changed, or when an earlier update is still owed. A delete of nothing owes
	/// nothing, so it costs the sidecar nothing, and repeating a delete is what finishes one whose update failed.
	/// </summary>
	public async Task<ReindexOutcome> RequestAsync(string collection, bool changed, CancellationToken cancellationToken)
	{
		TimeSpan interval = options.Value.Ingest.MinReindexInterval;
		TimeSpan wait;
		lock (_lock)
		{
			CollectionState state = StateOf(collection);
			if (!changed && !state.Owed)
			{
				return new ReindexOutcome(ReindexState.NotNeeded);
			}

			// A scheduled update that is due but has not started yet is still the one this delete shares, or the two would start back to back.
			wait = state.LastStarted + interval - timeProvider.GetUtcNow();
			if (wait > TimeSpan.Zero || state.Scheduled)
			{
				ScheduleOnce(collection, state, wait);
				return new ReindexOutcome(ReindexState.Scheduled, null, Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)));
			}

			state.LastStarted = timeProvider.GetUtcNow();
			state.Owed = false;
		}

		await using AsyncServiceScope scope = scopes.CreateAsyncScope();
		return await StartAsync(scope.ServiceProvider.GetRequiredService<IMemoriaQmdClient>(), collection, cancellationToken);
	}

	private CollectionState StateOf(string collection)
	{
		if (!_collections.TryGetValue(collection, out CollectionState? state))
		{
			state = new CollectionState();
			_collections[collection] = state;
		}

		return state;
	}

	/// <summary>Holds no request's token: the caller has its answer and may be gone when this runs.</summary>
	private void ScheduleOnce(string collection, CollectionState state, TimeSpan wait)
	{
		if (state.Scheduled)
		{
			return;
		}

		state.Scheduled = true;
		_ = Task.Run(async () =>
		{
			await Task.Delay(wait, timeProvider);
			lock (_lock)
			{
				state.Scheduled = false;
				state.LastStarted = timeProvider.GetUtcNow();
				state.Owed = false;
			}

			await using AsyncServiceScope scope = scopes.CreateAsyncScope();
			await StartAsync(scope.ServiceProvider.GetRequiredService<IMemoriaQmdClient>(), collection, CancellationToken.None);
		});
	}

	private async Task<ReindexOutcome> StartAsync(IMemoriaQmdClient sidecar, string collection, CancellationToken cancellationToken)
	{
		try
		{
			SidecarJob job = await sidecar.ReindexAsync(new SidecarReindexRequest([collection], Embed: false), cancellationToken);
			return new ReindexOutcome(ReindexState.Started, job.Id);
		}
		catch (SidecarException exception)
		{
			LogFailed(logger, exception.Code);
			lock (_lock)
			{
				CollectionState state = StateOf(collection);
				state.Owed = true;
				state.LastStarted = DateTimeOffset.MinValue;
			}

			return new ReindexOutcome(ReindexState.Failed);
		}
	}

	[LoggerMessage(EventId = 3310, Level = LogLevel.Warning, Message = "An index update after a delete could not start ({Code}). A deleted document stays searchable until the sidecar's own debounced update, or until the delete is repeated.")]
	private static partial void LogFailed(ILogger logger, string code);

	private sealed class CollectionState
	{
		public DateTimeOffset LastStarted { get; set; } = DateTimeOffset.MinValue;

		public bool Owed { get; set; }

		public bool Scheduled { get; set; }
	}
}
