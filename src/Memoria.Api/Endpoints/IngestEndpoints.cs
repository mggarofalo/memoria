using System.ComponentModel;
using System.Text.Json;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Core.Contracts;
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
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status429TooManyRequests)
			.ProducesProblem(StatusCodes.Status502BadGateway)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable)
			.ProducesProblem(StatusCodes.Status504GatewayTimeout);

		MapDocumentRoutes(group);
		MapSessionRoutes(group);
		return app;
	}

	private static void MapDocumentRoutes(RouteGroupBuilder group)
	{
		group.MapPost("/documents", IngestDocumentAsync)
			.WithName("ingestDocument")
			.WithSummary("Store a document")
			.WithDescription(
				"Renders the envelope to frontmatter, redacts the whole document, and stores it. Redaction cannot be turned off, and a document it "
				+ "cannot finish is refused with 422. `source` and `sourceId` are its identity: sending them again overwrites, and `updated` orders versions, "
				+ "so an older version gets 409. The response counts what was replaced, by rule, with no values. The document becomes searchable when the job finishes.")
			.Accepts<DocumentIngestRequest>("application/json")
			.ProducesValidationProblem()
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge)
			.ProducesProblem(StatusCodes.Status422UnprocessableEntity);

		group.MapDelete("/documents", DeleteDocumentAsync)
			.WithName("deleteDocument")
			.WithSummary("Delete a document")
			.WithDescription(
				"Names a document by `source` and `sourceId`, or by `path` within the collection, and removes it. The index update starts at once, because a deleted "
				+ "document stays searchable until the index changes. It also stops an older version being stored again. Safe to repeat: "
				+ "a missing document answers `deleted: false`. When `reindexJobId` is null the update could not be queued, so repeat the request.")
			.ProducesValidationProblem()
			.ProducesProblem(StatusCodes.Status409Conflict);
	}

	private static void MapSessionRoutes(RouteGroupBuilder group)
	{
		group.MapPost("/ingest/session", IngestSessionAsync)
			.WithName("ingestSession")
			.WithSummary("Store a raw session transcript")
			.WithDescription(
				"The body is the raw JSONL of a Claude Code or Codex session, read as a stream. It is rendered and redacted here into one or more parts. 202 means every part arrived. "
				+ "207 means some did not: `parts` says which, the session stays `partial`, and when `retryable` is true sending the same upload again finishes it. "
				+ "An older version than the one stored gets 409, as does a second upload of the session while one is running. Parts a shrunken session no longer has are deleted last.")
			.Accepts<Stream>("application/x-ndjson")
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
		HttpContext context,
		DocumentIngestor ingestor,
		IOptions<MemoriaOptions> options,
		TimeProvider timeProvider,
		CancellationToken cancellationToken)
	{
		// Escaping can double text in JSON, and the envelope sits beside the body. What fits is checked once rendered.
		long limit = (options.Value.Ingest.MaxDocumentBytes * 2L) + 64 * 1024;
		DocumentIngestRequest? request = await ReadDocumentAsync(context, limit, cancellationToken);
		Dictionary<string, string[]> errors = IngestValidation.Document(request, timeProvider.GetUtcNow());
		if (errors.Count > 0)
		{
			return TypedResults.ValidationProblem(errors);
		}

		DocumentIngestResult result = await ingestor.IngestAsync(request!, cancellationToken);
		return TypedResults.Accepted($"/v1/documents/{result.Path}", result);
	}

	private static async Task<DocumentIngestRequest?> ReadDocumentAsync(HttpContext context, long limit, CancellationToken cancellationToken)
	{
		await using Stream body = RequestBody.Open(context, limit);
		try
		{
			return await JsonSerializer.DeserializeAsync<DocumentIngestRequest>(body, Json, cancellationToken);
		}
		catch (JsonException)
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
		IngestGate gate,
		IOptions<MemoriaOptions> options,
		TimeProvider timeProvider,
		CancellationToken cancellationToken)
	{
		RequestBody.AllowUpTo(context, options.Value.Ingest.MaxSessionBytes);
		Dictionary<string, string[]> errors = IngestValidation.Session(query, updated, timeProvider.GetUtcNow());
		if (errors.Count > 0)
		{
			return TypedResults.ValidationProblem(errors);
		}

		SessionIngestInput input = query.ToInput(updated!.Value);
		IngestRules.RequireCollection(scope, input.Collection);
		SessionIngestResult result = await gate.RunAsync(async () =>
		{
			await using Stream body = RequestBody.Open(context, options.Value.Ingest.MaxSessionBytes);
			return await ingestor.IngestAsync(input, body, cancellationToken);
		});
		return result.Complete
			? TypedResults.Accepted((string?)null, result)
			: TypedResults.Json(result, statusCode: StatusCodes.Status207MultiStatus);
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

	internal SessionIngestInput ToInput(DateTimeOffset updated)
	{
		SessionFormat format = Format == IngestEndpointNames.Codex ? SessionFormat.Codex : SessionFormat.ClaudeCode;
		return new SessionIngestInput(Collection!, format, SessionId!, updated, string.IsNullOrWhiteSpace(ProjectDirectory) ? null : ProjectDirectory);
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

		RequireText(errors, "collection", request.Collection);
		RequireText(errors, "source", request.Source);
		RequireText(errors, "sourceId", request.SourceId);
		if (request.Body is null)
		{
			errors["body"] = ["A body is required. It may be empty."];
		}

		RequireVersion(errors, request.Updated, now);
		return errors;
	}

	public static Dictionary<string, string[]> Delete(string? collection, string? source, string? sourceId, string? path)
	{
		Dictionary<string, string[]> errors = [];
		RequireText(errors, "collection", collection);
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
		RequireText(errors, "collection", query.Collection);
		RequireText(errors, "sessionId", query.SessionId);
		if (query.Format is not (IngestEndpointNames.Claude or IngestEndpointNames.Codex))
		{
			errors["format"] = ["format must be claude or codex."];
		}

		if (query.ProjectDirectory?.Length > IngestRules.MaxIdentityChars)
		{
			errors["projectDirectory"] = [$"At most {IngestRules.MaxIdentityChars} characters."];
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
	}

	/// <summary>A version far ahead of the clock would lock out every real update after it.</summary>
	private static void RequireVersion(Dictionary<string, string[]> errors, DateTimeOffset? updated, DateTimeOffset now)
	{
		if (updated is null)
		{
			errors["updated"] = ["updated is required: it orders versions, so an older copy cannot replace a newer one."];
		}
		else if (updated > now + IngestRules.MaxFutureSkew)
		{
			errors["updated"] = ["updated may not be more than a day ahead of the server's clock."];
		}
	}

	/// <summary>The sidecar checks this as well. Dot segments are refused there by design: QMD never indexes them, and they are things like <c>.git/config</c>.</summary>
	private static void RequireRelativePath(Dictionary<string, string[]> errors, string path)
	{
		string[] segments = path.Replace('\\', '/').Split('/');
		if (path.Length > IngestRules.MaxIdentityChars || path.StartsWith('/') || segments.Any(static segment => segment.Length == 0 || segment.StartsWith('.')))
		{
			errors["path"] = ["path must be relative to the collection, with no empty segment and none starting with a dot."];
		}
	}
}
