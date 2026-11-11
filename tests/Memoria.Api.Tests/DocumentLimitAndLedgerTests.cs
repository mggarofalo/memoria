using System.Net;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Memoria.Api.Tests.IngestKit;

namespace Memoria.Api.Tests;

/// <summary>Where a document's size limit falls, and what the ledger keeps when a write does not land.</summary>
public sealed class DocumentLimitAndLedgerTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private const int Limit = 65536;

	private static string UniqueId()
	{
		return Guid.NewGuid().ToString("N");
	}

	private async Task<(WebApplicationFactory<Program> App, HttpClient Client)> SmallLimitAsync()
	{
		WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Ingest:MaxDocumentBytes", Limit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		return (app, AuthApiFactory.Authorized(app.CreateClient(), key));
	}

	[Fact]
	public async Task A_body_over_the_limit_is_refused_as_the_body_and_one_exactly_at_it_as_the_rendered_document()
	{
		(WebApplicationFactory<Program> app, HttpClient client) = await SmallLimitAsync();
		await using (app)
		{
			FakeCorpus corpus = new(factory.Sidecar);

			HttpResponseMessage over = await PostDocumentAsync(client, sourceId: UniqueId(), body: new string('w', Limit + 1));
			HttpResponseMessage exact = await PostDocumentAsync(client, sourceId: UniqueId(), body: new string('w', Limit));

			Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
			Assert.Equal($"The body alone can be at most {Limit} bytes. Split it.", (await ReadAsync(over)).GetProperty("detail").GetString());
			Assert.Equal(HttpStatusCode.RequestEntityTooLarge, exact.StatusCode);
			Assert.Equal($"A document once rendered can be at most {Limit} bytes. Split it.", (await ReadAsync(exact)).GetProperty("detail").GetString());
			Assert.Empty(corpus.Calls);
		}
	}

	[Fact]
	public async Task A_rendered_document_that_grows_past_the_limit_when_redacted_is_told_the_limit()
	{
		(WebApplicationFactory<Program> app, HttpClient client) = await SmallLimitAsync();
		await using (app)
		{
			_ = new FakeCorpus(factory.Sidecar);
			const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
			System.Text.StringBuilder text = new();
			for (int i = 0; text.Length < 65000; i++)
			{
				text.Append("AK").Append("IA").Append(string.Concat(Enumerable.Range(0, 16).Select(n => Alphabet[(i >> (n % 12)) % Alphabet.Length]))).Append(' ');
			}

			HttpResponseMessage response = await client.PostAsync(
				$"/v1/ingest/rendered?collection=c&path={UniqueId()}.md&updated={Uri.EscapeDataString(Stamp.ToString("O"))}",
				new StringContent(text.ToString(), System.Text.Encoding.UTF8, "text/markdown"));

			Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
			Assert.Equal($"A document once redacted can be at most {Limit} bytes.", (await ReadAsync(response)).GetProperty("detail").GetString());
		}
	}

	[Fact]
	public async Task A_write_the_sidecar_failed_with_500_keeps_its_path_claimed_and_partial()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		corpus.OnWrite = static _ => FakeSidecar.Json(HttpStatusCode.InternalServerError, """{"error":"internal","message":"The write may have landed."}""");
		HttpClient client = await factory.ClientWithScopesAsync([MemoriaScopes.Ingest], ["c"]);

		HttpResponseMessage response = await PostDocumentAsync(client, sourceId: UniqueId());
		string path = Assert.Single(corpus.Calls)["write ".Length..];

		Assert.NotEqual(HttpStatusCode.Accepted, response.StatusCode);
		IngestRecord record = Assert.IsType<IngestRecord>(await FindAsync(path));
		Assert.Equal(IngestStateName.Partial, record.State);
		Assert.Equal([path], record.Paths);
	}

	[Fact]
	public async Task A_write_the_sidecar_refused_with_a_4xx_claims_nothing()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		corpus.OnWrite = static _ => TooLarge();
		HttpClient client = await factory.ClientWithScopesAsync([MemoriaScopes.Ingest], ["c"]);

		await PostDocumentAsync(client, sourceId: UniqueId());
		string path = Assert.Single(corpus.Calls)["write ".Length..];

		IngestRecord record = Assert.IsType<IngestRecord>(await FindAsync(path));
		Assert.Equal(IngestStateName.Partial, record.State);
		Assert.Empty(record.Paths);
	}

	[Fact]
	public async Task A_deleted_document_sent_again_with_another_title_is_new_content_and_is_stored()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient client = await factory.ClientWithScopesAsync([MemoriaScopes.Ingest], ["c"]);
		string id = UniqueId();
		await PostDocumentAsync(client, sourceId: id, body: "the same body", updated: Stamp, title: "first");
		await client.DeleteAsync($"/v1/documents?collection=c&source=notes&sourceId={id}");

		// Dated after the delete, which is what a tombstone is dated.
		HttpResponseMessage same = await PostDocumentAsync(client, sourceId: id, body: "the same body", updated: DateTimeOffset.UtcNow.AddMinutes(10), title: "first");
		HttpResponseMessage retitled = await PostDocumentAsync(client, sourceId: id, body: "the same body", updated: DateTimeOffset.UtcNow.AddMinutes(20), title: "second");

		Assert.NotEqual(HttpStatusCode.Accepted, same.StatusCode);
		Assert.Equal(HttpStatusCode.Accepted, retitled.StatusCode);
		Assert.Contains("second", Assert.Single(corpus.Files).Value, StringComparison.Ordinal);
	}

	private async Task<IngestRecord?> FindAsync(string path)
	{
		using IServiceScope scope = factory.Services.CreateScope();
		return await scope.ServiceProvider.GetRequiredService<IIngestLedger>().FindAsync(DocumentIngestor.IdentityOf("c", path), CancellationToken.None);
	}
}
