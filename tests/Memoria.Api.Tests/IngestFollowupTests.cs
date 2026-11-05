using System.Net;
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
using Microsoft.Extensions.Logging;
using static Memoria.Api.Tests.IngestKit;

namespace Memoria.Api.Tests;

/// <summary>What a spot-check proved a hostile key or a damaged copy could still do.</summary>
public sealed class IngestFollowupTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private Task<HttpClient> WriterAsync()
	{
		return factory.ClientWithScopesAsync([MemoriaScopes.Ingest], ["c"]);
	}

	private static string SessionSharing(string prefix, string tail)
	{
		return $"{prefix}-{tail}-1111-2222-333333333333";
	}

	private static string NewPrefix()
	{
		return Guid.NewGuid().ToString("N")[..8];
	}

	private static async Task<string> StateAsync(HttpClient client, string sessionId)
	{
		return (await ReadAsync(await client.GetAsync($"/v1/ingest/session?collection=c&format=claude&sessionId={sessionId}"))).GetProperty("state").GetString()!;
	}

	// --- lockout -----------------------------------------------------------------------

	[Fact]
	public async Task Squatting_on_both_short_names_does_not_lock_a_session_out()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string prefix = NewPrefix();
		string victim = SessionSharing(prefix, "aaaa");
		await PostSessionAsync(client, ClaudeLine("user", "squat one"), sessionId: SessionSharing(prefix, "bbbb"));
		await PostSessionAsync(client, ClaudeLine("user", "squat two"), sessionId: SessionSharing(prefix, "cccc"), projectDirectory: victim);

		HttpResponseMessage response = await PostSessionAsync(client, ClaudeLine("user", "the victim"), sessionId: victim);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Contains(corpus.Files.Values, static content => content.Contains("the victim", StringComparison.Ordinal));
		Assert.Equal(3, corpus.Files.Count);
	}

	[Fact]
	public async Task A_project_folder_cannot_produce_the_digest_a_clash_falls_back_to()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string prefix = NewPrefix();
		string victim = SessionSharing(prefix, "aaaa");
		await PostSessionAsync(client, ClaudeLine("user", "first holder"), sessionId: SessionSharing(prefix, "bbbb"));
		await PostSessionAsync(client, ClaudeLine("user", "the victim"), sessionId: victim);
		string victimPath = corpus.Files.Keys.Single(path => corpus.Files[path].Contains("the victim", StringComparison.Ordinal));

		await PostSessionAsync(client, ClaudeLine("user", "squatter"), sessionId: SessionSharing(prefix, "cccc"), projectDirectory: victim);

		string squatPath = corpus.Files.Keys.Single(path => corpus.Files[path].Contains("squatter", StringComparison.Ordinal));
		Assert.NotEqual(victimPath, squatPath);
	}

	[Fact]
	public async Task A_short_name_is_free_again_once_its_owner_has_no_files()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string prefix = NewPrefix();
		await PostSessionAsync(client, ClaudeLine("user", "squatter"), sessionId: SessionSharing(prefix, "bbbb"));
		string squatted = Assert.Single(corpus.Files).Key;

		await client.DeleteAsync($"/v1/documents?collection=c&path={squatted}");
		HttpResponseMessage response = await PostSessionAsync(client, ClaudeLine("user", "the victim"), sessionId: SessionSharing(prefix, "aaaa"));

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Equal(squatted, Assert.Single(corpus.Files).Key);
	}

	[Fact]
	public async Task When_every_longer_name_is_held_on_purpose_the_answer_is_a_conflict_and_nothing_is_written()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		IIngestLedger ledger = factory.Services.GetRequiredService<IIngestLedger>();
		string id = Guid.NewGuid().ToString();
		RedactedSession session = await SessionRenderer.RenderAsync(new MemoryStream(Encoding.UTF8.GetBytes(ClaudeLine("user", "x"))), new SessionSource(SessionFormat.ClaudeCode, id));
		DateTimeOffset past = DateTimeOffset.UtcNow.AddHours(-1);
		for (RedactedSession? stem = session; stem is not null; stem = stem.WithDistinctPaths())
		{
			IngestClaim held = await ledger.ClaimAsync(new IngestClaimRequest("holder" + stem.PathStem, "c", Stamp, past, ["held-" + stem.PathStem + ".md"], Stem: stem.PathStem), default);
			await ledger.FinishAsync("holder" + stem.PathStem, held.LeaseToken!, "c", IngestStateName.Complete, ["held-" + stem.PathStem + ".md"], null);
		}

		HttpResponseMessage response = await PostSessionAsync(client, ClaudeLine("user", "x"), sessionId: id);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Equal("path_owned", (await ReadAsync(response)).GetProperty("code").GetString());
		Assert.DoesNotContain(corpus.Calls, static call => call.StartsWith("write", StringComparison.Ordinal));
	}

	[Fact]
	public async Task With_the_first_longer_name_held_too_a_session_takes_the_next()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		IIngestLedger ledger = factory.Services.GetRequiredService<IIngestLedger>();
		string id = Guid.NewGuid().ToString();
		RedactedSession session = await SessionRenderer.RenderAsync(new MemoryStream(Encoding.UTF8.GetBytes(ClaudeLine("user", "x"))), new SessionSource(SessionFormat.ClaudeCode, id));
		foreach (RedactedSession stem in new[] { session, session.WithDistinctPaths()! })
		{
			IngestClaim held = await ledger.ClaimAsync(new IngestClaimRequest("holder" + stem.PathStem, "c", Stamp, DateTimeOffset.UtcNow.AddHours(-1), ["held-" + stem.PathStem + ".md"], Stem: stem.PathStem), default);
			await ledger.FinishAsync("holder" + stem.PathStem, held.LeaseToken!, "c", IngestStateName.Complete, ["held-" + stem.PathStem + ".md"], null);
		}

		HttpResponseMessage response = await PostSessionAsync(client, ClaudeLine("user", "x"), sessionId: id);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Matches($"^2026-01-15_{id[..8]}-[0-9a-f]{{16}}\\.md$", Assert.Single(corpus.Files).Key);
	}

	// --- lost content -------------------------------------------------------------------

	[Fact]
	public async Task A_corrupt_middle_line_with_the_same_count_does_not_destroy_what_it_lost()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string a = ClaudeLine("user", "message A"), b = ClaudeLine("assistant", "message B", 1), c = ClaudeLine("user", "message C", 2), d = ClaudeLine("assistant", "message D", 3);
		await PostSessionAsync(client, a + b + c, sessionId: id);

		HttpResponseMessage damaged = await PostSessionAsync(client, a + "{corrupt\n" + c + d, sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal(HttpStatusCode.Conflict, damaged.StatusCode);
		Assert.Equal("lost_content", (await ReadAsync(damaged)).GetProperty("code").GetString());
		Assert.Contains("message B", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
		Assert.Equal(IngestStateName.Complete, await StateAsync(client, id));
	}

	[Fact]
	public async Task A_live_transcripts_unfinished_last_line_still_overwrites_with_what_grew()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string a = ClaudeLine("user", "message A"), b = ClaudeLine("assistant", "message B", 1);
		await PostSessionAsync(client, a, sessionId: id);

		HttpResponseMessage grown = await PostSessionAsync(client, a + b + b.TrimEnd('\n'), sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal(HttpStatusCode.MultiStatus, grown.StatusCode);
		Assert.Contains("message B", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_live_session_with_a_torn_last_line_keeps_storing_new_messages()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string a = ClaudeLine("user", "message A"), b = ClaudeLine("assistant", "message B", 1), c = ClaudeLine("user", "message C", 2);

		Assert.Equal(HttpStatusCode.MultiStatus, (await PostSessionAsync(client, a + b[..20], sessionId: id)).StatusCode);
		Assert.Equal(HttpStatusCode.MultiStatus, (await PostSessionAsync(client, a + b + c[..20], sessionId: id, updated: Stamp.AddMinutes(1))).StatusCode);
		Assert.Contains("message B", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
		Assert.Equal(HttpStatusCode.Accepted, (await PostSessionAsync(client, a + b + c, sessionId: id, updated: Stamp.AddMinutes(2))).StatusCode);
		Assert.Contains("message C", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("{torn\n")]
	[InlineData("[1]\n")]
	public async Task A_bad_line_that_held_no_stored_message_does_not_block_new_ones(string bad)
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string a = ClaudeLine("user", "message A"), b = ClaudeLine("assistant", "message B", 1);
		await PostSessionAsync(client, a, sessionId: id);

		HttpResponseMessage grown = await PostSessionAsync(client, a + bad + b, sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal(HttpStatusCode.MultiStatus, grown.StatusCode);
		Assert.Contains("message B", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_whole_copy_that_renders_to_fewer_parts_needs_no_flag()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		IIngestLedger ledger = factory.Services.GetRequiredService<IIngestLedger>();
		string jsonl = ClaudeLine("user", "one") + ClaudeLine("assistant", "two", 1);
		await PostSessionAsync(client, jsonl, sessionId: id);
		string identity = IngestIdentity.For("session", "c", (await SessionRenderer.RenderAsync(new MemoryStream(Encoding.UTF8.GetBytes(jsonl)), new SessionSource(SessionFormat.ClaudeCode, id))).PathIdentity);
		string stale = $"2026-01-15_{id[..8]}_part-002.md";
		corpus.Files[stale] = "an old second part";
		IngestClaim held = await ledger.ClaimAsync(new IngestClaimRequest(identity, "c", Stamp, DateTimeOffset.UtcNow, [stale], AllowShrink: true), default);
		await ledger.FinishAsync(identity, held.LeaseToken!, "c", IngestStateName.Complete, [$"2026-01-15_{id[..8]}.md", stale], null);

		HttpResponseMessage response = await PostSessionAsync(client, jsonl, sessionId: id);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.False(corpus.Files.ContainsKey(stale));
	}

	[Fact]
	public async Task Allow_shrink_says_the_history_really_changed()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		await PostSessionAsync(client, ClaudeLine("user", "message A") + ClaudeLine("assistant", "message B", 1), sessionId: id);

		HttpResponseMessage rewritten = await PostSessionAsync(client, ClaudeLine("user", "rewritten") + ClaudeLine("assistant", "message B", 1), sessionId: id, updated: Stamp.AddHours(1), allowShrink: true);

		Assert.Equal(HttpStatusCode.Accepted, rewritten.StatusCode);
		Assert.Contains("rewritten", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
	}

	// --- content after a delete -------------------------------------------------------------

	[Fact]
	public async Task The_same_content_under_a_later_date_does_not_come_back_after_a_delete()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString("N");
		await PostDocumentAsync(client, sourceId: id, body: "the secret text", updated: Stamp);
		await client.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");

		HttpResponseMessage replay = await PostDocumentAsync(client, sourceId: id, body: "the secret text", updated: DateTimeOffset.UtcNow.AddMinutes(30));

		Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
		Assert.Empty(corpus.Files);
		Assert.Equal(HttpStatusCode.Accepted, (await PostDocumentAsync(client, sourceId: id, body: "really new text", updated: DateTimeOffset.UtcNow.AddMinutes(30))).StatusCode);
	}

	[Fact]
	public async Task A_date_ahead_of_the_clock_does_not_hold_honest_updates_back()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString("N");
		await PostDocumentAsync(client, sourceId: id, body: "dated ahead", updated: DateTimeOffset.UtcNow.AddMinutes(50));
		await Task.Delay(5);

		HttpResponseMessage honest = await PostDocumentAsync(client, sourceId: id, body: "honest", updated: DateTimeOffset.UtcNow);

		Assert.Equal(HttpStatusCode.Accepted, honest.StatusCode);
		Assert.EndsWith("honest", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
	}

	// --- delete against a live upload --------------------------------------------------------

}

/// <summary>A delete against an upload in progress: it waits for the write, and is then authoritative.</summary>
public sealed class DeleteDuringUploadTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private Task<HttpClient> WriterAsync()
	{
		return factory.ClientWithScopesAsync([MemoriaScopes.Ingest], ["c"]);
	}

	private static (ManualResetEventSlim Entered, ManualResetEventSlim Release) Gate(FakeCorpus corpus, string pathPart)
	{
		ManualResetEventSlim entered = new();
		ManualResetEventSlim release = new();
		corpus.OnWrite = path =>
		{
			if (path.Contains(pathPart, StringComparison.Ordinal))
			{
				entered.Set();
				release.Wait(TimeSpan.FromSeconds(30));
			}

			return null;
		};
		return (entered, release);
	}

	[Fact]
	public async Task A_delete_waits_for_the_write_in_flight_and_then_removes_what_landed()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString("N");
		(ManualResetEventSlim entered, ManualResetEventSlim release) = Gate(corpus, "notes-");

		Task<HttpResponseMessage> upload = PostDocumentAsync(client, sourceId: id, body: "holds a credential");
		Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
		Task<HttpResponseMessage> deleting = client.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");
		await Task.Delay(100);
		Assert.DoesNotContain(corpus.Calls, static call => call.StartsWith("delete", StringComparison.Ordinal));
		release.Set();
		HttpResponseMessage deleted = await deleting;
		await upload;

		// However the two answers were ordered, the delete came after the write and took it.
		Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);
		Assert.True((await ReadAsync(deleted)).GetProperty("deleted").GetBoolean());
		Assert.Empty(corpus.Files);
		Assert.Equal(IngestStateName.Deleted, (await factory.Services.GetRequiredService<IIngestLedger>().FindAsync(DocumentIngestor.IdentityOf("c", new DocumentEnvelope("notes", id).StoragePath()), default))!.State);
	}

	[Fact]
	public async Task A_delete_that_cannot_wait_long_enough_says_so_and_changes_nothing()
	{
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Ingest:DeleteWriteWait", "00:00:00.1"));
		FakeCorpus corpus = new(factory.Sidecar);
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		HttpClient client = AuthApiFactory.Authorized(app.CreateClient(), key);
		string id = Guid.NewGuid().ToString("N");
		(ManualResetEventSlim entered, ManualResetEventSlim release) = Gate(corpus, "notes-");

		Task<HttpResponseMessage> upload = PostDocumentAsync(client, sourceId: id, body: "landing");
		Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
		string url = $"/v1/documents?collection=c&source=notes&sourceId={id}";
		HttpResponseMessage unconfirmed = await client.DeleteAsync(url);
		HttpResponseMessage repeated = await client.DeleteAsync(url);

		Assert.Equal(HttpStatusCode.ServiceUnavailable, unconfirmed.StatusCode);
		Assert.Equal("delete_unconfirmed", (await ReadAsync(unconfirmed)).GetProperty("code").GetString());
		Assert.True(unconfirmed.Headers.Contains("Retry-After"));
		Assert.Equal(HttpStatusCode.ServiceUnavailable, repeated.StatusCode);

		release.Set();
		Assert.Equal(HttpStatusCode.Accepted, (await upload).StatusCode);
		Assert.Single(corpus.Files);

		Assert.Equal(HttpStatusCode.Accepted, (await client.DeleteAsync(url)).StatusCode);
		Assert.Empty(corpus.Files);
	}

	[Fact]
	public async Task A_sidecar_that_cannot_confirm_gets_an_honest_503_and_the_repeat_finishes_it()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString("N");
		await PostDocumentAsync(client, sourceId: id, body: "first");
		string url = $"/v1/documents?collection=c&source=notes&sourceId={id}";
		corpus.OnDelete = static _ => Busy();

		HttpResponseMessage response = await client.DeleteAsync(url);
		HttpResponseMessage replay = await PostDocumentAsync(client, sourceId: id, body: "first");

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		Assert.Equal("delete_unconfirmed", (await ReadAsync(response)).GetProperty("code").GetString());
		Assert.Single(corpus.Files);
		Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);

		corpus.OnDelete = static _ => null;
		HttpResponseMessage repeated = await client.DeleteAsync(url);

		Assert.True((await ReadAsync(repeated)).GetProperty("deleted").GetBoolean());
		Assert.Empty(corpus.Files);
	}

	[Fact]
	public async Task A_delete_refused_on_its_merits_is_answered_as_that_and_is_not_called_unconfirmed()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		corpus.OnDelete = static _ => FakeSidecar.Json(HttpStatusCode.Conflict, """{"error":"is_directory","message":"path refers to a directory"}""");

		HttpResponseMessage response = await (await WriterAsync()).DeleteAsync($"/v1/documents?collection=c&path={Guid.NewGuid():N}.md");

		Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		Assert.NotEqual(HttpStatusCode.Accepted, response.StatusCode);
	}

	[Fact]
	public async Task A_write_a_delete_took_before_the_upload_finished_is_not_reported_as_stored()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		IIngestLedger ledger = factory.Services.GetRequiredService<IIngestLedger>();
		string id = Guid.NewGuid().ToString("N");
		string path = new DocumentEnvelope("notes", id).StoragePath();
		corpus.OnWrite = _ =>
		{
			ledger.MarkDeletedAsync("c", path, DocumentIngestor.IdentityOf("c", path), DateTimeOffset.UtcNow).GetAwaiter().GetResult();
			return null;
		};

		HttpResponseMessage response = await PostDocumentAsync(await WriterAsync(), sourceId: id);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Equal("deleted_during_upload", (await ReadAsync(response)).GetProperty("code").GetString());
	}

	[Fact]
	public async Task A_delete_that_gets_the_lock_first_stops_the_write_that_was_waiting_for_it()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		IIngestLedger ledger = factory.Services.GetRequiredService<IIngestLedger>();
		string id = Guid.NewGuid().ToString("N");
		string path = new DocumentEnvelope("notes", id).StoragePath();
		string identity = DocumentIngestor.IdentityOf("c", path);
		IDisposable held = (await factory.Services.GetRequiredService<IngestPathLocks>().TryHoldAsync("c", path, TimeSpan.Zero, default))!;

		Task<HttpResponseMessage> upload = PostDocumentAsync(await WriterAsync(), sourceId: id);
		for (int i = 0; i < 500 && (await ledger.FindAsync(identity, default))?.LeaseOwner is null; i++)
		{
			await Task.Delay(10);
		}

		await ledger.MarkDeletedAsync("c", path, identity, DateTimeOffset.UtcNow);
		held.Dispose();
		HttpResponseMessage response = await upload;

		Assert.Equal("deleted_during_upload", (await ReadAsync(response)).GetProperty("code").GetString());
		Assert.DoesNotContain(corpus.Calls, static call => call.StartsWith("write", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_collection_with_settings_but_not_enabled_stays_closed()
	{
		string collection = "listed-" + Guid.NewGuid().ToString("N");
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder => builder.UseSetting($"Memoria:Ingest:Collections:{collection}:Redaction", "Disabled"));
		FakeCorpus corpus = new(factory.Sidecar);
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], [collection]);

		HttpResponseMessage response = await PostDocumentAsync(AuthApiFactory.Authorized(app.CreateClient(), key), collection: collection, sourceId: "one");

		Assert.Equal("ingest_not_enabled", (await ReadAsync(response)).GetProperty("code").GetString());
		Assert.Empty(corpus.Calls);
	}

	[Fact]
	public async Task Repeating_a_delete_does_not_move_the_tombstone()
	{
		IIngestLedger ledger = factory.Services.GetRequiredService<IIngestLedger>();
		string path = Guid.NewGuid().ToString("N") + ".md";
		string identity = DocumentIngestor.IdentityOf("c", path);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		IngestClaim claim = await ledger.ClaimAsync(new IngestClaimRequest(identity, "c", Stamp, now, [path]), default);
		await ledger.BeginWriteAsync(identity, claim.LeaseToken!, "c", path, now, default);

		await ledger.MarkDeletedAsync("c", path, identity, now);
		DateTimeOffset first = (await ledger.FindAsync(identity, default))!.Version;
		await ledger.MarkDeletedAsync("c", path, identity, now.AddHours(1));

		Assert.Equal(first, (await ledger.FindAsync(identity, default))!.Version);
	}

	[Fact]
	public async Task A_lost_answer_is_finished_by_sending_again_which_overwrites()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString("N");
		string path = new DocumentEnvelope("notes", id).StoragePath();
		corpus.OnWrite = written =>
		{
			corpus.Files[written] = "landed, and nobody was told";
			throw new HttpRequestException("reset");
		};
		HttpResponseMessage lost = await PostDocumentAsync(client, sourceId: id, body: "the document");
		corpus.OnWrite = static _ => null;

		HttpResponseMessage retried = await PostDocumentAsync(client, sourceId: id, body: "the document");

		Assert.NotEqual(HttpStatusCode.Accepted, lost.StatusCode);
		Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
		Assert.EndsWith("the document", corpus.Files[path], StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_delete_removes_what_a_lost_answer_left_and_leaves_its_tombstone()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString("N");
		corpus.OnWrite = written =>
		{
			corpus.Files[written] = "landed, and nobody was told";
			throw new HttpRequestException("reset");
		};
		await PostDocumentAsync(client, sourceId: id, body: "the document");
		corpus.OnWrite = static _ => null;

		HttpResponseMessage deleted = await client.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");
		HttpResponseMessage replay = await PostDocumentAsync(client, sourceId: id, body: "the document");

		Assert.True((await ReadAsync(deleted)).GetProperty("deleted").GetBoolean());
		Assert.Empty(corpus.Files);
		Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
	}

	[Fact]
	public async Task A_part_lost_to_a_delete_finds_its_session_even_though_no_answer_came_back()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		corpus.OnWrite = written =>
		{
			corpus.Files[written] = "landed, and nobody was told";
			throw new HttpRequestException("reset");
		};
		await PostSessionAsync(client, ClaudeLine("user", "x"), sessionId: id);
		corpus.OnWrite = static _ => null;

		await client.DeleteAsync($"/v1/documents?collection=c&path={path}");
		JsonElement again = await ReadAsync(await PostSessionAsync(client, ClaudeLine("user", "x"), sessionId: id));

		Assert.Empty(corpus.Files);
		Assert.Equal([SessionPartState.Deleted], States(again));
	}

	[Fact]
	public async Task A_delete_of_a_part_during_an_upload_stops_the_rest_and_is_reported()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string transcript = Transcript(2);
		await PostSessionAsync(client, transcript, sessionId: id);
		string[] names = [.. corpus.Files.Keys.Order(StringComparer.Ordinal)];
		Assert.Equal(2, names.Length);
		(ManualResetEventSlim entered, ManualResetEventSlim release) = Gate(corpus, names[0]);

		Task<HttpResponseMessage> upload = PostSessionAsync(client, transcript + ClaudeLine("user", "more", 40), sessionId: id, updated: Stamp.AddHours(1));
		Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
		HttpResponseMessage deleted = await client.DeleteAsync($"/v1/documents?collection=c&path={names[1]}");
		release.Set();
		HttpResponseMessage answered = await upload;
		JsonElement result = await ReadAsync(answered);

		Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);
		Assert.Equal((HttpStatusCode)207, answered.StatusCode);
		Assert.Contains(SessionPartState.Deleted, States(result));
		Assert.DoesNotContain(names[1], corpus.Files.Keys);
	}

	[Fact]
	public async Task A_delete_of_a_part_being_written_for_the_first_time_waits_and_takes_it()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		(ManualResetEventSlim entered, ManualResetEventSlim release) = Gate(corpus, "2026-01-15_");

		Task<HttpResponseMessage> upload = PostSessionAsync(client, ClaudeLine("user", "holds a credential"), sessionId: id);
		Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
		Task<HttpResponseMessage> deleting = client.DeleteAsync($"/v1/documents?collection=c&path={path}");
		await Task.Delay(100);
		Assert.DoesNotContain(corpus.Calls, static call => call.StartsWith("delete", StringComparison.Ordinal));
		release.Set();
		JsonElement deleted = await ReadAsync(await deleting);
		await upload;

		Assert.True(deleted.GetProperty("deleted").GetBoolean());
		Assert.Empty(corpus.Files);
	}

	[Fact]
	public async Task A_delete_in_another_case_is_another_path_and_says_so()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = "abcdef01" + Guid.NewGuid().ToString()[8..];
		await PostSessionAsync(client, ClaudeLine("user", "x"), sessionId: id);
		string path = Assert.Single(corpus.Files).Key;

		HttpResponseMessage response = await client.DeleteAsync($"/v1/documents?collection=c&path={path.ToUpperInvariant()}");

		Assert.False((await ReadAsync(response)).GetProperty("deleted").GetBoolean());
		Assert.Single(corpus.Files);
	}

	[Fact]
	public async Task The_delete_route_removes_whatever_is_at_the_path_as_everything_in_the_collection_is_ours()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string name = Guid.NewGuid().ToString("N") + ".md";
		corpus.Files[name] = "no record of this";

		HttpResponseMessage response = await (await WriterAsync()).DeleteAsync($"/v1/documents?collection=c&path={name}");

		Assert.True((await ReadAsync(response)).GetProperty("deleted").GetBoolean());
		Assert.Empty(corpus.Files);
	}

	[Fact]
	public async Task A_file_already_at_a_path_is_overwritten_as_everything_in_the_collection_is_ours()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string id = Guid.NewGuid().ToString("N");
		string path = new DocumentEnvelope("notes", id).StoragePath();
		corpus.Files[path] = "no record of this";

		HttpResponseMessage response = await PostDocumentAsync(await WriterAsync(), sourceId: id, body: "the document");

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.EndsWith("the document", corpus.Files[path], StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_refused_write_is_finished_by_sending_again()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		corpus.OnWrite = static _ => Busy();
		await PostSessionAsync(client, ClaudeLine("user", "x"), sessionId: id);
		corpus.OnWrite = static _ => null;

		HttpResponseMessage retry = await PostSessionAsync(client, ClaudeLine("user", "x"), sessionId: id);

		Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
		Assert.Single(corpus.Files);
	}

	[Fact]
	public async Task A_collection_nobody_enabled_cannot_be_written_or_deleted_from()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string collection = "closed-" + Guid.NewGuid().ToString("N");
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], [collection]);
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), key);

		HttpResponseMessage written = await PostDocumentAsync(client, collection: collection, sourceId: "one");
		HttpResponseMessage session = await PostSessionAsync(client, ClaudeLine("user", "x"), collection: collection, sessionId: Guid.NewGuid().ToString());
		HttpResponseMessage deleted = await client.DeleteAsync($"/v1/documents?collection={collection}&path=x.md");

		foreach (HttpResponseMessage refused in new[] { written, session, deleted })
		{
			Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
			Assert.Equal("ingest_not_enabled", (await ReadAsync(refused)).GetProperty("code").GetString());
		}

		Assert.Empty(corpus.Calls);
	}

	[Fact]
	public async Task A_collection_opens_only_if_empty_and_is_asked_once()
	{
		string collection = "fresh-" + Guid.NewGuid().ToString("N");
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder => builder.UseSetting($"Memoria:Ingest:Collections:{collection}:Enabled", "true"));
		FakeCorpus corpus = new(factory.Sidecar) { HoldsForeignFiles = true };
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], [collection]);
		HttpClient client = AuthApiFactory.Authorized(app.CreateClient(), key);

		HttpResponseMessage refused = await PostDocumentAsync(client, collection: collection, sourceId: "one");

		Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
		Assert.Equal("collection_not_empty", (await ReadAsync(refused)).GetProperty("code").GetString());
		Assert.Empty(corpus.Calls);

		corpus.HoldsForeignFiles = false;
		Assert.Equal(HttpStatusCode.Accepted, (await PostDocumentAsync(client, collection: collection, sourceId: "one")).StatusCode);

		// It has files now, and that is no longer a reason to refuse.
		corpus.HoldsForeignFiles = true;
		Assert.Equal(HttpStatusCode.Accepted, (await PostDocumentAsync(client, collection: collection, sourceId: "two")).StatusCode);
	}

	[Fact]
	public async Task Path_locks_serialize_one_path_and_leave_others_alone_and_clean_up()
	{
		IngestPathLocks locks = new();
		IDisposable first = (await locks.TryHoldAsync("c", "a.md", TimeSpan.Zero, default))!;

		Assert.Null(await locks.TryHoldAsync("c", "a.md", TimeSpan.FromMilliseconds(10), default));
		using (IDisposable? other = await locks.TryHoldAsync("c", "b.md", TimeSpan.Zero, default))
		{
			Assert.NotNull(other);
		}

		Task<IDisposable?> waiting = locks.TryHoldAsync("c", "a.md", TimeSpan.FromSeconds(30), default);
		Assert.False(waiting.IsCompleted);
		first.Dispose();
		(await waiting)!.Dispose();
		(await locks.TryHoldAsync("c", "a.md", TimeSpan.Zero, default))!.Dispose();
	}

	[Fact]
	public async Task An_upload_whose_writes_all_failed_leaves_no_mark_on_what_comes_next()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		corpus.OnWrite = static _ => Busy();
		await PostSessionAsync(client, Transcript(5), sessionId: id, updated: Stamp.AddHours(1));
		corpus.OnWrite = static _ => null;

		HttpResponseMessage shorter = await PostSessionAsync(client, Transcript(2), sessionId: id, updated: Stamp);

		Assert.Equal(HttpStatusCode.Accepted, shorter.StatusCode);
	}

	[Fact]
	public async Task A_failed_write_after_a_delete_keeps_the_tombstone()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString("N");
		await PostDocumentAsync(client, sourceId: id, body: "first", updated: Stamp);
		await client.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");
		corpus.OnWrite = static _ => Busy();
		await PostDocumentAsync(client, sourceId: id, body: "second", updated: DateTimeOffset.UtcNow.AddMinutes(30));
		corpus.OnWrite = static _ => null;

		Assert.Equal(HttpStatusCode.Conflict, (await PostDocumentAsync(client, sourceId: id, body: "first", updated: DateTimeOffset.UtcNow.AddMinutes(30))).StatusCode);
	}

	[Fact]
	public async Task A_line_too_long_before_that_now_fits_is_not_lost_content()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string a = ClaudeLine("user", "message A"), b = ClaudeLine("assistant", "message B", 1);
		await PostSessionAsync(client, "{was too long once\n" + b, sessionId: id);

		HttpResponseMessage whole = await PostSessionAsync(client, a + b, sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal(HttpStatusCode.Accepted, whole.StatusCode);
		Assert.Contains("message A", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
	}
}

/// <summary>What the ledger may hold about a document, and the operator's way past a tombstone.</summary>
public sealed class IngestTombstoneTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	[Fact]
	public async Task The_stored_hash_is_over_redacted_text_so_it_confirms_no_guessed_secret()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await factory.ClientWithScopesAsync([MemoriaScopes.Ingest], ["c"]);
		string id = Guid.NewGuid().ToString("N");
		await PostDocumentAsync(client, sourceId: id, body: "my key is " + Token(40), updated: Stamp);
		await client.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");

		HttpResponseMessage another = await PostDocumentAsync(client, sourceId: id, body: "my key is " + Token(41), updated: DateTimeOffset.UtcNow.AddMinutes(30));

		// Two different secrets are one text once redacted, which is only so if nothing raw was hashed.
		Assert.Equal(HttpStatusCode.Conflict, another.StatusCode);
	}

	[Fact]
	public void Digests_are_keyed_and_a_previous_key_still_matches()
	{
		IngestDigests old = Digests("the-old-key-of-at-least-thirty-two-characters");
		string stored = old.Of("hunter2");

		Assert.NotEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("hunter2"))), stored);
		Assert.True(old.Matches(stored, "hunter2"));
		Assert.False(old.Matches(stored, "hunter3"));
		Assert.False(Digests("a-new-key-of-at-least-thirty-two-characters").Matches(stored, "hunter2"));
		Assert.True(Digests("a-new-key-of-at-least-thirty-two-characters", "the-old-key-of-at-least-thirty-two-characters").Matches(stored, "hunter2"));
	}

	private static IngestDigests Digests(string key, params string[] previous)
	{
		return new IngestDigests(Microsoft.Extensions.Options.Options.Create(new Memoria.Api.Configuration.MemoriaOptions { Ingest = new Memoria.Api.Configuration.IngestOptions { DigestKey = key, PreviousDigestKeys = [.. previous] } }));
	}

	[Theory]
	[InlineData(null, false)]
	[InlineData("too-short", false)]
	[InlineData("a-key-of-at-least-thirty-two-characters", true)]
	public void The_digest_key_is_required_when_api_keys_are_on(string? key, bool valid)
	{
		Memoria.Api.Configuration.MemoriaOptions options = new()
		{
			Sidecar = new Memoria.Api.Configuration.SidecarOptions { BaseUrl = "http://127.0.0.1:8282" },
			Auth = new Memoria.Api.Configuration.AuthOptions { DatabasePath = "x.db" },
			Ingest = new Memoria.Api.Configuration.IngestOptions { DigestKey = key },
		};

		string[] failures = new Memoria.Api.Configuration.MemoriaOptionsValidator().Validate(null, options).Failures?.ToArray() ?? [];

		Assert.Equal(valid, !failures.Any(static failure => failure.Contains("DigestKey", StringComparison.Ordinal)));
	}

	[Fact]
	public async Task Only_an_admin_key_can_restore_deleted_content()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string id = Guid.NewGuid().ToString("N");
		HttpClient writer = await factory.ClientWithScopesAsync([MemoriaScopes.Ingest], ["c"]);
		await PostDocumentAsync(writer, sourceId: id, body: "worth keeping", updated: Stamp);
		await writer.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");

		string json = JsonSerializer.Serialize(new { collection = "c", source = "notes", sourceId = id, body = "worth keeping", updated = Stamp });
		HttpResponseMessage refused = await writer.PostAsync("/v1/documents?restoreDeleted=true", new StringContent(json, Encoding.UTF8, "application/json"));
		Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
		Assert.Equal("restore_needs_admin", (await ReadAsync(refused)).GetProperty("code").GetString());

		(Memoria.Api.Auth.ApiKeyRecord record, string key) = await factory.IssueAsync([MemoriaScopes.Ingest, MemoriaScopes.Admin], ["c"]);
		HttpResponseMessage restored = await AuthApiFactory.Authorized(factory.CreateClient(), key).PostAsync("/v1/documents?restoreDeleted=true", new StringContent(json, Encoding.UTF8, "application/json"));
		Assert.Equal(HttpStatusCode.Accepted, restored.StatusCode);
		Assert.Single(corpus.Files);

		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(found => found.Any(r => r.KeyId == record.Id));
		Assert.Contains("\"Restored\":true", Assert.Single(rows, r => r.KeyId == record.Id).Target!, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_read_is_diagnosable_from_the_table_without_logging_it_and_a_credential_reference_is_not_kept()
	{
		factory.Sidecar.On("GET", "/docs/claude-sessions/x.md", HttpStatusCode.OK, SidecarFixtures.Document);
		string token = Token(42);
		factory.Sidecar.OnPrefix("GET", "/docs/gh", static (_, _) => FakeSidecar.Json(HttpStatusCode.NotFound, """{"error":"not_found","message":"gone"}"""));
		factory.Logs.Clear();
		(Memoria.Api.Auth.ApiKeyRecord record, string key) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), key);

		await client.GetAsync("/v1/docs/claude-sessions/x.md");
		foreach (string fooled in new[] { token, token + "%09x", token.ToUpperInvariant(), "bare-secret-with-no-shape" })
		{
			await client.GetAsync($"/v1/docs/{fooled}");
		}

		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(found => found.Count(r => r.KeyId == record.Id) >= 5);
		string[] targets = [.. rows.Where(r => r.KeyId == record.Id).Select(static r => r.Target ?? "")];
		Assert.Contains(targets, static target => target.Contains("\"Found\":true", StringComparison.Ordinal));
		Assert.Contains(targets, static target => target.Contains("[unresolved]", StringComparison.Ordinal));
		Assert.DoesNotContain(targets, target => target.Contains(token, StringComparison.OrdinalIgnoreCase) || target.Contains("bare-secret", StringComparison.Ordinal));
	}
}

/// <summary>Sharing the work and the slots between keys.</summary>
public sealed class IngestFairnessTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	[Fact]
	public async Task Small_work_does_not_wait_behind_large_work()
	{
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Ingest:MaxConcurrentRenders", "1"));
		IngestGates gates = app.Services.GetRequiredService<IngestGates>();
		using ManualResetEventSlim release = new();
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task<int> hog = Task.Run(() => gates.WorkAsync(Large, () =>
		{
			started.SetResult();
			release.Wait(TimeSpan.FromSeconds(30));
			return 1;
		}, default));
		await started.Task;

		int small = await gates.WorkAsync(100, static () => 2, default).WaitAsync(TimeSpan.FromSeconds(10));
		release.Set();

		Assert.Equal(2, small);
		Assert.Equal(1, await hog);
	}

	[Fact]
	public async Task Past_the_limit_a_large_upload_is_told_to_come_back_and_gives_back_its_place()
	{
		LargeUploads uploads = new(Microsoft.Extensions.Options.Options.Create(new Memoria.Api.Configuration.MemoriaOptions { Ingest = new Memoria.Api.Configuration.IngestOptions { MaxConcurrentLargeUploads = 1 } }));
		byte[] over = new byte[(LargeUploads.LargeBytes + 1)];
		await using (Stream small = uploads.Watch(new MemoryStream(new byte[1024]), null))
		{
			Assert.Equal(1024, await small.ReadAsync(new byte[2048]));
		}

		Stream declared = uploads.Watch(new MemoryStream(), LargeUploads.LargeBytes + 1);
		Assert.Equal(503, Assert.Throws<IngestRefusedException>(() => uploads.Watch(new MemoryStream(), LargeUploads.LargeBytes + 1)).Status);
		await declared.DisposeAsync();

		// With no declared length, the byte that crosses the line is what takes the place.
		Stream undeclared = uploads.Watch(new MemoryStream(over), null);
		Assert.Equal(over.Length, await undeclared.ReadAsync(new byte[over.Length]));
		Stream refused = uploads.Watch(new MemoryStream(over), null);
		await Assert.ThrowsAsync<IngestRefusedException>(async () => await refused.ReadExactlyAsync(new byte[over.Length]));
		await undeclared.DisposeAsync();
		await using Stream again = uploads.Watch(new MemoryStream(), LargeUploads.LargeBytes + 1);
	}

	[Fact]
	public async Task A_large_document_holds_its_place_until_its_request_ends_and_a_small_one_needs_none()
	{
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Ingest:MaxConcurrentLargeDocuments", "1"));
		FakeCorpus corpus = new(factory.Sidecar);
		using ManualResetEventSlim entered = new();
		using ManualResetEventSlim release = new();
		corpus.OnWrite = _ =>
		{
			entered.Set();
			release.Wait(TimeSpan.FromSeconds(30));
			return null;
		};
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		HttpClient client = AuthApiFactory.Authorized(app.CreateClient(), key);
		string large = new('w', (int)LargeUploads.LargeDocumentBytes + 1);

		// Still in the sidecar, so long past reading its body.
		Task<HttpResponseMessage> first = PostDocumentAsync(client, sourceId: Guid.NewGuid().ToString("N"), body: large);
		Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
		corpus.OnWrite = static _ => null;
		HttpResponseMessage refused = await PostDocumentAsync(client, sourceId: Guid.NewGuid().ToString("N"), body: large);
		HttpResponseMessage small = await PostDocumentAsync(client, sourceId: Guid.NewGuid().ToString("N"), body: new string('w', 1024));
		release.Set();
		HttpResponseMessage held = await first;
		HttpResponseMessage after = await PostDocumentAsync(client, sourceId: Guid.NewGuid().ToString("N"), body: large);

		Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
		Assert.Equal("ingest_busy", (await ReadAsync(refused)).GetProperty("code").GetString());
		Assert.True(refused.Headers.Contains("Retry-After"));
		Assert.True(refused.Headers.ConnectionClose, "draining the body of a refused request is what runs a burst of retries out of memory");
		Assert.Equal(HttpStatusCode.Accepted, small.StatusCode);
		Assert.Equal(HttpStatusCode.Accepted, held.StatusCode);
		Assert.Equal(HttpStatusCode.Accepted, after.StatusCode);
	}

	[Fact]
	public async Task Small_uploads_are_served_while_the_large_places_are_full()
	{
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Ingest:MaxConcurrentLargeUploads", "1"));
		_ = new FakeCorpus(factory.Sidecar);
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		HttpClient client = AuthApiFactory.Authorized(app.CreateClient(), key);
		await using Stream held = app.Services.GetRequiredService<LargeUploads>().Watch(new MemoryStream(), LargeUploads.LargeBytes + 1);

		HttpResponseMessage large = await PostSessionAsync(client, new string('x', (int)LargeUploads.LargeBytes + 1), sessionId: Guid.NewGuid().ToString());
		HttpResponseMessage small = await PostSessionAsync(client, ClaudeLine("user", "x"), sessionId: Guid.NewGuid().ToString());
		HttpResponseMessage document = await PostDocumentAsync(client, sourceId: Guid.NewGuid().ToString("N"));

		Assert.Equal(HttpStatusCode.ServiceUnavailable, large.StatusCode);
		Assert.Equal("ingest_busy", (await ReadAsync(large)).GetProperty("code").GetString());
		Assert.True(large.Headers.Contains("Retry-After"));
		Assert.Equal(HttpStatusCode.Accepted, small.StatusCode);
		Assert.Equal(HttpStatusCode.Accepted, document.StatusCode);
	}

	[Fact]
	public void An_empty_digest_key_is_a_bug_and_never_a_digest()
	{
		IngestDigests digests = new(Microsoft.Extensions.Options.Options.Create(new Memoria.Api.Configuration.MemoriaOptions()));

		Assert.Throws<InvalidOperationException>(() => digests.Of("hunter2"));
		Assert.Throws<InvalidOperationException>(() => digests.Matches("x", "hunter2"));
	}

	[Fact]
	public async Task Large_work_runs_after_the_heap_is_compacted()
	{
		IngestGates gates = factory.Services.GetRequiredService<IngestGates>();

		Assert.Equal(7, await gates.WorkAsync(64L * 1024 * 1024, static () => 7, default));
	}

	[Fact]
	public async Task Running_out_of_memory_is_a_reason_to_come_back_and_the_retry_succeeds()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), key);
		string id = Guid.NewGuid().ToString("N");
		factory.Sidecar.On("POST", "/documents", static (_, _) => throw new OutOfMemoryException());

		HttpResponseMessage refused = await PostDocumentAsync(client, sourceId: id);
		corpus = new FakeCorpus(factory.Sidecar);
		HttpResponseMessage retried = await PostDocumentAsync(client, sourceId: id);

		Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
		Assert.Equal("ingest_busy", (await ReadAsync(refused)).GetProperty("code").GetString());
		Assert.True(refused.Headers.Contains("Retry-After"));
		Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
		Assert.Single(corpus.Files);
	}

	private const long Large = 2 * 1024 * 1024;

	[Fact]
	public async Task Any_number_of_slow_uploads_leave_everyone_served()
	{
		SlowBodies slow = new();
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(slow)));
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient[] clients = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => KeyedAsync(app)));
		List<Task<HttpResponseMessage>> running = [.. clients[..3].SelectMany(static client => new[] { PostSlowAsync(client), PostSlowAsync(client), PostSlowAsync(client) })];
		for (int i = 0; i < 500 && slow.Entered < 9; i++)
		{
			await Task.Delay(10);
		}

		Assert.Equal(9, slow.Entered);
		HttpResponseMessage fresh = await PostSessionAsync(clients[3], ClaudeLine("user", "a fresh key"), sessionId: Guid.NewGuid().ToString());
		HttpResponseMessage same = await PostSessionAsync(clients[0], ClaudeLine("user", "the same key"), sessionId: Guid.NewGuid().ToString());
		slow.Finish.SetResult();
		await Task.WhenAll(running);

		Assert.Equal(HttpStatusCode.Accepted, fresh.StatusCode);
		Assert.Equal(HttpStatusCode.Accepted, same.StatusCode);
	}

	private async Task<HttpClient> KeyedAsync(WebApplicationFactory<Program> app)
	{
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		return AuthApiFactory.Authorized(app.CreateClient(), key);
	}

	private static Task<HttpResponseMessage> PostSlowAsync(HttpClient client)
	{
		HttpRequestMessage request = new(HttpMethod.Post, $"/v1/ingest/session?collection=c&format=claude&sessionId={Guid.NewGuid()}&updated=2026-01-15T12:00:00Z") { Content = new StringContent("", Encoding.UTF8, "application/x-ndjson") };
		request.Headers.Add(SlowBodies.Marker, "1");
		return client.SendAsync(request);
	}
}

/// <summary>Query strings and request paths stay out of the log whatever the configuration says.</summary>
public sealed class RequestDetailLoggingTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private const string Marker = "markerquerytext";

	private static void Everything(IWebHostBuilder builder)
	{
		builder.UseSetting("Logging:LogLevel:Default", "Trace");
		builder.UseSetting("Logging:LogLevel:Microsoft", "Trace");
		builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Trace");
		builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics", "Trace");
		builder.UseSetting("Logging:Capture:LogLevel:Default", "Trace");
	}

	private async Task SendAsync(WebApplicationFactory<Program> app)
	{
		_ = new FakeCorpus(factory.Sidecar);
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.SearchResponse);
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Read, MemoriaScopes.Ingest], ["c"]);
		HttpClient client = AuthApiFactory.Authorized(app.CreateClient(), key);
		await client.GetAsync($"/v1/search?query={Marker}&collection=c");
		await client.GetAsync($"/v1/docs/c/{Marker}.md");
		await client.DeleteAsync($"/v1/documents?collection=c&path={Marker}.md");
		await client.GetAsync($"/v1/no-such-route/{Marker}");
	}

	[Fact]
	public async Task No_configuration_puts_a_query_or_path_in_the_log()
	{
		factory.Logs.Clear();
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(Everything);

		await SendAsync(app);

		Assert.DoesNotContain(factory.Logs, static log => log.Message.Contains(Marker, StringComparison.Ordinal));
		Assert.DoesNotContain(factory.Logs, static log => log.State.Values.Any(static value => value?.ToString()?.Contains(Marker, StringComparison.Ordinal) == true));
		Assert.DoesNotContain(factory.Logs, static log => log.Category == "Microsoft.AspNetCore.Hosting.Diagnostics");
		Assert.Contains(factory.Logs, static log => log.Message.StartsWith("HTTP GET", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("Enabled", false)]
	[InlineData("enabled", true)]
	[InlineData("true", true)]
	[InlineData("", true)]
	public async Task Only_the_exact_setting_lets_them_back_with_a_warning(string value, bool stillHidden)
	{
		factory.Logs.Clear();
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder =>
		{
			Everything(builder);
			builder.UseSetting(RequestDetailLogging.Setting, value);
		});

		await SendAsync(app);

		Assert.Equal(!stillHidden, factory.Logs.Any(static log => log.Message.Contains(Marker, StringComparison.Ordinal)));
		Assert.Equal(!stillHidden, factory.Logs.Any(static log => log.EventId.Id == 1100 && log.Level == LogLevel.Warning));
	}

	[Fact]
	public async Task The_framework_can_still_warn()
	{
		factory.Logs.Clear();
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(Everything);

		ILogger capped = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore.Something");
		capped.LogInformation("hidden {Marker}", Marker);
		capped.LogWarning("shown");
		using IDisposable? scope = capped.BeginScope("a scope");

		Assert.False(capped.IsEnabled(LogLevel.Information));
		Assert.Contains(factory.Logs, static log => log.Message == "shown");

		ILogger mcp = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ModelContextProtocol.Server.McpServer");
		mcp.LogWarning("names a tool {Marker}", Marker);
		mcp.LogError("names a tool {Marker}", Marker);
		Assert.DoesNotContain(factory.Logs, static log => log.Message.Contains(Marker, StringComparison.Ordinal));
	}

	[Fact]
	public async Task The_audit_line_in_the_log_leaves_out_paths_the_table_keeps()
	{
		_ = new FakeCorpus(factory.Sidecar);
		factory.Logs.Clear();
		(Memoria.Api.Auth.ApiKeyRecord record, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		string id = Guid.NewGuid().ToString("N");
		string path = new DocumentEnvelope("notes", id).StoragePath();

		await PostDocumentAsync(AuthApiFactory.Authorized(factory.CreateClient(), key), sourceId: id);

		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(found => found.Any(r => r.KeyId == record.Id));
		Assert.Contains(path, Assert.Single(rows, r => r.KeyId == record.Id).Target!, StringComparison.Ordinal);
		Assert.DoesNotContain(factory.Logs, log => log.Message.Contains(path, StringComparison.Ordinal));
	}
}
