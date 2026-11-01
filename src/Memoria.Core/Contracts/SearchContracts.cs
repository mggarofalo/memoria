namespace Memoria.Core.Contracts;

/// <summary>Result of <c>GET /v1/search</c>.</summary>
public sealed record SearchResult(
	IReadOnlyList<SearchHit> Results,
	int Total,
	int TookMs,
	SearchQueryInfo Query);

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
