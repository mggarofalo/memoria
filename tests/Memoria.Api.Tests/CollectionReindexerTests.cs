using System.Collections.Concurrent;
using Memoria.Api.Configuration;
using Memoria.Api.Ingest;
using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Tests;

/// <summary>When a delete's index update starts, waits, or is owed, on a clock that moves only when told.</summary>
public sealed class CollectionReindexerTests : IDisposable
{
	private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

	private readonly ManualClock _clock = new();

	private readonly ListLogger _logger = new();

	private readonly ScriptedSidecarClient _script;

	private readonly ServiceProvider _services;

	private readonly CollectionReindexer _reindexer;

	private int _reindexes;

	public CollectionReindexerTests()
	{
		(IMemoriaQmdClient client, _script) = ScriptedSidecarClient.Create();
		_script.On(nameof(IMemoriaQmdClient.ReindexAsync), _ =>
		{
			int count = Interlocked.Increment(ref _reindexes);
			return Task.FromResult(new SidecarJob($"job-{count}", "queued", new SidecarJobRequest(["c"], false, false, "delete"), "now", null, null, null, null));
		});
		_services = new ServiceCollection().AddSingleton(client).BuildServiceProvider();
		_reindexer = new CollectionReindexer(
			_services.GetRequiredService<IServiceScopeFactory>(),
			Options.Create(new MemoriaOptions { Ingest = new IngestOptions { MinReindexInterval = Interval } }),
			_clock,
			_logger);
	}

	public void Dispose()
	{
		_services.Dispose();
	}

	private int Reindexes => Volatile.Read(ref _reindexes);

	private async Task UntilReindexesAsync(int count)
	{
		for (int i = 0; i < 3000 && Reindexes < count; i++)
		{
			await Task.Delay(10);
		}

		Assert.Equal(count, Reindexes);
	}

	[Fact]
	public async Task An_update_starts_at_once_the_moment_the_interval_has_passed()
	{
		Assert.Equal(ReindexState.Started, (await _reindexer.RequestAsync("c", true, default)).State);

		_clock.MoveWithoutFiring(Interval);
		ReindexOutcome next = await _reindexer.RequestAsync("c", true, default);

		Assert.Equal(ReindexState.Started, next.State);
		Assert.Equal("job-2", next.JobId);
	}

	[Fact]
	public async Task An_update_that_started_owes_nothing_more()
	{
		await _reindexer.RequestAsync("c", true, default);

		ReindexOutcome nothing = await _reindexer.RequestAsync("c", false, default);

		Assert.Equal(ReindexState.NotNeeded, nothing.State);
		Assert.Equal(1, Reindexes);
	}

	[Fact]
	public async Task Once_the_scheduled_update_runs_nothing_is_owed_and_the_next_interval_starts_at_once()
	{
		await _reindexer.RequestAsync("c", true, default);
		Assert.Equal(ReindexState.Scheduled, (await _reindexer.RequestAsync("c", true, default)).State);
		await _clock.UntilATimerIsSetAsync();
		_clock.Advance(Interval);
		await UntilReindexesAsync(2);

		Assert.Equal(ReindexState.NotNeeded, (await _reindexer.RequestAsync("c", false, default)).State);

		_clock.MoveWithoutFiring(Interval);
		ReindexOutcome next = await _reindexer.RequestAsync("c", true, default);

		Assert.Equal(ReindexState.Started, next.State);
		Assert.Equal(3, Reindexes);
	}

	[Fact]
	public async Task An_update_that_could_not_start_is_logged_with_the_sidecars_code()
	{
		_script.On(nameof(IMemoriaQmdClient.ReindexAsync), static _ => Task.FromException<SidecarJob>(new SidecarException("busy", "Too much is running.")));

		ReindexOutcome failed = await _reindexer.RequestAsync("c", true, default);

		Assert.Equal(ReindexState.Failed, failed.State);
		(LogLevel level, EventId eventId, string message) = Assert.Single(_logger.Entries);
		Assert.Equal(LogLevel.Warning, level);
		Assert.Equal(3310, eventId.Id);
		Assert.Contains("(busy)", message, StringComparison.Ordinal);
	}

	private sealed class ListLogger : ILogger<CollectionReindexer>
	{
		public ConcurrentQueue<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = new();

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return true;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			Entries.Enqueue((logLevel, eventId, formatter(state, exception)));
		}
	}

	/// <summary>Time that moves only when told, with timers that fire only when told.</summary>
	private sealed class ManualClock : TimeProvider
	{
		private readonly Lock _lock = new();
		private readonly List<ManualTimer> _timers = [];
		private DateTimeOffset _now = DateTimeOffset.UtcNow;

		public override DateTimeOffset GetUtcNow()
		{
			lock (_lock)
			{
				return _now;
			}
		}

		public async Task UntilATimerIsSetAsync()
		{
			for (int i = 0; i < 3000; i++)
			{
				lock (_lock)
				{
					if (_timers.Count > 0)
					{
						return;
					}
				}

				await Task.Delay(10);
			}
		}

		public void MoveWithoutFiring(TimeSpan by)
		{
			lock (_lock)
			{
				_now += by;
			}
		}

		public void Advance(TimeSpan by)
		{
			ManualTimer[] due;
			lock (_lock)
			{
				_now += by;
				due = [.. _timers.Where(timer => timer.Due <= _now)];
				_timers.RemoveAll(due.Contains);
			}

			foreach (ManualTimer timer in due)
			{
				timer.Fire();
			}
		}

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			ManualTimer timer = new(this, callback, state);
			timer.Change(dueTime, period);
			return timer;
		}

		private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
		{
			public DateTimeOffset Due { get; private set; }

			public void Fire()
			{
				callback(state);
			}

			public bool Change(TimeSpan dueTime, TimeSpan period)
			{
				lock (clock._lock)
				{
					clock._timers.Remove(this);
					if (dueTime != Timeout.InfiniteTimeSpan)
					{
						Due = clock._now + dueTime;
						clock._timers.Add(this);
					}
				}

				return true;
			}

			public void Dispose()
			{
				Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
			}

			public ValueTask DisposeAsync()
			{
				Dispose();
				return ValueTask.CompletedTask;
			}
		}
	}
}
