using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Memoria.Api.Http;
using Memoria.Api.Search;
using Memoria.Core.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Memoria.Api.Tests;

/// <summary>The API half of the glob grammar. The sidecar enforces the same cases from the same file.</summary>
public sealed class MultiGetPolicyTests
{
	private static readonly Lazy<JsonDocument> Cases = new(() =>
	{
		string? directory = AppContext.BaseDirectory;
		while (directory is not null && !File.Exists(Path.Combine(directory, "Memoria.slnx")))
		{
			directory = Path.GetDirectoryName(directory);
		}

		return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!, "sidecar", "test", "glob-policy-cases.json")));
	});

	public static TheoryData<string> Accepted => Load("accepted");

	public static TheoryData<string> Refused => Load("refused");

	private static TheoryData<string> Load(string name)
	{
		return new TheoryData<string>(Cases.Value.RootElement.GetProperty(name).EnumerateArray().Select(static e => e.GetString()!));
	}

	[Theory]
	[MemberData(nameof(Accepted))]
	public void RealisticGlobs_AreAccepted(string glob)
	{
		Assert.Null(MultiGetPolicy.GlobViolation(glob));
		Assert.Empty(MultiGetPolicy.Validate(null, glob, null, null));
		Assert.Empty(MultiGetPolicy.Validate([glob], null, null, null));
	}

	[Theory]
	[MemberData(nameof(Refused))]
	public void HostileGlobs_AreRefused_AsPatternAndAsId(string glob)
	{
		Assert.NotEmpty(MultiGetPolicy.Validate(null, glob, null, null));
		Assert.NotEmpty(MultiGetPolicy.Validate([glob], null, null, null));
		Assert.NotEmpty(MultiGetPolicy.Validate(null, $"claude-sessions/*.md,{glob}", null, null));
	}

	[Fact]
	public void Caps_AreEnforced()
	{
		Assert.NotEmpty(MultiGetPolicy.Validate(null, "a*" + new string('b', 256), null, null));
		Assert.NotEmpty(MultiGetPolicy.Validate(Enumerable.Range(0, 11).Select(static i => $"c{i}/*.md").ToArray(), null, null, null));
		Assert.NotEmpty(MultiGetPolicy.Validate(null, string.Join(',', Enumerable.Range(0, 11).Select(static i => $"c{i}/*.md")), null, null));
		Assert.NotEmpty(MultiGetPolicy.Validate(Enumerable.Range(0, 201).Select(static i => $"r{i}.md").ToArray(), null, null, null));
		Assert.NotEmpty(MultiGetPolicy.Validate(null, string.Join(',', Enumerable.Range(0, 201).Select(static i => $"r{i}.md")), null, null));
		Assert.NotEmpty(MultiGetPolicy.Validate([new string('x', 1025)], null, null, null));
		Assert.NotEmpty(MultiGetPolicy.Validate(["a.md,b.md"], null, null, null));
		Assert.Empty(MultiGetPolicy.Validate(Enumerable.Range(0, 200).Select(static i => $"r{i}.md").ToArray(), null, null, null));
	}

	[Theory]
	[InlineData(0, false)]
	[InlineData(1, true)]
	[InlineData(MultiGetPolicy.MaxBytesCeiling, true)]
	[InlineData(MultiGetPolicy.MaxBytesCeiling + 1, false)]
	[InlineData(int.MaxValue, false)]
	public void MaxBytes_HasACeiling(int maxBytes, bool accepted)
	{
		Assert.Equal(accepted, MultiGetPolicy.Validate(["a.md"], null, maxBytes, null).Count == 0);
	}
}

/// <summary>A hostile glob must stop at the API. Every call has a hard timeout so a regression fails instead of hanging CI.</summary>
public sealed class MultiGetBoundaryTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private static readonly TimeSpan HardLimit = TimeSpan.FromSeconds(20);

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private static readonly string[] Hostile = ["+(*)Z", "*(*)Z", "(*)*(*)*(*)*Z", "@(*|*)+Z", "*?*?*?*?*?*?*?*?*?*?Z", "{a,b}{a,b}{a,b}{a,b}*"];

	[Fact]
	public async Task Rest_RefusesHostileGlobs_InPatternAndIds_WithoutCallingTheSidecar()
	{
		factory.Sidecar.Requests.Clear();
		HttpClient reader = await factory.ClientWithScopesAsync([MemoriaScopes.Read], allCollections: true);

		foreach (string glob in Hostile)
		{
			foreach (MultiGetRequest request in new[]
			{
				new MultiGetRequest(null, glob, null, null, null, null),
				new MultiGetRequest([glob], null, null, null, null, null),
			})
			{
				HttpResponseMessage response = await reader.PostAsJsonAsync("/v1/docs:multiGet", request, Json).WaitAsync(HardLimit);
				Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			}
		}

		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task Mcp_RefusesHostileGlobs_WithoutCallingTheSidecar()
	{
		factory.Sidecar.Requests.Clear();
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		HttpClient http = AuthApiFactory.Authorized(factory.CreateClient(), token);
		await using McpClient client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			TransportMode = HttpTransportMode.StreamableHttp,
			Name = "memoria-tests",
		}, http));

		foreach (string glob in Hostile)
		{
			CallToolResult byPattern = await client.CallToolAsync("get_documents", new Dictionary<string, object?> { ["pattern"] = glob }).AsTask().WaitAsync(HardLimit);
			CallToolResult byId = await client.CallToolAsync("get_documents", new Dictionary<string, object?> { ["ids"] = new[] { glob } }).AsTask().WaitAsync(HardLimit);
			Assert.True(byPattern.IsError);
			Assert.True(byId.IsError);
		}

		CallToolResult huge = await client.CallToolAsync("get_documents", new Dictionary<string, object?> { ["ids"] = new[] { "a.md" }, ["maxBytes"] = int.MaxValue }).AsTask().WaitAsync(HardLimit);
		Assert.True(huge.IsError);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task Rest_RefusesMaxBytesOverTheCeiling()
	{
		factory.Sidecar.Requests.Clear();
		HttpClient reader = await factory.ClientWithScopesAsync([MemoriaScopes.Read], allCollections: true);

		HttpResponseMessage response = await reader.PostAsJsonAsync("/v1/docs:multiGet", new MultiGetRequest(["a.md"], null, MultiGetPolicy.MaxBytesCeiling + 1, null, null, null), Json);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task RealisticGlob_StillWorks()
	{
		factory.Sidecar.On("POST", "/docs:multiGet", HttpStatusCode.OK, SidecarFixtures.MultiGet);
		HttpClient reader = await factory.ReaderOfAsync("claude-sessions");

		HttpResponseMessage response = await reader.PostAsJsonAsync("/v1/docs:multiGet", new MultiGetRequest(null, "claude-sessions/2026-10*.md", null, null, null, null), Json);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task ScopedKey_SendsItsAllowlist_OnDocumentGetAndMultiGet()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/docs/f6feba", HttpStatusCode.OK, SidecarFixtures.Document);
		factory.Sidecar.On("POST", "/docs:multiGet", HttpStatusCode.OK, SidecarFixtures.MultiGet);
		HttpClient reader = await factory.ReaderOfAsync("claude-sessions", "a b/c");

		await reader.GetAsync("/v1/docs/f6feba");
		await reader.PostAsJsonAsync("/v1/docs:multiGet", new MultiGetRequest(null, "*/*.md", null, null, null, null), Json);

		FakeSidecar.RecordedRequest get = Assert.Single(factory.Sidecar.Requests, r => r.Method == "GET");
		Assert.Equal("?collections=claude-sessions&collections=a%20b%2Fc", get.Uri.Query);
		FakeSidecar.RecordedRequest post = Assert.Single(factory.Sidecar.Requests, r => r.Method == "POST");
		using JsonDocument body = JsonDocument.Parse(post.Body!);
		Assert.Equal(["claude-sessions", "a b/c"], body.RootElement.GetProperty("collections").EnumerateArray().Select(static e => e.GetString()!).ToArray());
	}

	[Fact]
	public async Task AllCollectionsKey_SendsNoAllowlist()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/docs/f6feba", HttpStatusCode.OK, SidecarFixtures.Document);
		factory.Sidecar.On("POST", "/docs:multiGet", HttpStatusCode.OK, SidecarFixtures.MultiGet);
		HttpClient everything = await factory.ClientWithScopesAsync([MemoriaScopes.Read], allCollections: true);

		await everything.GetAsync("/v1/docs/f6feba");
		await everything.PostAsJsonAsync("/v1/docs:multiGet", new MultiGetRequest(null, "*/*.md", null, null, null, null), Json);

		Assert.Equal(string.Empty, Assert.Single(factory.Sidecar.Requests, r => r.Method == "GET").Uri.Query);
		using JsonDocument body = JsonDocument.Parse(Assert.Single(factory.Sidecar.Requests, r => r.Method == "POST").Body!);
		Assert.False(body.RootElement.TryGetProperty("collections", out _));
	}

	[Fact]
	public async Task OversizedReference_IsAMiss_WithoutCallingTheSidecar()
	{
		factory.Sidecar.Requests.Clear();
		HttpClient everything = await factory.ClientWithScopesAsync([MemoriaScopes.Read], allCollections: true);

		HttpResponseMessage response = await everything.GetAsync($"/v1/docs/{new string('x', 1025)}");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Empty(factory.Sidecar.Requests);
	}
}

public sealed class AllowsFileTests
{
	private sealed class Scope(params string[] collections) : ICallerScope
	{
		public IReadOnlyCollection<string>? Collections { get; } = collections;

		public bool SeesBackendDetails => false;
	}

	private sealed class Everything : ICallerScope
	{
		public IReadOnlyCollection<string>? Collections => null;

		public bool SeesBackendDetails => false;
	}

	[Theory]
	[InlineData("alpha/a.md", true)]
	[InlineData("alpha/sub/a.md", true)]
	[InlineData("beta/a.md", false)]
	[InlineData("alpha/../beta/a.md", false)]
	[InlineData("alpha/./a.md", false)]
	[InlineData("../alpha/a.md", false)]
	[InlineData("alpha//a.md", false)]
	[InlineData("/alpha/a.md", false)]
	[InlineData("alpha\\..\\beta\\a.md", false)]
	[InlineData("alpha", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	public void ScopedCaller(string? file, bool expected)
	{
		Assert.Equal(expected, new Scope("alpha").AllowsFile(file));
	}

	[Theory]
	[InlineData("beta/a.md", true)]
	[InlineData("alpha/../beta/a.md", false)]
	public void UnrestrictedCaller_AlsoRefusesDotSegments(string file, bool expected)
	{
		Assert.Equal(expected, new Everything().AllowsFile(file));
	}
}
