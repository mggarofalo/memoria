namespace Memoria.Core.Contracts;

/// <summary>Result of <c>GET /v1/collections</c>.</summary>
public sealed record CollectionList(IReadOnlyList<CollectionInfo> Collections);

/// <summary>
/// A collection as the public API describes it. The sidecar also knows the
/// root directory and glob on its host; those are deployment details and are
/// deliberately not part of this contract.
/// </summary>
public sealed record CollectionInfo(
	string Name,
	int Documents,
	string? LastModified,
	bool IncludeByDefault,
	string? Context);

/// <summary>
/// Result of <c>GET /v1/status</c>. For a collection-scoped caller the totals
/// cover only their collections; <see cref="NeedsEmbedding"/> is null then
/// because QMD reports that backlog only for the whole index.
/// <see cref="DocumentsSetAside"/>, documents out of vector search because a worker
/// refused them, is reported only to admin keys.
/// </summary>
public sealed record IndexStatus(
	int TotalDocuments,
	int? NeedsEmbedding,
	bool HasVectorIndex,
	IReadOnlyList<IndexCollectionStatus> Collections,
	int? DocumentsSetAside);

public sealed record IndexCollectionStatus(
	string Name,
	int Documents,
	string LastUpdated);
