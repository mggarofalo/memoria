using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Core.Contracts;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static Memoria.Api.Tests.IngestKit;

namespace Memoria.Api.Tests;

/// <summary>Writing and deleting single documents, written from the point of view of someone trying to break it.</summary>
public sealed class DocumentIngestTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private Task<HttpClient> WriterAsync(params string[] collections)
	{
		return factory.ClientWithScopesAsync([MemoriaScopes.Ingest], collections.Length == 0 ? ["c"] : collections);
	}

	private static string UniqueId()
	{
		return Guid.NewGuid().ToString("N");
	}

	// --- what gets stored --------------------------------------------------------

	[Fact]
	public async Task A_credential_in_the_body_never_reaches_the_sidecar_and_is_counted_by_rule()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string token = Token();

		HttpResponseMessage response = await PostDocumentAsync(await WriterAsync(), sourceId: UniqueId(), body: "my key is " + token);

		string raw = await response.Content.ReadAsStringAsync();
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
		FakeSidecar.RecordedRequest sent = Assert.Single(factory.Sidecar.Requests, static r => r.Method == "POST" && r.Uri.AbsolutePath == "/documents");
		Assert.DoesNotContain(token, sent.Body!, StringComparison.Ordinal);
		Assert.Contains("[REDACTED:", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);

		JsonElement result = JsonDocument.Parse(raw).RootElement;
		Assert.Equal(1, result.GetProperty("redactions").GetProperty("github-token").GetInt32());
		Assert.StartsWith("job-", result.GetProperty("jobId").GetString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_credential_in_a_field_is_reported_by_field_and_rule_with_no_value()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string token = Token(2);

		HttpResponseMessage response = await PostDocumentAsync(await WriterAsync(), sourceId: UniqueId(), title: "about " + token);

		string raw = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
		Assert.DoesNotContain(token, Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
		JsonElement result = JsonDocument.Parse(raw).RootElement;
		JsonElement refusal = Assert.Single(result.GetProperty("refusals").EnumerateArray());
		Assert.Equal("title", refusal.GetProperty("field").GetString());
		Assert.Equal("github-token", refusal.GetProperty("rule").GetString());
		Assert.Equal(1, result.GetProperty("redactions").GetProperty("github-token").GetInt32());
	}

	[Fact]
	public async Task A_credential_written_with_json_escapes_is_redacted_all_the_same()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string token = Token(3);
		string escaped = string.Concat(token.Select(static c => $"\\u{(int)c:x4}"));
		string json = $$"""{"collection":"c","source":"notes","sourceId":"{{UniqueId()}}","body":"key {{escaped}}","updated":"2026-01-15T12:00:00Z"}""";

		HttpResponseMessage response = await (await WriterAsync()).PostAsync("/v1/documents", new StringContent(json, Encoding.UTF8, "application/json"));

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.DoesNotContain(token, Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
	}

	/// <summary>What an encoding does to redaction, stated rather than hidden. Invisible characters are seen through. The forms marked false are known gaps: a rule reads text and decodes nothing, so a credential encoded this way is stored as it came.</summary>
	[Theory]
	[InlineData("zero-width-joined", true)]
	[InlineData("base64", false)]
	[InlineData("url-encoded", false)]
	[InlineData("literal-json-escapes", false)]
	[InlineData("reversed", false)]
	public async Task What_an_encoding_does_to_redaction_is_pinned(string encoding, bool caught)
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string token = Token(7);
		string encoded = encoding switch
		{
			"zero-width-joined" => string.Join('​', token.ToCharArray()),
			"base64" => Convert.ToBase64String(Encoding.UTF8.GetBytes(token)),
			"url-encoded" => string.Concat(token.Select(static c => $"%{(int)c:X2}")),
			"literal-json-escapes" => string.Concat(token.Select(static c => $"\\u{(int)c:x4}")),
			_ => new string([.. token.Reverse()]),
		};

		JsonElement result = await ReadAsync(await PostDocumentAsync(await WriterAsync(), sourceId: UniqueId(), body: encoded));

		Assert.Equal(caught, !Assert.Single(corpus.Files).Value.Contains(encoded, StringComparison.Ordinal));
		Assert.Equal(caught, result.GetProperty("redactions").EnumerateObject().Any());
	}

	[Fact]
	public async Task Redaction_that_cannot_finish_refuses_the_document_and_names_only_the_rule()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string token = Token(4);
		await using WebApplicationFactory<Program> failing = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
			services.AddSingleton<IRedactor>(new FailingRedactor())));
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);

		HttpResponseMessage response = await PostDocumentAsync(AuthApiFactory.Authorized(failing.CreateClient(), key), sourceId: UniqueId(), body: token);

		string raw = await response.Content.ReadAsStringAsync();
		Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
		Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
		JsonElement problem = JsonDocument.Parse(raw).RootElement;
		Assert.Equal("redaction_failed", problem.GetProperty("code").GetString());
		Assert.Equal("some-rule", problem.GetProperty("rule").GetString());
		Assert.Empty(corpus.Files);
	}

	// --- scope --------------------------------------------------------------------

	[Fact]
	public async Task A_key_for_one_collection_cannot_write_to_another()
	{
		FakeCorpus corpus = new(factory.Sidecar);

		HttpResponseMessage response = await PostDocumentAsync(await WriterAsync("c"), collection: "other", sourceId: UniqueId());

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal("collection_forbidden", (await ReadAsync(response)).GetProperty("code").GetString());
		Assert.Empty(corpus.Calls);
	}

	[Theory]
	[InlineData(MemoriaScopes.Read)]
	[InlineData(MemoriaScopes.Admin)]
	public async Task A_key_without_the_ingest_scope_cannot_write_even_with_the_collection(string scope)
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await factory.ClientWithScopesAsync([scope], ["c"]);

		Assert.Equal(HttpStatusCode.Forbidden, (await PostDocumentAsync(client, sourceId: UniqueId())).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/v1/documents?collection=c&path=a.md")).StatusCode);
		Assert.Empty(corpus.Calls);
	}

	[Fact]
	public async Task No_key_gets_401()
	{
		new FakeCorpus(factory.Sidecar).ToString();

		Assert.Equal(HttpStatusCode.Unauthorized, (await PostDocumentAsync(factory.CreateClient(), sourceId: UniqueId())).StatusCode);
	}

	// --- identity and paths ---------------------------------------------------------

	[Fact]
	public async Task Sending_a_source_and_id_again_overwrites_the_same_file()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = UniqueId();

		await PostDocumentAsync(client, sourceId: id, body: "first");
		JsonElement second = await ReadAsync(await PostDocumentAsync(client, sourceId: id, body: "second"));
		await PostDocumentAsync(client, sourceId: UniqueId(), body: "another document");

		Assert.False(second.GetProperty("created").GetBoolean());
		Assert.Equal(2, corpus.Files.Count);
		Assert.EndsWith("second", corpus.Files[second.GetProperty("path").GetString()!], StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("../../../etc/passwd")]
	[InlineData("..\\..\\windows\\system32")]
	[InlineData("/etc/passwd")]
	[InlineData(".git/config")]
	[InlineData("a/b/c.md")]
	[InlineData("*.md")]
	public async Task A_source_id_cannot_steer_the_path(string sourceId)
	{
		FakeCorpus corpus = new(factory.Sidecar);

		HttpResponseMessage response = await PostDocumentAsync(await WriterAsync(), sourceId: sourceId);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Matches("^notes-[0-9a-f]{32}\\.md$", Assert.Single(corpus.Files).Key);
	}

	[Theory]
	[InlineData("../../evil", "evil")]
	[InlineData(".hidden", "hidden")]
	[InlineData("A B/C", "a-b-c")]
	public async Task A_source_cannot_steer_the_path(string source, string label)
	{
		FakeCorpus corpus = new(factory.Sidecar);

		await PostDocumentAsync(await WriterAsync(), source: source, sourceId: UniqueId());

		Assert.Matches($"^{label}-[0-9a-f]{{32}}\\.md$", Assert.Single(corpus.Files).Key);
	}

	[Fact]
	public async Task A_credential_in_the_identity_stays_out_of_the_file_name_the_file_and_the_ledger()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string token = Token(5);

		await PostDocumentAsync(await WriterAsync(), source: token, sourceId: token);

		(string path, string content) = Assert.Single(corpus.Files);
		Assert.Matches("^doc-[0-9a-f]{32}\\.md$", path);
		Assert.DoesNotContain(token, content, StringComparison.Ordinal);
		await using Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={factory.DatabasePath}");
		await connection.OpenAsync();
		await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT group_concat(identity || paths) || (SELECT group_concat(collection || path || owner) FROM ingest_paths) FROM ingest_state;";
		Assert.DoesNotContain(token, (string)(await command.ExecuteScalarAsync())!, StringComparison.Ordinal);
	}

	[Fact]
	public async Task An_envelope_field_cannot_inject_frontmatter()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		const string Attack = "innocent\"\nsource: forged\n---\n\n# Fake heading\ninjected: true";

		await PostDocumentAsync(await WriterAsync(), sourceId: UniqueId(), title: Attack, body: "---\nsource: forged-in-body\n---\nbody");

		string[] lines = Assert.Single(corpus.Files).Value.Split('\n');
		int close = Array.IndexOf(lines, "---", 1);
		string[] frontmatter = lines[1..close];
		Assert.Contains("source: \"notes\"", frontmatter);
		Assert.DoesNotContain(frontmatter, static line => line.StartsWith("source: forged", StringComparison.Ordinal));
		Assert.DoesNotContain(frontmatter, static line => line.StartsWith("injected:", StringComparison.Ordinal));
		Assert.All(frontmatter, static line => Assert.Matches("^[a-z_]+:|^  - ", line));
	}

	// --- ordering -------------------------------------------------------------------

	[Fact]
	public async Task An_older_version_cannot_replace_a_newer_one()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = UniqueId();
		await PostDocumentAsync(client, sourceId: id, body: "newer", updated: Stamp);

		HttpResponseMessage replay = await PostDocumentAsync(client, sourceId: id, body: "older", updated: Stamp.AddHours(-1));

		Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
		Assert.Equal("stale_version", (await ReadAsync(replay)).GetProperty("code").GetString());
		Assert.EndsWith("newer", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
	}

	[Fact]
	public async Task The_same_version_is_a_retry_and_a_later_one_replaces()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = UniqueId();

		Assert.Equal(HttpStatusCode.Accepted, (await PostDocumentAsync(client, sourceId: id, updated: Stamp)).StatusCode);
		Assert.Equal(HttpStatusCode.Accepted, (await PostDocumentAsync(client, sourceId: id, updated: Stamp)).StatusCode);
		Assert.Equal(HttpStatusCode.Accepted, (await PostDocumentAsync(client, sourceId: id, body: "later", updated: Stamp.AddSeconds(1))).StatusCode);

		Assert.EndsWith("later", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_version_far_in_the_future_cannot_poison_the_document()
	{
		FakeCorpus corpus = new(factory.Sidecar);

		HttpResponseMessage response = await PostDocumentAsync(await WriterAsync(), sourceId: UniqueId(), updated: DateTimeOffset.UtcNow.AddDays(3));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty(corpus.Calls);
	}

	[Theory]
	[InlineData("""{"source":"s","sourceId":"i","body":"b","updated":"2026-01-01T00:00:00Z"}""", "collection")]
	[InlineData("""{"collection":"c","sourceId":"i","body":"b","updated":"2026-01-01T00:00:00Z"}""", "source")]
	[InlineData("""{"collection":"c","source":"s","body":"b","updated":"2026-01-01T00:00:00Z"}""", "sourceId")]
	[InlineData("""{"collection":"c","source":"s","sourceId":"i","updated":"2026-01-01T00:00:00Z"}""", "body")]
	[InlineData("""{"collection":"c","source":"s","sourceId":"i","body":"b"}""", "updated")]
	[InlineData("""not json""", "body")]
	public async Task A_missing_field_is_a_validation_error(string json, string field)
	{
		FakeCorpus corpus = new(factory.Sidecar);

		HttpResponseMessage response = await (await WriterAsync()).PostAsync("/v1/documents", new StringContent(json, Encoding.UTF8, "application/json"));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
		Assert.Empty(corpus.Calls);
	}

	// --- limits and backend failures --------------------------------------------------

	[Fact]
	public async Task A_rendered_document_over_the_limit_gets_413_without_reaching_the_sidecar()
	{
		FakeCorpus corpus = new(factory.Sidecar);

		HttpResponseMessage response = await PostDocumentAsync(await WriterAsync(), sourceId: UniqueId(), body: new string('x', (2 * 1024 * 1024) + 1));

		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		Assert.Equal("document_too_large", (await ReadAsync(response)).GetProperty("code").GetString());
		Assert.Empty(corpus.Calls);
	}

	[Fact]
	public async Task A_body_over_the_request_limit_is_refused_as_it_arrives()
	{
		new FakeCorpus(factory.Sidecar).ToString();
		HttpClient client = await WriterAsync();
		using EndlessContent endless = new();

		HttpResponseMessage response = await client.PostAsync("/v1/documents", endless);

		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		Assert.Equal("payload_too_large", (await ReadAsync(response)).GetProperty("code").GetString());
	}

	[Fact]
	public async Task A_declared_length_over_the_limit_is_refused_before_reading()
	{
		new FakeCorpus(factory.Sidecar).ToString();
		HttpClient client = await WriterAsync();
		using ByteArrayContent content = new(new byte[5 * 1024 * 1024]);

		HttpResponseMessage response = await client.PostAsync("/v1/documents", content);

		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
	}

	[Fact]
	public async Task The_sidecars_413_is_passed_on()
	{
		FakeCorpus corpus = new(factory.Sidecar) { OnWrite = static _ => TooLarge() };

		HttpResponseMessage response = await PostDocumentAsync(await WriterAsync(), sourceId: UniqueId());

		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		Assert.Empty(corpus.Files);
	}

	[Theory]
	[InlineData(17, "17")]
	[InlineData(null, "5")]
	public async Task A_busy_sidecar_becomes_503_with_retry_after(int? sidecarSays, string expected)
	{
		_ = new FakeCorpus(factory.Sidecar) { OnWrite = _ => Busy(sidecarSays) };
		HttpClient client = await WriterAsync();
		string id = UniqueId();

		HttpResponseMessage response = await PostDocumentAsync(client, sourceId: id);

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		Assert.Equal(expected, response.Headers.GetValues("Retry-After").Single());

		_ = new FakeCorpus(factory.Sidecar);
		Assert.Equal(HttpStatusCode.Accepted, (await PostDocumentAsync(client, sourceId: id)).StatusCode);
	}

	// --- delete -----------------------------------------------------------------------

	[Fact]
	public async Task Deleting_forces_a_reindex_at_once()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = UniqueId();
		await PostDocumentAsync(client, sourceId: id);
		factory.Sidecar.Requests.Clear();

		HttpResponseMessage response = await client.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		JsonElement result = await ReadAsync(response);
		Assert.True(result.GetProperty("deleted").GetBoolean());
		Assert.NotNull(result.GetProperty("reindexJobId").GetString());
		Assert.Equal(ReindexState.Started, result.GetProperty("reindexState").GetString());
		Assert.Empty(corpus.Files);
		string[] order = [.. factory.Sidecar.Requests.Select(static r => r.Method + " " + r.Uri.AbsolutePath)];
		Assert.Equal(["DELETE /documents", "POST /reindex"], order);
		using JsonDocument reindex = JsonDocument.Parse(factory.Sidecar.Last().Body!);
		Assert.Equal("c", Assert.Single(reindex.RootElement.GetProperty("collections").EnumerateArray()).GetString());
		Assert.False(reindex.RootElement.GetProperty("embed").GetBoolean());
	}

	[Fact]
	public async Task Deleting_what_is_not_there_is_safe_and_costs_no_reindex()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string collection = "nothing-" + UniqueId();

		HttpResponseMessage response = await (await WriterAsync(collection)).DeleteAsync($"/v1/documents?collection={collection}&source=notes&sourceId={UniqueId()}");

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		JsonElement result = await ReadAsync(response);
		Assert.False(result.GetProperty("deleted").GetBoolean());
		Assert.Equal(ReindexState.NotNeeded, result.GetProperty("reindexState").GetString());
		Assert.DoesNotContain(factory.Sidecar.Requests, static r => r.Uri.AbsolutePath == "/reindex");
		Assert.Empty(corpus.Files);
	}

	[Fact]
	public async Task A_document_that_does_not_exist_in_another_collection_still_gets_403()
	{
		FakeCorpus corpus = new(factory.Sidecar);

		HttpResponseMessage response = await (await WriterAsync("c")).DeleteAsync("/v1/documents?collection=other&path=never-existed.md");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Empty(corpus.Calls);
		Assert.DoesNotContain(factory.Sidecar.Requests, static r => r.Uri.AbsolutePath == "/reindex");
	}

	[Theory]
	[InlineData(".git/config")]
	[InlineData("../other/secret.md")]
	[InlineData("a/../../b.md")]
	[InlineData("/etc/passwd")]
	[InlineData("a//b.md")]
	[InlineData("..\\b.md")]
	public async Task Path_tricks_on_delete_are_refused_here(string path)
	{
		FakeCorpus corpus = new(factory.Sidecar);

		HttpResponseMessage response = await (await WriterAsync()).DeleteAsync($"/v1/documents?collection=c&path={Uri.EscapeDataString(path)}");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty(corpus.Calls);
	}

	[Fact]
	public async Task When_the_reindex_cannot_be_queued_the_answer_says_so_and_a_retry_finishes()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string collection = "owed-" + UniqueId();
		HttpClient client = await WriterAsync(collection);
		string id = UniqueId();
		await PostDocumentAsync(client, collection: collection, sourceId: id);
		factory.Sidecar.On("POST", "/reindex", (_, _) => Busy());

		JsonElement first = await ReadAsync(await client.DeleteAsync($"/v1/documents?collection={collection}&source=notes&sourceId={id}"));

		Assert.True(first.GetProperty("deleted").GetBoolean());
		Assert.Equal(ReindexState.Failed, first.GetProperty("reindexState").GetString());
		Assert.Equal(JsonValueKind.Null, first.GetProperty("reindexJobId").ValueKind);

		_ = new FakeCorpus(factory.Sidecar);
		JsonElement second = await ReadAsync(await client.DeleteAsync($"/v1/documents?collection={collection}&source=notes&sourceId={id}"));
		Assert.False(second.GetProperty("deleted").GetBoolean());
		Assert.NotNull(second.GetProperty("reindexJobId").GetString());
		Assert.Empty(corpus.Files);
	}

	[Fact]
	public async Task An_older_copy_replayed_after_a_delete_cannot_bring_it_back()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = UniqueId();
		await PostDocumentAsync(client, sourceId: id, updated: Stamp);
		await client.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");

		HttpResponseMessage replay = await PostDocumentAsync(client, sourceId: id, updated: Stamp);

		Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
		Assert.Empty(corpus.Files);
		Assert.Equal(HttpStatusCode.Accepted, (await PostDocumentAsync(client, sourceId: id, updated: DateTimeOffset.UtcNow.AddMinutes(30))).StatusCode);
	}

	[Fact]
	public async Task A_delete_by_path_protects_the_document_the_same_way()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = UniqueId();
		string path = (await ReadAsync(await PostDocumentAsync(client, sourceId: id))).GetProperty("path").GetString()!;

		await client.DeleteAsync($"/v1/documents?collection=c&path={path}");

		Assert.Equal(HttpStatusCode.Conflict, (await PostDocumentAsync(client, sourceId: id)).StatusCode);
		Assert.Empty(corpus.Files);
	}

	[Fact]
	public async Task A_delete_while_an_upload_holds_the_document_gets_409()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string id = UniqueId();
		string path = new DocumentEnvelope("notes", id).StoragePath();
		IIngestLedger ledger = factory.Services.GetRequiredService<IIngestLedger>();
		await ledger.ClaimAsync(new IngestClaimRequest(DocumentIngestor.IdentityOf("c", path), "c", Stamp, DateTimeOffset.UtcNow, [path]), default);

		HttpResponseMessage response = await (await WriterAsync()).DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Equal("ingest_in_progress", (await ReadAsync(response)).GetProperty("code").GetString());
		Assert.InRange(int.Parse(response.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture), 200, 300);
		Assert.Empty(corpus.Calls);
	}

	// --- audit ---------------------------------------------------------------------------

	[Fact]
	public async Task Writes_and_deletes_are_audited_with_counts_and_never_content()
	{
		_ = new FakeCorpus(factory.Sidecar);
		string token = Token(6);
		string id = "private-id-" + UniqueId();
		(Memoria.Api.Auth.ApiKeyRecord record, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), key);

		await PostDocumentAsync(client, sourceId: id, body: "secret text " + token, title: "private title");
		await PostDocumentAsync(client, sourceId: id, updated: Stamp.AddDays(-1));
		await client.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");

		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(found => found.Count(r => r.KeyId == record.Id) >= 3);
		AuthApiFactory.AuditRow[] mine = [.. rows.Where(r => r.KeyId == record.Id)];
		Assert.Equal(3, mine.Length);
		string all = JsonSerializer.Serialize(mine);
		foreach (string value in new[] { token, id, "secret text", "private title" })
		{
			Assert.DoesNotContain(value, all, StringComparison.Ordinal);
		}

		AuthApiFactory.AuditRow written = Assert.Single(mine, static r => r.Status == 202 && r.Target!.Contains("\"document\"", StringComparison.Ordinal));
		Assert.Equal(1, written.ResultCount);
		Assert.Contains("c", written.Collections!, StringComparison.Ordinal);
		Assert.Contains("\"Redactions\":1", written.Target!, StringComparison.Ordinal);

		Assert.Contains(mine, static r => r.Status == 409 && r.Collections!.Contains('c'));
		AuthApiFactory.AuditRow deleted = Assert.Single(mine, static r => r.Target?.Contains("\"delete\"", StringComparison.Ordinal) == true);
		Assert.Contains("\"Deleted\":1", deleted.Target!, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_write_outside_the_allowlist_is_audited_as_attempted()
	{
		_ = new FakeCorpus(factory.Sidecar);
		(Memoria.Api.Auth.ApiKeyRecord record, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);

		await PostDocumentAsync(AuthApiFactory.Authorized(factory.CreateClient(), key), collection: "other", sourceId: UniqueId());

		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(found => found.Any(r => r.KeyId == record.Id));
		AuthApiFactory.AuditRow row = Assert.Single(rows, r => r.KeyId == record.Id);
		Assert.Equal(403, row.Status);
		Assert.Contains("other", row.AttemptedCollections!, StringComparison.Ordinal);
	}

	private sealed class FailingRedactor : IRedactor
	{
		public RedactedMarkdown Redact(UnredactedMarkdown markdown)
		{
			throw new RedactionException("some-rule", RedactionFailure.TimedOut);
		}
	}
}

/// <summary>A long body with no declared length, as a hostile client would send. The test server buffers it and may declare its length, so what this proves is the answer. <see cref="RequestBodyTests"/> covers a body whose length nobody declared.</summary>
internal sealed class EndlessContent : HttpContent
{
	private const int ChunkBytes = 64 * 1024;

	public long Sent { get; private set; }

	protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
	{
		byte[] chunk = new byte[ChunkBytes];
		Array.Fill(chunk, (byte)'x');
		while (Sent < 32L * 1024 * 1024)
		{
			await stream.WriteAsync(chunk);
			Sent += ChunkBytes;
		}
	}

	protected override bool TryComputeLength(out long length)
	{
		length = 0;
		return false;
	}
}
