using System.Net;
using System.Text;
using System.Text.Json;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Memoria.Api.Tests.IngestKit;

namespace Memoria.Api.Tests;

/// <summary>What a session upload says about each of its parts, and what it leaves recorded, when parts land, fail, or are deleted under it.</summary>
public sealed class SessionPartOutcomeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private const string DeletedMessage = "This part was deleted, and stays deleted however often the session is sent.";

	private Task<HttpClient> WriterAsync()
	{
		return factory.ClientWithScopesAsync([MemoriaScopes.Ingest], ["c"]);
	}

	private static string NewSession()
	{
		return Guid.NewGuid().ToString();
	}

	private static string PartPath(string id, int part)
	{
		return part == 1 ? $"2026-01-15_{id[..8]}.md" : $"2026-01-15_{id[..8]}_part-{part:D3}.md";
	}

	private static JsonElement Part(JsonElement result, int index)
	{
		return result.GetProperty("parts")[index];
	}

	private static int? RetryAfter(JsonElement part)
	{
		return part.TryGetProperty("retryAfterSeconds", out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
	}

	private static async Task<JsonElement> StatusAsync(HttpClient client, string id, string format = "claude")
	{
		return await ReadAsync(await client.GetAsync($"/v1/ingest/session?collection=c&format={format}&sessionId={id}"));
	}

	[Fact]
	public async Task Parts_after_a_busy_backend_are_not_attempted_and_say_a_retry_will_send_them()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string id = NewSession();
		corpus.OnWrite = path => path == PartPath(id, 1) ? Busy() : null;

		HttpResponseMessage response = await PostSessionAsync(await WriterAsync(), Transcript(3), sessionId: id);
		JsonElement result = await ReadAsync(response);

		Assert.Equal((HttpStatusCode)207, response.StatusCode);
		Assert.Equal([SessionPartState.Failed, SessionPartState.NotAttempted, SessionPartState.NotAttempted], States(result));
		Assert.True(Part(result, 1).GetProperty("retryable").GetBoolean());
		Assert.True(Part(result, 2).GetProperty("retryable").GetBoolean());
		Assert.True(result.GetProperty("retryable").GetBoolean());
	}

	[Fact]
	public async Task The_job_reported_is_the_last_parts_and_nothing_is_reindexed_when_nothing_was_superseded()
	{
		_ = new FakeCorpus(factory.Sidecar);

		HttpResponseMessage response = await PostSessionAsync(await WriterAsync(), Transcript(2), sessionId: NewSession());
		JsonElement result = await ReadAsync(response);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Equal("job-2", result.GetProperty("jobId").GetString());
		Assert.Equal(ReindexState.NotNeeded, result.GetProperty("reindexState").GetString());
		Assert.DoesNotContain(factory.Sidecar.Requests, static request => request.Uri.AbsolutePath == "/reindex");
	}

	[Fact]
	public async Task A_part_refused_on_its_merits_keeps_the_earlier_job_and_is_not_told_to_wait()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string id = NewSession();
		corpus.OnWrite = path => path == PartPath(id, 2) ? TooLarge() : null;

		JsonElement result = await ReadAsync(await PostSessionAsync(await WriterAsync(), Transcript(2), sessionId: id));

		Assert.Equal([SessionPartState.Written, SessionPartState.Failed], States(result));
		Assert.Equal("job-1", result.GetProperty("jobId").GetString());
		Assert.False(Part(result, 1).GetProperty("retryable").GetBoolean());
		Assert.Null(RetryAfter(Part(result, 1)));
	}

	[Fact]
	public async Task A_part_the_backend_was_too_busy_for_is_told_when_to_come_back()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		string id = NewSession();
		corpus.OnWrite = path => path == PartPath(id, 1) ? Busy(7) : null;

		JsonElement result = await ReadAsync(await PostSessionAsync(await WriterAsync(), Transcript(1), sessionId: id));

		Assert.True(Part(result, 0).GetProperty("retryable").GetBoolean());
		Assert.Equal(7, RetryAfter(Part(result, 0)));
	}

	[Fact]
	public async Task A_part_deleted_during_the_upload_is_reported_deleted_and_the_parts_after_it_not_attempted()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		HttpClient deleter = await WriterAsync();
		string id = NewSession();
		corpus.OnWrite = path =>
		{
			if (path == PartPath(id, 2))
			{
				Assert.Equal(HttpStatusCode.Accepted, deleter.DeleteAsync($"/v1/documents?collection=c&path={PartPath(id, 1)}").GetAwaiter().GetResult().StatusCode);
			}

			return null;
		};

		HttpResponseMessage response = await PostSessionAsync(client, Transcript(3), sessionId: id);
		JsonElement result = await ReadAsync(response);

		Assert.Equal((HttpStatusCode)207, response.StatusCode);
		Assert.Equal([SessionPartState.Deleted, SessionPartState.Written, SessionPartState.NotAttempted], States(result));
		Assert.Equal("part_deleted", Part(result, 0).GetProperty("code").GetString());
		Assert.Equal(DeletedMessage, Part(result, 0).GetProperty("message").GetString());
		Assert.True(Part(result, 2).GetProperty("retryable").GetBoolean());
	}

	[Fact]
	public async Task A_part_deleted_before_the_session_is_sent_again_says_it_stays_deleted()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		await PostSessionAsync(client, Transcript(2), sessionId: id);
		await client.DeleteAsync($"/v1/documents?collection=c&path={PartPath(id, 2)}");

		JsonElement result = await ReadAsync(await PostSessionAsync(client, Transcript(2), sessionId: id, updated: Stamp.AddHours(1)));

		Assert.Equal([SessionPartState.Written, SessionPartState.Deleted], States(result));
		Assert.Equal("part_deleted", Part(result, 1).GetProperty("code").GetString());
		Assert.Equal(DeletedMessage, Part(result, 1).GetProperty("message").GetString());
		Assert.False(corpus.Files.ContainsKey(PartPath(id, 2)));
	}

	[Fact]
	public async Task A_restored_session_records_its_parts_in_part_order()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();
		await PostSessionAsync(client, Transcript(2), sessionId: id);
		await client.DeleteAsync($"/v1/documents?collection=c&path={PartPath(id, 1)}");
		HttpClient admin = await factory.ClientWithScopesAsync([MemoriaScopes.Ingest, MemoriaScopes.Admin], allCollections: true);

		HttpResponseMessage restored = await PostSessionAsync(admin, Transcript(2), sessionId: id, updated: Stamp.AddHours(1), restoreDeleted: true);
		JsonElement status = await StatusAsync(client, id);

		Assert.Equal(HttpStatusCode.Accepted, restored.StatusCode);
		Assert.Equal(IngestStateName.Complete, status.GetProperty("state").GetString());
		Assert.Equal([PartPath(id, 1), PartPath(id, 2)], status.GetProperty("paths").EnumerateArray().Select(static path => path.GetString()!).ToArray());
	}

	[Fact]
	public async Task Each_format_is_named_in_the_answer_as_the_route_names_it()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string codexId = NewSession();
		string codex = "{\"timestamp\":\"2026-01-15T10:00:00.000Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + codexId + "\",\"cwd\":\"/srv/demo\"}}\n"
			+ """{"timestamp":"2026-01-15T10:00:01.000Z","type":"response_item","payload":""" + JsonSerializer.Serialize(new { type = "message", role = "user", content = new object[] { new { type = "input_text", text = "hello" } } }) + "}\n";

		JsonElement claude = await ReadAsync(await PostSessionAsync(client, ClaudeLine("user", "hello"), sessionId: NewSession()));
		JsonElement fromCodex = await ReadAsync(await PostSessionAsync(client, codex, format: "codex", sessionId: codexId));

		Assert.Equal("claude", claude.GetProperty("format").GetString());
		Assert.Equal("codex", fromCodex.GetProperty("format").GetString());
	}

	[Fact]
	public async Task The_status_of_a_session_with_two_copies_gives_the_newest_version()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();

		Assert.Equal(HttpStatusCode.Accepted, (await PostSessionAsync(client, Transcript(1), sessionId: id, projectDirectory: "-srv-demo")).StatusCode);
		DateTimeOffset first = (await StatusAsync(client, id)).GetProperty("updated").GetDateTimeOffset();
		Assert.Equal(HttpStatusCode.Accepted, (await PostSessionAsync(client, Transcript(1), sessionId: id, projectDirectory: "-srv-elsewhere", updated: Stamp.AddHours(1))).StatusCode);
		JsonElement both = await StatusAsync(client, id);

		Assert.Equal(Stamp, first);
		Assert.Equal(2, both.GetProperty("paths").GetArrayLength());
		Assert.Equal(Stamp.AddHours(1), both.GetProperty("updated").GetDateTimeOffset());
	}

	[Fact]
	public async Task A_blank_project_directory_is_the_same_as_none()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = NewSession();

		JsonElement without = await ReadAsync(await PostSessionAsync(client, Transcript(1), sessionId: id));
		HttpResponseMessage blank = await PostSessionAsync(client, Transcript(1), sessionId: id, projectDirectory: "  ", updated: Stamp.AddHours(1));

		Assert.Equal(HttpStatusCode.Accepted, blank.StatusCode);
		Assert.Equal(Part(without, 0).GetProperty("path").GetString(), Part(await ReadAsync(blank), 0).GetProperty("path").GetString());
		Assert.Equal(PartPath(id, 1), Part(without, 0).GetProperty("path").GetString());
	}

	[Fact]
	public async Task A_low_document_limit_brings_the_part_size_down_with_it()
	{
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Ingest:MaxDocumentBytes", "65536"));
		_ = new FakeCorpus(factory.Sidecar);
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		HttpClient client = AuthApiFactory.Authorized(app.CreateClient(), key);
		string text = string.Join(' ', Enumerable.Repeat("abcdefgh", 1000));
		StringBuilder transcript = new();
		for (int i = 0; i < 7; i++)
		{
			transcript.Append(ClaudeLine(i % 2 == 0 ? "user" : "assistant", text, i));
		}

		HttpResponseMessage response = await PostSessionAsync(client, transcript.ToString(), sessionId: NewSession());
		JsonElement result = await ReadAsync(response);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Equal(7, result.GetProperty("parts").GetArrayLength());
	}

	/// <summary>A session's name is stored as a digest of fixed inputs. Changing them would lose every session recorded before, so they are pinned.</summary>
	[Fact]
	public async Task A_session_is_found_under_its_stable_name()
	{
		_ = new FakeCorpus(factory.Sidecar);
		string id = NewSession();

		await PostSessionAsync(await WriterAsync(), Transcript(1), sessionId: id);

		using IServiceScope scope = factory.Services.CreateScope();
		IReadOnlyList<IngestRecord> records = await scope.ServiceProvider.GetRequiredService<IIngestLedger>().FindByLookupAsync(IngestIdentity.For("session-name", "c", "ClaudeCode", id), CancellationToken.None);
		Assert.Equal(IngestStateName.Complete, Assert.Single(records).State);
	}
}
