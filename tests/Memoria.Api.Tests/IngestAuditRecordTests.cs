using System.Net;
using System.Text;
using Memoria.Api.Auth;
using Memoria.Api.Endpoints;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Core.Contracts;
using Memoria.Core.Sessions;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Memoria.Api.Tests.IngestKit;

namespace Memoria.Api.Tests;

/// <summary>
/// What the ingestors report for the audit record, whichever way a write ends, and what they refuse at their own door. They are built
/// here with a recording audit and a fixed caller, as a request would build them, so each step's report can be read back.
/// </summary>
public sealed class IngestAuditRecordTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private static readonly CancellationToken None = CancellationToken.None;

	private static T Build<T>(IServiceProvider services, RecordingAudit audit, params string[]? collections)
		where T : notnull
	{
		return ActivatorUtilities.CreateInstance<T>(services, new FixedScope(collections), audit);
	}

	private static string UniqueId()
	{
		return Guid.NewGuid().ToString("N");
	}

	// --- documents --------------------------------------------------------------

	[Fact]
	public async Task A_document_write_the_sidecar_refuses_is_audited_as_attempted_and_not_complete()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		corpus.OnWrite = static _ => TooLarge();
		using IServiceScope scope = factory.Services.CreateScope();
		RecordingAudit audit = new();
		DocumentIngestor ingestor = Build<DocumentIngestor>(scope.ServiceProvider, audit, "c");

		await Assert.ThrowsAsync<SidecarException>(() => ingestor.IngestAsync(new DocumentIngestRequest("c", "notes", UniqueId(), "hello", Stamp), false, None));

		AuditIngest target = audit.LastIngest();
		Assert.Equal(("document", false, 0, 1), (target.Kind, target.Complete, target.Written, target.Failed));
		Assert.Equal(("c", 0), audit.LastResult());
	}

	[Fact]
	public async Task A_stored_document_is_audited_as_written_and_complete()
	{
		_ = new FakeCorpus(factory.Sidecar);
		using IServiceScope scope = factory.Services.CreateScope();
		RecordingAudit audit = new();
		DocumentIngestor ingestor = Build<DocumentIngestor>(scope.ServiceProvider, audit, "c");

		await ingestor.IngestAsync(new DocumentIngestRequest("c", "notes", UniqueId(), "hello", Stamp), false, None);

		AuditIngest target = audit.LastIngest();
		Assert.Equal(("document", true, 1, 0), (target.Kind, target.Complete, target.Written, target.Failed));
		Assert.Equal(("c", 1), audit.LastResult());
	}

	[Fact]
	public async Task Checking_a_collection_is_writable_records_it_before_any_body_is_read()
	{
		_ = new FakeCorpus(factory.Sidecar);
		using IServiceScope scope = factory.Services.CreateScope();
		RecordingAudit audit = new();

		await Build<DocumentIngestor>(scope.ServiceProvider, audit, "c").RequireWritableAsync("c", None);

		Assert.Equal(("c", 0), audit.LastResult());
	}

	[Fact]
	public async Task A_rendered_write_the_sidecar_refuses_is_audited_as_attempted_and_not_complete()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		corpus.OnWrite = static _ => TooLarge();
		using IServiceScope scope = factory.Services.CreateScope();
		RecordingAudit audit = new();
		DocumentIngestor ingestor = Build<DocumentIngestor>(scope.ServiceProvider, audit, "c");

		await Assert.ThrowsAsync<SidecarException>(() => ingestor.StoreRenderedAsync("c", $"{UniqueId()}.md", "body\n", Stamp, None));

		AuditIngest target = audit.LastIngest();
		Assert.Equal(("rendered", false, 0, 1), (target.Kind, target.Complete, target.Written, target.Failed));
		Assert.Empty(audit.Results);
	}

	[Fact]
	public async Task A_stored_rendered_document_is_audited_as_written_and_complete()
	{
		_ = new FakeCorpus(factory.Sidecar);
		using IServiceScope scope = factory.Services.CreateScope();
		RecordingAudit audit = new();
		DocumentIngestor ingestor = Build<DocumentIngestor>(scope.ServiceProvider, audit, "c");

		await ingestor.StoreRenderedAsync("c", $"{UniqueId()}.md", "body\n", Stamp, None);

		AuditIngest target = audit.LastIngest();
		Assert.Equal(("rendered", true, 1, 0), (target.Kind, target.Complete, target.Written, target.Failed));
		Assert.Equal(("c", 1), audit.LastResult());
	}

	[Fact]
	public async Task A_delete_the_sidecar_cannot_confirm_is_audited_as_an_unfinished_delete()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		corpus.OnDelete = static _ => Busy();
		using IServiceScope scope = factory.Services.CreateScope();
		RecordingAudit audit = new();
		DocumentIngestor ingestor = Build<DocumentIngestor>(scope.ServiceProvider, audit, "c");

		await Assert.ThrowsAsync<IngestRefusedException>(() => ingestor.DeleteAsync("c", $"{UniqueId()}.md", None));

		AuditIngest target = audit.LastIngest();
		Assert.Equal(("delete", false, 0), (target.Kind, target.Complete, target.Deleted));
		Assert.Equal(("c", 0), audit.LastResult());
	}

	[Fact]
	public async Task Deleting_what_is_there_counts_one_and_deleting_what_is_not_counts_none()
	{
		_ = new FakeCorpus(factory.Sidecar);
		using IServiceScope scope = factory.Services.CreateScope();
		string path = $"{UniqueId()}.md";
		await Build<DocumentIngestor>(scope.ServiceProvider, new RecordingAudit(), "c").StoreRenderedAsync("c", path, "body\n", Stamp, None);

		RecordingAudit present = new();
		DocumentDeleteResult removed = await Build<DocumentIngestor>(scope.ServiceProvider, present, "c").DeleteAsync("c", path, None);
		RecordingAudit absent = new();
		DocumentDeleteResult nothing = await Build<DocumentIngestor>(scope.ServiceProvider, absent, "c").DeleteAsync("c", path, None);

		Assert.True(removed.Deleted);
		Assert.Equal(("c", 1), present.LastResult());
		Assert.Equal(("delete", true, 1), (present.LastIngest().Kind, present.LastIngest().Complete, present.LastIngest().Deleted));
		Assert.False(nothing.Deleted);
		Assert.Equal(("c", 0), absent.LastResult());
		Assert.Equal(("delete", true, 0), (absent.LastIngest().Kind, absent.LastIngest().Complete, absent.LastIngest().Deleted));
	}

	[Fact]
	public async Task The_rendered_status_of_a_collection_outside_the_caller_is_refused()
	{
		using IServiceScope scope = factory.Services.CreateScope();
		DocumentIngestor ingestor = Build<DocumentIngestor>(scope.ServiceProvider, new RecordingAudit(), "other");

		await Assert.ThrowsAsync<CollectionForbiddenException>(() => ingestor.RenderedStatusAsync("c", "a.md", None));
	}

	[Fact]
	public async Task A_document_ingest_given_nothing_is_an_argument_error()
	{
		using IServiceScope scope = factory.Services.CreateScope();
		DocumentIngestor ingestor = Build<DocumentIngestor>(scope.ServiceProvider, new RecordingAudit(), "c");

		ArgumentNullException thrown = await Assert.ThrowsAsync<ArgumentNullException>(() => ingestor.IngestAsync(null!, false, None));

		Assert.Equal("request", thrown.ParamName);
	}

	[Fact]
	public async Task A_rendered_document_over_the_limit_is_refused_with_the_limit_and_one_exactly_at_it_is_stored()
	{
		await using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Ingest:MaxDocumentBytes", "65536"));
		FakeCorpus corpus = new(factory.Sidecar);
		using IServiceScope scope = app.Services.CreateScope();
		DocumentIngestor ingestor = Build<DocumentIngestor>(scope.ServiceProvider, new RecordingAudit(), "c");
		string exact = Filled(65536);

		IngestRefusedException refused = await Assert.ThrowsAsync<IngestRefusedException>(() => ingestor.StoreRenderedAsync("c", $"{UniqueId()}.md", exact + "w", Stamp, None));
		RenderedDocumentResult stored = await ingestor.StoreRenderedAsync("c", $"{UniqueId()}.md", exact, Stamp, None);

		Assert.Equal(413, refused.Status);
		Assert.Equal("A document once redacted can be at most 65536 bytes.", refused.Message);
		Assert.Equal(65536, stored.Bytes);
		Assert.Single(corpus.Files);
	}

	// --- sessions ---------------------------------------------------------------

	[Fact]
	public async Task A_transcript_naming_another_session_is_refused_with_the_reason_and_audited_against_its_collection()
	{
		_ = new FakeCorpus(factory.Sidecar);
		using IServiceScope scope = factory.Services.CreateScope();
		RecordingAudit audit = new();
		SessionIngestor ingestor = Build<SessionIngestor>(scope.ServiceProvider, audit, "c");
		using MemoryStream body = new(Encoding.UTF8.GetBytes(CodexMeta(Guid.NewGuid().ToString()) + CodexMessage("hello")));

		IngestRefusedException refused = await Assert.ThrowsAsync<IngestRefusedException>(() => ingestor.IngestAsync(new SessionIngestInput("c", SessionFormat.Codex, "someone-else", Stamp, null), body, None));

		Assert.Equal("The transcript's session_meta names a different session from sessionId. Nothing was changed.", refused.Message);
		Assert.Equal(("c", 0), Assert.Single(audit.Results));
	}

	[Fact]
	public async Task A_session_for_a_collection_outside_the_caller_is_refused_by_the_ingestor_itself()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		using IServiceScope scope = factory.Services.CreateScope();
		SessionIngestor ingestor = Build<SessionIngestor>(scope.ServiceProvider, new RecordingAudit(), "other");
		using MemoryStream body = new(Encoding.UTF8.GetBytes(ClaudeLine("user", "hello")));

		await Assert.ThrowsAsync<CollectionForbiddenException>(() => ingestor.IngestAsync(new SessionIngestInput("c", SessionFormat.ClaudeCode, Guid.NewGuid().ToString(), Stamp, null), body, None));
		await Assert.ThrowsAsync<CollectionForbiddenException>(() => ingestor.StatusAsync(new SessionIngestInput("c", SessionFormat.ClaudeCode, Guid.NewGuid().ToString(), Stamp, null), None));
		Assert.Empty(corpus.Files);
	}

	[Fact]
	public async Task A_session_ingest_or_status_given_nothing_is_an_argument_error()
	{
		using IServiceScope scope = factory.Services.CreateScope();
		SessionIngestor ingestor = Build<SessionIngestor>(scope.ServiceProvider, new RecordingAudit(), "c");

		Assert.Equal("input", (await Assert.ThrowsAsync<ArgumentNullException>(() => ingestor.IngestAsync(null!, Stream.Null, None))).ParamName);
		Assert.Equal("input", (await Assert.ThrowsAsync<ArgumentNullException>(() => ingestor.StatusAsync(null!, None))).ParamName);
	}

	[Fact]
	public async Task A_shrunk_session_audits_the_parts_it_removed_beside_the_part_it_wrote()
	{
		FakeCorpus corpus = new(factory.Sidecar);
		using IServiceScope scope = factory.Services.CreateScope();
		string id = Guid.NewGuid().ToString();
		using (MemoryStream three = new(Encoding.UTF8.GetBytes(Transcript(3))))
		{
			await Build<SessionIngestor>(scope.ServiceProvider, new RecordingAudit(), "c").IngestAsync(new SessionIngestInput("c", SessionFormat.ClaudeCode, id, Stamp, null), three, None);
		}

		RecordingAudit audit = new();
		using MemoryStream one = new(Encoding.UTF8.GetBytes(Transcript(1)));
		SessionIngestResult result = await Build<SessionIngestor>(scope.ServiceProvider, audit, "c").IngestAsync(new SessionIngestInput("c", SessionFormat.ClaudeCode, id, Stamp.AddHours(1), null, AllowShrink: true), one, None);

		Assert.Equal(2, result.Superseded.Deleted.Count);
		AuditIngest target = audit.LastIngest();
		Assert.Equal(2, target.Deleted);
		string[] expected = [.. result.Parts.Select(static part => part.Path), .. result.Superseded.Deleted];
		Assert.Equal(expected, target.Paths);
		Assert.Single(corpus.Files);
	}

	// --- the content-type filter and the body declaration ---------------------------

	[Fact]
	public void Declaring_a_body_on_no_builder_is_an_argument_error()
	{
		Assert.Equal("builder", Assert.Throws<ArgumentNullException>(() => IngestBody.TakesBody<string>(null!, "text/markdown")).ParamName);
	}

	[Fact]
	public async Task The_content_type_filter_given_nothing_is_an_argument_error()
	{
		EndpointFilterInvocationContext context = EndpointFilterInvocationContext.Create(new DefaultHttpContext());

		Assert.Equal("context", (await Assert.ThrowsAsync<ArgumentNullException>(async () => await IngestBody.RequireContentTypeAsync(null!, static _ => ValueTask.FromResult<object?>(null)))).ParamName);
		Assert.Equal("next", (await Assert.ThrowsAsync<ArgumentNullException>(async () => await IngestBody.RequireContentTypeAsync(context, null!))).ParamName);
	}

	private static string Filled(int bytes)
	{
		StringBuilder text = new();
		while (text.Length + 5 < bytes)
		{
			text.Append("word ");
		}

		text.Append(new string('w', bytes - text.Length - 1)).Append('\n');
		return text.ToString();
	}

	private static string CodexMeta(string id)
	{
		return "{\"timestamp\":\"2026-01-15T10:00:00.000Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"cwd\":\"/srv/demo\"}}\n";
	}

	private static string CodexMessage(string text)
	{
		string payload = System.Text.Json.JsonSerializer.Serialize(new { type = "message", role = "user", content = new object[] { new { type = "input_text", text } } });
		return """{"timestamp":"2026-01-15T10:00:01.000Z","type":"response_item","payload":""" + payload + "}\n";
	}
}

/// <summary>Keeps what a component reports for the audit record, in order.</summary>
internal sealed class RecordingAudit : IRequestAudit
{
	public List<(string Collection, int Count)> Results { get; } = [];

	public List<object> Targets { get; } = [];

	public void SetOperation(string operation)
	{
	}

	public void SetQuery(string? query)
	{
	}

	public void SetResult(IEnumerable<string> collections, int resultCount)
	{
		Results.Add((string.Join(',', collections), resultCount));
	}

	public void SetOutcome(int status, IEnumerable<string>? attemptedCollections = null)
	{
	}

	public void SetTarget(object target)
	{
		Targets.Add(target);
	}

	public AuditIngest LastIngest()
	{
		return Assert.IsType<AuditIngest>(Assert.Single(Targets.TakeLast(1)));
	}

	public (string Collection, int Count) LastResult()
	{
		Assert.NotEmpty(Results);
		return Results[^1];
	}
}

/// <summary>A caller allowed exactly these collections, or every one when given none.</summary>
internal sealed class FixedScope(string[]? collections) : ICallerScope
{
	public IReadOnlyCollection<string>? Collections { get; } = collections is { Length: > 0 } ? collections : null;

	public bool SeesBackendDetails => false;
}
