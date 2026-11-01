using System.Net;
using Memoria.Api.Hosting;

namespace Memoria.Api.Tests;

public sealed class HealthProbeTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	[Theory]
	[InlineData(null, "http://127.0.0.1:8080/health")]
	[InlineData("http://0.0.0.0:8080", "http://127.0.0.1:8080/health")]
	[InlineData("http://+:9090;https://+:9443", "http://127.0.0.1:9090/health")]
	[InlineData("http://localhost:8080", "http://127.0.0.1:8080/health")]
	[InlineData("http://[::]:8080", "http://[::1]:8080/health")]
	[InlineData("http://127.0.0.1:8090", "http://127.0.0.1:8090/health")]
	[InlineData("http://192.168.1.5:8080", "http://192.168.1.5:8080/health")]
	public void ResolveUrl_PointsAtLoopbackHealth(string? urls, string expected)
	{
		Assert.Equal(new Uri(expected), HealthProbe.ResolveUrl(urls));
	}

	[Fact]
	public void IsRequested_OnlyForLeadingSwitch()
	{
		Assert.True(HealthProbe.IsRequested(["--health-check"]));
		Assert.False(HealthProbe.IsRequested([]));
		Assert.False(HealthProbe.IsRequested(["--urls", "http://x", "--health-check"]));
	}

	[Fact]
	public async Task Run_ReturnsZeroWhenHealthAnswers()
	{
		HttpClient client = factory.CreateClient();

		int exit = await HealthProbe.RunAsync(client, new Uri(client.BaseAddress!, "/health"));

		Assert.Equal(0, exit);
	}

	[Fact]
	public async Task Run_ReturnsOneWhenUnreachableOrFailing()
	{
		HttpClient client = factory.CreateClient();
		Assert.Equal(1, await HealthProbe.RunAsync(client, new Uri(client.BaseAddress!, "/no-such-route")));

		using HttpClient dead = new(new FakeSidecar().Unreachable());
		Assert.Equal(1, await HealthProbe.RunAsync(dead, new Uri("http://localhost:1/health")));
	}

	[Fact]
	public async Task Health_StatusIsOk()
	{
		HttpClient client = factory.CreateClient();
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
	}
}
