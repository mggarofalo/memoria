using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Memoria.Api.Tests;

/// <summary>What every request to the sidecar carries, and where it goes when the sidecar sits under a path.</summary>
public sealed class SidecarClientRequestTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	[Fact]
	public async Task A_sidecar_behind_a_path_is_asked_under_that_path()
	{
		using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Sidecar:BaseUrl", "http://sidecar.test:8282/qmd/"));
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/qmd/collections", HttpStatusCode.OK, SidecarFixtures.Collections);

		HttpResponseMessage response = await host.CreateClient().GetAsync("/v1/collections");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("/qmd/collections", factory.Sidecar.Single().Uri.AbsolutePath);
	}

	[Fact]
	public async Task Every_request_asks_for_json()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);

		await factory.CreateClient().GetAsync("/v1/collections");

		Assert.Equal("application/json", factory.Sidecar.Single().Headers["Accept"]);
	}
}
