using System.Text.Json;
using System.Text.Json.Serialization;
using Memoria.Core.Contracts;

namespace Memoria.Core.Sidecar;

// Wire types for the memoria-qmd sidecar. Property names and nullability
// follow sidecar/openapi.json; SidecarContractTests fails when the committed
// document stops matching these records.

/// <summary>Error envelope every non-2xx sidecar response carries.</summary>
public sealed record SidecarError(string Error, string Message, JsonElement? Details);

/// <summary><c>GET /health</c>.</summary>
public sealed record SidecarHealth(string Status, string Version, int UptimeSeconds);

/// <summary><c>GET /ready</c>. The same body is returned with 200 and 503.</summary>
public sealed record SidecarReadiness(
	string Status,
	string Version,
	int UptimeSeconds,
	SidecarIndexInfo Index,
	SidecarModelsInfo Models,
	SidecarWarmupInfo Warmup,
	SidecarMetadataIndexInfo Metadata,
	SidecarJobsInfo Jobs);

public sealed record SidecarIndexInfo(string DbPath, int TotalDocuments, int NeedsEmbedding, bool HasVectorIndex);

public sealed record SidecarModelsInfo(string Embed, string Rerank, string Generate, SidecarModelsLoaded Loaded, bool Resident);

public sealed record SidecarModelsLoaded(bool Embed, bool Rerank, bool Generate);

public sealed record SidecarWarmupInfo(bool Requested, bool Done, string? Error, double? DurationMs);

public sealed record SidecarMetadataIndexInfo(int Documents, string? RefreshedAt);

public sealed record SidecarJobsInfo(int Pending);

/// <summary>Readiness as the client reports it: <see cref="Ready"/> is the HTTP outcome, <see cref="Details"/> the body.</summary>
public sealed record SidecarReadinessResult(bool Ready, SidecarReadiness Details);

/// <summary><c>GET /status</c>.</summary>
public sealed record SidecarStatus(
	int TotalDocuments,
	int NeedsEmbedding,
	bool HasVectorIndex,
	IReadOnlyList<SidecarStatusCollection> Collections);

public sealed record SidecarStatusCollection(string Name, string? Path, string? Pattern, int Documents, string LastUpdated);

/// <summary>Sub-query kinds accepted by <c>POST /search</c>.</summary>
public static class SidecarQueryType
{
	public const string Lex = "lex";
	public const string Vec = "vec";
	public const string Hyde = "hyde";
}

public sealed record SidecarSubQuery(string Type, string Query);

/// <summary>Frontmatter filters, applied by the sidecar after retrieval.</summary>
public sealed record SidecarSearchFilters(
	IReadOnlyList<string>? Project = null,
	IReadOnlyList<string>? Branch = null,
	string? Since = null,
	string? Until = null,
	IReadOnlyList<string>? Tags = null)
{
	public bool IsEmpty =>
		(Project is null || Project.Count == 0)
		&& (Branch is null || Branch.Count == 0)
		&& Since is null
		&& Until is null
		&& (Tags is null || Tags.Count == 0);
}

/// <summary>Body of <c>POST /search</c>. Exactly one of <see cref="Query"/> or <see cref="Searches"/> must be set.</summary>
public sealed record SidecarSearchRequest
{
	public string? Query { get; init; }
	public IReadOnlyList<SidecarSubQuery>? Searches { get; init; }
	public string? Intent { get; init; }
	public IReadOnlyList<string>? Collections { get; init; }
	public SidecarSearchFilters? Filters { get; init; }
	public int? Limit { get; init; }
	public double? MinScore { get; init; }
	public int? CandidateLimit { get; init; }
	public bool? Rerank { get; init; }
}

public sealed record SidecarSearchResponse(
	IReadOnlyList<SidecarSearchHit> Results,
	int Total,
	int TookMs,
	SidecarSearchQueryInfo Query);

public sealed record SidecarSearchHit(
	string Docid,
	string File,
	string Collection,
	string Path,
	string Title,
	double Score,
	string? Context,
	int Line,
	string Snippet,
	DocumentMetadata? Metadata);

public sealed record SidecarSearchQueryInfo(
	IReadOnlyList<string> Collections,
	bool Rerank,
	bool Filtered,
	int Fetched,
	int Limit);

/// <summary>Query string of <c>GET /docs/{reference}</c>.</summary>
public sealed record SidecarDocumentQuery(
	int? FromLine = null,
	int? MaxLines = null,
	bool? LineNumbers = null,
	bool? Body = null,
	IReadOnlyList<string>? Collections = null,
	bool? SimilarFiles = null);

public sealed record SidecarDocument(
	string Docid,
	string File,
	string Collection,
	string Path,
	string Title,
	string? Context,
	string ModifiedAt,
	int BodyLength,
	int? FromLine,
	int? MaxLines,
	string? Body,
	DocumentMetadata? Metadata);

/// <summary>Body of <c>POST /docs:multiGet</c>.</summary>
public sealed record SidecarMultiGetRequest
{
	public string? Pattern { get; init; }
	public IReadOnlyList<string>? Ids { get; init; }

	/// <summary>Match and read only inside these collections. Empty means nothing, not everything.</summary>
	public IReadOnlyList<string>? Collections { get; init; }

	/// <summary>False skips QMD's scan of every path on a miss, and any suggestions.</summary>
	public bool? SimilarFiles { get; init; }

	/// <summary>Budget for all bodies in the response. The sidecar stops reading at it.</summary>
	public int? MaxTotalBytes { get; init; }

	public int? MaxBytes { get; init; }
	public int? MaxLines { get; init; }
	public bool? LineNumbers { get; init; }
	public bool? IncludeBody { get; init; }
}

public sealed record SidecarMultiGetResponse(
	IReadOnlyList<SidecarDocument> Docs,
	IReadOnlyList<SidecarSkippedDocument> Skipped,
	IReadOnlyList<string> Errors,
	bool Truncated = false);

public sealed record SidecarSkippedDocument(string File, string Reason);

/// <summary><c>GET /collections</c>.</summary>
public sealed record SidecarCollectionsResponse(IReadOnlyList<SidecarCollection> Collections);

public sealed record SidecarCollection(
	string Name,
	string Path,
	string Pattern,
	int Documents,
	int TotalDocuments,
	string? LastModified,
	bool IncludeByDefault,
	string? Context);

/// <summary>Body of <c>POST /documents</c>. <see cref="Content"/> must already be redacted.</summary>
public sealed record SidecarIngestRequest(
	string Collection,
	string Path,
	string Content,
	bool? Embed = null,
	bool? Overwrite = null);

public sealed record SidecarIngestResponse(
	string Collection,
	string Path,
	string File,
	long Bytes,
	bool Created,
	DocumentMetadata? Metadata,
	SidecarJob Job);

/// <summary>Body of <c>POST /reindex</c>.</summary>
public sealed record SidecarReindexRequest(
	IReadOnlyList<string>? Collections = null,
	bool? Embed = null,
	bool? Force = null);

public sealed record SidecarJobResponse(SidecarJob Job);

public sealed record SidecarJobsResponse(IReadOnlyList<SidecarJob> Jobs);

/// <summary>States a reindex job moves through.</summary>
public static class SidecarJobState
{
	public const string Queued = "queued";
	public const string Running = "running";
	public const string Succeeded = "succeeded";
	public const string Failed = "failed";
}

public sealed record SidecarJob(
	string Id,
	string State,
	SidecarJobRequest Request,
	string CreatedAt,
	string? StartedAt,
	string? FinishedAt,
	SidecarJobResult? Result,
	string? Error);

public sealed record SidecarJobRequest(IReadOnlyList<string>? Collections, bool Embed, bool Force, string Reason);

public sealed record SidecarJobResult(SidecarUpdateSummary Update, SidecarEmbedSummary? Embed, SidecarMetadataSummary Metadata);

public sealed record SidecarUpdateSummary(int Collections, int Indexed, int Updated, int Unchanged, int Removed, int Skipped, int NeedsEmbedding);

public sealed record SidecarEmbedSummary(int DocsProcessed, int ChunksEmbedded, int Errors, double DurationMs);

public sealed record SidecarMetadataSummary(int Documents, int WithMetadata);

/// <summary>Source-generated serializer metadata for every sidecar wire type.</summary>
[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SidecarError))]
[JsonSerializable(typeof(SidecarHealth))]
[JsonSerializable(typeof(SidecarReadiness))]
[JsonSerializable(typeof(SidecarStatus))]
[JsonSerializable(typeof(SidecarSearchRequest))]
[JsonSerializable(typeof(SidecarSearchResponse))]
[JsonSerializable(typeof(SidecarDocument))]
[JsonSerializable(typeof(SidecarMultiGetRequest))]
[JsonSerializable(typeof(SidecarMultiGetResponse))]
[JsonSerializable(typeof(SidecarCollectionsResponse))]
[JsonSerializable(typeof(SidecarIngestRequest))]
[JsonSerializable(typeof(SidecarIngestResponse))]
[JsonSerializable(typeof(SidecarReindexRequest))]
[JsonSerializable(typeof(SidecarJobResponse))]
[JsonSerializable(typeof(SidecarJobsResponse))]
public sealed partial class SidecarJsonContext : JsonSerializerContext;
