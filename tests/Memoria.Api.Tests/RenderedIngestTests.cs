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
using static Memoria.Api.Tests.IngestKit;

namespace Memoria.Api.Tests;

/// <summary>Moving an old corpus: already rendered documents, kept as they came except for redaction.</summary>
public sealed class RenderedIngestTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private Task<HttpClient> WriterAsync(params string[] collections)
	{
		return factory.ClientWithScopesAsync([MemoriaScopes.Ingest], collections.Length == 0 ? ["c"] : collections);
	}

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

	[Fact]
	public async Task A_date_too_far_ahead_is_refused_saying_how_far()
	{
		_ = new FakeCorpus(factory.Sidecar);

		HttpResponseMessage response = await PostAsync(await WriterAsync(), UniquePath(), "body\n", updated: DateTimeOffset.UtcNow.AddHours(2));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("an hour ahead", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}
}
