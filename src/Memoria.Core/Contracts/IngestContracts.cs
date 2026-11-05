namespace Memoria.Core.Contracts;

/// <summary>
/// Body of <c>POST /v1/documents</c>. <see cref="Source"/> and <see cref="SourceId"/> together are the
/// document's identity: sending them again overwrites. <see cref="Updated"/> orders versions, so an older
/// copy cannot replace a newer one.
/// </summary>
public sealed record DocumentIngestRequest(
	string? Collection,
	string? Source,
	string? SourceId,
	string? Body,
	DateTimeOffset? Updated,
	string? Url = null,
	string? Title = null,
	string? Project = null,
	IReadOnlyList<string>? Tags = null);

/// <summary>A field or message replaced because the check on a finished document found a credential in it. Names only: never a value.</summary>
/// <param name="Part">The 1-based part, or 0 for something belonging to the whole session.</param>
/// <param name="Field">The frontmatter key, <c>title</c>, or <c>message:N</c>.</param>
/// <param name="Rule">The rule that found it.</param>
public sealed record IngestRefusal(int Part, string Field, string Rule);

/// <summary>Result of <c>POST /v1/documents</c>. What was stored is redacted; <see cref="Redactions"/> says how much was replaced, by rule.</summary>
public sealed record DocumentIngestResult(
	string Collection,
	string Path,
	string File,
	long Bytes,
	bool Created,
	string Redaction,
	IReadOnlyDictionary<string, int> Redactions,
	IReadOnlyList<IngestRefusal> Refusals,
	string JobId);

/// <summary>Whether what was stored went through the redactor. Only an operator's configuration decides, per collection.</summary>
public static class IngestRedaction
{
	public const string Enabled = "enabled";

	/// <summary>An operator turned redaction off for this collection, so the text was stored as it came and the counts are empty.</summary>
	public const string Disabled = "disabled";
}

/// <summary>
/// Result of <c>DELETE /v1/documents</c>. The document stays searchable until the index changes, and
/// <see cref="ReindexState"/> says when that will be. <see cref="ReindexJobId"/> is the update when one started
/// now, and <see cref="ReindexInSeconds"/> the wait when one is coming because another just ran.
/// </summary>
public sealed record DocumentDeleteResult(
	string Collection,
	string Path,
	bool Deleted,
	string ReindexState,
	string? ReindexJobId = null,
	int? ReindexInSeconds = null);

/// <summary>What happened about the index update a delete needs. See <see cref="DocumentDeleteResult"/>.</summary>
public static class ReindexState
{
	/// <summary>Nothing changed, so no update is needed.</summary>
	public const string NotNeeded = "not_needed";

	public const string Started = "started";

	/// <summary>An update just ran in this collection. Another starts when the interval ends.</summary>
	public const string Scheduled = "scheduled";

	/// <summary>The update could not start. The file is gone but stays searchable until the sidecar's own delayed update, or until this request is repeated.</summary>
	public const string Failed = "failed";
}

/// <summary>One part of a session upload. <see cref="State"/> is one of <see cref="SessionPartState"/>.</summary>
public sealed record SessionPartResult(
	int Part,
	string Path,
	string State,
	string? Code = null,
	string? Message = null,
	bool Retryable = false,
	int? RetryAfterSeconds = null);

public static class SessionPartState
{
	public const string Written = "written";
	public const string Failed = "failed";

	/// <summary>The backend stopped answering before this part's turn. Retry the session.</summary>
	public const string NotAttempted = "not_attempted";
}

/// <summary>Older parts of a session that this upload no longer has.</summary>
/// <param name="Deleted">Removed, and being removed from search.</param>
/// <param name="Failed">Could not be removed. Retry the session.</param>
/// <param name="Kept">Left alone, because this upload lost content or was empty and may not be the whole session.</param>
public sealed record SessionSupersededResult(
	IReadOnlyList<string> Deleted,
	IReadOnlyList<string> Failed,
	IReadOnlyList<string> Kept);

/// <summary>What happened to a transcript's lines. Counts only.</summary>
public sealed record SessionReadSummary(
	long BytesRead,
	long Lines,
	long Messages,
	long MalformedLines,
	long OversizedLines,
	long UnterminatedLines,
	long MessagesOverLimit,
	bool SkippedContent);

/// <summary>
/// Result of <c>POST /v1/ingest/session</c>. <see cref="Complete"/> is true only when the transcript had messages and lost none,
/// every part was written and every superseded part dealt with. Otherwise the answer is 207, the session's state stays
/// <c>partial</c>, and sending the same upload again finishes it when <see cref="Retryable"/> is true.
/// </summary>
public sealed record SessionIngestResult(
	string Collection,
	string SessionId,
	string Format,
	bool Complete,
	bool Retryable,
	IReadOnlyList<SessionPartResult> Parts,
	SessionSupersededResult Superseded,
	string Redaction,
	IReadOnlyDictionary<string, int> Redactions,
	IReadOnlyList<IngestRefusal> Refusals,
	int RefusalCount,
	SessionReadSummary Read,
	string? JobId,
	string? ReindexJobId,
	string ReindexState);

/// <summary>Result of <c>GET /v1/ingest/session</c>. <see cref="State"/> is one of <see cref="IngestStateName"/>, and only <c>complete</c> means all of it arrived.</summary>
public sealed record SessionIngestStatus(
	string Collection,
	string SessionId,
	string State,
	DateTimeOffset? Updated,
	IReadOnlyList<string> Paths);

public static class IngestStateName
{
	/// <summary>Nothing recorded.</summary>
	public const string None = "none";

	/// <summary>An upload is running.</summary>
	public const string Writing = "writing";

	/// <summary>Not all of the session is stored: an upload stopped, lost content, or a part was deleted. Some parts may be searchable.</summary>
	public const string Partial = "partial";

	public const string Complete = "complete";

	/// <summary>Deleted. Only a newer version can bring it back.</summary>
	public const string Deleted = "deleted";
}
