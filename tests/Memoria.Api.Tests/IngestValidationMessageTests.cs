using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Memoria.Api.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static Memoria.Api.Tests.IngestKit;

namespace Memoria.Api.Tests;

/// <summary>What a caller is told when an ingest request is malformed: the field, and the exact words, before anything is read or stored.</summary>
public sealed class IngestValidationMessageTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private Task<HttpClient> WriterAsync()
	{
		return factory.ClientWithScopesAsync([MemoriaScopes.Ingest], ["c"]);
	}

	private static async Task<string[]> ErrorsAsync(HttpResponseMessage response, string field)
	{
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		JsonElement errors = (await ReadAsync(response)).GetProperty("errors");
		return [.. errors.GetProperty(field).EnumerateArray().Select(static e => e.GetString()!)];
	}

	private static async Task<bool> HasErrorAsync(HttpResponseMessage response, string field)
	{
		JsonElement body = await ReadAsync(response);
		return body.TryGetProperty("errors", out JsonElement errors) && errors.TryGetProperty(field, out _);
	}

	private static string SessionStatusUrl(string sessionId = "s-1", string format = "claude", string? projectDirectory = null)
	{
		return $"/v1/ingest/session?collection=c&format={format}&sessionId={Uri.EscapeDataString(sessionId)}{(projectDirectory is null ? "" : "&projectDirectory=" + Uri.EscapeDataString(projectDirectory))}";
	}

	[Fact]
	public async Task A_document_body_that_is_not_json_says_so()
	{
		HttpResponseMessage response = await (await WriterAsync()).PostAsync("/v1/documents", new StringContent("{not json", Encoding.UTF8, "application/json"));

		Assert.Equal(["The body must be a JSON object."], await ErrorsAsync(response, "body"));
	}

	[Fact]
	public async Task A_document_with_no_body_no_date_and_no_collection_names_each_field()
	{
		HttpResponseMessage response = await (await WriterAsync()).PostAsJsonAsync("/v1/documents", new { source = "notes", sourceId = "one" });

		Assert.Equal(["A body is required. It may be empty."], await ErrorsAsync(response, "body"));
		response = await (await WriterAsync()).PostAsJsonAsync("/v1/documents", new { source = "notes", sourceId = "one" });
		Assert.Equal(["updated is required: it orders versions, so an older copy cannot replace a newer one."], await ErrorsAsync(response, "updated"));
		response = await (await WriterAsync()).PostAsJsonAsync("/v1/documents", new { source = "notes", sourceId = "one" });
		Assert.Equal(["collection is required."], await ErrorsAsync(response, "collection"));
	}

	[Fact]
	public async Task A_collection_that_looks_like_a_credential_is_refused_without_repeating_it()
	{
		string token = Token(11);

		HttpResponseMessage response = await PostDocumentAsync(await WriterAsync(), collection: token);
		string raw = await response.Content.ReadAsStringAsync();

		Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
		Assert.Equal(["collection looks like a credential, so it was not used or logged."], await ErrorsAsync(response, "collection"));
	}

	[Fact]
	public async Task A_delete_by_identity_with_neither_part_names_both()
	{
		HttpClient client = await WriterAsync();

		HttpResponseMessage response = await client.DeleteAsync("/v1/documents?collection=c");

		JsonElement errors = (await ReadAsync(response)).GetProperty("errors");
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal("source is required.", errors.GetProperty("source")[0].GetString());
		Assert.Equal("sourceId is required.", errors.GetProperty("sourceId")[0].GetString());
	}

	[Fact]
	public async Task A_rendered_document_with_no_collection_is_refused_before_anything_is_read()
	{
		_ = new FakeCorpus(factory.Sidecar);
		HttpClient client = await WriterAsync();

		HttpResponseMessage response = await client.PostAsync($"/v1/ingest/rendered?path=a.md&updated={Uri.EscapeDataString(Stamp.ToString("O"))}", new StringContent("body\n", Encoding.UTF8, "text/markdown"));

		Assert.Equal(["collection is required."], await ErrorsAsync(response, "collection"));
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task The_rendered_status_of_no_path_is_a_validation_problem()
	{
		HttpResponseMessage response = await (await WriterAsync()).GetAsync("/v1/ingest/rendered?collection=c");

		Assert.Equal(["path is required: it identifies the document."], await ErrorsAsync(response, "path"));
	}

	[Theory]
	[InlineData("../outside.md")]
	[InlineData(".git/config")]
	[InlineData("/rooted.md")]
	public async Task A_path_outside_the_collection_is_refused_with_the_rule(string path)
	{
		HttpResponseMessage response = await (await WriterAsync()).GetAsync($"/v1/ingest/rendered?collection=c&path={Uri.EscapeDataString(path)}");

		Assert.Equal(["path must be relative to the collection, with forward slashes, no empty segment and none starting with a dot."], await ErrorsAsync(response, "path"));
	}

	[Fact]
	public async Task A_path_that_looks_like_a_credential_is_refused_without_repeating_it()
	{
		string token = Token(12);

		HttpResponseMessage response = await (await WriterAsync()).GetAsync($"/v1/ingest/rendered?collection=c&path={token}");
		string raw = await response.Content.ReadAsStringAsync();

		Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
		Assert.Equal(["path looks like a credential, so it was not used or logged."], await ErrorsAsync(response, "path"));
	}

	[Fact]
	public async Task A_path_of_exactly_the_longest_identity_is_accepted_and_one_more_is_not()
	{
		HttpClient client = await WriterAsync();
		string longest = new('a', 1024);

		HttpResponseMessage atLimit = await client.GetAsync($"/v1/ingest/rendered?collection=c&path={longest}");
		HttpResponseMessage over = await client.GetAsync($"/v1/ingest/rendered?collection=c&path={longest}b");

		Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
		Assert.Equal("none", (await ReadAsync(atLimit)).GetProperty("state").GetString());
		Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
	}

	[Fact]
	public async Task The_session_status_of_an_unknown_format_is_a_validation_problem()
	{
		HttpResponseMessage response = await (await WriterAsync()).GetAsync(SessionStatusUrl(format: "gemini"));

		Assert.Equal(["format must be claude or codex."], await ErrorsAsync(response, "format"));
	}

	[Fact]
	public async Task A_session_id_of_exactly_the_longest_identity_is_accepted_and_one_more_is_told_the_limit()
	{
		HttpClient client = await WriterAsync();
		string longest = new('s', 1024);

		HttpResponseMessage atLimit = await client.GetAsync(SessionStatusUrl(longest));
		HttpResponseMessage over = await client.GetAsync(SessionStatusUrl(longest + "s"));

		Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
		Assert.Equal(["sessionId may be at most 1024 characters."], await ErrorsAsync(over, "sessionId"));
	}

	[Fact]
	public async Task A_session_id_holding_a_nul_is_refused_with_the_reason()
	{
		HttpResponseMessage response = await (await WriterAsync()).GetAsync(SessionStatusUrl("a\0b"));

		Assert.Equal(["sessionId may not hold a NUL: identities are joined on it, so two could become one."], await ErrorsAsync(response, "sessionId"));
	}

	[Fact]
	public async Task A_session_with_no_id_is_told_it_is_required()
	{
		HttpResponseMessage response = await (await WriterAsync()).GetAsync("/v1/ingest/session?collection=c&format=claude");

		Assert.Equal(["sessionId is required."], await ErrorsAsync(response, "sessionId"));
	}

	[Fact]
	public async Task A_project_directory_of_exactly_the_longest_identity_is_accepted_and_one_more_is_told_the_limit()
	{
		HttpClient client = await WriterAsync();
		string longest = new('p', 1024);

		HttpResponseMessage atLimit = await client.GetAsync(SessionStatusUrl(projectDirectory: longest));
		HttpResponseMessage over = await client.GetAsync(SessionStatusUrl(projectDirectory: longest + "p"));
		HttpResponseMessage nul = await client.GetAsync(SessionStatusUrl(projectDirectory: "a\0b"));

		Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
		Assert.Equal(["At most 1024 characters, and no NUL."], await ErrorsAsync(over, "projectDirectory"));
		Assert.Equal(["At most 1024 characters, and no NUL."], await ErrorsAsync(nul, "projectDirectory"));
	}

	[Fact]
	public async Task A_version_exactly_an_hour_ahead_of_the_clock_is_allowed_and_a_second_more_is_not()
	{
		MovableClock clock = new();
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock)));
		(_, string key) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		HttpClient client = AuthApiFactory.Authorized(app.CreateClient(), key);
		DateTimeOffset limit = clock.GetUtcNow().AddHours(1);

		// An unknown format keeps the request from going further, so only the date is in question.
		HttpResponseMessage atLimit = await PostSessionAsync(client, ClaudeLine("user", "x"), format: "gemini", updated: limit);
		HttpResponseMessage over = await PostSessionAsync(client, ClaudeLine("user", "x"), format: "gemini", updated: limit.AddSeconds(1));

		Assert.Equal(HttpStatusCode.BadRequest, atLimit.StatusCode);
		Assert.False(await HasErrorAsync(atLimit, "updated"));
		Assert.Equal(["updated may not be more than an hour ahead of the server's clock."], await ErrorsAsync(over, "updated"));
	}

	[Fact]
	public async Task A_session_for_a_collection_outside_the_key_is_refused_for_the_collection_before_the_restore_right()
	{
		HttpClient client = await WriterAsync();

		HttpResponseMessage response = await PostSessionAsync(client, ClaudeLine("user", "x"), collection: "open", restoreDeleted: true);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal("collection_forbidden", (await ReadAsync(response)).GetProperty("code").GetString());
	}

	[Fact]
	public async Task Restoring_without_the_admin_scope_is_told_who_may()
	{
		HttpResponseMessage response = await (await WriterAsync()).PostAsync(
			"/v1/documents?restoreDeleted=true",
			JsonContent.Create(new { collection = "c", source = "notes", sourceId = Guid.NewGuid().ToString("N"), body = "x", updated = Stamp }));

		JsonElement problem = await ReadAsync(response);
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal("Only a key with the admin scope can restore what was deleted.", problem.GetProperty("detail").GetString());
	}

	[Fact]
	public async Task A_rendered_body_that_is_not_utf8_is_a_validation_problem_and_nothing_is_written()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		ByteArrayContent content = new([0x68, 0x69, 0xFF, 0xFE, 0x0A]);
		content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/markdown");

		HttpResponseMessage response = await (await WriterAsync()).PostAsync($"/v1/ingest/rendered?collection=c&path=bad-bytes.md&updated={Uri.EscapeDataString(Stamp.ToString("O"))}", content);

		Assert.Equal(["The body must be UTF-8."], await ErrorsAsync(response, "body"));
		Assert.Empty(corpus.Calls);
	}

	[Fact]
	public async Task Running_out_of_memory_tells_the_caller_to_come_back()
	{
		_ = new FakeCorpus(factory.Sidecar);
		factory.Sidecar.On("POST", "/documents", static (_, _) => throw new OutOfMemoryException());

		HttpResponseMessage response = await PostDocumentAsync(await WriterAsync(), sourceId: Guid.NewGuid().ToString("N"));

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		Assert.Equal("The server ran short of memory for this upload. Try again shortly.", (await ReadAsync(response)).GetProperty("detail").GetString());
	}
}
