using System.Net;
using System.Text;
using System.Text.Json;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static Memoria.Api.Tests.IngestKit;

namespace Memoria.Api.Tests;

/// <summary>Raw transcripts, and above all what an upload that stops halfway leaves behind.</summary>
public sealed class SessionIngestTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private Task<HttpClient> WriterAsync()
	{
		return factory.ClientWithScopesAsync([MemoriaScopes.Ingest], ["c"]);
	}

	private static string NewSession()
	{
		return Guid.NewGuid().ToString();
	}

	private static async Task<JsonElement> StatusAsync(HttpClient client, string sessionId)
	{
		return await ReadAsync(await client.GetAsync($"/v1/ingest/session?collection=c&format=claude&sessionId={Uri.EscapeDataString(sessionId)}"));
	}

	[Fact]
	public async Task One_session_becomes_many_parts_and_is_complete_only_when_all_arrive()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();

		HttpResponseMessage response = await PostSessionAsync(client, Transcript(5), sessionId: id);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		JsonElement result = await ReadAsync(response);
		Assert.True(result.GetProperty("complete").GetBoolean());
		Assert.Equal(5, result.GetProperty("parts").GetArrayLength());
		Assert.All(States(result), static state => Assert.Equal(SessionPartState.Written, state));
		Assert.Equal(5, corpus.Files.Count);
		Assert.NotNull(result.GetProperty("jobId").GetString());

		JsonElement status = await StatusAsync(client, id);
		Assert.Equal(IngestStateName.Complete, status.GetProperty("state").GetString());
		Assert.Equal(5, status.GetProperty("paths").GetArrayLength());
	}

	[Fact]
	public async Task A_credential_in_a_transcript_never_reaches_the_sidecar()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string token = Token(10);
		string jsonl = ClaudeLine("user", "my key is " + token) + ClaudeLine("assistant", "noted");

		HttpResponseMessage response = await PostSessionAsync(await WriterAsync(), jsonl, sessionId: NewSession());

		string raw = await response.Content.ReadAsStringAsync();
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
		Assert.All(factory.Sidecar.Requests, request => Assert.DoesNotContain(token, request.Body ?? "", StringComparison.Ordinal));
		Assert.Contains("[REDACTED:", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
		Assert.Equal(1, JsonDocument.Parse(raw).RootElement.GetProperty("redactions").GetProperty("gitleaks-github-pat").GetInt32());
	}

	// --- part 3 of 5 ---------------------------------------------------------------

	[Fact]
	public async Task When_part_3_of_5_finds_the_backend_down_nothing_looks_complete_and_a_retry_finishes()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		string jsonl = Transcript(5);
		corpus.OnWrite = static path => path.Contains("_part-003", StringComparison.Ordinal) ? Busy(9) : null;

		HttpResponseMessage first = await PostSessionAsync(client, jsonl, sessionId: id);

		Assert.Equal(HttpStatusCode.MultiStatus, first.StatusCode);
		JsonElement result = await ReadAsync(first);
		Assert.False(result.GetProperty("complete").GetBoolean());
		Assert.True(result.GetProperty("retryable").GetBoolean());
		Assert.Equal(["written", "written", "failed", "not_attempted", "not_attempted"], States(result));
		JsonElement third = result.GetProperty("parts")[2];
		Assert.True(third.GetProperty("retryable").GetBoolean());
		Assert.Equal(9, third.GetProperty("retryAfterSeconds").GetInt32());
		Assert.Equal("busy", third.GetProperty("code").GetString());
		Assert.Equal(2, corpus.Files.Count);
		Assert.Equal(IngestStateName.Partial, (await StatusAsync(client, id)).GetProperty("state").GetString());

		corpus.OnWrite = static _ => null;
		HttpResponseMessage retry = await PostSessionAsync(client, jsonl, sessionId: id);

		Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
		Assert.Equal(5, corpus.Files.Count);
		Assert.Equal(IngestStateName.Complete, (await StatusAsync(client, id)).GetProperty("state").GetString());
	}

	[Fact]
	public async Task A_part_the_sidecar_refuses_for_size_fails_alone_and_is_not_retryable()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		corpus.OnWrite = static path => path.Contains("_part-003", StringComparison.Ordinal) ? TooLarge() : null;

		HttpResponseMessage response = await PostSessionAsync(client, Transcript(5), sessionId: id);

		Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
		JsonElement result = await ReadAsync(response);
		Assert.Equal(["written", "written", "failed", "written", "written"], States(result));
		Assert.Equal("document_too_large", result.GetProperty("parts")[2].GetProperty("code").GetString());
		Assert.False(result.GetProperty("parts")[2].GetProperty("retryable").GetBoolean());
		Assert.False(result.GetProperty("retryable").GetBoolean());
		Assert.Equal(IngestStateName.Partial, (await StatusAsync(client, id)).GetProperty("state").GetString());
		Assert.DoesNotContain(corpus.Calls, static call => call.StartsWith("delete", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_failed_upload_deletes_nothing_even_when_the_session_shrank()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		await PostSessionAsync(client, Transcript(5), sessionId: id);
		corpus.OnWrite = static path => path.Contains("_part-002", StringComparison.Ordinal) ? Busy() : null;

		await PostSessionAsync(client, Transcript(2), sessionId: id, updated: Stamp.AddHours(1), allowShrink: true);

		Assert.Equal(5, corpus.Files.Count);
		Assert.DoesNotContain(corpus.Calls, static call => call.StartsWith("delete", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_sidecar_that_rejects_memorias_secret_answers_the_whole_request_and_stays_partial()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		corpus.OnWrite = static _ => FakeSidecar.Json(HttpStatusCode.Unauthorized, """{"error":"unauthorized","message":"no"}""");

		HttpResponseMessage response = await PostSessionAsync(client, Transcript(3), sessionId: id);

		Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
		Assert.Single(corpus.Calls, static call => call.StartsWith("write", StringComparison.Ordinal));
		Assert.Equal(IngestStateName.Partial, (await StatusAsync(client, id)).GetProperty("state").GetString());
	}

	[Fact]
	public async Task An_unknown_collection_is_the_requests_answer_not_a_parts()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		corpus.OnWrite = static _ => FakeSidecar.Json(HttpStatusCode.NotFound, """{"error":"unknown_collection","message":"Unknown collection: c"}""");

		HttpResponseMessage response = await PostSessionAsync(await WriterAsync(), Transcript(3), sessionId: NewSession());

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	// --- shrinking -----------------------------------------------------------------

	[Fact]
	public async Task A_session_that_shrinks_loses_its_old_parts_last_and_they_leave_search_at_once()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		await PostSessionAsync(client, Transcript(5), sessionId: id);
		corpus.Calls.Clear();
		factory.Sidecar.Requests.Clear();

		HttpResponseMessage response = await PostSessionAsync(client, Transcript(2), sessionId: id, updated: Stamp.AddHours(1), allowShrink: true);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		JsonElement result = await ReadAsync(response);
		Assert.Equal(3, result.GetProperty("superseded").GetProperty("deleted").GetArrayLength());
		Assert.NotNull(result.GetProperty("reindexJobId").GetString());
		Assert.Equal(2, corpus.Files.Count);
		string[] calls = [.. corpus.Calls.Where(static call => call.StartsWith("write", StringComparison.Ordinal) || call.StartsWith("delete", StringComparison.Ordinal))];
		Assert.All(calls[..2], static call => Assert.StartsWith("write", call, StringComparison.Ordinal));
		Assert.All(calls[3..], static call => Assert.StartsWith("delete", call, StringComparison.Ordinal));
		Assert.Equal("POST /reindex", factory.Sidecar.Requests.Select(static r => r.Method + " " + r.Uri.AbsolutePath).Last());
		Assert.Equal(2, (await StatusAsync(client, id)).GetProperty("paths").GetArrayLength());
	}

	[Fact]
	public async Task A_shrink_never_touches_another_sessions_parts()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string mine = NewSession();
		string other = NewSession();
		await PostSessionAsync(client, Transcript(3), sessionId: other);
		await PostSessionAsync(client, Transcript(3), sessionId: mine);

		await PostSessionAsync(client, Transcript(1), sessionId: mine, updated: Stamp.AddHours(1), allowShrink: true);

		Assert.Equal(3, corpus.Files.Keys.Count(path => path.Contains(other[..8], StringComparison.Ordinal)));
		Assert.Equal(1, corpus.Files.Keys.Count(path => path.Contains(mine[..8], StringComparison.Ordinal)));
	}

	[Fact]
	public async Task An_upload_that_lost_content_keeps_the_old_parts_and_does_not_claim_completion()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		await PostSessionAsync(client, Transcript(4), sessionId: id);

		string cut = Transcript(1);
		HttpResponseMessage response = await PostSessionAsync(client, cut + cut.TrimEnd('\n'), sessionId: id, updated: Stamp.AddHours(1), allowShrink: true);

		Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
		JsonElement result = await ReadAsync(response);
		Assert.True(result.GetProperty("read").GetProperty("skippedContent").GetBoolean());
		Assert.Equal(3, result.GetProperty("superseded").GetProperty("kept").GetArrayLength());
		Assert.False(result.GetProperty("retryable").GetBoolean());
		Assert.Equal(4, corpus.Files.Count);
		Assert.Equal(IngestStateName.Partial, (await StatusAsync(client, id)).GetProperty("state").GetString());
		Assert.DoesNotContain(corpus.Calls, static call => call.StartsWith("delete", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_transcript_past_the_retention_budget_never_reads_as_complete()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		StringBuilder jsonl = new();
		string message = ClaudeLine("user", new string('m', 1024 * 1024));
		for (int i = 0; i < 40; i++)
		{
			jsonl.Append(message);
		}

		HttpResponseMessage response = await PostSessionAsync(client, jsonl.ToString(), sessionId: id);

		Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
		JsonElement result = await ReadAsync(response);
		Assert.False(result.GetProperty("complete").GetBoolean());
		Assert.False(result.GetProperty("retryable").GetBoolean());
		Assert.True(result.GetProperty("read").GetProperty("messagesOverLimit").GetInt64() > 0);
		Assert.Equal(IngestStateName.Partial, (await StatusAsync(client, id)).GetProperty("state").GetString());
	}

	[Fact]
	public async Task An_empty_upload_never_wipes_a_session()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		await PostSessionAsync(client, Transcript(3), sessionId: id);

		HttpResponseMessage response = await PostSessionAsync(client, "", sessionId: id, updated: Stamp.AddHours(1), allowShrink: true);

		Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
		Assert.False((await ReadAsync(response)).GetProperty("complete").GetBoolean());
		Assert.Equal(3, corpus.Files.Count);
		Assert.Equal(IngestStateName.Complete, (await StatusAsync(client, id)).GetProperty("state").GetString());
	}

	[Fact]
	public async Task A_part_that_could_not_be_deleted_leaves_the_session_partial_until_a_retry_clears_it()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		await PostSessionAsync(client, Transcript(3), sessionId: id);
		corpus.OnDelete = static _ => Busy();

		HttpResponseMessage first = await PostSessionAsync(client, Transcript(1), sessionId: id, updated: Stamp.AddHours(1), allowShrink: true);

		Assert.Equal(HttpStatusCode.MultiStatus, first.StatusCode);
		JsonElement result = await ReadAsync(first);
		Assert.Equal(2, result.GetProperty("superseded").GetProperty("failed").GetArrayLength());
		Assert.True(result.GetProperty("retryable").GetBoolean());
		Assert.Equal(IngestStateName.Partial, (await StatusAsync(client, id)).GetProperty("state").GetString());

		corpus.OnDelete = static _ => null;
		Assert.Equal(HttpStatusCode.Accepted, (await PostSessionAsync(client, Transcript(1), sessionId: id, updated: Stamp.AddHours(1), allowShrink: true)).StatusCode);
		Assert.Single(corpus.Files);
	}

	[Fact]
	public async Task When_the_index_cannot_update_the_session_is_not_called_complete()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		await PostSessionAsync(client, Transcript(3), sessionId: id);
		factory.Sidecar.On("POST", "/reindex", (_, _) => Busy());

		HttpResponseMessage response = await PostSessionAsync(client, Transcript(1), sessionId: id, updated: Stamp.AddHours(1), allowShrink: true);

		Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
		Assert.Equal(JsonValueKind.Null, (await ReadAsync(response)).GetProperty("reindexJobId").ValueKind);
	}

	// --- ordering and exclusion -------------------------------------------------------

	[Fact]
	public async Task An_older_upload_cannot_replace_a_newer_one()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		await PostSessionAsync(client, Transcript(3), sessionId: id, updated: Stamp);
		corpus.Calls.Clear();

		HttpResponseMessage replay = await PostSessionAsync(client, Transcript(1, 'o'), sessionId: id, updated: Stamp.AddHours(-1));

		Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
		Assert.Equal("stale_version", (await ReadAsync(replay)).GetProperty("code").GetString());
		Assert.DoesNotContain(corpus.Calls, static call => call.StartsWith("write", StringComparison.Ordinal) || call.StartsWith("delete", StringComparison.Ordinal));
		Assert.Equal(IngestStateName.Complete, (await StatusAsync(client, id)).GetProperty("state").GetString());
	}

	[Fact]
	public async Task Two_uploads_of_one_session_cannot_interleave()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		using ManualResetEventSlim entered = new();
		using ManualResetEventSlim release = new();
		corpus.OnWrite = _ =>
		{
			entered.Set();
			release.Wait(TimeSpan.FromSeconds(30));
			return null;
		};

		Task<HttpResponseMessage> running = PostSessionAsync(client, Transcript(2), sessionId: id);
		Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
		HttpResponseMessage second = await PostSessionAsync(client, Transcript(2, 'z'), sessionId: id);
		JsonElement during = await StatusAsync(client, id);
		release.Set();
		HttpResponseMessage first = await running;

		Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
		Assert.Equal("ingest_in_progress", (await ReadAsync(second)).GetProperty("code").GetString());
		Assert.True(second.Headers.Contains("Retry-After"));
		Assert.Equal(IngestStateName.Writing, during.GetProperty("state").GetString());
		Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
		Assert.DoesNotContain(corpus.Files.Values, static content => content.Contains("zzzz", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_crashed_upload_releases_its_lease_and_its_paths_survive()
	{
		IIngestLedger ledger = factory.Services.GetRequiredService<IIngestLedger>();
		string identity = IngestIdentity.For("crash", Guid.NewGuid().ToString());
		DateTimeOffset now = DateTimeOffset.UtcNow;
		IngestClaimRequest At(DateTimeOffset when) => new(identity, "crash", Stamp, when, ["a.md"]);
		IngestClaim first = await ledger.ClaimAsync(At(now), default);
		await ledger.FinishAsync(identity, first.LeaseToken!, "crash", IngestStateName.Partial, ["a.md"], null);
		IngestClaim second = await ledger.ClaimAsync(At(now), default);

		Assert.Equal(IngestClaimOutcome.Busy, (await ledger.ClaimAsync(At(now.AddMinutes(1)), default)).Outcome);
		Assert.True(await ledger.BeginWriteAsync(identity, second.LeaseToken!, "crash", "a.md", now.AddMinutes(4), default));
		Assert.Equal(IngestClaimOutcome.Busy, (await ledger.ClaimAsync(At(now.AddMinutes(6)), default)).Outcome);

		IngestClaim after = await ledger.ClaimAsync(At(now.AddMinutes(10)), default);

		Assert.Equal(IngestClaimOutcome.Acquired, after.Outcome);
		Assert.Equal(["a.md"], after.PreviousPaths);
	}

	[Fact]
	public async Task Concurrent_claims_on_the_ledger_admit_exactly_one()
	{
		IIngestLedger ledger = factory.Services.GetRequiredService<IIngestLedger>();
		string identity = IngestIdentity.For("race", Guid.NewGuid().ToString());
		DateTimeOffset now = DateTimeOffset.UtcNow;

		IngestClaim[] claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => ledger.ClaimAsync(new IngestClaimRequest(identity, "race", Stamp, now, []), default))));

		Assert.Equal(1, claims.Count(static claim => claim.Outcome == IngestClaimOutcome.Acquired));
		Assert.Equal(15, claims.Count(static claim => claim.Outcome == IngestClaimOutcome.Busy));
	}

	// --- hostile input ------------------------------------------------------------------

	[Theory]
	[InlineData("../../../etc/passwd")]
	[InlineData("..\\..\\evil")]
	[InlineData("/abs/path")]
	[InlineData(".git")]
	[InlineData("a/b")]
	[InlineData("*")]
	[InlineData("x\ny")]
	public async Task A_session_id_cannot_steer_any_path(string sessionId)
	{
		FakeCorpus corpus = new(factory.Sidecar);

		HttpResponseMessage response = await PostSessionAsync(await WriterAsync(), Transcript(2), sessionId: sessionId, updated: DateTimeOffset.UtcNow);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Equal(2, corpus.Files.Count);
		Assert.All(corpus.Files.Keys, static path => Assert.Matches("^2026-01-15_[A-Za-z0-9._-]+\\.md$", path));
	}

	[Theory]
	[InlineData("codex", "../../evil")]
	[InlineData("codex", "a/b")]
	public async Task A_codex_session_id_cannot_steer_any_path(string format, string sessionId)
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string jsonl = """{"timestamp":"2026-01-15T10:00:00.000Z","type":"session_meta","payload":{"cwd":"/srv/demo"}}""" + "\n"
			+ CodexMessage("user", "hello", "t1");

		await PostSessionAsync(await WriterAsync(), jsonl, format: format, sessionId: sessionId, updated: DateTimeOffset.UtcNow);

		Assert.All(corpus.Files.Keys, static path => Assert.Matches("^[0-9-]+_codex_[A-Za-z0-9._-]+_chunk-[0-9]+\\.md$", path));
	}

	[Fact]
	public async Task A_session_built_to_force_refusals_is_stored_and_answers_with_a_bounded_list()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string random = RandomBody(32);
		StringBuilder jsonl = new("""{"timestamp":"2026-01-15T10:00:00.000Z","type":"session_meta","payload":{"cwd":"/srv/demo"}}""" + "\n");
		string filler = string.Join(' ', Enumerable.Repeat("word", 6000));
		for (int part = 0; part < 210; part++)
		{
			jsonl.Append(CodexMessage("user", filler, "--token")).Append(CodexMessage("assistant", filler, random));
		}

		HttpResponseMessage response = await PostSessionAsync(await WriterAsync(), jsonl.ToString(), format: "codex", sessionId: NewSession(), updated: DateTimeOffset.UtcNow);

		string raw = await response.Content.ReadAsStringAsync();
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.DoesNotContain(random, raw, StringComparison.Ordinal);
		Assert.All(corpus.Files.Values, content => Assert.DoesNotContain(random, content, StringComparison.Ordinal));
		JsonElement result = JsonDocument.Parse(raw).RootElement;
		Assert.True(result.GetProperty("refusalCount").GetInt32() > 200, "the attack should have produced more refusals than the answer lists");
		Assert.Equal(200, result.GetProperty("refusals").GetArrayLength());
		Assert.Equal(JsonValueKind.Number, result.GetProperty("refusals")[0].GetProperty("part").ValueKind);
	}

	[Fact]
	public async Task A_giant_message_is_split_so_every_part_fits_and_all_its_text_arrives()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string line = new string('x', 79) + "\n";
		string giant = string.Concat(Enumerable.Repeat(line, 70_000));

		HttpResponseMessage response = await PostSessionAsync(await WriterAsync(), ClaudeLine("user", giant), sessionId: NewSession());

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.True(corpus.Files.Count >= 3);
		Assert.All(corpus.Files.Values, static content => Assert.InRange(Encoding.UTF8.GetByteCount(content), 1, 2 * 1024 * 1024));
		Assert.Equal(70_000, corpus.Files.Values.Sum(static content => content.Split('\n').Count(static l => l.Length == 79 && l[0] == 'x')));
	}

	[Fact]
	public async Task A_key_for_one_collection_cannot_write_a_session_to_another_or_read_its_status()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();

		HttpResponseMessage write = await PostSessionAsync(client, Transcript(1), collection: "other", sessionId: NewSession());
		HttpResponseMessage read = await client.GetAsync("/v1/ingest/session?collection=other&format=claude&sessionId=x");

		Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
		Assert.Empty(corpus.Calls);
	}

	[Theory]
	[InlineData("format=claude&sessionId=x&updated=2026-01-01T00:00:00Z", "collection")]
	[InlineData("collection=c&sessionId=x&updated=2026-01-01T00:00:00Z", "format")]
	[InlineData("collection=c&format=word&sessionId=x&updated=2026-01-01T00:00:00Z", "format")]
	[InlineData("collection=c&format=claude&updated=2026-01-01T00:00:00Z", "sessionId")]
	[InlineData("collection=c&format=claude&sessionId=x", "updated")]
	[InlineData("collection=c&format=claude&sessionId=x&updated=2999-01-01T00:00:00Z", "updated")]
	public async Task A_bad_query_is_a_validation_error(string query, string field)
	{
		_ = new FakeCorpus(factory.Sidecar);

		HttpResponseMessage response = await (await WriterAsync()).PostAsync($"/v1/ingest/session?{query}", new StringContent("", Encoding.UTF8, "application/x-ndjson"));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
	}

	[Fact]
	public async Task An_unknown_session_has_no_state()
	{
		JsonElement status = await StatusAsync(await WriterAsync(), NewSession());

		Assert.Equal(IngestStateName.None, status.GetProperty("state").GetString());
	}

	// --- audit --------------------------------------------------------------------------

	[Fact]
	public async Task A_session_is_audited_with_counts_and_no_content_or_session_id()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		corpus.OnWrite = static path => path.Contains("_part-002", StringComparison.Ordinal) ? Busy() : null;
		string token = Token(11);
		string id = "private-session-" + Guid.NewGuid().ToString("N");
		(Memoria.Api.Auth.ApiKeyRecord record, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);

		await PostSessionAsync(AuthApiFactory.Authorized(factory.CreateClient(), key), ClaudeLine("user", "key " + token) + Transcript(3), sessionId: id);

		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(found => found.Any(r => r.KeyId == record.Id));
		AuthApiFactory.AuditRow row = Assert.Single(rows, r => r.KeyId == record.Id);
		string all = JsonSerializer.Serialize(row);
		Assert.DoesNotContain(token, all, StringComparison.Ordinal);
		Assert.DoesNotContain(id, all, StringComparison.Ordinal);
		Assert.Equal(207, row.Status);
		Assert.Equal(1, row.ResultCount);
		Assert.Contains("\"Kind\":\"session\"", row.Target!, StringComparison.Ordinal);
		Assert.Contains("\"Complete\":false", row.Target!, StringComparison.Ordinal);
		Assert.Contains("\"Redactions\":1", row.Target!, StringComparison.Ordinal);
	}

	private static string RandomBody(int length)
	{
		return string.Concat(Enumerable.Range(0, length).Select(static i => "aZ3kQ9xT7mB2vN8pL5wR1yH6cF4dG0jS"[(i * 11) % 32]));
	}

	private static string CodexMessage(string role, string text, string turnId)
	{
		string payload = JsonSerializer.Serialize(new { type = "message", role, content = new object[] { new { type = "input_text", text } }, internal_chat_message_metadata_passthrough = new { turn_id = turnId } });
		return """{"timestamp":"2026-01-15T10:00:01.000Z","type":"response_item","payload":""" + payload + "}\n";
	}
}

/// <summary>Limits set low, so what happens past them is cheap to test.</summary>
public sealed class IngestLimitTests : IClassFixture<AuthApiFactory>
{
	private readonly AuthApiFactory _factory;

	private readonly WebApplicationFactory<Program> _limited;

	public IngestLimitTests(AuthApiFactory factory)
	{
		_factory = factory;
		_limited = factory.WithWebHostBuilder(static builder =>
		{
			builder.UseSetting("Memoria:Ingest:MaxSessionBytes", "4096");
		});
	}

	private async Task<HttpClient> WriterAsync()
	{
		(_, string key) = await _factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		return AuthApiFactory.Authorized(_limited.CreateClient(), key);
	}

	[Fact]
	public async Task A_transcript_with_a_declared_length_over_the_limit_gets_413()
	{
		FakeCorpus corpus = new(_factory.Sidecar);

		HttpResponseMessage response = await PostSessionAsync(await WriterAsync(), new string('x', 5000));

		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		Assert.Equal("payload_too_large", (await ReadAsync(response)).GetProperty("code").GetString());
		Assert.Empty(corpus.Calls);
	}

	[Fact]
	public async Task A_stream_with_no_declared_length_is_stopped_at_the_limit_and_nothing_is_written()
	{
		FakeCorpus corpus = new(_factory.Sidecar);
		HttpClient client = await WriterAsync();
		string identity = Guid.NewGuid().ToString();
		using EndlessContent endless = new();

		HttpResponseMessage response = await client.PostAsync($"/v1/ingest/session?collection=c&format=claude&sessionId={identity}&updated=2026-01-15T12:00:00Z", endless);

		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		Assert.Empty(corpus.Calls);
		JsonElement status = await ReadAsync(await client.GetAsync($"/v1/ingest/session?collection=c&format=claude&sessionId={identity}"));
		Assert.Equal(IngestStateName.None, status.GetProperty("state").GetString());
	}

}

public sealed class IngestWithoutKeysTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	[Fact]
	public async Task With_auth_disabled_nothing_can_be_written()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = factory.CreateClient();

		HttpResponseMessage document = await PostDocumentAsync(client);
		HttpResponseMessage session = await PostSessionAsync(client, Transcript(1));
		HttpResponseMessage delete = await client.DeleteAsync("/v1/documents?collection=c&path=a.md");
		HttpResponseMessage status = await client.GetAsync("/v1/ingest/session?collection=c&format=claude&sessionId=x");

		foreach (HttpResponseMessage response in new[] { document, session, delete, status })
		{
			Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
			Assert.Equal("auth_disabled", (await ReadAsync(response)).GetProperty("code").GetString());
		}

		Assert.Empty(corpus.Calls);
	}
}

public sealed class IngestLedgerMigrationTests
{
	[Fact]
	public async Task A_version_2_database_gains_the_content_hash_and_keeps_its_rows()
	{
		string path = Path.Combine(Path.GetTempPath(), "memoria-tests", Guid.NewGuid().ToString("N"), "control.db");
		Memoria.Api.Auth.ControlDatabase database = new(path);
		await database.InitializeAsync();
		SqliteIngestLedger ledger = new(database);
		IngestClaimRequest kept = new("kept", "c", Stamp, Stamp, ["a.md"]);
		IngestClaim claim = await ledger.ClaimAsync(kept, default);
		await ledger.FinishAsync("kept", claim.LeaseToken!, "c", IngestStateName.Complete, ["a.md"], kept);
		await using (Microsoft.Data.Sqlite.SqliteConnection connection = await database.OpenAsync())
		{
			await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				ALTER TABLE ingest_state ADD COLUMN deleted_version_ticks INTEGER NULL;
				ALTER TABLE ingest_state DROP COLUMN content_hash;
				ALTER TABLE ingest_state DROP COLUMN messages_digest;
				ALTER TABLE ingest_state DROP COLUMN deleted_paths;
				DROP TABLE ingest_collections;
				PRAGMA user_version = 2;
				""";
			await command.ExecuteNonQueryAsync();
		}

		await new Memoria.Api.Auth.ControlDatabase(path).InitializeAsync();

		Assert.Equal(IngestStateName.Complete, (await ledger.FindAsync("kept", default))!.State);
		Assert.Equal(IngestClaimOutcome.Acquired, (await ledger.ClaimAsync(new IngestClaimRequest("new", "c", Stamp, Stamp, [], ContentHash: "h"), default)).Outcome);
	}

	[Fact]
	public async Task A_version_1_database_gains_the_ledger_and_keeps_its_data()
	{
		string path = Path.Combine(Path.GetTempPath(), "memoria-tests", Guid.NewGuid().ToString("N"), "control.db");
		Memoria.Api.Auth.ControlDatabase database = new(path);
		await database.InitializeAsync();
		await using (Microsoft.Data.Sqlite.SqliteConnection connection = await database.OpenAsync())
		{
			await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				DROP TABLE ingest_state;
				DROP TABLE ingest_paths;
				DROP TABLE ingest_stems;
				DROP TABLE ingest_collections;
				INSERT INTO api_keys (id, sha256_hash, label, scopes, collections, all_collections, created_at) VALUES ('k', x'00', 'l', '[]', '[]', 0, 't');
				PRAGMA user_version = 1;
				""";
			await command.ExecuteNonQueryAsync();
		}

		await new Memoria.Api.Auth.ControlDatabase(path).InitializeAsync();

		SqliteIngestLedger ledger = new(database);
		Assert.Equal(IngestClaimOutcome.Acquired, (await ledger.ClaimAsync(new IngestClaimRequest("x", "c", Stamp, Stamp, []), default)).Outcome);
		await using Microsoft.Data.Sqlite.SqliteConnection check = await database.OpenAsync();
		await using Microsoft.Data.Sqlite.SqliteCommand count = check.CreateCommand();
		count.CommandText = "SELECT COUNT(*) FROM api_keys;";
		Assert.Equal(1L, await count.ExecuteScalarAsync());
	}
}

/// <summary>What stops a body whose length nobody declared, which the test server cannot produce through a client.</summary>
public sealed class RequestBodyTests
{
	private static DefaultHttpContext Context(int bytes, long? declared = null)
	{
		DefaultHttpContext context = new();
		context.Request.Body = new MemoryStream(new byte[bytes]);
		context.Request.ContentLength = declared;
		return context;
	}

	[Fact]
	public void A_body_with_no_declared_length_fails_as_it_passes_the_limit()
	{
		using Stream body = RequestBody.Open(Context(5000), 4096);

		IngestRefusedException refused = Assert.Throws<IngestRefusedException>(() => body.ReadExactly(new byte[5000]));

		Assert.Equal(StatusCodes.Status413PayloadTooLarge, refused.Status);
		Assert.Equal("payload_too_large", refused.Code);
	}

	[Fact]
	public void A_body_at_the_limit_reads_whole()
	{
		using Stream body = RequestBody.Open(Context(4096), 4096);

		body.ReadExactly(new byte[4096]);
	}

	[Fact]
	public void A_declared_length_over_the_limit_is_refused_before_any_read()
	{
		Assert.Throws<IngestRefusedException>(() => RequestBody.Open(Context(0, declared: 4097), 4096));
	}

	[Fact]
	public void The_servers_own_limit_leaves_room_to_read_the_rest_after_refusing()
	{
		DefaultHttpContext context = Context(0);
		LimitFeature feature = new();
		context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

		RequestBody.AllowUpTo(context, 4096);
		Assert.Equal(8192, feature.MaxRequestBodySize);

		RequestBody.AllowUpTo(context, long.MaxValue / 2 + 1);
		Assert.Equal(long.MaxValue, feature.MaxRequestBodySize);
	}

	private sealed class LimitFeature : IHttpMaxRequestBodySizeFeature
	{
		public bool IsReadOnly => false;

		public long? MaxRequestBodySize { get; set; }
	}
}
