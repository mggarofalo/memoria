using Memoria.Api.Auth;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Core.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Memoria.Api.Endpoints;

/// <summary>
/// Issue, list, rotate and revoke API keys. Admin scope only, and unavailable
/// when authentication is disabled: an anonymous caller must not be able to mint
/// a key that outlives the switch back to <c>ApiKey</c> mode.
/// </summary>
public static partial class AdminKeyEndpoints
{
	public const int MaxCollectionsPerKey = 50;

	private const int MaxLabelLength = 200;

	private const int MaxCollectionNameLength = 200;

	public static IEndpointRouteBuilder MapAdminKeys(this IEndpointRouteBuilder app)
	{
		RouteGroupBuilder keys = app.MapGroup("/v1/admin/keys")
			.WithMemoriaScope(MemoriaScopes.Admin)
			.DecideContentTypeAfterAuthorization()
			.WithTags("Admin")
			.AddEndpointFilter(RequireApiKeyModeAsync)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status429TooManyRequests);

		keys.MapPost("/", CreateAsync)
			.WithName("createApiKey")
			.WithSummary("Issue an API key")
			.WithDescription(
				"Creates a key and returns its plaintext. This response is the only time the plaintext exists. "
				+ "`read` and `ingest` keys must list `collections` or set `allCollections` to true; an empty list is refused. "
				+ "A key with only the `admin` scope omits both. Scopes do not imply each other: `admin` does not grant `read`.")
			.ProducesValidationProblem();

		keys.MapGet("/", ListAsync)
			.WithName("listApiKeys")
			.WithSummary("List API keys")
			.WithDescription("Every key, including revoked and expired ones. Never includes key material.");

		keys.MapDelete("/{id}", RevokeAsync)
			.WithName("revokeApiKey")
			.WithSummary("Revoke an API key")
			.WithDescription("Takes effect on the key's next request. Revoking a revoked key succeeds and keeps the original timestamp.")
			.ProducesProblem(StatusCodes.Status404NotFound);

		keys.MapPost("/{id}:rotate", RotateAsync)
			.WithName("rotateApiKey")
			.WithSummary("Rotate an API key")
			.WithDescription(
				"Replaces the key's secret and returns the new plaintext, once. The id, scopes, collections and expiry stay; "
				+ "the previous token stops working immediately. A revoked or expired key cannot be rotated (409).")
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);

		return app;
	}

	internal static async ValueTask<object?> RequireApiKeyModeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
	{
		IOptions<MemoriaOptions> options = context.HttpContext.RequestServices.GetRequiredService<IOptions<MemoriaOptions>>();
		if (options.Value.Auth.IsDisabled)
		{
			return TypedResults.Problem(
				statusCode: StatusCodes.Status404NotFound,
				title: "Unavailable without API keys",
				detail: "Memoria:Auth:Mode is Disabled. Key management, operations and writing to the corpus need API keys.",
				extensions: new Dictionary<string, object?> { ["code"] = "auth_disabled" });
		}

		return await next(context);
	}

	private static async Task<Results<Created<IssuedApiKey>, ValidationProblem>> CreateAsync(
		CreateApiKeyRequest request,
		IApiKeyStore store,
		TimeProvider timeProvider,
		HttpContext context,
		ILoggerFactory loggerFactory,
		IRequestAudit audit,
		KnownGoodKeys knownGood,
		CancellationToken cancellationToken)
	{
		DateTimeOffset now = timeProvider.GetUtcNow();
		Dictionary<string, string[]> errors = Validate(request, now);
		if (errors.Count > 0)
		{
			return TypedResults.ValidationProblem(errors);
		}

		string[] scopes = request.Scopes!.Distinct(StringComparer.Ordinal).ToArray();
		string[] collections = (request.Collections ?? []).Distinct(StringComparer.Ordinal).ToArray();
		(ApiKeyRecord record, string token) = ApiKeyRecord.Issue(request.Label!.Trim(), scopes, collections, request.AllCollections == true, request.ExpiresAt, now);
		await store.InsertAsync(record, cancellationToken);

		knownGood.Remember(record.Id, record.Sha256Hash);
		audit.SetTarget(ToAuditTarget(record));
		LogIssued(loggerFactory.CreateLogger(typeof(AdminKeyEndpoints).FullName!), record.Id, Actor(context));
		context.Response.Headers[HeaderNames.CacheControl] = "no-store";
		return TypedResults.Created($"/v1/admin/keys/{record.Id}", new IssuedApiKey(token, ToInfo(record)));
	}

	private static async Task<Ok<ApiKeyList>> ListAsync(IApiKeyStore store, CancellationToken cancellationToken)
	{
		IReadOnlyList<ApiKeyRecord> records = await store.ListAsync(cancellationToken);
		return TypedResults.Ok(new ApiKeyList(records.Select(ToInfo).ToArray()));
	}

	private static async Task<Results<NoContent, ProblemHttpResult>> RevokeAsync(
		string id,
		IApiKeyStore store,
		TimeProvider timeProvider,
		HttpContext context,
		ILoggerFactory loggerFactory,
		IRequestAudit audit,
		KnownGoodKeys knownGood,
		CancellationToken cancellationToken)
	{
		if (!await store.RevokeAsync(id, timeProvider.GetUtcNow(), cancellationToken))
		{
			return UnknownKey();
		}

		knownGood.Forget(id);
		if (await store.FindAsync(id, cancellationToken) is { } revoked)
		{
			audit.SetTarget(ToAuditTarget(revoked));
		}

		LogRevoked(loggerFactory.CreateLogger(typeof(AdminKeyEndpoints).FullName!), id, Actor(context));
		return TypedResults.NoContent();
	}

	private static async Task<Results<Ok<IssuedApiKey>, ProblemHttpResult>> RotateAsync(
		string id,
		IApiKeyStore store,
		TimeProvider timeProvider,
		HttpContext context,
		ILoggerFactory loggerFactory,
		IRequestAudit audit,
		KnownGoodKeys knownGood,
		CancellationToken cancellationToken)
	{
		ApiKeyRecord? record = await store.FindAsync(id, cancellationToken);
		if (record is null)
		{
			return UnknownKey();
		}

		if (!record.IsActive(timeProvider.GetUtcNow()))
		{
			return InactiveKey();
		}

		string token = ApiKeyToken.NewToken(id);
		byte[] hash = ApiKeyToken.Hash(token);
		if (!await store.ReplaceHashAsync(id, hash, cancellationToken))
		{
			return InactiveKey();
		}

		knownGood.Remember(id, hash);
		audit.SetTarget(ToAuditTarget(record));
		LogRotated(loggerFactory.CreateLogger(typeof(AdminKeyEndpoints).FullName!), id, Actor(context));
		context.Response.Headers[HeaderNames.CacheControl] = "no-store";
		return TypedResults.Ok(new IssuedApiKey(token, ToInfo(record with { Sha256Hash = hash })));
	}

	private static Dictionary<string, string[]> Validate(CreateApiKeyRequest request, DateTimeOffset now)
	{
		Dictionary<string, string[]> errors = [];

		if (string.IsNullOrWhiteSpace(request.Label) || request.Label.Trim().Length > MaxLabelLength)
		{
			errors["label"] = [$"A label of 1 to {MaxLabelLength} characters is required."];
		}

		if (request.Scopes is not { Count: > 0 })
		{
			errors["scopes"] = ["At least one scope is required: read, ingest, or admin."];
		}
		else if (request.Scopes.Any(static scope => !MemoriaScopes.All.Contains(scope)))
		{
			errors["scopes"] = ["Scopes must be read, ingest, or admin, in lower case."];
		}

		if (CollectionsProblem(request) is { } problem)
		{
			errors["collections"] = [problem];
		}

		if (request.ExpiresAt is { } expiresAt && expiresAt <= now)
		{
			errors["expiresAt"] = ["expiresAt must be in the future."];
		}

		return errors;
	}

	private static string? CollectionsProblem(CreateApiKeyRequest request)
	{
		bool allCollections = request.AllCollections == true;
		if (request.Collections is not { } collections)
		{
			bool adminOnly = request.Scopes is { Count: > 0 } && request.Scopes.All(static scope => scope == MemoriaScopes.Admin);
			return allCollections || adminOnly ? null : "List the collections this key may read, or set allCollections to true to request every collection.";
		}

		if (allCollections)
		{
			return "Set allCollections or list collections, not both.";
		}

		return ListProblem(collections);
	}

	private static string? ListProblem(IReadOnlyList<string> collections)
	{
		if (collections.Count == 0)
		{
			return "An empty list is not allowed. List the collections this key may read, or set allCollections to true to request every collection.";
		}

		if (collections.Count > MaxCollectionsPerKey)
		{
			return $"At most {MaxCollectionsPerKey} collections per key.";
		}

		return collections.All(IsValidCollectionName) ? null : $"Collection names must be 1 to {MaxCollectionNameLength} characters with no slash or surrounding whitespace. They are case-sensitive.";
	}

	internal static bool IsValidCollectionName(string? name)
	{
		return !string.IsNullOrWhiteSpace(name)
			&& name.Length <= MaxCollectionNameLength
			&& !name.Contains('/', StringComparison.Ordinal)
			&& string.Equals(name, name.Trim(), StringComparison.Ordinal);
	}

	private static ApiKeyInfo ToInfo(ApiKeyRecord record)
	{
		return new ApiKeyInfo(
			record.Id,
			record.Label,
			record.Scopes,
			record.Collections,
			record.AllCollections,
			record.CreatedAt,
			record.ExpiresAt,
			record.RevokedAt,
			record.LastUsedAt);
	}

	private static AuditTarget ToAuditTarget(ApiKeyRecord record)
	{
		return new AuditTarget(record.Id, record.Scopes, record.Collections, record.AllCollections);
	}

	private static string? Actor(HttpContext context)
	{
		return context.User.FindFirst(MemoriaClaims.KeyId)?.Value;
	}

	private static ProblemHttpResult UnknownKey()
	{
		return TypedResults.Problem(
			statusCode: StatusCodes.Status404NotFound,
			title: "Not found",
			detail: "No API key has that id.",
			extensions: new Dictionary<string, object?> { ["code"] = "not_found" });
	}

	private static ProblemHttpResult InactiveKey()
	{
		return TypedResults.Problem(
			statusCode: StatusCodes.Status409Conflict,
			title: "Key is not active",
			detail: "A revoked or expired key cannot be rotated. Issue a new one.",
			extensions: new Dictionary<string, object?> { ["code"] = "key_inactive" });
	}

	[LoggerMessage(EventId = 3300, Level = LogLevel.Information, Message = "API key {KeyId} issued by {ActorKeyId}")]
	private static partial void LogIssued(ILogger logger, string keyId, string? actorKeyId);

	[LoggerMessage(EventId = 3301, Level = LogLevel.Information, Message = "API key {KeyId} revoked by {ActorKeyId}")]
	private static partial void LogRevoked(ILogger logger, string keyId, string? actorKeyId);

	[LoggerMessage(EventId = 3302, Level = LogLevel.Information, Message = "API key {KeyId} rotated by {ActorKeyId}")]
	private static partial void LogRotated(ILogger logger, string keyId, string? actorKeyId);
}
