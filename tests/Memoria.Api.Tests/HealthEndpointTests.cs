using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Memoria.Core.Contracts;

namespace Memoria.Api.Tests;

public sealed class HealthEndpointTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	[Fact]
	public async Task Health_IsProcessLocal()
	{
		HttpClient client = factory.CreateClient();
		factory.Sidecar.Requests.Clear();

		HttpResponseMessage response = await client.GetAsync("/health");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
		Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("version").GetString()));
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task Ready_WhenSidecarReady_Returns200WithSidecarDetails()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.Readiness("ready", resident: true));
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/ready");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		ReadinessReport report = (await response.Content.ReadFromJsonAsync<ReadinessReport>(Json))!;
		Assert.True(report.Ready);
		Assert.Equal(ReadinessStatus.Ready, report.Status);
		Assert.Null(report.Reason);
		Assert.Equal(1502, report.Sidecar!.Index.TotalDocuments);
		Assert.True(report.Sidecar.Models.Resident);

		FakeSidecar.RecordedRequest probe = factory.Sidecar.Single();
		Assert.Equal("/ready", probe.Uri.AbsolutePath);
		Assert.Equal("?requireModels=true", probe.Uri.Query);
		Assert.Equal("Bearer " + MemoriaApiFactory.SidecarSecret, probe.Headers["Authorization"]);
	}

	[Fact]
	public async Task Ready_WhenSidecarWarming_Returns503WithReason()
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.ServiceUnavailable, SidecarFixtures.Readiness("warming", resident: false));
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/ready");

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		ReadinessReport report = (await response.Content.ReadFromJsonAsync<ReadinessReport>(Json))!;
		Assert.False(report.Ready);
		Assert.Equal(ReadinessStatus.Warming, report.Status);
		Assert.Equal("models are still loading", report.Reason);
		Assert.NotNull(report.Sidecar);
	}

	[Fact]
	public async Task Ready_WhenWarmupFailed_ReportsTheError()
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.ServiceUnavailable, SidecarFixtures.Readiness("warming", resident: false, warmupError: "CUDA out of memory"));
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/ready");

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		ReadinessReport report = (await response.Content.ReadFromJsonAsync<ReadinessReport>(Json))!;
		Assert.Equal("warmup failed: CUDA out of memory", report.Reason);
	}

	[Fact]
	public async Task Ready_WhenSidecarUnreachable_Returns503Unavailable()
	{
		factory.Sidecar.Unreachable();
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/ready");

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		ReadinessReport report = (await response.Content.ReadFromJsonAsync<ReadinessReport>(Json))!;
		Assert.False(report.Ready);
		Assert.Equal(ReadinessStatus.Unavailable, report.Status);
		Assert.StartsWith("sidecar_unreachable:", report.Reason);
		Assert.Null(report.Sidecar);
	}

	[Fact]
	public async Task Ready_WhenSidecarHangs_TimesOutWithinReadinessTimeout()
	{
		factory.Sidecar.Hanging();
		HttpClient client = factory.CreateClient();

		DateTime started = DateTime.UtcNow;
		HttpResponseMessage response = await client.GetAsync("/ready");

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "the probe should give up after the 1 s readiness timeout, not the 2 min client timeout");
		ReadinessReport report = (await response.Content.ReadFromJsonAsync<ReadinessReport>(Json))!;
		Assert.Equal(ReadinessStatus.Unavailable, report.Status);
		Assert.StartsWith("sidecar_timeout:", report.Reason);
	}

	[Fact]
	public async Task Ready_WhenSidecarRejectsSecret_Returns503Unavailable()
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.Unauthorized, """{"error":"unauthorized","message":"Missing or invalid bearer token"}""");
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/ready");

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		ReadinessReport report = (await response.Content.ReadFromJsonAsync<ReadinessReport>(Json))!;
		Assert.Equal("unauthorized: Missing or invalid bearer token", report.Reason);
	}
}
