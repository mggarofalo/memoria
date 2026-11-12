using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Memoria.Api.Auth;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Core.Contracts;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Endpoints;

/// <summary>
/// Writing to the corpus. Everything here needs the <see cref="MemoriaScopes.Ingest"/> scope and the target
/// collection in the key's allowlist. Bodies are read by hand, through a limit, because binding would buffer first.
/// </summary>
public static class IngestEndpoints
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	public static IEndpointRouteBuilder MapIngest(this IEndpointRouteBuilder app)
	{
		RouteGroupBuilder group = app.MapGroup("/v1")
			.WithMemoriaScope(MemoriaScopes.Ingest)
			.WithTags("Ingest")
			.AddEndpointFilter(AdminKeyEndpoints.RequireApiKeyModeAsync)
			// After authorization, so a key without the scope gets 403 whatever it sends.
			.AddEndpointFilter(IngestBody.RequireContentTypeAsync)
			.AddEndpointFilter(ComeBackWhenOutOfMemoryAsync)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status429TooManyRequests)
			.ProducesProblem(StatusCodes.Status502BadGateway)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable)
			.ProducesProblem(StatusCodes.Status504GatewayTimeout);

		MapDocumentRoutes(group);
		MapSessionRoutes(group);
		MapRenderedRoutes(group);
		return app;
	}

	/// <summary>The budget is an estimate, so memory can still run out under load. That is a reason to come back, not a fault: the request is simply answered with 503, like any other refusal.</summary>
	private static async ValueTask<object?> ComeBackWhenOutOfMemoryAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
	{
		try
		{
			return await next(context);
		}
		catch (OutOfMemoryException)
		{
			throw new IngestRefusedException(StatusCodes.Status503ServiceUnavailable, "ingest_busy", "The server ran short of memory for this upload. Try again shortly.", 30);
		}
	}

	private static void MapDocumentRoutes(RouteGroupBuilder group)
	{
		group.MapPost("/documents", IngestDocumentAsync)
			.WithName("ingestDocument")
			.WithSummary("Store a document")
			.WithDescription(
				"Renders the envelope to frontmatter, redacts the whole document, and stores it. No caller can turn redaction off: only an operator can, for a named collection, "
				+ "and `redaction` in the response says which applied. A document redaction cannot finish is refused with 422. `source` and `sourceId` are its identity: sending them again overwrites, and `updated` orders versions, "
				+ "so an older version gets 409. The response counts what was replaced, by rule, with no values. The document becomes searchable when the job finishes.")
			.TakesBody<DocumentIngestRequest>("application/json")
			.ProducesValidationProblem()
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge)
			.ProducesProblem(StatusCodes.Status422UnprocessableEntity);

		group.MapDelete("/documents", DeleteDocumentAsync)
			.WithName("deleteDocument")
			.WithSummary("Delete a document")
			.WithDescription(
				"Names a document by `source` and `sourceId`, or by `path` within the collection, and removes it, waiting for any write of it in progress to finish first. 503 `delete_unconfirmed` means it could not be confirmed done, and repeating it is safe. A deleted document stays searchable until the index changes, so an update starts at once, "
				+ "or within `reindexInSeconds` when one just ran in this collection: `reindexState` says which. It also stops an older copy, or the same content under a later date, being stored again. Safe to repeat: "
				+ "a missing document answers `deleted: false` and costs no update. When `reindexState` is `failed`, repeat the request.")
			.ProducesValidationProblem()
			.ProducesProblem(StatusCodes.Status409Conflict);
	}

	private static void MapRenderedRoutes(RouteGroupBuilder group)
	{
		group.MapPost("/ingest/rendered", IngestRenderedAsync)
			.WithName("ingestRendered")
			.WithSummary("Store an already rendered document")
			.WithDescription(
				"For moving an old corpus. The body is the markdown as it is, frontmatter included, and it is stored at `path` as it came, except that all of it goes through the collection's redaction decision. `frontmatter` in the answer says whether "
				+ "the sidecar could read the frontmatter: anything but `read` means the document is searchable but matches no date, project or tag filter. Safe to send again: the path is the identity, an equal `updated` overwrites with the same result, and an older one gets 409. "
				+ "Past the size limit it gets 413, and past the limit on large documents at once it gets 503 with `Retry-After`, so pause before sending again. It cannot delete.")
			.TakesBody<string>("text/markdown")
			.ProducesValidationProblem()
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge)
			.ProducesProblem(StatusCodes.Status422UnprocessableEntity);

		group.MapGet("/ingest/rendered", GetRenderedStatusAsync)
			.WithName("getRenderedIngestStatus")
			.WithSummary("What is recorded for a rendered document's path")
			.WithDescription("Says what the ledger holds for a path, so a migration can check what arrived. `none` means nothing is recorded. `complete` means a write of it landed.")
			.ProducesValidationProblem();
	}

	private static void MapSessionRoutes(RouteGroupBuilder group)
	{
		group.MapPost("/ingest/session", IngestSessionAsync)
			.WithName("ingestSession")
			.WithSummary("Store a raw session transcript")
			.WithDescription(
				"The body is the raw JSONL of a Claude Code or Codex session, read as a stream. It is rendered and redacted here into one or more parts. 202 means every part arrived. "
				+ "207 means not all of it is stored: `parts` and `read` say why, the session stays `partial`, and when `retryable` is true sending the same upload again finishes it. "
				+ "A transcript with no messages, or that lost any, is never 202. An older version than the one stored gets 409, as does a second upload of the session while one is running, a file the ledger does not say is this session's, "
				+ "and messages that do not start with the stored ones, unless `allowShrink` is set. Parts it no longer has are deleted last, and a part someone deleted stays deleted.")
			.TakesBody<Stream>("application/x-ndjson")
			.Produces<SessionIngestResult>(StatusCodes.Status207MultiStatus)
			.ProducesValidationProblem()
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge)
			.ProducesProblem(StatusCodes.Status422UnprocessableEntity);

		group.MapGet("/ingest/session", GetSessionStatusAsync)
			.WithName("getSessionIngestStatus")
			.WithSummary("How a session's upload ended")
			.WithDescription("Only `complete` means all of it arrived. `partial` means some parts may be searchable, and the upload should be sent again.")
			.ProducesValidationProblem();
	}

	private static async Task<Results<Accepted<DocumentIngestResult>, ValidationProblem>> IngestDocumentAsync(
		[FromQuery(Name = "restoreDeleted")][Description("Stores this even though it was deleted, including the same content. Needs a key with the `admin` scope as well.")] bool? restoreDeleted,
		HttpContext context,
		DocumentIngestor ingestor,
		LargeUploads large,
		IOptions<MemoriaOptions> options,
		TimeProvider timeProvider,
		CancellationToken cancellationToken)
	{
		// Escaping can double text in JSON, and the envelope sits beside the body. What fits is checked once parsed.
		long limit = (options.Value.Ingest.MaxDocumentBytes * 2L) + 64 * 1024;
		RequestBody.AllowUpTo(context, limit);
		RequestBody.RequireRate(context, options.Value.Ingest.MinUploadBytesPerSecond);
		RequireRestoreRight(context, restoreDeleted);
		await using Stream body = large.WatchDocument(RequestBody.Open(context, limit), context.Request.ContentLength);
		DocumentIngestRequest? request = await IngestRules.WithinDeadlineAsync(
			options.Value.Ingest.DocumentUploadDeadline,
			token => ReadDocumentAsync(body, token),
			cancellationToken);
		Dictionary<string, string[]> errors = IngestValidation.Document(request, timeProvider.GetUtcNow());
		if (errors.Count > 0)
		{
			return TypedResults.ValidationProblem(errors);
		}

		return TypedResults.Accepted((string?)null, await ingestor.IngestAsync(request!, restoreDeleted == true, cancellationToken));
	}

	/// <summary>Bringing back what someone deleted is an operator's decision: a deleted document may have held a credential.</summary>
	private static void RequireRestoreRight(HttpContext context, bool? restoreDeleted)
	{
		if (restoreDeleted == true && !context.User.IsUnboundAdmin())
		{
			throw new IngestRefusedException(StatusCodes.Status403Forbidden, "restore_needs_admin", "Only a key with the admin scope can restore what was deleted.");
		}
	}

	private static async Task<DocumentIngestRequest?> ReadDocumentAsync(Stream body, CancellationToken cancellationToken)
	{
		try
		{
			return await JsonSerializer.DeserializeAsync<DocumentIngestRequest>(body, Json, cancellationToken);
		}
		catch (JsonException)
		// Stryker disable once Block : an emptied catch falls through to the method's end, where Stryker returns default, which is the same null.
		{
			return null;
		}
	}

	private static async Task<Results<Accepted<DocumentDeleteResult>, ValidationProblem>> DeleteDocumentAsync(
		[FromQuery(Name = "collection")][Description("The collection the document is in.")] string? collection,
		[FromQuery(Name = "source")][Description("The source it was stored under. Use with `sourceId`.")] string? source,
		[FromQuery(Name = "sourceId")][Description("Its identity within that source.")] string? sourceId,
		[FromQuery(Name = "path")][Description("Its path within the collection, instead of `source` and `sourceId`.")] string? path,
		DocumentIngestor ingestor,
		CancellationToken cancellationToken)
	{
		Dictionary<string, string[]> errors = IngestValidation.Delete(collection, source, sourceId, path);
		if (errors.Count > 0)
		{
			return TypedResults.ValidationProblem(errors);
		}

		string target = string.IsNullOrWhiteSpace(path) ? new DocumentEnvelope(source!, sourceId!).StoragePath() : path;
		return TypedResults.Accepted((string?)null, await ingestor.DeleteAsync(collection!, target, cancellationToken));
	}

	private static async Task<Results<Accepted<SessionIngestResult>, JsonHttpResult<SessionIngestResult>, ValidationProblem>> IngestSessionAsync(
		[AsParameters] SessionQuery query,
		[FromQuery(Name = "updated")][Description("When this copy of the transcript last changed, as an ISO timestamp. Orders uploads, so an older one is refused.")] DateTimeOffset? updated,
		HttpContext context,
		SessionIngestor ingestor,
		ICallerScope scope,
		LargeUploads large,
		IOptions<MemoriaOptions> options,
		TimeProvider timeProvider,
		CancellationToken cancellationToken)
	{
		RequestBody.AllowUpTo(context, options.Value.Ingest.MaxSessionBytes);
		RequestBody.RequireRate(context, options.Value.Ingest.MinUploadBytesPerSecond);
		Dictionary<string, string[]> errors = IngestValidation.Session(query, updated, timeProvider.GetUtcNow());
		if (errors.Count > 0)
		{
			return TypedResults.ValidationProblem(errors);
		}

		SessionIngestInput input = query.ToInput(updated!.Value);
		IngestRules.RequireCollection(scope, input.Collection);
		RequireRestoreRight(context, query.RestoreDeleted);
		await using Stream body = large.Watch(RequestBody.Open(context, options.Value.Ingest.MaxSessionBytes), context.Request.ContentLength);
		SessionIngestResult result = await ingestor.IngestAsync(input, body, cancellationToken);
		return result.Complete
			? TypedResults.Accepted((string?)null, result)
			: TypedResults.Json(result, statusCode: StatusCodes.Status207MultiStatus);
	}

	private static async Task<Results<Accepted<RenderedDocumentResult>, ValidationProblem>> IngestRenderedAsync(
		[FromQuery(Name = "collection")][Description("The collection to write to, which must be open to ingest.")] string? collection,
		[FromQuery(Name = "path")][Description("Where it goes, relative to the collection. Its existing path, which identifies it.")] string? path,
		[FromQuery(Name = "updated")][Description("When this copy last changed, as an ISO timestamp. Orders versions, so send the same value when sending it again.")] DateTimeOffset? updated,
		HttpContext context,
		DocumentIngestor ingestor,
		LargeUploads large,
		IOptions<MemoriaOptions> options,
		TimeProvider timeProvider,
		CancellationToken cancellationToken)
	{
		long limit = options.Value.Ingest.MaxDocumentBytes;
		RequestBody.AllowUpTo(context, limit);
		RequestBody.RequireRate(context, options.Value.Ingest.MinUploadBytesPerSecond);
		Dictionary<string, string[]> errors = IngestValidation.Rendered(collection, path, updated, timeProvider.GetUtcNow());
		if (errors.Count > 0)
		{
			return TypedResults.ValidationProblem(errors);
		}

		await ingestor.RequireWritableAsync(collection!, cancellationToken);
		await using Stream body = large.WatchDocument(RequestBody.Open(context, limit), context.Request.ContentLength);
		string? text = await IngestRules.WithinDeadlineAsync(options.Value.Ingest.DocumentUploadDeadline, token => ReadMarkdownAsync(body, token), cancellationToken);
		if (text is null)
		{
			return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["The body must be UTF-8."] });
		}

		return TypedResults.Accepted((string?)null, await ingestor.StoreRenderedAsync(collection!, path!, text, updated!.Value, cancellationToken));
	}

	private static async Task<string?> ReadMarkdownAsync(Stream body, CancellationToken cancellationToken)
	{
		using MemoryStream bytes = new();
		await body.CopyToAsync(bytes, cancellationToken);
		try
		{
			return StrictUtf8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
		}
		catch (DecoderFallbackException)
		// Stryker disable once Block : an emptied catch falls through to the method's end, where Stryker returns default, which is the same null.
		{
			return null;
		}
	}

	// Stryker disable once Boolean : encoderShouldEmitUTF8Identifier only changes GetPreamble, which writers read; GetString, the only use here, never does.
	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	private static async Task<Results<Ok<RenderedDocumentStatus>, ValidationProblem>> GetRenderedStatusAsync(
		[FromQuery(Name = "collection")][Description("The collection it is in.")] string? collection,
		[FromQuery(Name = "path")][Description("Its path within the collection.")] string? path,
		DocumentIngestor ingestor,
		TimeProvider timeProvider,
		CancellationToken cancellationToken)
	{
		Dictionary<string, string[]> errors = IngestValidation.Rendered(collection, path, timeProvider.GetUtcNow(), timeProvider.GetUtcNow());
		if (errors.Count > 0)
		{
			return TypedResults.ValidationProblem(errors);
		}

		return TypedResults.Ok(await ingestor.RenderedStatusAsync(collection!, path!, cancellationToken));
	}

	private static async Task<Results<Ok<SessionIngestStatus>, ValidationProblem>> GetSessionStatusAsync(
		[AsParameters] SessionQuery query,
		SessionIngestor ingestor,
		CancellationToken cancellationToken)
	{
		Dictionary<string, string[]> errors = IngestValidation.SessionIdentity(query);
		if (errors.Count > 0)
		{
			return TypedResults.ValidationProblem(errors);
		}

		return TypedResults.Ok(await ingestor.StatusAsync(query.ToInput(DateTimeOffset.MinValue), cancellationToken));
	}
}

/// <summary>What names a session in a query string.</summary>
public sealed class SessionQuery
{
	[FromQuery(Name = "collection")]
	[Description("The collection to write to.")]
	public string? Collection { get; set; }

	[FromQuery(Name = "format")]
	[Description("`claude` or `codex`.")]
	public string? Format { get; set; }

	[FromQuery(Name = "sessionId")]
	[Description("The session's id, normally the transcript's file name without its extension.")]
	public string? SessionId { get; set; }

	[FromQuery(Name = "projectDirectory")]
	[Description("The Claude Code project folder the transcript was in. Tells apart two copies of one session.")]
	public string? ProjectDirectory { get; set; }

	[FromQuery(Name = "allowShrink")]
	[Description("Set when the session's history really did change. Without it, an upload whose messages do not start with the stored ones is refused, as it looks just like a cut or damaged copy.")]
	public bool? AllowShrink { get; set; }

	[FromQuery(Name = "restoreDeleted")]
	[Description("Writes parts of this session that were deleted, which otherwise stay deleted. Needs a key with the `admin` scope as well.")]
	public bool? RestoreDeleted { get; set; }

	internal SessionIngestInput ToInput(DateTimeOffset updated)
	{
		SessionFormat format = Format == IngestEndpointNames.Codex ? SessionFormat.Codex : SessionFormat.ClaudeCode;
		return new SessionIngestInput(Collection!, format, SessionId!, updated, string.IsNullOrWhiteSpace(ProjectDirectory) ? null : ProjectDirectory, AllowShrink == true, Restore: RestoreDeleted == true);
	}
}

internal static class IngestValidation
{
	public static Dictionary<string, string[]> Document(DocumentIngestRequest? request, DateTimeOffset now)
	{
		Dictionary<string, string[]> errors = [];
		if (request is null)
		{
			errors["body"] = ["The body must be a JSON object."];
			return errors;
		}

		RequireCollectionName(errors, request.Collection);
		RequireText(errors, "source", request.Source);
		RequireText(errors, "sourceId", request.SourceId);
		if (request.Body is null)
		{
			errors["body"] = ["A body is required. It may be empty."];
		}

		RequireVersion(errors, request.Updated, now);
		return errors;
	}

	public static Dictionary<string, string[]> Rendered(string? collection, string? path, DateTimeOffset? updated, DateTimeOffset now)
	{
		Dictionary<string, string[]> errors = [];
		RequireCollectionName(errors, collection);
		if (string.IsNullOrWhiteSpace(path))
		{
			errors["path"] = ["path is required: it identifies the document."];
		}
		else
		{
			RequireRelativePath(errors, path);
		}

		RequireVersion(errors, updated, now);
		return errors;
	}

	public static Dictionary<string, string[]> Delete(string? collection, string? source, string? sourceId, string? path)
	{
		Dictionary<string, string[]> errors = [];
		RequireCollectionName(errors, collection);
		if (!string.IsNullOrWhiteSpace(path))
		{
			RequireRelativePath(errors, path);
			return errors;
		}

		RequireText(errors, "source", source);
		RequireText(errors, "sourceId", sourceId);
		return errors;
	}

	public static Dictionary<string, string[]> Session(SessionQuery query, DateTimeOffset? updated, DateTimeOffset now)
	{
		Dictionary<string, string[]> errors = SessionIdentity(query);
		RequireVersion(errors, updated, now);
		return errors;
	}

	public static Dictionary<string, string[]> SessionIdentity(SessionQuery query)
	{
		Dictionary<string, string[]> errors = [];
		RequireCollectionName(errors, query.Collection);
		RequireText(errors, "sessionId", query.SessionId);
		if (query.Format is not (IngestEndpointNames.Claude or IngestEndpointNames.Codex))
		{
			errors["format"] = ["format must be claude or codex."];
		}

		if (query.ProjectDirectory?.Length > IngestRules.MaxIdentityChars || query.ProjectDirectory?.Contains(NoCharacter) == true)
		{
			errors["projectDirectory"] = [$"At most {IngestRules.MaxIdentityChars} characters, and no NUL."];
		}

		return errors;
	}

	private static void RequireText(Dictionary<string, string[]> errors, string name, string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			errors[name] = [$"{name} is required."];
		}
		else if (value.Length > IngestRules.MaxIdentityChars)
		{
			errors[name] = [$"{name} may be at most {IngestRules.MaxIdentityChars} characters."];
		}
		else if (value.Contains(NoCharacter))
		{
			errors[name] = [$"{name} may not hold a NUL: identities are joined on it, so two could become one."];
		}
	}

	/// <summary>A collection name reaches logs and tables, so one holding a credential stops here. The answer does not repeat it.</summary>
	private static void RequireCollectionName(Dictionary<string, string[]> errors, string? value)
	{
		RequireText(errors, "collection", value);
		if (!errors.ContainsKey("collection") && Redactor.HoldsCredential(value!))
		{
			errors["collection"] = ["collection looks like a credential, so it was not used or logged."];
		}
	}

	private const char NoCharacter = '\0';

	/// <summary>A version far ahead of the clock would lock out every real update after it.</summary>
	private static void RequireVersion(Dictionary<string, string[]> errors, DateTimeOffset? updated, DateTimeOffset now)
	{
		if (updated is null)
		{
			errors["updated"] = ["updated is required: it orders versions, so an older copy cannot replace a newer one."];
		}
		else if (updated > now + IngestRules.MaxFutureSkew)
		{
			errors["updated"] = ["updated may not be more than an hour ahead of the server's clock."];
		}
	}

	/// <summary>The sidecar checks this as well. Dot segments are refused there by design: QMD never indexes them, and they are things like <c>.git/config</c>.</summary>
	private static void RequireRelativePath(Dictionary<string, string[]> errors, string path)
	{
		string[] segments = path.Split('/');

		// The sidecar reads a backslash as a separator, so it would be a second name for the same file, with a ledger record of its own.
		if (path.Length > IngestRules.MaxIdentityChars || path.Contains(NoCharacter) || path.Contains('\\') || path.StartsWith('/') || segments.Any(static segment => segment.Length == 0 || segment.StartsWith('.')))
		{
			errors["path"] = ["path must be relative to the collection, with forward slashes, no empty segment and none starting with a dot."];
		}
		else if (Redactor.HoldsCredential(path))
		{
			errors["path"] = ["path looks like a credential, so it was not used or logged."];
		}
	}
}
