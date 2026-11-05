using System.Net;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Memoria.Api.Tests;

/// <summary>The sidecar's search limit answers 503 with <c>Retry-After</c>, which a caller needs to see on both surfaces.</summary>
public sealed class SidecarBusyTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	private void SearchIsBusy(int? retryAfterSeconds)
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/search", (_, _) => IngestKit.Busy(retryAfterSeconds));
	}

	[Theory]
	[InlineData(2, "2", "Search backend busy")]
	[InlineData(null, "5", "Search backend not ready")]
	public async Task Rest_forwards_the_wait_or_gives_a_default(int? sidecarSays, string expected, string title)
	{
		SearchIsBusy(sidecarSays);

		HttpResponseMessage response = await factory.CreateClient().GetAsync("/v1/search?query=x");

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		Assert.Equal(expected, response.Headers.GetValues("Retry-After").Single());
		Assert.Equal(title, (await IngestKit.ReadAsync(response)).GetProperty("title").GetString());
	}

	[Fact]
	public async Task A_wait_in_fractions_of_no_time_still_asks_for_a_second()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/search", (_, _) => IngestKit.Busy(0));

		HttpResponseMessage response = await factory.CreateClient().GetAsync("/v1/search?query=x");

		Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
	}

	[Fact]
	public async Task A_refusal_that_waiting_cannot_fix_has_no_retry_after()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/search", HttpStatusCode.BadRequest, """{"error":"validation","message":"no"}""");

		HttpResponseMessage response = await factory.CreateClient().GetAsync("/v1/search?query=x");

		Assert.False(response.Headers.Contains("Retry-After"));
	}

	[Fact]
	public async Task Mcp_says_the_backend_is_busy_and_how_long_to_wait()
	{
		SearchIsBusy(2);
		HttpClient http = factory.CreateClient();
		HttpClientTransport transport = new(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			TransportMode = HttpTransportMode.StreamableHttp,
			Name = "memoria-tests",
		}, http);
		await using McpClient client = await McpClient.CreateAsync(transport);

		CallToolResult result = await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "x" });

		Assert.True(result.IsError);
		string text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(static c => c.Text));
		Assert.Contains("busy", text, StringComparison.Ordinal);
		Assert.Contains("Try again in 2 seconds", text, StringComparison.Ordinal);
	}
}
