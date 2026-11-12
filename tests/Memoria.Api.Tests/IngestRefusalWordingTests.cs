using System.Net;
using System.Text;
using System.Text.Json;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Core.Contracts;
using Memoria.Core.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static Memoria.Api.Tests.IngestKit;

namespace Memoria.Api.Tests;

/// <summary>What a caller is told, word for word, when an ingest is turned away, so the answer says what happened and what to do next.</summary>
public sealed class IngestRefusalWordingTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private Task<HttpClient> WriterAsync()
	{
		return factory.ClientWithScopesAsync([MemoriaScopes.Ingest], ["c"]);
	}

	private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code, string detail)
	{
		JsonElement body = await ReadAsync(response);
		Assert.Equal(status, response.StatusCode);
		Assert.Equal(code, body.GetProperty("code").GetString());
		Assert.Equal(detail, body.GetProperty("detail").GetString());
	}

	private static Task<HttpResponseMessage> PostRenderedAsync(HttpClient client, string path, string text, DateTimeOffset updated)
	{
		string url = $"/v1/ingest/rendered?collection=c&path={Uri.EscapeDataString(path)}&updated={Uri.EscapeDataString(updated.ToString("O"))}";
		return client.PostAsync(url, new StringContent(text, Encoding.UTF8, "text/markdown"));
	}

	private static string RenderedPart(string sessionId, string word, int through)
	{
		return $"---\nsession_id: \"{sessionId}\"\nmessage_end: {through}\n---\n\n## User\n\n{word}\n";
	}

	[Fact]
	public async Task An_older_copy_is_told_a_newer_one_is_stored()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString("N");
		await PostDocumentAsync(client, sourceId: id, updated: Stamp);

		HttpResponseMessage older = await PostDocumentAsync(client, sourceId: id, updated: Stamp.AddHours(-1));

		await AssertRefusedAsync(older, HttpStatusCode.Conflict, "stale_version", "A newer version is already stored, or this was deleted after this version. Nothing was changed.");
	}

	[Fact]
	public async Task A_copy_sent_while_another_is_written_is_told_to_wait()
	{
		_ = new FakeCorpus(factory.Sidecar);
		string id = Guid.NewGuid().ToString("N");
		string path = new DocumentEnvelope("notes", id).StoragePath();
		await factory.Services.GetRequiredService<IIngestLedger>().ClaimAsync(new IngestClaimRequest(DocumentIngestor.IdentityOf("c", path), "c", Stamp, DateTimeOffset.UtcNow, [path]), default);

		HttpResponseMessage busy = await PostDocumentAsync(await WriterAsync(), sourceId: id);

		await AssertRefusedAsync(busy, HttpStatusCode.Conflict, "ingest_in_progress", "Another upload of this is running. Nothing was changed.");
		Assert.True(busy.Headers.Contains("Retry-After"));
	}

	[Fact]
	public async Task A_document_whose_path_belongs_to_another_is_told_so()
	{
		_ = new FakeCorpus(factory.Sidecar);
		IIngestLedger ledger = factory.Services.GetRequiredService<IIngestLedger>();
		string path = $"2024/{Guid.NewGuid():N}.md";
		IngestClaimRequest squat = new("squatter-" + Guid.NewGuid().ToString("N"), "c", Stamp, DateTimeOffset.UtcNow.AddHours(-1), [path]);
		IngestClaim held = await ledger.ClaimAsync(squat, default);
		await ledger.FinishAsync(squat.Identity, held.LeaseToken!, "c", IngestStateName.Complete, [path], squat);

		HttpResponseMessage refused = await PostRenderedAsync(await WriterAsync(), path, "# notes\n", Stamp);

		await AssertRefusedAsync(refused, HttpStatusCode.Conflict, "path_owned", "A file this would write belongs to another document or session. Nothing was changed.");
	}

	[Fact]
	public async Task A_rendered_copy_holding_fewer_messages_is_told_it_may_be_cut()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string path = $"2026-01-15_{id[..8]}.md";
		Assert.Equal(HttpStatusCode.Accepted, (await PostRenderedAsync(client, path, RenderedPart(id, "whole", 5), Stamp)).StatusCode);

		HttpResponseMessage cut = await PostRenderedAsync(client, path, RenderedPart(id, "cut", 1), Stamp.AddHours(1));

		await AssertRefusedAsync(cut, HttpStatusCode.Conflict, "session_shrank", "This document says it holds fewer messages than the copy stored at its path, so it may be a cut copy. Nothing was changed. If it really is shorter, delete the path and send it again dated after the delete.");
	}

	[Fact]
	public async Task A_session_that_lost_its_stored_start_is_told_how_to_send_it()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString();
		string a = ClaudeLine("user", "message A"), b = ClaudeLine("assistant", "message B", 1), c = ClaudeLine("user", "message C", 2), d = ClaudeLine("assistant", "message D", 3);
		await PostSessionAsync(client, a + b + c, sessionId: id);

		HttpResponseMessage damaged = await PostSessionAsync(client, a + "{corrupt\n" + c + d, sessionId: id, updated: Stamp.AddHours(1));

		await AssertRefusedAsync(damaged, HttpStatusCode.Conflict, "lost_content", "This upload's messages do not start with the ones stored, so storing it would replace text it does not have. Nothing was changed. Send a whole copy, or allowShrink=true if the history really did change.");
	}

	[Fact]
	public async Task A_write_a_delete_took_over_is_told_it_was_not_stored()
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

		await AssertRefusedAsync(response, HttpStatusCode.Conflict, "deleted_during_upload", "A delete took this over while it was being written. It was not stored.");
	}

	[Fact]
	public async Task A_delete_that_could_not_be_confirmed_says_repeating_it_is_safe()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await WriterAsync();
		string id = Guid.NewGuid().ToString("N");
		await PostDocumentAsync(client, sourceId: id);
		corpus.OnDelete = static _ => Busy();

		HttpResponseMessage response = await client.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");

		await AssertRefusedAsync(response, HttpStatusCode.ServiceUnavailable, "delete_unconfirmed", "This delete could not be confirmed, so the file may still be there. Repeat the request: it is safe to.");
	}
}

/// <summary>A body that arrives too slowly, and one whose caller hangs up before it ends.</summary>
public sealed class SlowUploadOutcomeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private static string SessionUrl()
	{
		return $"/v1/ingest/session?collection=c&format=claude&sessionId={Guid.NewGuid()}&updated=2026-01-15T12:00:00Z";
	}

	private static HttpRequestMessage Slow()
	{
		HttpRequestMessage request = new(HttpMethod.Post, SessionUrl()) { Content = new StringContent("", Encoding.UTF8, "application/x-ndjson") };
		request.Headers.Add(SlowBodies.Marker, "1");
		return request;
	}

	[Fact]
	public async Task A_body_too_slow_for_the_deadline_is_told_it_did_not_arrive_in_time()
	{
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder =>
		{
			builder.UseSetting("Memoria:Ingest:SessionUploadDeadline", "00:00:00.2");
			builder.ConfigureTestServices(static services => services.AddSingleton<IStartupFilter>(new SlowBodies()));
		});
		_ = new FakeCorpus(factory.Sidecar);
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);

		HttpResponseMessage response = await AuthApiFactory.Authorized(app.CreateClient(), key).SendAsync(Slow());

		JsonElement body = await ReadAsync(response);
		Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
		Assert.Equal("upload_too_slow", body.GetProperty("code").GetString());
		Assert.Equal("The request body did not arrive in time. Nothing was changed.", body.GetProperty("detail").GetString());
	}

	[Fact]
	public async Task A_caller_who_hangs_up_mid_body_is_not_recorded_as_too_slow()
	{
		SlowBodies slow = new();
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder =>
		{
			builder.UseSetting("Memoria:Ingest:SessionUploadDeadline", "00:05:00");
			builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(slow));
		});
		_ = new FakeCorpus(factory.Sidecar);
		(Memoria.Api.Auth.ApiKeyRecord record, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		using CancellationTokenSource hangUp = new();

		Task<HttpResponseMessage> sending = AuthApiFactory.Authorized(app.CreateClient(), key).SendAsync(Slow(), hangUp.Token);
		for (int i = 0; i < 500 && slow.Entered < 1; i++)
		{
			await Task.Delay(10);
		}

		Assert.Equal(1, slow.Entered);
		await hangUp.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(rows => rows.Any(row => row.KeyId == record.Id));
		AuthApiFactory.AuditRow row = Assert.Single(rows, row => row.KeyId == record.Id);
		Assert.NotEqual(StatusCodes.Status408RequestTimeout, row.Status);
	}
}

/// <summary>The startup warning about redaction, under the category an operator filters ingest logs by. Each test boots a host of its own, as the warning is written once per host.</summary>
public sealed class RedactionWarningCategoryTests
{
	[Fact]
	public async Task The_redaction_warning_is_logged_under_the_ingest_category()
	{
		await using AuthApiFactory own = new();
		await using WebApplicationFactory<Program> app = own.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Ingest:Collections:open:Redaction", "Disabled"));

		_ = app.CreateClient();

		MemoriaApiFactory.CapturedLog warning = Assert.Single(own.Logs, static log => log.EventId.Id == 3300);
		Assert.Equal("Memoria.Api.Ingest", warning.Category);
	}
}
