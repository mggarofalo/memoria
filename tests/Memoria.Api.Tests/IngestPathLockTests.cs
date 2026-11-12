using Memoria.Api.Ingest;

namespace Memoria.Api.Tests;

/// <summary>Measures the managed heap, so nothing else may run beside it.</summary>
[CollectionDefinition(nameof(HeapMeasuringCollection), DisableParallelization = true)]
public sealed class HeapMeasuringCollection;

public sealed class IngestPathLockTests
{
	[Fact]
	public async Task A_collection_and_path_that_spell_the_same_text_are_still_different_locks()
	{
		IngestPathLocks locks = new();
		using IDisposable held = (await locks.TryHoldAsync("ab", "c", TimeSpan.Zero, default))!;

		using IDisposable? other = await locks.TryHoldAsync("a", "bc", TimeSpan.Zero, default);

		Assert.NotNull(other);
		Assert.Null(await locks.TryHoldAsync("ab", "c", TimeSpan.Zero, default));
	}
}

/// <summary>The lock table holds a path only while someone holds or waits for it, so a server that has written many paths keeps no memory for them.</summary>
[Collection(nameof(HeapMeasuringCollection))]
public sealed class IngestPathLockMemoryTests
{
	private const int Paths = 4000;

	[Fact]
	public async Task A_path_nobody_holds_or_waits_for_is_forgotten()
	{
		IngestPathLocks locks = new();
		string padding = new('p', 16_000);
		long before = GC.GetTotalMemory(forceFullCollection: true);

		for (int i = 0; i < Paths; i++)
		{
			string path = padding + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
			IDisposable held = (await locks.TryHoldAsync("c", path, TimeSpan.Zero, default))!;
			Assert.Null(await locks.TryHoldAsync("c", path, TimeSpan.Zero, default));
			held.Dispose();
		}

		long grown = GC.GetTotalMemory(forceFullCollection: true) - before;
		GC.KeepAlive(locks);

		// Remembering every path would keep about 128 MB: each key is a 32 KB string.
		Assert.True(grown < 32L * 1024 * 1024, $"The lock table kept {grown} bytes after every path was released.");
	}
}
