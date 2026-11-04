namespace Memoria.Core.Contracts;

/// <summary>
/// Result of <c>GET /v1/search</c>. <see cref="Degraded"/> means the search ran without
/// something a full search uses: the results are valid but may rank worse.
/// <see cref="DegradedReasons"/> says what, as <see cref="DegradedReason"/> tokens only.
/// </summary>
public sealed record SearchResult(
	IReadOnlyList<SearchHit> Results,
	int Total,
	int TookMs,
	SearchQueryInfo Query,
	bool Degraded,
	IReadOnlyList<string> DegradedReasons);

/// <summary>What a degraded search went without. Names of capabilities, so any caller may see them.</summary>
public static class DegradedReason
{
	public const string NoRerank = "no_rerank";
	public const string NoExpansion = "no_expansion";
	public const string CpuEmbedding = "cpu_embedding";
	public const string NoVectors = "no_vectors";

	public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { NoRerank, NoExpansion, CpuEmbedding, NoVectors };
}

/// <summary>One ranked hit. <see cref="Line"/> is the 1-indexed line of the best chunk; pass it to the document endpoint as <c>fromLine</c>.</summary>
public sealed record SearchHit(
	string Docid,
	string File,
	string Collection,
	string Path,
	string Title,
	double Score,
	int Line,
	string Snippet,
	string? Context,
	DocumentMetadata? Metadata);

/// <summary>Echo of how the search was executed, after defaults and scoping were applied.</summary>
public sealed record SearchQueryInfo(
	IReadOnlyList<string> Collections,
	bool Rerank,
	bool Filtered,
	int Fetched,
	int Limit);
