using System.Net;
using Memoria.Api.Configuration;
using Memoria.Api.Sidecar;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Tests;

/// <summary>
/// <c>/ready</c> is anonymous, and each probe makes the sidecar scan its index, so callers share one answer.
/// </summary>
public sealed class ReadinessCacheTests : IDisposable
{
	private readonly MemoriaApiFactory _factory = new();

	private readonly WebApplicationFactory<Program> _cached;

	public ReadinessCacheTests()
	{
		_cached = _factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Sidecar:ReadinessCacheTime", "00:00:30"));
	}

	public void Dispose()
	{
		_cached.Dispose();
		_factory.Dispose();
	}

	private int ReadyProbes => _factory.Sidecar.Requests.Count(r => r.Uri.AbsolutePath == "/ready");

	[Fact]
	public async Task Callers_at_once_share_one_probe_and_later_ones_reuse_its_answer()
	{
		using ManualResetEventSlim release = new();
		_factory.Sidecar.On("GET", "/ready", (_, _) =>
		{
			release.Wait(TimeSpan.FromSeconds(10));
			return FakeSidecar.Json(HttpStatusCode.OK, SidecarFixtures.Readiness("ready", resident: true));
		});
		HttpClient client = _cached.CreateClient();

		Task<HttpResponseMessage>[] callers = [.. Enumerable.Range(0, 20).Select(_ => client.GetAsync("/ready"))];
		await Task.Delay(200);
		release.Set();
		HttpResponseMessage[] answers = await Task.WhenAll(callers);
		HttpResponseMessage later = await client.GetAsync("/ready");

		Assert.All(answers, answer => Assert.Equal(HttpStatusCode.OK, answer.StatusCode));
		Assert.Equal(HttpStatusCode.OK, later.StatusCode);
		Assert.Equal(1, ReadyProbes);
	}

	[Fact]
	public async Task A_failure_is_shared_too_so_a_down_sidecar_is_not_asked_by_every_caller()
	{
		_factory.Sidecar.Unreachable();
		HttpClient client = _cached.CreateClient();

		HttpResponseMessage first = await client.GetAsync("/ready");
		HttpResponseMessage second = await client.GetAsync("/ready");

		Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
		Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
		Assert.Equal(1, ReadyProbes);
	}

	[Fact]
	public async Task A_caller_who_gives_up_does_not_cancel_the_probe_others_wait_on()
	{
		using ManualResetEventSlim release = new();
		_factory.Sidecar.On("GET", "/ready", (_, _) =>
		{
			release.Wait(TimeSpan.FromSeconds(10));
			return FakeSidecar.Json(HttpStatusCode.OK, SidecarFixtures.Readiness("ready", resident: true));
		});
		HttpClient client = _cached.CreateClient();
		using CancellationTokenSource impatient = new();

		Task<HttpResponseMessage> leaving = client.GetAsync("/ready", impatient.Token);
		await Task.Delay(100);
		Task<HttpResponseMessage> staying = client.GetAsync("/ready");
		await Task.Delay(100);
		await impatient.CancelAsync();
		await Task.Delay(100);
		release.Set();

		Assert.Equal(HttpStatusCode.OK, (await staying).StatusCode);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
		Assert.Equal(1, ReadyProbes);
		Assert.DoesNotContain(true, _factory.Sidecar.CancelledBeforeAnswer);
	}

	[Fact]
	public async Task An_answer_goes_stale_after_the_cache_time_and_the_next_caller_probes_again()
	{
		SteppedClock time = new();
		MemoriaOptions settings = new() { Sidecar = { ReadinessCacheTime = TimeSpan.FromSeconds(2), ReadinessTimeout = TimeSpan.FromSeconds(5) } };
		ReadinessCache cache = new(time, Options.Create(settings));
		int probes = 0;
		Task<SidecarReadinessResult> Probe(CancellationToken _)
		{
			probes++;
			return Task.FromResult<SidecarReadinessResult>(null!);
		}

		await cache.GetAsync(Probe);
		await cache.GetAsync(Probe);
		time.Advance(TimeSpan.FromSeconds(1.9));
		await cache.GetAsync(Probe);
		Assert.Equal(1, probes);

		time.Advance(TimeSpan.FromSeconds(0.2));
		await cache.GetAsync(Probe);
		Assert.Equal(2, probes);
	}

	[Fact]
	public async Task With_no_cache_time_only_a_probe_in_flight_is_shared()
	{
		MemoriaOptions settings = new() { Sidecar = { ReadinessCacheTime = TimeSpan.Zero, ReadinessTimeout = TimeSpan.FromSeconds(5) } };
		ReadinessCache cache = new(new SteppedClock(), Options.Create(settings));
		TaskCompletionSource<SidecarReadinessResult> pending = new();
		int probes = 0;
		Task<SidecarReadinessResult> Probe(CancellationToken _)
		{
			probes++;
			return pending.Task;
		}

		Task<SidecarReadinessResult> first = cache.GetAsync(Probe);
		Task<SidecarReadinessResult> second = cache.GetAsync(Probe);
		Assert.Same(first, second);
		pending.SetResult(null!);
		await first;
		await cache.GetAsync(_ => { probes++; return Task.FromResult<SidecarReadinessResult>(null!); });
		Assert.Equal(2, probes);
	}

	/// <summary>
	/// A caller resumes on another thread the moment the probe's answer is out. If the finish time were marked after that, it could
	/// ask again first and be handed the finished probe as if still in flight. Many rounds make that window show if it is there.
	/// </summary>
	[Fact]
	public async Task A_caller_who_asks_again_the_moment_an_answer_lands_gets_a_new_probe()
	{
		MemoriaOptions settings = new() { Sidecar = { ReadinessCacheTime = TimeSpan.Zero, ReadinessTimeout = TimeSpan.FromSeconds(5) } };
		ReadinessCache cache = new(new SteppedClock(), Options.Create(settings));
		int probes = 0;
		Task<SidecarReadinessResult> Probe(CancellationToken _)
		{
			Interlocked.Increment(ref probes);
			return Task.FromResult<SidecarReadinessResult>(null!);
		}

		for (int round = 1; round <= 2_000; round++)
		{
			await cache.GetAsync(Probe);
			Assert.Equal(round, Volatile.Read(ref probes));
		}
	}

	[Fact]
	public async Task The_probe_has_its_own_timeout()
	{
		MemoriaOptions settings = new() { Sidecar = { ReadinessCacheTime = TimeSpan.Zero, ReadinessTimeout = TimeSpan.FromMilliseconds(100) } };
		ReadinessCache cache = new(TimeProvider.System, Options.Create(settings));

		Task<SidecarReadinessResult> probe = cache.GetAsync(async token =>
		{
			await Task.Delay(Timeout.Infinite, token);
			return null!;
		});
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
	}

	[Fact]
	public async Task A_wall_clock_stepped_back_does_not_keep_an_answer_longer()
	{
		SteppedClock time = new();
		MemoriaOptions settings = new() { Sidecar = { ReadinessCacheTime = TimeSpan.FromSeconds(2), ReadinessTimeout = TimeSpan.FromSeconds(5) } };
		ReadinessCache cache = new(time, Options.Create(settings));
		int probes = 0;
		Task<SidecarReadinessResult> Probe(CancellationToken _)
		{
			probes++;
			return Task.FromResult<SidecarReadinessResult>(null!);
		}

		await cache.GetAsync(Probe);
		time.StepWallClock(TimeSpan.FromHours(-1));
		time.Advance(TimeSpan.FromSeconds(3));
		await cache.GetAsync(Probe);

		Assert.Equal(2, probes);
	}

	[Fact]
	public async Task A_probe_that_throws_at_once_is_shared_as_a_failure_and_the_next_caller_after_it_probes_again()
	{
		MemoriaOptions settings = new() { Sidecar = { ReadinessCacheTime = TimeSpan.Zero, ReadinessTimeout = TimeSpan.FromSeconds(5) } };
		ReadinessCache cache = new(new SteppedClock(), Options.Create(settings));

		await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(_ => throw new InvalidOperationException("refused")));
		Assert.Null(await cache.GetAsync(_ => Task.FromResult<SidecarReadinessResult>(null!)));
	}

	/// <summary>An admin has a key, and so a rate limit, and wants the sidecar to log the call under its request's trace id.</summary>
	[Fact]
	public async Task An_admin_probes_on_its_own_and_gets_the_full_report()
	{
		using AuthApiFactory auth = new();
		using WebApplicationFactory<Program> cached = auth.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Sidecar:ReadinessCacheTime", "00:00:30"));
		auth.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.Readiness("ready", resident: true));
		(_, string key) = await auth.IssueAsync([Memoria.Api.Http.MemoriaScopes.Admin]);

		string anonymous = await cached.CreateClient().GetStringAsync("/ready");
		HttpClient admin = AuthApiFactory.Authorized(cached.CreateClient(), key);
		await admin.GetStringAsync("/ready");
		string full = await admin.GetStringAsync("/ready");

		Assert.Equal(3, auth.Sidecar.Requests.Count(r => r.Uri.AbsolutePath == "/ready"));
		Assert.Contains("\"sidecar\":{", full, StringComparison.Ordinal);
		Assert.Contains("\"sidecar\":null", anonymous, StringComparison.Ordinal);
		Assert.Contains("\"reason\":null", anonymous, StringComparison.Ordinal);
	}

	/// <summary>Only the admin scope skips the shared probe: any other key shares it, and sees the status word alone.</summary>
	[Fact]
	public async Task A_key_without_the_admin_scope_shares_the_anonymous_answer()
	{
		using AuthApiFactory auth = new();
		using WebApplicationFactory<Program> cached = auth.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Sidecar:ReadinessCacheTime", "00:00:30"));
		auth.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.Readiness("ready", resident: true));
		(_, string key) = await auth.IssueAsync([Memoria.Api.Http.MemoriaScopes.Read], ["c"]);

		await cached.CreateClient().GetStringAsync("/ready");
		string word = await AuthApiFactory.Authorized(cached.CreateClient(), key).GetStringAsync("/ready");

		Assert.Equal(1, auth.Sidecar.Requests.Count(r => r.Uri.AbsolutePath == "/ready"));
		Assert.Contains("\"sidecar\":null", word, StringComparison.Ordinal);
	}

	/// <summary>Disabled mode treats every caller as trusted, but anyone who can reach the API is such a caller, so they share it.</summary>
	[Fact]
	public async Task Disabled_mode_shares_the_probe()
	{
		using WebApplicationFactory<Program> cached = _factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Sidecar:ReadinessCacheTime", "00:00:30"));
		_factory.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.Readiness("ready", resident: true));

		HttpClient client = cached.CreateClient();
		await client.GetStringAsync("/ready");
		await client.GetStringAsync("/ready");

		Assert.Equal(1, ReadyProbes);
	}

	/// <summary>A clock whose monotonic timestamp and wall time move apart, as when NTP steps the wall clock.</summary>
	private sealed class SteppedClock : TimeProvider
	{
		private long _ticks;
		private DateTimeOffset _wall = DateTimeOffset.UnixEpoch.AddYears(56);

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public override long GetTimestamp()
		{
			return _ticks;
		}

		public override DateTimeOffset GetUtcNow()
		{
			return _wall;
		}

		public void Advance(TimeSpan by)
		{
			_ticks += by.Ticks;
			_wall += by;
		}

		public void StepWallClock(TimeSpan by)
		{
			_wall += by;
		}
	}
}
