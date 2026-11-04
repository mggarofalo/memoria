namespace Memoria.Core.Contracts;

/// <summary>A document as returned by <c>GET /v1/docs/{id}</c> and the batch endpoint.</summary>
public sealed record Document(
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

/// <summary>Body of <c>POST /v1/docs:multiGet</c>. At least one of <see cref="Ids"/> or <see cref="Pattern"/> is required.</summary>
public sealed record MultiGetRequest(
	IReadOnlyList<string>? Ids,
	string? Pattern,
	int? MaxBytes,
	int? MaxLines,
	bool? LineNumbers,
	bool? IncludeBody);

/// <summary>
/// Result of the batch endpoint. Skipped entries name documents that were matched but not
/// returned (per-document cap, response budget). <see cref="Truncated"/> means globs matched
/// more documents than one request may select, and the rest were not looked at.
/// </summary>
public sealed record MultiGetResult(
	IReadOnlyList<Document> Docs,
	IReadOnlyList<SkippedDocument> Skipped,
	IReadOnlyList<string> Errors,
	bool Truncated);

public sealed record SkippedDocument(string File, string Reason);
