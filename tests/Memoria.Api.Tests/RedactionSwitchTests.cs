using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Memoria.Api.Http;
using Memoria.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using static Memoria.Api.Tests.IngestKit;

namespace Memoria.Api.Tests;

/// <summary>
/// The per-collection off switch. "open" is off, "closed" sits beside it, and nothing a caller sends may
/// matter. The default configuration is covered by <see cref="DocumentIngestTests"/>, which never sets it.
/// </summary>
public sealed class RedactionSwitchTests : IClassFixture<AuthApiFactory>, IAsyncDisposable
{
	private readonly AuthApiFactory _factory;

	private readonly WebApplicationFactory<Program> _configured;

	public RedactionSwitchTests(AuthApiFactory factory)
	{
		_factory = factory;
		_configured = factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Ingest:Collections:open:Redaction", "Disabled"));
	}

	public async ValueTask DisposeAsync()
	{
		await _configured.DisposeAsync();
	}

	private async Task<HttpClient> ClientAsync(string[] scopes, params string[] collections)
	{
		(_, string key) = await _factory.IssueAsync(scopes, collections);
		return AuthApiFactory.Authorized(_configured.CreateClient(), key);
	}

	private Task<HttpClient> WriterOfBothAsync()
	{
		return ClientAsync([MemoriaScopes.Ingest], "open", "closed");
	}

	private static string Unique()
	{
		return Guid.NewGuid().ToString("N");
	}

	private static string StoredWith(FakeCorpus _, string collection, FakeSidecar sidecar)
	{
		FakeSidecar.RecordedRequest sent = sidecar.Requests.Last(r => r.Uri.AbsolutePath == "/documents" && JsonDocument.Parse(r.Body!).RootElement.GetProperty("collection").GetString() == collection);
		return JsonDocument.Parse(sent.Body!).RootElement.GetProperty("content").GetString()!;
	}

	[Fact]
	public async Task A_key_scoped_to_both_gets_each_collections_own_behaviour()
	{
		FakeCorpus corpus = new(_factory.Sidecar);
		HttpClient client = await WriterOfBothAsync();
		string token = Token(20);

		HttpResponseMessage open = await PostDocumentAsync(client, collection: "open", sourceId: Unique(), body: "key " + token, title: "title " + token);
		string openStored = StoredWith(corpus, "open", _factory.Sidecar);
		HttpResponseMessage closed = await PostDocumentAsync(client, collection: "closed", sourceId: Unique(), body: "key " + token, title: "title " + token);
		string closedStored = StoredWith(corpus, "closed", _factory.Sidecar);

		Assert.Contains("key " + token, openStored, StringComparison.Ordinal);
		Assert.Contains("title " + token, openStored, StringComparison.Ordinal);
		JsonElement openResult = await ReadAsync(open);
		Assert.Equal(IngestRedaction.Disabled, openResult.GetProperty("redaction").GetString());
		Assert.Empty(openResult.GetProperty("redactions").EnumerateObject());
		Assert.Empty(openResult.GetProperty("refusals").EnumerateArray());

		Assert.DoesNotContain(token, closedStored, StringComparison.Ordinal);
		JsonElement closedResult = await ReadAsync(closed);
		Assert.Equal(IngestRedaction.Enabled, closedResult.GetProperty("redaction").GetString());
		Assert.Equal(2, closedResult.GetProperty("redactions").GetProperty("gitleaks-github-pat").GetInt32());
	}

	[Fact]
	public async Task A_session_follows_its_collections_setting()
	{
		FakeCorpus corpus = new(_factory.Sidecar);
		HttpClient client = await WriterOfBothAsync();
		string token = Token(21);
		string jsonl = ClaudeLine("user", "key " + token) + ClaudeLine("assistant", "noted");

		HttpResponseMessage open = await PostSessionAsync(client, jsonl, collection: "open", sessionId: Guid.NewGuid().ToString());
		string openStored = StoredWith(corpus, "open", _factory.Sidecar);
		HttpResponseMessage closed = await PostSessionAsync(client, jsonl, collection: "closed", sessionId: Guid.NewGuid().ToString());
		string closedStored = StoredWith(corpus, "closed", _factory.Sidecar);

		Assert.Equal(HttpStatusCode.Accepted, open.StatusCode);
		Assert.Contains(token, openStored, StringComparison.Ordinal);
		JsonElement openResult = await ReadAsync(open);
		Assert.Equal(IngestRedaction.Disabled, openResult.GetProperty("redaction").GetString());
		Assert.Empty(openResult.GetProperty("redactions").EnumerateObject());

		Assert.DoesNotContain(token, closedStored, StringComparison.Ordinal);
		Assert.Equal(IngestRedaction.Enabled, (await ReadAsync(closed)).GetProperty("redaction").GetString());
	}

	[Fact]
	public async Task Nothing_a_caller_sends_can_turn_redaction_off()
	{
		FakeCorpus corpus = new(_factory.Sidecar);
		string token = Token(22);
		(_, string key) = await _factory.IssueAsync([MemoriaScopes.Ingest], ["closed"], label: "Redaction=Disabled");
		HttpClient client = AuthApiFactory.Authorized(_configured.CreateClient(), key);
		foreach (string header in new[] { "Redaction", "X-Redaction", "X-Memoria-Redaction", "Memoria-Ingest-Redaction" })
		{
			client.DefaultRequestHeaders.Add(header, "Disabled");
		}

		object envelope = new
		{
			collection = "closed",
			source = "notes",
			sourceId = Unique(),
			body = "key " + token,
			updated = Stamp,
			redaction = "Disabled",
			redact = false,
			tags = new[] { "redaction:disabled" },
			collections = new { closed = new { Redaction = "Disabled" } },
		};
		HttpResponseMessage document = await client.PostAsJsonAsync("/v1/documents?redaction=Disabled&Memoria:Ingest:Collections:closed:Redaction=Disabled", envelope);
		string documentStored = StoredWith(corpus, "closed", _factory.Sidecar);

		string query = "collection=closed&format=claude&sessionId=" + Guid.NewGuid() + "&updated=2026-01-15T12:00:00Z&redaction=Disabled&Memoria:Ingest:Collections:closed:Redaction=Disabled";
		HttpResponseMessage session = await client.PostAsync("/v1/ingest/session?" + query, new StringContent(ClaudeLine("user", "key " + token), Encoding.UTF8, "application/x-ndjson"));
		string sessionStored = StoredWith(corpus, "closed", _factory.Sidecar);

		Assert.Equal(HttpStatusCode.Accepted, document.StatusCode);
		Assert.Equal(HttpStatusCode.Accepted, session.StatusCode);
		Assert.DoesNotContain(token, documentStored, StringComparison.Ordinal);
		Assert.DoesNotContain(token, sessionStored, StringComparison.Ordinal);
		Assert.Equal(IngestRedaction.Enabled, (await ReadAsync(document)).GetProperty("redaction").GetString());
		Assert.Equal(IngestRedaction.Enabled, (await ReadAsync(session)).GetProperty("redaction").GetString());
	}

	[Fact]
	public async Task A_name_differing_only_by_case_is_another_collection_and_an_enabled_one_stays_redacted()
	{
		FakeCorpus corpus = new(_factory.Sidecar);
		HttpClient client = await ClientAsync([MemoriaScopes.Ingest], "Open", "closed");
		string token = Token(23);

		HttpResponseMessage wrongCase = await PostDocumentAsync(client, collection: "Open", sourceId: Unique(), body: token);
		await PostDocumentAsync(client, collection: "closed", sourceId: Unique(), body: token);

		Assert.Equal(HttpStatusCode.Conflict, wrongCase.StatusCode);
		Assert.Equal("ingest_not_enabled", (await ReadAsync(wrongCase)).GetProperty("code").GetString());
		Assert.DoesNotContain(token, StoredWith(corpus, "closed", _factory.Sidecar), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("disabled")]
	[InlineData("DISABLED")]
	[InlineData(" Disabled")]
	[InlineData("Disabled ")]
	[InlineData("Disable")]
	[InlineData("Off")]
	[InlineData("false")]
	[InlineData("0")]
	[InlineData("None")]
	[InlineData("")]
	public async Task A_misspelled_or_wrong_case_value_leaves_redaction_on_and_warns(string value)
	{
		FakeCorpus corpus = new(_factory.Sidecar);
		string collection = "typo-" + Unique();
		await using WebApplicationFactory<Program> typo = _factory.WithWebHostBuilder(builder =>
		{
			builder.UseSetting($"Memoria:Ingest:Collections:{collection}:Redaction", value);
			builder.UseSetting($"Memoria:Ingest:Collections:{collection}:Enabled", "true");
		});
		(_, string key) = await _factory.IssueAsync([MemoriaScopes.Ingest], [collection]);
		string token = Token(24);

		HttpResponseMessage response = await PostDocumentAsync(AuthApiFactory.Authorized(typo.CreateClient(), key), collection: collection, sourceId: Unique(), body: token);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.DoesNotContain(token, StoredWith(corpus, collection, _factory.Sidecar), StringComparison.Ordinal);
		Assert.Equal(IngestRedaction.Enabled, (await ReadAsync(response)).GetProperty("redaction").GetString());
		Assert.Contains(_factory.Logs, log => log.EventId.Id == 3301 && log.Message.Contains(collection, StringComparison.Ordinal));
		Assert.DoesNotContain(_factory.Logs, log => log.EventId.Id == 3300 && log.Message.Contains(collection, StringComparison.Ordinal));
	}

	[Fact]
	public void Startup_warns_loudly_naming_the_collection()
	{
		_ = _configured.CreateClient();

		Assert.Contains(_factory.Logs, static log => log.EventId.Id == 3300 && log.Level == LogLevel.Warning && log.Message.Contains("REDACTION IS OFF for collection open", StringComparison.Ordinal));
	}

	[Fact]
	public async Task The_audit_record_says_redaction_was_disabled()
	{
		_ = new FakeCorpus(_factory.Sidecar);
		string token = Token(25);
		(Memoria.Api.Auth.ApiKeyRecord record, string key) = await _factory.IssueAsync([MemoriaScopes.Ingest], ["open", "closed"]);
		HttpClient client = AuthApiFactory.Authorized(_configured.CreateClient(), key);

		await PostDocumentAsync(client, collection: "open", sourceId: Unique(), body: "body " + token);
		await PostDocumentAsync(client, collection: "closed", sourceId: Unique(), body: "body " + token);

		List<AuthApiFactory.AuditRow> rows = await _factory.WaitForAuditAsync(found => found.Count(r => r.KeyId == record.Id) >= 2);
		AuthApiFactory.AuditRow[] mine = [.. rows.Where(r => r.KeyId == record.Id)];
		Assert.Contains("\"Redaction\":\"disabled\"", Assert.Single(mine, static r => r.Collections!.Contains("open", StringComparison.Ordinal)).Target!, StringComparison.Ordinal);
		Assert.Contains("\"Redaction\":\"enabled\"", Assert.Single(mine, static r => r.Collections!.Contains("closed", StringComparison.Ordinal)).Target!, StringComparison.Ordinal);
		Assert.DoesNotContain(token, JsonSerializer.Serialize(mine), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Status_shows_unredacted_collections_only_to_an_admin()
	{
		_factory.Sidecar.Requests.Clear();
		_factory.Sidecar.On("GET", "/status", HttpStatusCode.OK, SidecarFixtures.Status);
		_factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);

		(_, string adminKey) = await _factory.IssueAsync([MemoriaScopes.Read, MemoriaScopes.Admin], allCollections: true);
		HttpResponseMessage admin = await AuthApiFactory.Authorized(_configured.CreateClient(), adminKey).GetAsync("/v1/status");
		HttpResponseMessage bound = await (await ClientAsync([MemoriaScopes.Read, MemoriaScopes.Admin], "open")).GetAsync("/v1/status");
		HttpResponseMessage reader = await (await ClientAsync([MemoriaScopes.Read], "open")).GetAsync("/v1/status");

		Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
		Assert.Equal("open", Assert.Single((await ReadAsync(admin)).GetProperty("unredactedCollections").EnumerateArray()).GetString());
		Assert.Equal(HttpStatusCode.OK, reader.StatusCode);
		JsonElement readerStatus = await ReadAsync(reader);
		Assert.True(!readerStatus.TryGetProperty("unredactedCollections", out JsonElement listed) || listed.ValueKind == JsonValueKind.Null);
		JsonElement boundStatus = await ReadAsync(bound);
		Assert.True(!boundStatus.TryGetProperty("unredactedCollections", out JsonElement boundListed) || boundListed.ValueKind == JsonValueKind.Null);
	}

	[Fact]
	public async Task A_session_keeps_its_paths_when_the_switch_turns()
	{
		FakeCorpus corpus = new(_factory.Sidecar);
		HttpClient client = await WriterOfBothAsync();
		string id = Guid.NewGuid().ToString();

		await PostSessionAsync(client, Transcript(2), collection: "open", sessionId: id);
		string[] open = [.. corpus.Files.Keys.Order(StringComparer.Ordinal)];
		corpus.Files.Clear();
		await PostSessionAsync(client, Transcript(2), collection: "closed", sessionId: id);

		string[] closed = [.. corpus.Files.Keys.Order(StringComparer.Ordinal)];
		Assert.Equal<string>(open, closed);
	}
}
