using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Memoria.Api.Http;
using Memoria.Core.Contracts;

namespace Memoria.Api.Tests;

/// <summary>What reaches the sidecar for a document request, and the answer to one that names nothing.</summary>
public sealed class DocumentRequestShapeTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	[Fact]
	public async Task ABlankReference_Is404_WithoutAskingTheSidecar()
	{
		factory.Sidecar.Requests.Clear();

		HttpResponseMessage response = await factory.CreateClient().GetAsync("/v1/docs/%20%20");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = problem.RootElement;
		Assert.Equal("Not found", root.GetProperty("title").GetString());
		Assert.Equal(SidecarDisclosure.NotFoundMessage, root.GetProperty("detail").GetString());
		Assert.Equal("not_found", root.GetProperty("code").GetString());
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task AnEmptyIdList_BesideAPattern_IsLeftOut()
	{
		using JsonDocument sent = await SentForAsync(new MultiGetRequest([], "claude-sessions/*.md", null, null, null, null));

		Assert.False(sent.RootElement.TryGetProperty("ids", out _), sent.RootElement.GetRawText());
		Assert.Equal("claude-sessions/*.md", sent.RootElement.GetProperty("pattern").GetString());
	}

	[Fact]
	public async Task ABlankPattern_BesideIds_IsLeftOut()
	{
		using JsonDocument sent = await SentForAsync(new MultiGetRequest(["f6feba"], "   ", null, null, null, null));

		Assert.False(sent.RootElement.TryGetProperty("pattern", out _), sent.RootElement.GetRawText());
		Assert.Equal(["f6feba"], sent.RootElement.GetProperty("ids").EnumerateArray().Select(static id => id.GetString()));
	}

	private async Task<JsonDocument> SentForAsync(MultiGetRequest request)
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/docs:multiGet", HttpStatusCode.OK, SidecarFixtures.MultiGet);

		HttpResponseMessage response = await factory.CreateClient().PostAsJsonAsync("/v1/docs:multiGet", request, Json);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return JsonDocument.Parse(factory.Sidecar.Single().Body!);
	}
}

/// <summary>Readiness asks the sidecar whether it answers, not whether its models are loaded: that is what keeps a warming API in rotation.</summary>
public sealed class ReadinessProbeQueryTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	[Fact]
	public async Task AnAdminsOwnProbe_DoesNotRequireModels()
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.Readiness("warming", resident: false));
		HttpClient admin = await factory.AdminAsync();
		factory.Sidecar.Requests.Clear();

		HttpResponseMessage response = await admin.GetAsync("/ready");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		FakeSidecar.RecordedRequest sent = factory.Sidecar.Single();
		Assert.Equal("/ready", sent.Uri.AbsolutePath);
		Assert.Equal("", sent.Uri.Query);
	}
}
