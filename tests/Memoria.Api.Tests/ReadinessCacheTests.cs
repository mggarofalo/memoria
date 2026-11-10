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
		MovableClock time = new();
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
		ReadinessCache cache = new(new MovableClock(), Options.Create(settings));
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
}
