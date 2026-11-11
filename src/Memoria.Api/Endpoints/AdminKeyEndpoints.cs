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
		// A key bound to collections cannot manage keys: it could otherwise mint a key wider than itself, or revoke
		// the keys that are. It is an authorization requirement, so it is decided before the body is read.
		RouteGroupBuilder keys = app.MapGroup("/v1/admin/keys")
			.WithMemoriaScope(MemoriaScopes.Admin)
			.WithMetadata(new MemoriaUnboundKeyAttribute())
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
				+ "A key with only the `admin` scope omits both. Scopes do not imply each other: `admin` does not grant `read`. "
				+ "A key with `admin` acts on every collection, so it cannot list `collections` (400, `admin_key_bound`); give it `allCollections` if it also reads or ingests. "
				+ "With `Memoria:Auth:DefaultKeyLifetime` set, a key with `read` or `ingest` and no `expiresAt` expires that long after it is made. "
				+ "`expiresAt` may be at most two years ahead. A key never makes a key wider than itself: one that expires cannot make a key that outlives it (403, `key_exceeds_caller`).")
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
				+ "the previous token stops working immediately. A revoked or expired key cannot be rotated (409), "
				+ "nor can a key that outlives the caller's own (403, `key_exceeds_caller`).")
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

	private static async Task<Results<Created<IssuedApiKey>, ValidationProblem, ProblemHttpResult>> CreateAsync(
		CreateApiKeyRequest request,
		IApiKeyStore store,
		TimeProvider timeProvider,
		HttpContext context,
		ILoggerFactory loggerFactory,
		IRequestAudit audit,
		KnownGoodKeys knownGood,
		IOptions<MemoriaOptions> options,
		CancellationToken cancellationToken)
	{
		DateTimeOffset now = timeProvider.GetUtcNow();
		Dictionary<string, string[]> errors = Validate(request, now);
		if (errors.Count > 0)
		{
			return TypedResults.ValidationProblem(errors, extensions: ValidationCode(request));
		}

		ApiKeyRecord? caller = await CallerAsync(store, context, cancellationToken);
		string[] scopes = request.Scopes!.Distinct(StringComparer.Ordinal).ToArray();
		string[] collections = (request.Collections ?? []).Distinct(StringComparer.Ordinal).ToArray();
		bool allCollections = request.AllCollections == true;
		DateTimeOffset? expiresAt = request.ExpiresAt ?? Earliest(DefaultExpiry(scopes, now, options.Value.Auth.DefaultKeyLifetime), caller?.ExpiresAt);
		if (WiderThanCaller(caller, scopes, collections, allCollections, expiresAt) is { } wider)
		{
			return wider;
		}

		(ApiKeyRecord record, string token) = ApiKeyRecord.Issue(request.Label!.Trim(), scopes, collections, allCollections, expiresAt, now);
		await store.InsertAsync(record, cancellationToken);

		knownGood.Remember(record.Id, record.Sha256Hash);
		audit.SetTarget(ToAuditTarget(record));
		LogIssued(loggerFactory.CreateLogger(typeof(AdminKeyEndpoints).FullName!), record.Id, Actor(context));
		context.Response.Headers[HeaderNames.CacheControl] = "no-store";
		return TypedResults.Created($"/v1/admin/keys/{record.Id}", new IssuedApiKey(token, ToInfo(record)));
	}

	/// <summary>
	/// When a key with no <c>expiresAt</c> of its own expires: never with no default lifetime set, or for a key whose only scope is
	/// <c>admin</c>, so the operator is not locked out when every admin key lapses at once. A key that can read or ingest gets the
	/// default even beside <c>admin</c>.
	/// </summary>
	public static DateTimeOffset? DefaultExpiry(IReadOnlyCollection<string> scopes, DateTimeOffset now, TimeSpan? lifetime)
	{
		bool adminOnly = scopes.All(static scope => scope == MemoriaScopes.Admin);
		return lifetime is { } span && !adminOnly ? now + span : null;
	}

	/// <summary>The sooner of two expiries, where null is never.</summary>
	private static DateTimeOffset? Earliest(DateTimeOffset? first, DateTimeOffset? second)
	{
		if (first is not { } a)
		{
			return second;
		}

		return second is { } b && b < a ? b : a;
	}

	/// <summary>The calling key's own record. The route needs a key, so it is null only if that key vanished mid-request.</summary>
	private static async Task<ApiKeyRecord?> CallerAsync(IApiKeyStore store, HttpContext context, CancellationToken cancellationToken)
	{
		return Actor(context) is { } id ? await store.FindAsync(id, cancellationToken) : null;
	}

	/// <summary>Null when the caller may hand out a key of this shape; otherwise the 403 that says which way it is wider.</summary>
	private static ProblemHttpResult? WiderThanCaller(ApiKeyRecord? caller, IReadOnlyCollection<string> scopes, IReadOnlyCollection<string> collections, bool allCollections, DateTimeOffset? expiresAt)
	{
		string? dimension = caller is null ? "scopes" : KeyGrant.Exceeds(caller, scopes, collections, allCollections, expiresAt);
		if (dimension is null)
		{
			return null;
		}

		return TypedResults.Problem(
			statusCode: StatusCodes.Status403Forbidden,
			title: "Key is wider than the caller",
			detail: $"A key cannot issue or rotate a key wider than itself, and this one would be wider in its {dimension}. "
				+ "A key that expires cannot make one that outlives it: name an earlier expiresAt, or use a key that does not expire.",
			extensions: new Dictionary<string, object?> { ["code"] = "key_exceeds_caller", ["dimension"] = dimension });
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

		ApiKeyRecord? caller = await CallerAsync(store, context, cancellationToken);
		if (WiderThanCaller(caller, record.Scopes, record.Collections, record.AllCollections, record.ExpiresAt) is { } wider)
		{
			return wider;
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
		else if (request.ExpiresAt > now + KeyGrant.MaxLifetime)
		{
			errors["expiresAt"] = [$"expiresAt may be at most {KeyGrant.MaxLifetime.TotalDays:0} days ahead. Issue a replacement before the key lapses."];
		}

		return errors;
	}

	private const string AdminWithCollections =
		"A key with the admin scope acts on every collection, so it cannot list collections. Issue the admin key without collections, "
		+ "with allCollections if it must also read or ingest, and give a bound key only read or ingest.";

	/// <summary>The refusal of <c>admin</c> beside a collection list carries a code, so a client can tell it from a typo.</summary>
	private static Dictionary<string, object?>? ValidationCode(CreateApiKeyRequest request)
	{
		return IsAdminWithCollections(request) ? new Dictionary<string, object?> { ["code"] = "admin_key_bound" } : null;
	}

	private static bool IsAdminWithCollections(CreateApiKeyRequest request)
	{
		return request.Scopes is { } scopes && scopes.Contains(MemoriaScopes.Admin, StringComparer.Ordinal) && request.Collections is not null;
	}

	private static string? CollectionsProblem(CreateApiKeyRequest request)
	{
		bool allCollections = request.AllCollections == true;
		if (request.Collections is not { } collections)
		{
			bool adminOnly = request.Scopes is { Count: > 0 } && request.Scopes.All(static scope => scope == MemoriaScopes.Admin);
			return allCollections || adminOnly ? null : "List the collections this key may read, or set allCollections to true to request every collection.";
		}

		if (IsAdminWithCollections(request))
		{
			return AdminWithCollections;
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
