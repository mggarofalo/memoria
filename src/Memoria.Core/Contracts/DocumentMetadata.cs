namespace Memoria.Core.Contracts;

/// <summary>
/// Fields lifted from a document's YAML frontmatter by the sidecar. Null on a
/// document that has none. Shared verbatim between the sidecar contract and
/// Memoria's public v1 contract because the shape is QMD-independent.
/// </summary>
public sealed record DocumentMetadata(
	string? Project,
	string? Branch,
	string? Date,
	string? StartTime,
	string? EndTime,
	IReadOnlyList<string> Tags,
	string? SessionId,
	string? Source);
