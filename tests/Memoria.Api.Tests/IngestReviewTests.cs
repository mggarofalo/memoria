using System.Net;
using System.Text;
using System.Text.Json;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Core.Contracts;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Memoria.Api.Tests.IngestKit;

namespace Memoria.Api.Tests;

/// <summary>Attacks that once made the write path destroy or overwrite text while reporting success.</summary>
public sealed class IngestOwnershipTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private Task<HttpClient> WriterAsync()
	{
		return factory.ClientWithScopesAsync([MemoriaScopes.Ingest], ["c"]);
	}

	private static string NewSession()
	{
		return Guid.NewGuid().ToString();
	}

	private static async Task<string> StateAsync(HttpClient client, string sessionId, string format = "claude")
	{
		return (await ReadAsync(await client.GetAsync($"/v1/ingest/session?collection=c&format={format}&sessionId={sessionId}"))).GetProperty("state").GetString()!;
	}

	private static string CodexUpload(string? metaId, int messages = 1)
	{
		string id = metaId is null ? "" : "\"id\":\"" + metaId + "\",";
		StringBuilder lines = new("{\"timestamp\":\"2026-01-15T10:00:00.000Z\",\"type\":\"session_meta\",\"payload\":{" + id + "\"cwd\":\"/srv/demo\"}}\n");
		string filler = string.Join(' ', Enumerable.Repeat("word", 16000));
		for (int i = 0; i < messages; i++)
		{
			string payload = JsonSerializer.Serialize(new { type = "message", role = "user", content = new object[] { new { type = "input_text", text = filler } } });
			lines.Append("""{"timestamp":"2026-01-15T10:00:01.000Z","type":"response_item","payload":""").Append(payload).Append("}\n");
		}

		return lines.ToString();
	}

	// --- H1 ------------------------------------------------------------------------

	[Fact]
	public async Task A_codex_upload_naming_another_session_in_its_meta_is_refused_and_touches_nothing()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string victim = NewSession();
		await PostSessionAsync(client, CodexUpload(victim, 3), format: "codex", sessionId: victim);
		Dictionary<string, string> before = new(corpus.Files);
		corpus.Calls.Clear();

		HttpResponseMessage attack = await PostSessionAsync(client, CodexUpload(victim, 1), format: "codex", sessionId: "attacker-session", updated: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

		Assert.Equal(HttpStatusCode.BadRequest, attack.StatusCode);
		Assert.Equal("session_id_mismatch", (await ReadAsync(attack)).GetProperty("code").GetString());
		Assert.Empty(corpus.Calls);
		Assert.Equal(before, new Dictionary<string, string>(corpus.Files));
		Assert.Equal(IngestStateName.Complete, await StateAsync(client, victim, "codex"));
	}

	[Fact]
	public async Task A_codex_session_id_holding_the_metas_uuid_is_the_same_session()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string id = NewSession();

		HttpResponseMessage response = await PostSessionAsync(await WriterAsync(), CodexUpload(id), format: "codex", sessionId: $"rollout-2026-01-15-{id}");

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Contains(id, Assert.Single(corpus.Files).Key, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_session_sharing_the_first_eight_characters_gets_its_own_paths_and_deletes_nothing_of_the_other()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string prefix = Guid.NewGuid().ToString("N")[..8];
		string first = $"{prefix}-aaaa-1111-2222-333333333333";
		string second = $"{prefix}-bbbb-1111-2222-333333333333";
		await PostSessionAsync(client, Transcript(4), sessionId: first);
		string[] firstPaths = [.. corpus.Files.Keys];

		HttpResponseMessage response = await PostSessionAsync(client, Transcript(1, day: "2026-02-20"), sessionId: second);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		JsonElement result = await ReadAsync(response);
		Assert.Empty(result.GetProperty("superseded").GetProperty("deleted").EnumerateArray());
		Assert.All(firstPaths, path => Assert.True(corpus.Files.ContainsKey(path)));
		string secondPath = Assert.Single(corpus.Files.Keys, path => !firstPaths.Contains(path));
		Assert.Matches($"^2026-02-20_{prefix}-[0-9a-f]{{8}}\\.md$", secondPath);
		Assert.Equal(IngestStateName.Complete, await StateAsync(client, first));

		// The second session is stable on the path it was given, and shrinking it still does not reach the first.
		await PostSessionAsync(client, Transcript(2, day: "2026-02-20"), sessionId: second, updated: Stamp.AddHours(1));
		await PostSessionAsync(client, Transcript(1, day: "2026-02-20"), sessionId: second, updated: Stamp.AddHours(2), allowShrink: true);
		Assert.All(firstPaths, path => Assert.True(corpus.Files.ContainsKey(path)));
		Assert.Equal(5, corpus.Files.Count);
	}

	[Fact]
	public async Task An_older_version_cannot_slip_past_by_naming_its_project_folder()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		await PostSessionAsync(client, ClaudeLine("user", "newer text"), sessionId: id, updated: Stamp);

		HttpResponseMessage replay = await PostSessionAsync(client, ClaudeLine("user", "older text"), sessionId: id, updated: Stamp.AddHours(-1), projectDirectory: "-srv-demo");

		Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
		Assert.Equal("stale_version", (await ReadAsync(replay)).GetProperty("code").GetString());
		Assert.Contains("newer text", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Two_relocated_copies_of_one_session_order_independently()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		await PostSessionAsync(client, ClaudeLine("user", "copy one"), sessionId: id, updated: Stamp, projectDirectory: "-elsewhere-one");

		HttpResponseMessage second = await PostSessionAsync(client, ClaudeLine("user", "copy two"), sessionId: id, updated: Stamp.AddHours(-1), projectDirectory: "-elsewhere-two");

		Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
		Assert.Equal(2, corpus.Files.Count);
	}

	[Fact]
	public async Task A_path_owned_by_something_else_is_refused_before_anything_is_written()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		string path = $"2026-01-15_{id[..8]}.md";
		IIngestLedger ledger = factory.Services.GetRequiredService<IIngestLedger>();
		string someoneElse = "someone-else-" + id;
		DateTimeOffset earlier = DateTimeOffset.UtcNow.AddHours(-1);
		IngestClaim theirs = await ledger.ClaimAsync(new IngestClaimRequest(someoneElse, "c", Stamp, earlier, [path]), default);
		await ledger.BeginWriteAsync(someoneElse, theirs.LeaseToken!, "c", path, earlier, default);

		HttpResponseMessage response = await PostSessionAsync(client, ClaudeLine("user", "hello"), sessionId: id);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Equal("path_owned", (await ReadAsync(response)).GetProperty("code").GetString());
		Assert.DoesNotContain(corpus.Calls, static call => call.StartsWith("write", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_retry_after_a_partial_upload_overwrites_only_its_own_files()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		corpus.OnWrite = static path => path.Contains("_part-002", StringComparison.Ordinal) ? Busy() : null;
		await PostSessionAsync(client, Transcript(3), sessionId: id);
		corpus.OnWrite = static _ => null;

		Assert.Equal(HttpStatusCode.Accepted, (await PostSessionAsync(client, Transcript(3), sessionId: id)).StatusCode);
		Assert.Equal(3, corpus.Files.Count);
	}

	// --- H2 ------------------------------------------------------------------------

	[Fact]
	public async Task A_copy_cut_mid_record_at_the_same_version_is_refused_and_keeps_the_stored_text()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		string first = ClaudeLine("user", "first message");
		string second = ClaudeLine("assistant", "second message");
		await PostSessionAsync(client, first + second, sessionId: id);

		HttpResponseMessage cut = await PostSessionAsync(client, first + second[..(second.Length / 2)], sessionId: id);

		Assert.Equal(HttpStatusCode.Conflict, cut.StatusCode);
		Assert.Equal("session_shrank", (await ReadAsync(cut)).GetProperty("code").GetString());
		Assert.Contains("second message", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
		Assert.Equal(IngestStateName.Complete, await StateAsync(client, id));
	}

	[Theory]
	[InlineData("{not json\n", "malformedLines")]
	[InlineData("[1,2,3]\n", "lines")]
	public async Task A_bad_line_in_the_middle_is_never_complete(string bad, string counted)
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();

		HttpResponseMessage response = await PostSessionAsync(client, ClaudeLine("user", "one") + bad + ClaudeLine("assistant", "two"), sessionId: id);

		Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
		JsonElement result = await ReadAsync(response);
		Assert.False(result.GetProperty("complete").GetBoolean());
		Assert.False(result.GetProperty("retryable").GetBoolean());
		Assert.True(result.GetProperty("read").GetProperty(counted).GetInt64() > 0);
		Assert.True(result.GetProperty("read").GetProperty("skippedContent").GetBoolean());
		Assert.Equal(IngestStateName.Partial, await StateAsync(client, id));
	}

	[Fact]
	public async Task A_last_line_with_no_newline_is_never_complete()
	{
		_ = new FakeCorpus(factory.Sidecar);
		string line = ClaudeLine("user", "one");

		HttpResponseMessage response = await PostSessionAsync(await WriterAsync(), line + line.TrimEnd('\n'), sessionId: NewSession());

		Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
	}

	[Theory]
	[InlineData("")]
	[InlineData("garbage that is not a transcript\n")]
	[InlineData("{\"type\":\"progress\"}\n")]
	public async Task A_first_upload_with_no_messages_is_not_complete_and_records_nothing(string jsonl)
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();

		HttpResponseMessage response = await PostSessionAsync(client, jsonl, sessionId: id);

		Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
		JsonElement result = await ReadAsync(response);
		Assert.False(result.GetProperty("complete").GetBoolean());
		Assert.False(result.GetProperty("retryable").GetBoolean());
		Assert.Empty(result.GetProperty("parts").EnumerateArray());
		Assert.Empty(corpus.Calls);
		Assert.Equal(IngestStateName.None, await StateAsync(client, id));
	}

	[Fact]
	public async Task A_copy_cut_at_a_line_boundary_with_a_newer_version_is_refused_unless_the_caller_says_so()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		await PostSessionAsync(client, Transcript(5), sessionId: id);

		HttpResponseMessage cut = await PostSessionAsync(client, Transcript(1), sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal(HttpStatusCode.Conflict, cut.StatusCode);
		Assert.Equal("session_shrank", (await ReadAsync(cut)).GetProperty("code").GetString());
		Assert.Equal(5, corpus.Files.Count);

		HttpResponseMessage allowed = await PostSessionAsync(client, Transcript(1), sessionId: id, updated: Stamp.AddHours(1), allowShrink: true);

		Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
		Assert.Single(corpus.Files);

		HttpResponseMessage grown = await PostSessionAsync(client, Transcript(2), sessionId: id, updated: Stamp.AddHours(2));
		Assert.Equal(HttpStatusCode.Accepted, grown.StatusCode);
	}

	[Fact]
	public async Task A_refused_shrink_does_not_lower_the_recorded_count()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		await PostSessionAsync(client, Transcript(3), sessionId: id);
		await PostSessionAsync(client, Transcript(1), sessionId: id);

		Assert.Equal(HttpStatusCode.Conflict, (await PostSessionAsync(client, Transcript(2), sessionId: id)).StatusCode);
	}

	// --- M6 ------------------------------------------------------------------------

	[Fact]
	public async Task A_document_dated_ahead_cannot_come_back_after_a_delete_and_does_not_lock_out_honest_updates()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString("N");
		DateTimeOffset ahead = DateTimeOffset.UtcNow.AddMinutes(50);
		await PostDocumentAsync(client, sourceId: id, body: "dated ahead", updated: ahead);
		await client.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");

		Assert.Equal(HttpStatusCode.Conflict, (await PostDocumentAsync(client, sourceId: id, body: "dated ahead", updated: ahead)).StatusCode);
		Assert.Empty(corpus.Files);

		// A second delete must not lose what the first remembered about the copy.
		await client.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");
		Assert.Equal(HttpStatusCode.Conflict, (await PostDocumentAsync(client, sourceId: id, body: "dated ahead", updated: ahead)).StatusCode);

		await Task.Delay(5);
		Assert.Equal(HttpStatusCode.Accepted, (await PostDocumentAsync(client, sourceId: id, body: "honest", updated: DateTimeOffset.UtcNow)).StatusCode);
	}

	[Fact]
	public async Task A_version_only_just_past_the_skew_is_refused()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();

		Assert.Equal(HttpStatusCode.BadRequest, (await PostDocumentAsync(client, sourceId: Guid.NewGuid().ToString("N"), updated: DateTimeOffset.UtcNow.AddHours(23))).StatusCode);
	}

	// --- M7 ------------------------------------------------------------------------

	[Fact]
	public async Task A_lapsed_upload_cannot_undo_a_delete_or_release_a_newer_lease()
	{
		IIngestLedger ledger = factory.Services.GetRequiredService<IIngestLedger>();
		string path = Guid.NewGuid().ToString("N") + ".md";
		string identity = DocumentIngestor.IdentityOf("c", path);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		IngestClaim lapsed = await ledger.ClaimAsync(new IngestClaimRequest(identity, "c", Stamp, now, [path]), default);
		await ledger.BeginWriteAsync(identity, lapsed.LeaseToken!, "c", path, now, default);

		await ledger.MarkDeletedAsync("c", path, identity, now.AddMinutes(6));

		Assert.False(await ledger.FinishAsync(identity, lapsed.LeaseToken!, "c", IngestStateName.Complete, [path], null));
		Assert.False(await ledger.BeginWriteAsync(identity, lapsed.LeaseToken!, "c", path, now.AddMinutes(6), default));
		Assert.Equal(IngestStateName.Deleted, (await ledger.FindAsync(identity, default))!.State);

		string other = Guid.NewGuid().ToString("N");
		IngestClaim oldLease = await ledger.ClaimAsync(new IngestClaimRequest(other, "c", Stamp, now, []), default);
		IngestClaim newLease = await ledger.ClaimAsync(new IngestClaimRequest(other, "c", Stamp, now.AddMinutes(6), []), default);
		Assert.False(await ledger.FinishAsync(other, oldLease.LeaseToken!, "c", IngestStateName.Complete, [], null));
		Assert.Equal(IngestClaimOutcome.Busy, (await ledger.ClaimAsync(new IngestClaimRequest(other, "c", Stamp, now.AddMinutes(7), []), default)).Outcome);
		Assert.True(await ledger.FinishAsync(other, newLease.LeaseToken!, "c", IngestStateName.Complete, [], null));
	}

	[Fact]
	public async Task A_lease_left_by_a_dead_process_yields_to_a_retry_at_once()
	{
		Memoria.Api.Auth.ControlDatabase database = factory.Services.GetRequiredService<Memoria.Api.Auth.ControlDatabase>();
		SqliteIngestLedger before = new(database);
		SqliteIngestLedger after = new(database);
		string path = Guid.NewGuid().ToString("N") + ".md";
		string identity = DocumentIngestor.IdentityOf("c", path);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		await before.ClaimAsync(new IngestClaimRequest(identity, "c", Stamp, now, [path]), default);

		Assert.Equal(IngestStateName.Partial, after.EffectiveState((await after.FindAsync(identity, default))!, now));
		Assert.Equal(IngestClaimOutcome.Acquired, (await after.ClaimAsync(new IngestClaimRequest(identity, "c", Stamp, now, [path]), default)).Outcome);
	}

	[Fact]
	public async Task A_busy_lease_reports_the_time_it_really_has_left()
	{
		IIngestLedger ledger = factory.Services.GetRequiredService<IIngestLedger>();
		string path = Guid.NewGuid().ToString("N") + ".md";
		string identity = DocumentIngestor.IdentityOf("c", path);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		await ledger.ClaimAsync(new IngestClaimRequest(identity, "c", Stamp, now, [path]), default);

		IngestClaim claim = await ledger.ClaimAsync(new IngestClaimRequest(identity, "c", Stamp, now.AddMinutes(1), [path]), default);

		Assert.Equal(240, claim.RetryAfterSeconds);
	}

	// --- low ------------------------------------------------------------------------

	[Fact]
	public async Task Audit_rows_say_which_files()
	{
		_ = new FakeCorpus(factory.Sidecar);
		(Memoria.Api.Auth.ApiKeyRecord record, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), key);
		string id = Guid.NewGuid().ToString("N");
		string path = new DocumentEnvelope("notes", id).StoragePath();
		string sessionId = NewSession();

		await PostDocumentAsync(client, sourceId: id);
		await client.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");
		await PostSessionAsync(client, ClaudeLine("user", "hello"), sessionId: sessionId);

		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(found => found.Count(r => r.KeyId == record.Id) >= 3);
		AuthApiFactory.AuditRow[] mine = [.. rows.Where(r => r.KeyId == record.Id)];
		Assert.Contains(path, Assert.Single(mine, static r => r.Target!.Contains("\"delete\"", StringComparison.Ordinal)).Target!, StringComparison.Ordinal);
		Assert.Contains(path, Assert.Single(mine, static r => r.Target!.Contains("\"document\"", StringComparison.Ordinal)).Target!, StringComparison.Ordinal);
		Assert.Contains($"2026-01-15_{sessionId[..8]}.md", Assert.Single(mine, static r => r.Target!.Contains("\"session\"", StringComparison.Ordinal)).Target!, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_failed_write_is_audited_with_its_path()
	{
		_ = new FakeCorpus(factory.Sidecar) { OnWrite = static _ => TooLarge() };
		(Memoria.Api.Auth.ApiKeyRecord record, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		string id = Guid.NewGuid().ToString("N");

		await PostDocumentAsync(AuthApiFactory.Authorized(factory.CreateClient(), key), sourceId: id);

		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(found => found.Any(r => r.KeyId == record.Id));
		AuthApiFactory.AuditRow row = Assert.Single(rows, r => r.KeyId == record.Id);
		Assert.Equal(413, row.Status);
		Assert.Contains(new DocumentEnvelope("notes", id).StoragePath(), row.Target!, StringComparison.Ordinal);
		Assert.Contains("\"Complete\":false", row.Target!, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_credential_sent_as_a_collection_or_path_goes_nowhere()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string token = Token(30);
		(Memoria.Api.Auth.ApiKeyRecord record, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), key);

		HttpResponseMessage document = await PostDocumentAsync(client, collection: token);
		HttpResponseMessage session = await PostSessionAsync(client, ClaudeLine("user", "x"), collection: token);
		HttpResponseMessage deleteCollection = await client.DeleteAsync($"/v1/documents?collection={token}&path=a.md");
		HttpResponseMessage deletePath = await client.DeleteAsync($"/v1/documents?collection=c&path={token}");

		foreach (HttpResponseMessage response in new[] { document, session, deleteCollection, deletePath })
		{
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
		}

		Assert.Empty(corpus.Calls);
		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(found => found.Count(r => r.KeyId == record.Id) >= 4);
		Assert.DoesNotContain(token, JsonSerializer.Serialize(rows), StringComparison.Ordinal);
		Assert.DoesNotContain(factory.Logs, log => log.Message.Contains(token, StringComparison.Ordinal));
	}

	[Fact]
	public async Task The_answer_to_a_write_points_at_nothing_it_cannot_serve()
	{
		_ = new FakeCorpus(factory.Sidecar);

		HttpResponseMessage response = await PostDocumentAsync(await WriterAsync(), sourceId: Guid.NewGuid().ToString("N"));

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Null(response.Headers.Location);
	}

	[Theory]
	[InlineData("a\u0000", "b")]
	[InlineData("a", "\u0000b")]
	public async Task A_nul_in_an_identity_is_refused(string source, string sourceId)
	{
		FakeCorpus corpus = new(factory.Sidecar);

		HttpResponseMessage response = await PostDocumentAsync(await WriterAsync(), source: source, sourceId: sourceId);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty(corpus.Calls);
	}

	[Fact]
	public async Task A_nul_in_a_session_name_or_delete_is_refused()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();

		Assert.Equal(HttpStatusCode.BadRequest, (await PostSessionAsync(client, ClaudeLine("user", "x"), sessionId: "a\0b")).StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, (await PostSessionAsync(client, ClaudeLine("user", "x"), projectDirectory: "a\0b")).StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/v1/documents?collection=c&path=a%00b.md")).StatusCode);
		Assert.Empty(corpus.Calls);
	}

	[Fact]
	public void Storage_paths_of_identities_that_join_alike_differ()
	{
		Assert.NotEqual(new DocumentEnvelope("ab", "c").StoragePath()[^35..], new DocumentEnvelope("a", "bc").StoragePath()[^35..]);
	}

	[Fact]
	public async Task Deleting_something_never_stored_leaves_nothing_behind_and_blocks_nothing()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString("N");
		string path = new DocumentEnvelope("notes", id).StoragePath();

		await client.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");

		IIngestLedger ledger = factory.Services.GetRequiredService<IIngestLedger>();
		Assert.Null(await ledger.FindAsync(DocumentIngestor.IdentityOf("c", path), default));
		Assert.Equal(HttpStatusCode.Accepted, (await PostDocumentAsync(client, sourceId: id, updated: Stamp.AddYears(-1))).StatusCode);
	}

	[Fact]
	public async Task Deleting_one_part_stays_deleted_until_an_operator_restores_it()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		string jsonl = Transcript(3);
		await PostSessionAsync(client, jsonl, sessionId: id);
		string part = corpus.Files.Keys.Single(static path => path.Contains("_part-002", StringComparison.Ordinal));

		await client.DeleteAsync($"/v1/documents?collection=c&path={part}");

		Assert.Equal(IngestStateName.Partial, await StateAsync(client, id));
		JsonElement status = await ReadAsync(await client.GetAsync($"/v1/ingest/session?collection=c&format=claude&sessionId={id}"));
		Assert.DoesNotContain(status.GetProperty("paths").EnumerateArray(), element => element.GetString() == part);
		Assert.False(corpus.Files.ContainsKey(part));

		HttpResponseMessage resent = await PostSessionAsync(client, jsonl, sessionId: id);
		Assert.Equal(HttpStatusCode.MultiStatus, resent.StatusCode);
		JsonElement result = await ReadAsync(resent);
		Assert.Equal([SessionPartState.Written, SessionPartState.Deleted, SessionPartState.Written], States(result));
		Assert.False(result.GetProperty("retryable").GetBoolean());
		Assert.False(corpus.Files.ContainsKey(part));

		Assert.Equal(HttpStatusCode.Forbidden, (await PostSessionAsync(client, jsonl, sessionId: id, restoreDeleted: true)).StatusCode);
		HttpClient operatorKey = await factory.ClientWithScopesAsync([MemoriaScopes.Ingest, MemoriaScopes.Admin], ["c"]);
		Assert.Equal(HttpStatusCode.Accepted, (await PostSessionAsync(operatorKey, jsonl, sessionId: id, restoreDeleted: true)).StatusCode);
		Assert.True(corpus.Files.ContainsKey(part));
		Assert.Equal(IngestStateName.Complete, await StateAsync(client, id));
	}

	// --- what mutation testing found untested ---------------------------------------

	[Fact]
	public async Task A_failed_document_write_is_not_recorded_as_complete()
	{
		_ = new FakeCorpus(factory.Sidecar) { OnWrite = static _ => Busy() };
		string id = Guid.NewGuid().ToString("N");

		await PostDocumentAsync(await WriterAsync(), sourceId: id);

		IngestRecord record = (await factory.Services.GetRequiredService<IIngestLedger>().FindAsync(DocumentIngestor.IdentityOf("c", new DocumentEnvelope("notes", id).StoragePath()), default))!;
		Assert.Equal(IngestStateName.Partial, record.State);
	}
}

/// <summary>A clock tests can move, for the time a slow upload takes.</summary>
internal sealed class MovableClock : TimeProvider
{
	private DateTimeOffset _now = DateTimeOffset.UtcNow;

	public void Advance(TimeSpan by)
	{
		_now += by;
	}

	public override DateTimeOffset GetUtcNow()
	{
		return _now;
	}
}

public sealed class IngestLeaseRenewalTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	[Fact]
	public async Task A_long_upload_keeps_its_lease_part_by_part()
	{
		MovableClock clock = new();
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock)));
		FakeCorpus corpus = new(factory.Sidecar);
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		HttpClient client = AuthApiFactory.Authorized(app.CreateClient(), key);
		string id = Guid.NewGuid().ToString();
		using ManualResetEventSlim atLastPart = new();
		using ManualResetEventSlim release = new();
		corpus.OnWrite = path =>
		{
			clock.Advance(TimeSpan.FromMinutes(4));
			if (path.Contains("_part-004", StringComparison.Ordinal))
			{
				atLastPart.Set();
				release.Wait(TimeSpan.FromSeconds(30));
			}

			return null;
		};

		Task<HttpResponseMessage> running = PostSessionAsync(client, Transcript(4), sessionId: id, updated: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
		Assert.True(atLastPart.Wait(TimeSpan.FromSeconds(30)));

		// Four parts at four minutes each is well past the five-minute lease, had it not been renewed.
		HttpResponseMessage second = await PostSessionAsync(client, Transcript(4, 'z'), sessionId: id, updated: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
		release.Set();
		await running;

		Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
		Assert.Equal("ingest_in_progress", (await ReadAsync(second)).GetProperty("code").GetString());
	}
}

/// <summary>Records what a route set the server's minimum rate to, as only Kestrel has one.</summary>
internal sealed class RateProbe : IStartupFilter
{
	public RateFeature Feature { get; } = new();

	public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
	{
		return app =>
		{
			app.Use(async (context, inner) =>
			{
				context.Features.Set<Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature>(Feature);
				await inner();
			});
			next(app);
		};
	}

	public sealed class RateFeature : Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature
	{
		public Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate? MinDataRate { get; set; }
	}
}

/// <summary>Records what a route set the server's body limit to, as the test server has no such limit of its own.</summary>
internal sealed class BodyLimitProbe : IStartupFilter
{
	public LimitFeature Feature { get; } = new();

	public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
	{
		return app =>
		{
			app.Use(async (context, inner) =>
			{
				context.Features.Set<IHttpMaxRequestBodySizeFeature>(Feature);
				await inner();
			});
			next(app);
		};
	}

	public sealed class LimitFeature : IHttpMaxRequestBodySizeFeature
	{
		public bool IsReadOnly => false;

		public long? MaxRequestBodySize { get; set; } = 30_000_000;
	}
}

public sealed class IngestBodyLimitTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	[Fact]
	public async Task The_session_route_raises_the_servers_limit_even_when_it_refuses_the_request()
	{
		BodyLimitProbe probe = new();
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(probe)));
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);

		HttpResponseMessage refused = await AuthApiFactory.Authorized(app.CreateClient(), key)
			.PostAsync("/v1/ingest/session?collection=c&format=word&sessionId=x&updated=2026-01-01T00:00:00Z", new StringContent("", Encoding.UTF8, "application/x-ndjson"));

		Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
		Assert.Equal(268435456, probe.Feature.MaxRequestBodySize);
	}

	[Fact]
	public async Task The_document_route_raises_it_too_before_any_refusal()
	{
		BodyLimitProbe probe = new();
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(probe)));
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);

		await AuthApiFactory.Authorized(app.CreateClient(), key).PostAsync("/v1/documents", new StringContent("not json", Encoding.UTF8, "application/json"));

		Assert.Equal((2L * 2097152) + 65536, probe.Feature.MaxRequestBodySize);
	}

	[Fact]
	public async Task A_minimum_rate_is_set_before_any_refusal_on_both_routes()
	{
		RateProbe probe = new();
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder =>
		{
			builder.UseSetting("Memoria:Ingest:MinUploadBytesPerSecond", "12345");
			builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(probe));
		});
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		HttpClient client = AuthApiFactory.Authorized(app.CreateClient(), key);

		await client.PostAsync("/v1/ingest/session?collection=c&format=word&sessionId=x&updated=2026-01-01T00:00:00Z", new StringContent("", Encoding.UTF8, "application/x-ndjson"));
		Assert.Equal(12345, probe.Feature.MinDataRate?.BytesPerSecond);

		probe.Feature.MinDataRate = null;
		await client.PostAsync("/v1/documents", new StringContent("not json", Encoding.UTF8, "application/json"));
		Assert.Equal(12345, probe.Feature.MinDataRate?.BytesPerSecond);
	}
}

/// <summary>
/// Gives requests marked with a header a body that sends a little and then stalls, as a slow client does. The test
/// server buffers a client's whole body before the app sees any of it, so a slow client has to be made inside the server.
/// The live proof covers the same thing on real Kestrel.
/// </summary>
internal sealed class SlowBodies : IStartupFilter
{
	public const string Marker = "X-Test-Slow-Body";

	private int _entered;

	/// <summary>Ends every stalled body. Without it they end after a few seconds, so a server ignoring its deadline fails a test rather than hanging it.</summary>
	public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public int Entered => Volatile.Read(ref _entered);

	public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
	{
		return app =>
		{
			app.Use(async (context, inner) =>
			{
				if (context.Request.Headers.ContainsKey(Marker))
				{
					context.Request.Body = new SlowStream(this);
				}

				await inner();
			});
			next(app);
		};
	}

	private sealed class SlowStream(SlowBodies owner) : Stream
	{
		private byte[]? _first = Encoding.UTF8.GetBytes(ClaudeLine("user", "starting"));

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => throw new NotSupportedException();

		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			if (_first is { } first)
			{
				_first = null;
				first.CopyTo(buffer);
				Interlocked.Increment(ref owner._entered);
				return first.Length;
			}

			await owner.Finish.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ContinueWith(
				task => cancellationToken.ThrowIfCancellationRequested(),
				TaskScheduler.Default);
			return 0;
		}

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
		}

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}

/// <summary>M2, M3 and M4: what one key can do to everyone else.</summary>
public sealed class IngestLoadTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private static string SessionUrl(string collection = "c")
	{
		return $"/v1/ingest/session?collection={collection}&format=claude&sessionId={Guid.NewGuid()}&updated=2026-01-15T12:00:00Z";
	}

	private async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> app)
	{
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		return AuthApiFactory.Authorized(app.CreateClient(), key);
	}

	[Fact]
	public async Task One_keys_slow_uploads_leave_room_for_another_key_and_hold_no_render_slot()
	{
		SlowBodies slow = new();
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder =>
		{
			builder.UseSetting("Memoria:Ingest:MaxConcurrentRenders", "1");
			builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(slow));
		});
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient slowKey = await ClientAsync(app);
		HttpClient otherKey = await ClientAsync(app);
		Task<HttpResponseMessage> one = PostSlowAsync(slowKey, SessionUrl(), "application/x-ndjson");
		Task<HttpResponseMessage> two = PostSlowAsync(slowKey, SessionUrl(), "application/x-ndjson");
		for (int i = 0; i < 500 && slow.Entered < 2; i++)
		{
			await Task.Delay(10);
		}

		Assert.Equal(2, slow.Entered);
		HttpResponseMessage other = await PostSessionAsync(otherKey, ClaudeLine("user", "hello"), sessionId: Guid.NewGuid().ToString());
		slow.Finish.SetResult();
		await Task.WhenAll(one, two);

		Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
	}

	private static Task<HttpResponseMessage> PostSlowAsync(HttpClient client, string url, string contentType)
	{
		HttpRequestMessage request = new(HttpMethod.Post, url) { Content = new StringContent("", Encoding.UTF8, contentType) };
		request.Headers.Add(SlowBodies.Marker, "1");
		return client.SendAsync(request);
	}


	[Fact]
	public async Task A_body_too_slow_for_the_deadline_gets_408_and_changes_nothing()
	{
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder =>
		{
			builder.UseSetting("Memoria:Ingest:SessionUploadDeadline", "00:00:00.2");
			builder.ConfigureTestServices(static services => services.AddSingleton<IStartupFilter>(new SlowBodies()));
		});
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await ClientAsync(app);

		HttpResponseMessage response = await PostSlowAsync(client, SessionUrl(), "application/x-ndjson");

		Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
		Assert.Equal("upload_too_slow", (await ReadAsync(response)).GetProperty("code").GetString());
		Assert.Empty(corpus.Calls);
	}

	[Fact]
	public async Task A_document_too_slow_for_the_deadline_gets_408()
	{
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder =>
		{
			builder.UseSetting("Memoria:Ingest:DocumentUploadDeadline", "00:00:00.2");
			builder.ConfigureTestServices(static services => services.AddSingleton<IStartupFilter>(new SlowBodies()));
		});
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await ClientAsync(app);

		HttpResponseMessage response = await PostSlowAsync(client, "/v1/documents", "application/json");

		Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
	}

	[Fact]
	public async Task An_oversized_body_is_refused_before_redaction_runs()
	{
		CountingRedactor redactor = new();
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IRedactor>(redactor)));
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await ClientAsync(app);

		HttpResponseMessage response = await PostDocumentAsync(client, sourceId: Guid.NewGuid().ToString("N"), body: new string('x', (2 * 1024 * 1024) + 1));

		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		Assert.Equal(0, redactor.Calls);
	}

	[Fact]
	public async Task Redaction_waits_its_turn_behind_the_work_limit()
	{
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Ingest:MaxConcurrentRenders", "1"));
		IngestGates gates = app.Services.GetRequiredService<IngestGates>();
		using ManualResetEventSlim release = new();
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task<int> holding = Task.Run(() => gates.WorkAsync(2 * 1024 * 1024, () =>
		{
			started.SetResult();
			release.Wait(TimeSpan.FromSeconds(30));
			return 1;
		}, default));
		await started.Task;

		Task<int> waiting = gates.WorkAsync(2 * 1024 * 1024, static () => 2, default);
		await Task.Delay(50);
		Assert.False(waiting.IsCompleted);

		release.Set();
		Assert.Equal(2, await waiting);
		Assert.Equal(1, await holding);
	}

	[Fact]
	public async Task A_burst_of_deletes_shares_one_more_update_and_a_delete_of_nothing_costs_none()
	{
		string collection = "burst-" + Guid.NewGuid().ToString("N");
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder =>
		{
			builder.UseSetting("Memoria:Ingest:MinReindexInterval", "00:00:00.5");
			builder.UseSetting($"Memoria:Ingest:Collections:{collection}:Enabled", "true");
		});
		FakeCorpus corpus = new(factory.Sidecar);
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], [collection]);
		HttpClient client = AuthApiFactory.Authorized(app.CreateClient(), key);
		foreach (int i in Enumerable.Range(0, 3))
		{
			corpus.Files[$"f{i}.md"] = "x";
		}

		await client.DeleteAsync($"/v1/documents?collection={collection}&path=never.md");
		Assert.DoesNotContain(factory.Sidecar.Requests, static r => r.Uri.AbsolutePath == "/reindex");

		JsonElement first = await ReadAsync(await client.DeleteAsync($"/v1/documents?collection={collection}&path=f0.md"));
		JsonElement second = await ReadAsync(await client.DeleteAsync($"/v1/documents?collection={collection}&path=f1.md"));
		JsonElement third = await ReadAsync(await client.DeleteAsync($"/v1/documents?collection={collection}&path=f2.md"));

		Assert.Equal(ReindexState.Started, first.GetProperty("reindexState").GetString());
		Assert.Equal(ReindexState.Scheduled, second.GetProperty("reindexState").GetString());
		Assert.Equal(ReindexState.Scheduled, third.GetProperty("reindexState").GetString());
		Assert.Equal(1, second.GetProperty("reindexInSeconds").GetInt32());
		Assert.Single(factory.Sidecar.Requests, static r => r.Uri.AbsolutePath == "/reindex");

		for (int i = 0; i < 300 && factory.Sidecar.Requests.Count(static r => r.Uri.AbsolutePath == "/reindex") < 2; i++)
		{
			await Task.Delay(20);
		}

		Assert.Equal(2, factory.Sidecar.Requests.Count(static r => r.Uri.AbsolutePath == "/reindex"));
	}

	private sealed class CountingRedactor : IRedactor
	{
		private readonly Redactor _inner = new();

		public int Calls { get; private set; }

		public RedactedMarkdown Redact(UnredactedMarkdown markdown)
		{
			Calls++;
			return _inner.Redact(markdown);
		}
	}
}

/// <summary>Keys ignore case when configuration merges, so a later layer could move the switch to a different collection.</summary>
public sealed class RedactionSwitchLayerTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	[Fact]
	public async Task A_later_layer_spelling_a_name_differently_turns_the_switch_off_for_neither_and_warns()
	{
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder => builder.ConfigureAppConfiguration(static (_, config) =>
		{
			config.AddInMemoryCollection(new Dictionary<string, string?> { ["Memoria:Ingest:Collections:Notes:Redaction"] = "Disabled", ["Memoria:Ingest:Collections:Notes:Enabled"] = "true" });
			config.AddInMemoryCollection(new Dictionary<string, string?> { ["Memoria:Ingest:Collections:notes:Other"] = "x" });
		}));
		FakeCorpus corpus = new(factory.Sidecar);
		string token = Token(31);
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["Notes", "notes"]);
		HttpClient client = AuthApiFactory.Authorized(app.CreateClient(), key);

		JsonElement upper = await ReadAsync(await PostDocumentAsync(client, collection: "Notes", sourceId: Guid.NewGuid().ToString("N"), body: token));
		JsonElement lower = await ReadAsync(await PostDocumentAsync(client, collection: "notes", sourceId: Guid.NewGuid().ToString("N"), body: token));

		// Which spelling the setting landed on is an accident, so one of them is open to ingest. Whichever it is stays redacted.
		JsonElement[] stored = [.. new[] { upper, lower }.Where(static answer => answer.TryGetProperty("redaction", out _))];
		Assert.NotEmpty(stored);
		Assert.All(stored, static answer => Assert.Equal(IngestRedaction.Enabled, answer.GetProperty("redaction").GetString()));
		Assert.DoesNotContain(corpus.Files.Values, content => content.Contains(token, StringComparison.Ordinal));
		Assert.Contains(factory.Logs, static log => log.EventId.Id == 3302);
	}
}
