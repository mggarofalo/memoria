using System.Runtime;
using Memoria.Api.Configuration;
using Memoria.Api.Ingest;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Tests;

/// <summary>Which lane a job of a given size waits in, and what the lanes give back. Large work compacts the heap, so nothing else may run beside it.</summary>
[Collection(nameof(HeapMeasuringCollection))]
public sealed class IngestGateLaneTests
{
	private const long SmallWorkBytes = 1024 * 1024;

	private const long LargeWorkBytes = 32L * 1024 * 1024;

	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

	private static IngestGates Gates(int renders)
	{
		return new IngestGates(Options.Create(new MemoriaOptions { Ingest = new IngestOptions { MaxConcurrentRenders = renders } }));
	}

	/// <summary>Starts a job that holds its lane until released, and returns once it is running.</summary>
	private static async Task<Task<int>> HoldAsync(IngestGates gates, long bytes, ManualResetEventSlim release)
	{
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task<int> holding = Task.Run(() => gates.WorkAsync(bytes, () =>
		{
			started.SetResult();
			release.Wait(TimeSpan.FromSeconds(30));
			return 1;
		}, default));
		await started.Task.WaitAsync(Patience);
		return holding;
	}

	[Fact]
	public async Task A_job_of_exactly_the_small_size_takes_the_small_lane()
	{
		IngestGates gates = Gates(renders: 1);
		using ManualResetEventSlim release = new();
		Task<int> holding = await HoldAsync(gates, SmallWorkBytes + 1, release);

		int small = await gates.WorkAsync(SmallWorkBytes, static () => 2, default).WaitAsync(Patience);
		release.Set();

		Assert.Equal(2, small);
		Assert.Equal(1, await holding);
	}

	[Fact]
	public async Task Jobs_of_exactly_the_large_size_run_one_at_a_time_and_each_gives_its_turn_back()
	{
		IngestGates gates = Gates(renders: 2);
		using ManualResetEventSlim release = new();
		Task<int> holding = await HoldAsync(gates, LargeWorkBytes, release);
		int started = 0;

		Task<int> waiting = Task.Run(() => gates.WorkAsync(LargeWorkBytes, () => Interlocked.Increment(ref started), default));
		await Task.Delay(200);
		Assert.Equal(0, Volatile.Read(ref started));

		release.Set();
		Assert.Equal(1, await holding);
		Assert.Equal(1, await waiting.WaitAsync(Patience));
		Assert.Equal(5, await gates.WorkAsync(LargeWorkBytes, static () => 5, default).WaitAsync(Patience));
	}

	[Fact]
	public async Task Large_work_runs_once_the_heap_has_been_compacted()
	{
		GCLargeObjectHeapCompactionMode during = await Gates(renders: 1).WorkAsync(LargeWorkBytes, static () => GCSettings.LargeObjectHeapCompactionMode, default);

		Assert.Equal(GCLargeObjectHeapCompactionMode.Default, during);
	}
}

/// <summary>A lane that stays full for its whole wait turns the job away. The wait is thirty seconds and is not configurable, so this takes that long.</summary>
public sealed class IngestGateBusyTests
{
	[Fact]
	public async Task A_job_that_waits_out_a_full_lane_is_told_to_come_back()
	{
		IngestGates gates = new(Options.Create(new MemoriaOptions()));
		using ManualResetEventSlim release = new();
		using CountdownEvent started = new(2);
		Task<int>[] holding = [.. Enumerable.Range(0, 2).Select(_ => Task.Run(() => gates.WorkAsync(100, () =>
		{
			started.Signal();
			release.Wait(TimeSpan.FromSeconds(90));
			return 1;
		}, default)))];
		Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

		IngestRefusedException refused = await Assert.ThrowsAsync<IngestRefusedException>(() => gates.WorkAsync(100, static () => 2, default));
		release.Set();

		Assert.Equal(503, refused.Status);
		Assert.Equal("ingest_busy", refused.Code);
		Assert.Equal("The server is busy redacting. Try again shortly.", refused.Message);
		Assert.Equal(5, refused.RetryAfterSeconds);
		int[] results = await Task.WhenAll(holding);
		Assert.Equal([1, 1], results);
	}
}
