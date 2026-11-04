namespace Memoria.Core.Contracts;

/// <summary>
/// Body of <c>POST /v1/admin/keys</c>. An all-collections key needs
/// <see cref="AllCollections"/> set explicitly; it never results from an omission
/// or an empty <see cref="Collections"/> list.
/// </summary>
public sealed record CreateApiKeyRequest(
	string? Label,
	IReadOnlyList<string>? Scopes,
	IReadOnlyList<string>? Collections,
	bool? AllCollections,
	DateTimeOffset? ExpiresAt);

/// <summary>An API key's public description. Never carries key material.</summary>
public sealed record ApiKeyInfo(
	string Id,
	string Label,
	IReadOnlyList<string> Scopes,
	IReadOnlyList<string> Collections,
	bool AllCollections,
	DateTimeOffset CreatedAt,
	DateTimeOffset? ExpiresAt,
	DateTimeOffset? RevokedAt,
	DateTimeOffset? LastUsedAt);

/// <summary>Result of issuing or rotating a key. <see cref="Key"/> is the only copy of the plaintext that will ever exist.</summary>
public sealed record IssuedApiKey(string Key, ApiKeyInfo Info);

/// <summary>Result of <c>GET /v1/admin/keys</c>.</summary>
public sealed record ApiKeyList(IReadOnlyList<ApiKeyInfo> Keys);
