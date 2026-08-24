using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Memoria.Api.Tests;

public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory)
	: IClassFixture<WebApplicationFactory<Program>>
{
	[Fact]
	public async Task Health_ReturnsOk()
	{
		var client = factory.CreateClient();

		var response = await client.GetAsync("/health");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task Ready_ReturnsOk()
	{
		var client = factory.CreateClient();

		var response = await client.GetAsync("/ready");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}
}
