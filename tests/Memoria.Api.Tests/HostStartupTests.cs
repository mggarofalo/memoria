using System.Net;
using System.Text.Json;
using Memoria.Api.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Memoria.Api.Tests;

/// <summary>
/// What Program sets up at startup. Startup runs once per host, so each test starts a host of its own rather than sharing the
/// fixture's.
/// </summary>
public sealed class HostStartupTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>, IDisposable
{
	private readonly string _contentRoot = Directory.CreateTempSubdirectory("memoria-contentroot-").FullName;

	public void Dispose()
	{
		Directory.Delete(_contentRoot, recursive: true);
	}

	private static string? ListenUrls(WebApplicationFactory<Program> host)
	{
		return host.Services.GetRequiredService<IConfiguration>()[WebHostDefaults.ServerUrlsKey];
	}

	[Fact]
	public void With_no_address_configured_the_host_listens_on_the_lan_at_8080()
	{
		using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(static _ => { });

		Assert.Equal("http://0.0.0.0:8080", ListenUrls(host));
	}

	[Fact]
	public void A_configured_address_is_kept()
	{
		using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(static builder => builder.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:5999"));

		Assert.Equal("http://127.0.0.1:5999", ListenUrls(host));
	}

	[Fact]
	public void Kestrel_endpoints_are_left_to_choose_the_address()
	{
		using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(static builder => builder.UseSetting("Kestrel:Endpoints:Http:Url", "http://127.0.0.1:5998"));

		Assert.Null(ListenUrls(host));
	}

	[Fact]
	public async Task An_unknown_route_answers_with_a_problem_document()
	{
		using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(static _ => { });

		HttpResponseMessage response = await host.CreateClient().GetAsync("/no-such-route");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
		using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
	}

	[Fact]
	public async Task The_local_settings_file_in_the_content_root_is_read()
	{
		File.WriteAllText(Path.Combine(_contentRoot, LocalSettings.FileName), """{"Memoria":{"Search":{"RerankByDefault":false}}}""");
		using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder => builder.UseContentRoot(_contentRoot));
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.SearchResponse);

		HttpResponseMessage response = await host.CreateClient().GetAsync("/v1/search?query=receipts");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(factory.Sidecar.Single().Body!);
		Assert.False(body.RootElement.GetProperty("rerank").GetBoolean());
	}
}
