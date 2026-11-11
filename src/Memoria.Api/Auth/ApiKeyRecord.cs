using Memoria.Api.Http;

namespace Memoria.Api.Auth;

/// <summary>A stored API key. The plaintext is never stored; <see cref="Sha256Hash"/> is the only secret-derived value.</summary>
public sealed record ApiKeyRecord(
	string Id,
	byte[] Sha256Hash,
	string Label,
	IReadOnlyList<string> Scopes,
	IReadOnlyList<string> Collections,
	bool AllCollections,
	DateTimeOffset CreatedAt,
	DateTimeOffset? ExpiresAt,
	DateTimeOffset? RevokedAt,
	DateTimeOffset? LastUsedAt)
{
	public bool IsExpired(DateTimeOffset now)
	{
		return ExpiresAt is { } expiresAt && expiresAt <= now;
	}

	public bool IsRevoked => RevokedAt is not null;

	public bool IsActive(DateTimeOffset now)
	{
		return !IsRevoked && !IsExpired(now);
	}

	/// <summary>An active key whose admin scope is honoured: one that holds it and lists no collections.</summary>
	public bool IsActiveAdmin(DateTimeOffset now)
	{
		return IsActive(now) && Scopes.Contains(MemoriaScopes.Admin, StringComparer.Ordinal) && !HoldsBoundAdmin;
	}

	/// <summary>
	/// The key holds <c>admin</c> beside a collection list. Admin is all-or-nothing, so such a key can no longer be
	/// issued, and one already in the database is not given the scope: see <see cref="KeyGrant"/>.
	/// </summary>
	public bool HoldsBoundAdmin => KeyGrant.IsBoundAdmin(Scopes, Collections, AllCollections);

	/// <summary>Creates a record and the plaintext token it was hashed from.</summary>
	public static (ApiKeyRecord Record, string Token) Issue(
		string label,
		IReadOnlyList<string> scopes,
		IReadOnlyList<string> collections,
		bool allCollections,
		DateTimeOffset? expiresAt,
		DateTimeOffset now)
	{
		string id = ApiKeyToken.NewId();
		string token = ApiKeyToken.NewToken(id);
		return (new ApiKeyRecord(id, ApiKeyToken.Hash(token), label, scopes, collections, allCollections, now, expiresAt, null, null), token);
	}
}

/// <summary>Durable storage for API keys. Kept behind an interface so tests and later backends can substitute it.</summary>
public interface IApiKeyStore
{
	/// <summary>Creates or migrates the schema. Called once at startup; a failure stops the host.</summary>
	Task InitializeAsync(CancellationToken cancellationToken = default);

	Task InsertAsync(ApiKeyRecord record, CancellationToken cancellationToken = default);

	Task<ApiKeyRecord?> FindAsync(string id, CancellationToken cancellationToken = default);

	Task<IReadOnlyList<ApiKeyRecord>> ListAsync(CancellationToken cancellationToken = default);

	/// <summary>Marks a key revoked. False when no such key exists. Revoking twice keeps the first timestamp.</summary>
	Task<bool> RevokeAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default);

	/// <summary>Swaps in a new hash, invalidating the old token at once. False unless the key exists and is not revoked.</summary>
	Task<bool> ReplaceHashAsync(string id, byte[] sha256Hash, CancellationToken cancellationToken = default);

	Task TouchLastUsedAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default);

	Task<bool> HasActiveAdminAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}
