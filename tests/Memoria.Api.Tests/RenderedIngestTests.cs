using System.Net;
using System.Text;
using System.Text.Json;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Core.Contracts;
using Memoria.Core.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Memoria.Api.Tests.IngestKit;

namespace Memoria.Api.Tests;

/// <summary>Moving an old corpus: already rendered documents, kept as they came except for redaction.</summary>
public sealed class RenderedIngestTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private Task<HttpClient> WriterAsync(params string[] collections)
	{
		return factory.ClientWithScopesAsync([MemoriaScopes.Ingest], collections.Length == 0 ? ["c"] : collections);
	}

	/// <summary>What the first cwd of <see cref="ClaudeLine"/> encodes to, so these sessions show they are the copy in their own folder.</summary>
	private const string OwnFolder = "-srv-demo";

	private static string UniquePath()
	{
		return $"2024/{Guid.NewGuid():N}.md";
	}

	private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string text, string collection = "c", DateTimeOffset? updated = null)
	{
		string url = $"/v1/ingest/rendered?collection={collection}&path={Uri.EscapeDataString(path)}&updated={Uri.EscapeDataString((updated ?? Stamp).ToString("O"))}";
		return client.PostAsync(url, new StringContent(text, Encoding.UTF8, "text/markdown"));
	}

	[Fact]
	public async Task A_document_keeps_its_path_and_frontmatter_and_loses_its_credentials()
	{
		FakeCorpus corpus = new(factory.Sidecar) { ReadsFrontmatter = true };
		string path = UniquePath(), token = Token(61), other = Token(62);
		string text = $"---\nsession_id: \"abc\"\nproject: \"demo\"\ngit_remote: \"{token}\"\n---\n\n## User\n\nkey {other}\n";

		HttpResponseMessage response = await PostAsync(await WriterAsync(), path, text);
		JsonElement result = await ReadAsync(response);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Equal(path, result.GetProperty("path").GetString());
		Assert.Equal(IngestRedaction.Enabled, result.GetProperty("redaction").GetString());
		Assert.Equal(2, result.GetProperty("redactions").EnumerateObject().Sum(static rule => rule.Value.GetInt32()));
		Assert.Equal(RenderedFrontmatter.Read, result.GetProperty("frontmatter").GetString());
		string stored = corpus.Files[path];
		Assert.StartsWith("---\nsession_id: \"abc\"\nproject: \"demo\"\n", stored, StringComparison.Ordinal);
		Assert.DoesNotContain(token, stored, StringComparison.Ordinal);
		Assert.DoesNotContain(other, stored, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_collection_with_redaction_off_follows_its_switch_and_says_so()
	{
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Ingest:Collections:open:Redaction", "Disabled"));
		FakeCorpus corpus = new(factory.Sidecar);
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["open"]);
		string path = UniquePath(), token = Token(63);

		JsonElement result = await ReadAsync(await PostAsync(AuthApiFactory.Authorized(app.CreateClient(), key), path, $"---\na: \"{token}\"\n---\n\nbody\n", "open"));

		Assert.Equal(IngestRedaction.Disabled, result.GetProperty("redaction").GetString());
		Assert.Contains(token, corpus.Files[path], StringComparison.Ordinal);
	}

	[Fact]
	public async Task Sending_it_again_overwrites_with_the_same_result_and_an_older_one_is_refused()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string path = UniquePath();

		JsonElement first = await ReadAsync(await PostAsync(client, path, "---\na: 1\n---\n\nfirst\n"));
		HttpResponseMessage again = await PostAsync(client, path, "---\na: 1\n---\n\nfirst\n");
		HttpResponseMessage older = await PostAsync(client, path, "---\na: 1\n---\n\nolder\n", updated: Stamp.AddDays(-1));

		Assert.True(first.GetProperty("created").GetBoolean());
		Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
		Assert.False((await ReadAsync(again)).GetProperty("created").GetBoolean());
		Assert.Equal("stale_version", (await ReadAsync(older)).GetProperty("code").GetString());
		Assert.EndsWith("first\n", corpus.Files[path], StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_lost_answer_is_finished_by_sending_again()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string path = UniquePath();
		corpus.OnWrite = written =>
		{
			corpus.Files[written] = "landed, and nobody was told";
			throw new HttpRequestException("reset");
		};
		await PostAsync(client, path, "body\n");
		corpus.OnWrite = static _ => null;

		Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, path, "body\n")).StatusCode);
		Assert.Equal("body\n", corpus.Files[path]);
	}

	[Theory]
	[InlineData(RenderedFrontmatter.Missing)]
	[InlineData(RenderedFrontmatter.Unparsable)]
	[InlineData(RenderedFrontmatter.PastWindow)]
	public async Task A_document_whose_frontmatter_cannot_be_read_is_stored_whole_and_reported(string expected)
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string path = UniquePath();
		string text = expected switch
		{
			RenderedFrontmatter.Missing => "no frontmatter here\n",
			RenderedFrontmatter.Unparsable => "---\n: : :\n---\n\nbody\n",
			_ => $"---\nfiles_touched: [\"{new string('x', 9000)}\"]\n---\n\nbody\n",
		};

		HttpResponseMessage response = await PostAsync(await WriterAsync(), path, text);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Equal(expected, (await ReadAsync(response)).GetProperty("frontmatter").GetString());
		Assert.Equal(text, corpus.Files[path]);
	}

	[Fact]
	public void The_window_counts_code_points_as_the_sidecar_does()
	{
		string fits = "---\n" + string.Concat(Enumerable.Repeat("\U0001F600", 8180)) + "\n---\nbody";

		Assert.Equal(RenderedFrontmatter.Unparsable, RenderedDocument.FrontmatterOf(fits, false));
		Assert.Equal(RenderedFrontmatter.PastWindow, RenderedDocument.FrontmatterOf("---\n" + new string('x', 8200) + "\n---\nbody", false));
		Assert.Equal(RenderedFrontmatter.Unparsable, RenderedDocument.FrontmatterOf("﻿---\r\n: :\r\n---\r\n", false));
	}

	[Fact]
	public async Task Over_the_cap_is_refused_for_that_document_alone()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string path = UniquePath();

		HttpResponseMessage over = await PostAsync(client, path, new string('x', (2 * 1024 * 1024) + 1));
		HttpResponseMessage next = await PostAsync(client, UniquePath(), "body\n");

		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
		Assert.Equal("payload_too_large", (await ReadAsync(over)).GetProperty("code").GetString());
		Assert.False(corpus.Files.ContainsKey(path));
		Assert.Equal(HttpStatusCode.Accepted, next.StatusCode);
	}

	[Fact]
	public async Task A_large_document_counts_against_the_cap()
	{
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Ingest:MaxConcurrentLargeDocuments", "1"));
		_ = new FakeCorpus(factory.Sidecar);
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		await using Stream held = app.Services.GetRequiredService<LargeUploads>().WatchDocument(new MemoryStream(), LargeUploads.LargeDocumentBytes + 1);
		HttpClient client = AuthApiFactory.Authorized(app.CreateClient(), key);

		HttpResponseMessage large = await PostAsync(client, UniquePath(), new string('x', (int)LargeUploads.LargeDocumentBytes + 1));
		HttpResponseMessage small = await PostAsync(client, UniquePath(), "body\n");

		Assert.Equal(HttpStatusCode.ServiceUnavailable, large.StatusCode);
		Assert.True(large.Headers.Contains("Retry-After"));
		Assert.Equal(HttpStatusCode.Accepted, small.StatusCode);
	}

	[Fact]
	public async Task Something_deleted_stays_out_and_the_status_says_what_is_recorded()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await factory.ClientWithScopesAsync([MemoriaScopes.Ingest, MemoriaScopes.Read], ["c"]);
		string path = UniquePath(), status = $"/v1/ingest/rendered?collection=c&path={Uri.EscapeDataString(path)}";

		Assert.Equal(IngestStateName.None, (await ReadAsync(await client.GetAsync(status))).GetProperty("state").GetString());
		await PostAsync(client, path, "body\n");
		JsonElement stored = await ReadAsync(await client.GetAsync(status));
		await client.DeleteAsync($"/v1/documents?collection=c&path={Uri.EscapeDataString(path)}");
		HttpResponseMessage replay = await PostAsync(client, path, "body\n", updated: DateTimeOffset.UtcNow.AddMinutes(30));

		Assert.Equal(IngestStateName.Complete, stored.GetProperty("state").GetString());
		Assert.Equal(Stamp, stored.GetProperty("updated").GetDateTimeOffset());
		Assert.Equal("stale_version", (await ReadAsync(replay)).GetProperty("code").GetString());
		Assert.Empty(corpus.Files);
		Assert.Equal(IngestStateName.Deleted, (await ReadAsync(await client.GetAsync(status))).GetProperty("state").GetString());
	}

	[Theory]
	[InlineData("../outside.md")]
	[InlineData("/rooted.md")]
	[InlineData(".git/config")]
	[InlineData("a//b.md")]
	public async Task A_path_that_could_leave_or_is_hidden_is_refused(string path)
	{
		FakeCorpus corpus = new(factory.Sidecar);

		HttpResponseMessage response = await PostAsync(await WriterAsync(), path, "body\n");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty(corpus.Calls);
	}

	[Fact]
	public async Task It_needs_an_open_collection_and_one_the_key_may_write()
	{
		FakeCorpus corpus = new(factory.Sidecar);

		HttpResponseMessage forbidden = await PostAsync(await WriterAsync("c"), UniquePath(), "body\n", "closed");
		HttpResponseMessage closed = await PostAsync(await WriterAsync("never-enabled"), UniquePath(), "body\n", "never-enabled");

		Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
		Assert.Equal("ingest_not_enabled", (await ReadAsync(closed)).GetProperty("code").GetString());
		Assert.Empty(corpus.Calls);
	}

	[Fact]
	public async Task A_body_that_is_not_utf8_is_refused()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();

		HttpResponseMessage response = await client.PostAsync($"/v1/ingest/rendered?collection=c&path={UniquePath()}&updated={Uri.EscapeDataString(Stamp.ToString("O"))}", new ByteArrayContent([0x66, 0xFF, 0xFE]));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty(corpus.Calls);
	}

	private static string RenderedPart(string sessionId, string word, int through)
	{
		return $"---\nsession_id: \"{sessionId}\"\nmessage_end: {through}\n---\n\n## User\n\n{word}\n";
	}

	[Fact]
	public async Task A_session_takes_over_its_migrated_documents_and_no_message_stays_twice()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string same = $"2026-01-15_{id[..8]}.md", other = $"2026-01-15_{id[..8]}_part-009.md";
		await PostAsync(client, same, RenderedPart(id, "oldfirst", 0));
		await PostAsync(client, other, RenderedPart(id, "oldsecond", 1));

		HttpResponseMessage session = await PostSessionAsync(client, ClaudeLine("user", "newfirst") + ClaudeLine("assistant", "newsecond", 1), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal(HttpStatusCode.Accepted, session.StatusCode);
		Assert.Contains(other, (await ReadAsync(session)).GetProperty("superseded").GetProperty("deleted").EnumerateArray().Select(static path => path.GetString()));
		string stored = Assert.Single(corpus.Files).Value;
		Assert.Contains("newfirst", stored, StringComparison.Ordinal);
		Assert.DoesNotContain("oldfirst", stored, StringComparison.Ordinal);
		Assert.DoesNotContain(corpus.Files.Keys, static path => path.Contains("part-009", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_failed_takeover_loses_nothing_and_the_next_upload_finishes_it()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string other = $"2026-01-15_{id[..8]}_part-009.md";
		await PostAsync(client, other, RenderedPart(id, "oldsecond", 0));
		corpus.OnWrite = static _ => Busy();

		HttpResponseMessage failed = await PostSessionAsync(client, ClaudeLine("user", "x"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal((HttpStatusCode)207, failed.StatusCode);
		Assert.True(corpus.Files.ContainsKey(other), "the old document stays until every part has arrived");
		corpus.OnWrite = static _ => null;
		Assert.Equal(HttpStatusCode.Accepted, (await PostSessionAsync(client, ClaudeLine("user", "x"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1))).StatusCode);
		Assert.False(corpus.Files.ContainsKey(other));
	}

	[Fact]
	public async Task A_cut_copy_cannot_replace_what_the_migrated_documents_hold()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		await PostAsync(client, path, RenderedPart(id, "themigratedtext", 5));

		HttpResponseMessage cut = await PostSessionAsync(client, ClaudeLine("user", "only one"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));
		HttpResponseMessage whole = await PostSessionAsync(client, string.Concat(Enumerable.Range(0, 6).Select(static n => ClaudeLine(n % 2 == 0 ? "user" : "assistant", $"m{n}", n))), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal("session_shrank", (await ReadAsync(cut)).GetProperty("code").GetString());
		Assert.Equal(HttpStatusCode.Accepted, whole.StatusCode);
		Assert.Contains("m5", corpus.Files[path], StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_copy_older_than_the_migrated_document_is_refused()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		await PostAsync(client, $"2026-01-15_{id[..8]}.md", RenderedPart(id, "x", 0));

		HttpResponseMessage older = await PostSessionAsync(client, ClaudeLine("user", "x"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddDays(-1));

		Assert.Equal("stale_version", (await ReadAsync(older)).GetProperty("code").GetString());
	}

	[Fact]
	public async Task A_deleted_path_stays_deleted_when_its_session_arrives()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		await PostAsync(client, path, RenderedPart(id, "holds a credential", 0));
		await client.DeleteAsync($"/v1/documents?collection=c&path={path}");

		JsonElement session = await ReadAsync(await PostSessionAsync(client, ClaudeLine("user", "holds a credential"), projectDirectory: OwnFolder, sessionId: id, updated: DateTimeOffset.UtcNow.AddMinutes(30)));

		Assert.Equal([SessionPartState.Deleted], States(session));
		Assert.Empty(corpus.Files);

		// Still a tombstone, so the next upload gets the same answer.
		JsonElement resumed = await ReadAsync(await PostSessionAsync(client, ClaudeLine("user", "holds a credential") + ClaudeLine("assistant", "more", 1), projectDirectory: OwnFolder, sessionId: id, updated: DateTimeOffset.UtcNow.AddMinutes(40)));
		Assert.Equal([SessionPartState.Deleted], States(resumed));
		Assert.Empty(corpus.Files);

		// And so a rendered copy sent again stays out too.
		HttpResponseMessage rendered = await PostAsync(client, path, RenderedPart(id, "holds a credential", 0), updated: DateTimeOffset.UtcNow.AddMinutes(50));
		Assert.Equal("stale_version", (await ReadAsync(rendered)).GetProperty("code").GetString());
		Assert.Empty(corpus.Files);
	}

	[Fact]
	public async Task With_its_first_part_deleted_the_other_parts_still_go_so_nothing_stays_beside_the_session()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string first = $"2026-01-15_{id[..8]}.md", second = $"2026-01-15_{id[..8]}_part-002.md", third = $"2026-01-15_{id[..8]}_part-003.md";
		await PostAsync(client, first, RenderedPart(id, "one", 0));
		await PostAsync(client, second, RenderedPart(id, "two", 0));
		await PostAsync(client, third, RenderedPart(id, "three", 0));
		await client.DeleteAsync($"/v1/documents?collection=c&path={first}");

		JsonElement session = await ReadAsync(await PostSessionAsync(client, ClaudeLine("user", "one"), projectDirectory: OwnFolder, sessionId: id, updated: DateTimeOffset.UtcNow.AddMinutes(30)));

		Assert.Equal([SessionPartState.Deleted], States(session));
		Assert.False(session.GetProperty("complete").GetBoolean());
		Assert.Empty(corpus.Files);
	}

	[Fact]
	public async Task A_document_that_does_not_say_how_many_it_holds_is_not_replaced_on_a_guess()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		await PostAsync(client, path, $"---\nsession_id: \"{id}\"\n---\n\nmigrated\n");

		HttpResponseMessage refused = await PostSessionAsync(client, ClaudeLine("user", "x"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));
		HttpResponseMessage allowed = await PostSessionAsync(client, ClaudeLine("user", "x"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1), allowShrink: true);

		Assert.Equal("session_shrank", (await ReadAsync(refused)).GetProperty("code").GetString());
		Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
		Assert.DoesNotContain("migrated", corpus.Files[path], StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_path_with_a_backslash_is_refused_so_one_file_has_one_name()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string folder = Guid.NewGuid().ToString("N");

		HttpResponseMessage slash = await PostAsync(client, $"{folder}/b.md", "first\n");
		HttpResponseMessage backslash = await PostAsync(client, $"{folder}\\b.md", "second\n");
		HttpResponseMessage delete = await client.DeleteAsync($"/v1/documents?collection=c&path={Uri.EscapeDataString($"{folder}\\b.md")}");

		Assert.Equal(HttpStatusCode.Accepted, slash.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, backslash.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
		Assert.Equal("first\n", corpus.Files[$"{folder}/b.md"]);
	}

	[Fact]
	public async Task A_row_nothing_landed_for_has_no_date()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string path = UniquePath();
		corpus.OnWrite = static _ => Busy();
		await PostAsync(client, path, "body\n");
		corpus.OnWrite = static _ => null;

		JsonElement status = await ReadAsync(await client.GetAsync($"/v1/ingest/rendered?collection=c&path={Uri.EscapeDataString(path)}"));

		Assert.Equal(IngestStateName.Partial, status.GetProperty("state").GetString());
		Assert.Equal(JsonValueKind.Null, status.GetProperty("updated").ValueKind);
	}

	[Fact]
	public async Task A_document_that_only_passes_the_limit_once_redacted_is_refused_without_telling_it_to_split()
	{
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Ingest:MaxDocumentBytes", "65536"));
		FakeCorpus corpus = new(factory.Sidecar);
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
		StringBuilder text = new();
		for (int i = 0; text.Length < 65000; i++)
		{
			text.Append("AK").Append("IA").Append(string.Concat(Enumerable.Range(0, 16).Select(n => Alphabet[(i >> (n % 12)) % Alphabet.Length ^ 0]))).Append(' ');
		}

		HttpResponseMessage response = await PostAsync(AuthApiFactory.Authorized(app.CreateClient(), key), UniquePath(), text.ToString());
		JsonElement refused = await ReadAsync(response);

		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		Assert.Equal("document_too_large", refused.GetProperty("code").GetString());
		Assert.DoesNotContain("Split", refused.GetProperty("detail").GetString(), StringComparison.Ordinal);
		Assert.Empty(corpus.Calls);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task A_document_naming_no_session_or_another_is_not_taken_over(bool namesAnother)
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		string text = namesAnother ? RenderedPart(Guid.NewGuid().ToString(), "someone else", 0) : "---\ntitle: \"none\"\n---\n\nsomeone else\n";
		await PostAsync(client, path, text);

		HttpResponseMessage session = await PostSessionAsync(client, ClaudeLine("user", "x"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal("path_owned", (await ReadAsync(session)).GetProperty("code").GetString());
		Assert.Equal(text, corpus.Files[path]);
	}

	[Fact]
	public async Task After_a_takeover_the_session_owns_the_path_and_a_rendered_copy_is_refused()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await factory.ClientWithScopesAsync([MemoriaScopes.Ingest, MemoriaScopes.Read], ["c"]);
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		await PostAsync(client, path, RenderedPart(id, "oldword", 0));
		await PostSessionAsync(client, ClaudeLine("user", "newword"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));

		HttpResponseMessage again = await PostAsync(client, path, RenderedPart(id, "oldword", 0), updated: Stamp.AddHours(2));
		JsonElement status = await ReadAsync(await client.GetAsync($"/v1/ingest/rendered?collection=c&path={path}"));
		HttpResponseMessage resumed = await PostSessionAsync(client, ClaudeLine("user", "newword") + ClaudeLine("assistant", "resumed", 1), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(3));

		Assert.Equal("path_owned", (await ReadAsync(again)).GetProperty("code").GetString());
		Assert.Equal(IngestStateName.TakenOver, status.GetProperty("state").GetString());
		Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
		Assert.Contains("resumed", corpus.Files[path], StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_part_deleted_after_a_takeover_stays_gone_when_the_rendered_copy_is_sent_again()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await factory.ClientWithScopesAsync([MemoriaScopes.Ingest, MemoriaScopes.Read], ["c"]);
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		await PostAsync(client, path, RenderedPart(id, "oldword", 0));
		await PostSessionAsync(client, ClaudeLine("user", "newword"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));
		await client.DeleteAsync($"/v1/documents?collection=c&path={path}");

		HttpResponseMessage again = await PostAsync(client, path, RenderedPart(id, "oldword", 0), updated: DateTimeOffset.UtcNow.AddMinutes(30));

		Assert.Equal("path_owned", (await ReadAsync(again)).GetProperty("code").GetString());
		Assert.Empty(corpus.Files);
		JsonElement status = await ReadAsync(await client.GetAsync($"/v1/ingest/rendered?collection=c&path={path}"));
		Assert.Equal(IngestStateName.TakenOver, status.GetProperty("state").GetString());
	}

	[Fact]
	public async Task A_name_the_session_stopped_writing_stays_gone_when_the_rendered_copy_is_sent_again()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string other = $"2026-01-15_{id[..8]}_part-009.md";
		await PostAsync(client, other, RenderedPart(id, "oldword", 0));
		await PostSessionAsync(client, ClaudeLine("user", "newword"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));

		HttpResponseMessage again = await PostAsync(client, other, RenderedPart(id, "oldword", 0), updated: Stamp.AddHours(2));

		Assert.Equal("path_owned", (await ReadAsync(again)).GetProperty("code").GetString());
		Assert.False(corpus.Files.ContainsKey(other));
	}

	[Fact]
	public async Task A_rendered_copy_under_a_new_name_lands_beside_the_session_and_its_next_upload_removes_it()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string fresh = $"2026-01-15_{id[..8]}_part-007.md";
		await PostSessionAsync(client, ClaudeLine("user", "newword"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));

		HttpResponseMessage landed = await PostAsync(client, fresh, RenderedPart(id, "freshword", 0));

		Assert.Equal(HttpStatusCode.Accepted, landed.StatusCode);
		Assert.Contains("freshword", corpus.Files[fresh], StringComparison.Ordinal);
		HttpResponseMessage next = await PostSessionAsync(client, ClaudeLine("user", "newword") + ClaudeLine("assistant", "more", 1), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(2));
		Assert.Equal(HttpStatusCode.Accepted, next.StatusCode);
		Assert.False(corpus.Files.ContainsKey(fresh));
	}

	[Fact]
	public async Task A_copy_in_another_folder_is_left_alone_and_does_not_limit_the_session()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string root = $"2026-01-15_{id[..8]}.md", elsewhere = $"old-export/2026-01-15_{id[..8]}.md";
		await PostAsync(client, root, RenderedPart(id, "rootword", 0));
		string fork = RenderedPart(id, "onlyinthefork", 50);
		await PostAsync(client, elsewhere, fork);

		HttpResponseMessage session = await PostSessionAsync(client, ClaudeLine("user", "newword"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal(HttpStatusCode.Accepted, session.StatusCode);
		Assert.Contains("newword", corpus.Files[root], StringComparison.Ordinal);
		Assert.Equal(fork, corpus.Files[elsewhere]);
		Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, elsewhere, fork)).StatusCode);
	}

	[Fact]
	public async Task Codex_chunks_in_the_same_folder_are_taken_over_and_those_in_another_are_not()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string same = $"2026-01-15_codex_{id}_chunk-00009.md", elsewhere = $"old-export/2026-01-15_codex_{id}_chunk-00009.md";
		await PostAsync(client, same, RenderedPart(id, "oldword", 0));
		await PostAsync(client, elsewhere, RenderedPart(id, "forkword", 0));
		string payload = JsonSerializer.Serialize(new { type = "message", role = "user", content = new object[] { new { type = "input_text", text = "newword" } } });
		string transcript = "{\"timestamp\":\"2026-01-15T10:00:00.000Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"cwd\":\"/srv/demo\"}}\n{\"timestamp\":\"2026-01-15T10:00:01.000Z\",\"type\":\"response_item\",\"payload\":" + payload + "}\n";

		HttpResponseMessage session = await PostSessionAsync(client, transcript, format: "codex", sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal(HttpStatusCode.Accepted, session.StatusCode);
		Assert.False(corpus.Files.ContainsKey(same));
		Assert.Contains("forkword", corpus.Files[elsewhere], StringComparison.Ordinal);
		Assert.Contains(corpus.Files, pair => pair.Key.EndsWith("_chunk-00001.md", StringComparison.Ordinal) && !pair.Key.Contains('/', StringComparison.Ordinal) && pair.Value.Contains("newword", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_refused_cut_copy_warns_by_digest_and_tells_the_person_what_to_send()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		await PostAsync(client, $"2026-01-15_{id[..8]}.md", "---\nsession_id: \"" + id + "\"\n---\n\nsays no count\n");

		string body = await (await PostSessionAsync(client, ClaudeLine("user", "cutcopytext"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1))).Content.ReadAsStringAsync();

		Assert.Contains("session_shrank", body, StringComparison.Ordinal);
		Assert.Contains("does not say how many", body, StringComparison.Ordinal);
		Assert.Contains("allowShrink=true", body, StringComparison.Ordinal);
		MemoriaApiFactory.CapturedLog[] warnings = [.. factory.Logs.Where(static log => log.EventId.Id == 3311)];
		Assert.Contains(warnings, static log => log.Level == LogLevel.Warning && log.Message.Contains("collection c ", StringComparison.Ordinal) && log.Message.Contains("Shrunk", StringComparison.Ordinal));
		Assert.DoesNotContain(warnings, log => log.Message.Contains(id, StringComparison.Ordinal) || log.Message.Contains(id[..8], StringComparison.Ordinal) || log.Message.Contains("cutcopytext", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_shorter_rendered_copy_warns_and_says_what_works_on_this_route()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		await PostAsync(client, path, RenderedPart(id, "longword", 5));

		HttpResponseMessage shorter = await PostAsync(client, path, RenderedPart(id, "shortword", 1));

		string body = await shorter.Content.ReadAsStringAsync();
		Assert.Contains("session_shrank", body, StringComparison.Ordinal);
		Assert.DoesNotContain("allowShrink", body, StringComparison.Ordinal);
		Assert.Contains(factory.Logs, static log => log.EventId.Id == 3312 && log.Level == LogLevel.Warning);
		Assert.Contains("longword", corpus.Files[path], StringComparison.Ordinal);

		// What the answer says does work.
		await client.DeleteAsync($"/v1/documents?collection=c&path={path}");
		Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, path, RenderedPart(id, "shortword", 1), updated: DateTimeOffset.UtcNow.AddMinutes(30))).StatusCode);
	}

	[Fact]
	public async Task A_session_resumed_from_another_directory_takes_nothing_over_and_stays_on_its_own_name()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string original = $"2026-01-15_{id[..8]}.md";
		string export = RenderedPart(id, "onlyintheoriginal", 50);
		await PostAsync(client, original, export);

		JsonElement first = await ReadAsync(await PostSessionAsync(client, ClaudeLine("user", "forkword"), projectDirectory: "-other-place", sessionId: id, updated: Stamp.AddHours(1)));
		string own = Assert.Single(first.GetProperty("parts").EnumerateArray()).GetProperty("path").GetString()!;
		JsonElement later = await ReadAsync(await PostSessionAsync(client, ClaudeLine("user", "forkword") + ClaudeLine("assistant", "forkmore", 1), projectDirectory: "-other-place", sessionId: id, updated: Stamp.AddHours(2)));

		Assert.Equal(export, corpus.Files[original]);
		Assert.NotEqual(original, own);
		Assert.Equal(own, Assert.Single(later.GetProperty("parts").EnumerateArray()).GetProperty("path").GetString());
		Assert.Contains("forkmore", corpus.Files[own], StringComparison.Ordinal);
		Assert.Equal(2, corpus.Files.Count);

		// The copy in its own folder still takes the export over, and leaves the other copy alone.
		HttpResponseMessage continuing = await PostSessionAsync(client, string.Concat(Enumerable.Range(0, 51).Select(static n => ClaudeLine("user", $"m{n}", n))), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(3));
		Assert.Equal(HttpStatusCode.Accepted, continuing.StatusCode);
		Assert.DoesNotContain("onlyintheoriginal", corpus.Files[original], StringComparison.Ordinal);
		Assert.Contains("forkmore", corpus.Files[own], StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(null, "/srv/demo")]
	[InlineData("-srv-demo", "")]
	public async Task A_session_that_cannot_show_which_copy_it_is_replaces_no_export(string? projectDirectory, string cwd)
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string same = $"2026-01-15_{id[..8]}.md", elsewhere = $"2026-01-20_{id[..8]}.md";
		string atSame = RenderedPart(id, "samename", 0), atOther = RenderedPart(id, "othername", 0);
		await PostAsync(client, same, atSame);
		await PostAsync(client, elsewhere, atOther);

		HttpResponseMessage refused = await PostSessionAsync(client, ClaudeLine("user", "x", cwd: cwd), projectDirectory: projectDirectory, sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal("path_owned", (await ReadAsync(refused)).GetProperty("code").GetString());
		Assert.Contains("projectDirectory", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
		Assert.Equal(atSame, corpus.Files[same]);

		// Under a name no export holds, it lands beside them.
		HttpResponseMessage beside = await PostSessionAsync(client, ClaudeLine("user", "x", day: "2026-01-16", cwd: cwd), projectDirectory: projectDirectory, sessionId: id, updated: Stamp.AddHours(1));
		Assert.Equal(HttpStatusCode.Accepted, beside.StatusCode);
		Assert.Equal(atOther, corpus.Files[elsewhere]);
		Assert.Equal(atSame, corpus.Files[same]);
		Assert.Equal(3, corpus.Files.Count);
	}

	[Fact]
	public async Task A_session_that_cannot_show_which_copy_it_is_still_cannot_bring_back_a_deleted_path()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		await PostAsync(client, path, RenderedPart(id, "holds a credential", 0));
		await client.DeleteAsync($"/v1/documents?collection=c&path={path}");

		JsonElement session = await ReadAsync(await PostSessionAsync(client, ClaudeLine("user", "holds a credential"), sessionId: id, updated: DateTimeOffset.UtcNow.AddMinutes(30)));

		Assert.Equal([SessionPartState.Deleted], States(session));
		Assert.Empty(corpus.Files);
	}

	[Fact]
	public async Task A_rendered_write_the_sidecar_refused_holds_nothing_and_does_not_block_the_takeover()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		corpus.OnWrite = static _ => TooLarge();

		HttpResponseMessage refused = await PostAsync(client, path, RenderedPart(id, "never stored", 0));
		IngestRecord? row = await factory.Services.GetRequiredService<IIngestLedger>().FindAsync(DocumentIngestor.IdentityOf("c", path), default);
		Assert.NotNull(row);
		Assert.Null(row.MessageCount);
		Assert.Empty(row.Paths);
		corpus.OnWrite = static _ => null;
		HttpResponseMessage session = await PostSessionAsync(client, ClaudeLine("user", "newword"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
		Assert.Equal(HttpStatusCode.Accepted, session.StatusCode);
		Assert.Contains("newword", corpus.Files[path], StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_refused_resend_of_a_stored_export_keeps_its_path_and_what_it_holds()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		string export = RenderedPart(id, "stored first", 3);
		Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, path, export)).StatusCode);
		corpus.OnWrite = static _ => TooLarge();

		HttpResponseMessage refused = await PostAsync(client, path, RenderedPart(id, "never stored", 3), updated: Stamp.AddMinutes(1));
		corpus.OnWrite = static _ => null;
		IngestRecord? row = await factory.Services.GetRequiredService<IIngestLedger>().FindAsync(DocumentIngestor.IdentityOf("c", path), default);
		HttpResponseMessage cut = await PostSessionAsync(client, ClaudeLine("user", "only one"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
		Assert.Equal([path], row!.Paths);
		Assert.Equal(4, row.MessageCount);
		Assert.Equal("session_shrank", (await ReadAsync(cut)).GetProperty("code").GetString());
		Assert.Equal(export, corpus.Files[path]);
	}

	[Fact]
	public async Task A_rendered_write_whose_answer_was_lost_holds_the_takeover_until_it_is_sent_again()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		string export = RenderedPart(id, "landed unseen", 3);
		// The file lands, and the answer does not come back, so what it holds is unknown.
		corpus.OnWrite = written =>
		{
			corpus.Files[written] = export;
			return Busy();
		};

		HttpResponseMessage lost = await PostAsync(client, path, export);
		corpus.OnWrite = static _ => null;
		HttpResponseMessage held = await PostSessionAsync(client, ClaudeLine("user", "newword"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal(HttpStatusCode.ServiceUnavailable, lost.StatusCode);
		Assert.Equal("session_shrank", (await ReadAsync(held)).GetProperty("code").GetString());
		Assert.Equal(export, corpus.Files[path]);

		// Sending the export again records what it holds, and a session that reaches that far takes it over.
		Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, path, export)).StatusCode);
		HttpResponseMessage whole = await PostSessionAsync(client, string.Concat(Enumerable.Range(0, 4).Select(static n => ClaudeLine(n % 2 == 0 ? "user" : "assistant", $"m{n}", n))), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));
		Assert.Equal(HttpStatusCode.Accepted, whole.StatusCode);
		Assert.Contains("m3", corpus.Files[path], StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_session_waits_for_a_rendered_write_still_running()
	{
		_ = new FakeCorpus(factory.Sidecar);
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		IngestClaimRequest running = new(DocumentIngestor.IdentityOf("c", path), "c", Stamp, DateTimeOffset.UtcNow, [path], Lookup: IngestIdentity.ForRenderedSession("c", id, path));
		await factory.Services.GetRequiredService<IIngestLedger>().ClaimAsync(running, default);

		HttpResponseMessage session = await PostSessionAsync(await WriterAsync(), ClaudeLine("user", "x"), projectDirectory: OwnFolder, sessionId: id, updated: Stamp.AddHours(1));

		Assert.Equal("ingest_in_progress", (await ReadAsync(session)).GetProperty("code").GetString());
	}

	[Fact]
	public void A_session_and_how_far_a_document_reaches_are_read_from_its_frontmatter_alone()
	{
		Assert.Equal("abc", RenderedDocument.SessionIdOf("---\nsession_id: \"abc\"\n---\n\nbody\n"));
		Assert.Equal("abc", RenderedDocument.SessionIdOf("﻿---\r\nsession_id: abc\r\n---\r\n"));
		Assert.Null(RenderedDocument.SessionIdOf("---\ntitle: x\n---\n\nsession_id: \"not this\"\n"));
		Assert.Null(RenderedDocument.SessionIdOf("session_id: \"no block\"\n"));
		Assert.Null(RenderedDocument.SessionIdOf("---\nsession_id: \"never closes\"\n"));
		Assert.Equal("abc", RenderedDocument.SessionIdOf($"---\nfiles_touched: [\"{new string('x', 9000)}\"]\nsession_id: \"abc\"\n---\n"));
		Assert.Equal(6, RenderedDocument.MessagesThroughOf("---\nmessage_end: 5\n---\n"));
		Assert.Equal(6, RenderedDocument.MessagesThroughOf("---\nmessage_start: 4\nmessage_end: 5\nmessage_count: 2\n---\n"));
		Assert.Equal(7, RenderedDocument.MessagesThroughOf("---\nchunk_index: 2\nmessage_start: 5\nmessage_count: 2\n---\n"));
		Assert.Equal(2, RenderedDocument.MessagesThroughOf("---\nmessage_count: 2\n---\n"));
		Assert.Null(RenderedDocument.MessagesThroughOf("---\ntitle: x\n---\n"));
	}

	[Fact]
	public async Task A_date_too_far_ahead_is_refused_saying_how_far()
	{
		_ = new FakeCorpus(factory.Sidecar);

		HttpResponseMessage response = await PostAsync(await WriterAsync(), UniquePath(), "body\n", updated: DateTimeOffset.UtcNow.AddHours(2));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("an hour ahead", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}
}
