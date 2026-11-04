using Memoria.Core.Redaction;

namespace Memoria.Core.Sessions;

/// <summary>The transcript format of a raw session payload.</summary>
public enum SessionFormat
{
	ClaudeCode,
	Codex,
}

/// <summary>What the caller knows about a payload beyond its bytes.</summary>
/// <param name="Format">The transcript format.</param>
/// <param name="SessionId">
/// The session's identity, normally the transcript file's name without its extension. For
/// Claude Code it is authoritative, because a resumed session's records can name another id.
/// For Codex a UUID is taken out of it, and a <c>session_meta</c> record overrides it.
/// </param>
public sealed record SessionSource(SessionFormat Format, string SessionId)
{
	/// <summary>The Claude Code project directory name, used for the project only when no record carries a cwd.</summary>
	public string? ProjectDirectory { get; init; }

	/// <summary>
	/// The session start when no record has a usable timestamp. Defaults to the Unix epoch.
	/// This is an input rather than the clock or a file time so output stays deterministic.
	/// </summary>
	public DateTimeOffset? FallbackStart { get; init; }

	/// <summary>
	/// The home directory of the machine that produced the transcript. When null it is inferred
	/// from the shape of the path (<c>/Users/x</c>, <c>/home/x</c>, <c>/root</c>, <c>C:\Users\x</c>).
	/// </summary>
	public string? HomeDirectory { get; init; }
}

/// <summary>Limits for one render. The defaults are far above what real sessions need.</summary>
public sealed record SessionRenderOptions
{
	public static SessionRenderOptions Default { get; } = new();

	/// <summary>The part-splitting threshold, in code points. Matches the exporter's default.</summary>
	public int MaxPartChars { get; init; } = 65536;

	/// <summary>Longer lines are discarded without being buffered. Matches the sidecar's default body limit.</summary>
	public int MaxLineBytes { get; init; } = 16 * 1024 * 1024;

	/// <summary>
	/// The most text kept in memory for one session, in UTF-16 code units. Once it is reached every
	/// later message and file is dropped and counted, so the result is a prefix of the transcript.
	/// </summary>
	public long MaxRetainedChars { get; init; } = 32 * 1024 * 1024;
}

/// <summary>
/// What happened while reading a payload. Counts only: record contents, including their type
/// names and any parser message, are attacker-influenced and never appear here.
/// </summary>
public sealed class SessionReadReport
{
	public long BytesRead { get; internal set; }

	/// <summary>Lines seen, including every line counted below.</summary>
	public long Lines { get; internal set; }

	/// <summary>Lines that parsed as a JSON object.</summary>
	public long Records { get; internal set; }

	public long BlankLines { get; internal set; }

	/// <summary>Lines that are not valid UTF-8 or not valid JSON, or are nested too deeply.</summary>
	public long MalformedLines { get; internal set; }

	/// <summary>Valid JSON lines that are not objects.</summary>
	public long NonObjectLines { get; internal set; }

	/// <summary>Lines longer than <see cref="SessionRenderOptions.MaxLineBytes"/>, discarded unread.</summary>
	public long OversizedLines { get; internal set; }

	/// <summary>A final line with no newline. The writer may still be mid-record, so it is not read.</summary>
	public long UnterminatedLines { get; internal set; }

	/// <summary>Records of a type the renderer does not use. Routine: progress and system events land here.</summary>
	public long UnrenderedRecords { get; internal set; }

	/// <summary>Timestamps that were present but unusable, so their record contributes no time.</summary>
	public long InvalidTimestamps { get; internal set; }

	/// <summary>Strings holding an unpaired surrogate, kept with U+FFFD in its place.</summary>
	public long RepairedStrings { get; internal set; }

	/// <summary>Object keys holding an unpaired surrogate, which were skipped.</summary>
	public long InvalidKeys { get; internal set; }

	/// <summary>Metadata values cut to the field limit, because frontmatter must fit the sidecar's metadata head.</summary>
	public long ClippedFields { get; internal set; }

	/// <summary>Touched files cut so the frontmatter closes inside the sidecar's window, as <c>files_touched_truncated</c> records.</summary>
	public long FilesTruncated { get; internal set; }

	/// <summary>Codex turn ids cut so the frontmatter closes inside the sidecar's window, as <c>turn_ids_truncated</c> records.</summary>
	public long TurnIdsTruncated { get; internal set; }

	public long Messages { get; internal set; }

	/// <summary>Messages dropped once <see cref="SessionRenderOptions.MaxRetainedChars"/> was reached.</summary>
	public long MessagesOverLimit { get; internal set; }

	/// <summary>Touched-file entries dropped once <see cref="SessionRenderOptions.MaxRetainedChars"/> was reached.</summary>
	public long FilesOverLimit { get; internal set; }

	/// <summary>True when input was lost: anything other than routine, unrendered record types.</summary>
	public bool HasSkippedContent =>
		MalformedLines + NonObjectLines + OversizedLines + UnterminatedLines + InvalidKeys + MessagesOverLimit + FilesOverLimit + FilesTruncated + TurnIdsTruncated > 0;
}

/// <summary>One part of a rendered session, redacted and ready to store.</summary>
/// <param name="Path">The collection-relative path. Deterministic, and valid for the sidecar's ingest route.</param>
/// <param name="Part">The 1-based part number.</param>
/// <param name="MessageStart">The 0-based index of the part's first message within the session.</param>
/// <param name="MessageCount">The number of messages in the part.</param>
/// <param name="Content">The rendered markdown, redacted, having passed the check on the finished document.</param>
public sealed record RedactedSessionDocument(
	string Path,
	int Part,
	int MessageStart,
	int MessageCount,
	RedactedMarkdown Content);

/// <summary>A rendered session: every part, in order, with what was skipped along the way.</summary>
public sealed class RedactedSession
{
	private readonly Func<string, bool> _isInSessionFamily;

	internal RedactedSession(
		SessionFormat format,
		string sessionId,
		IReadOnlyList<RedactedSessionDocument> documents,
		SessionReadReport report,
		IReadOnlyDictionary<string, int> countsByRule,
		Func<string, bool> isInSessionFamily)
	{
		Format = format;
		SessionId = sessionId;
		Documents = documents;
		Report = report;
		CountsByRule = countsByRule;
		_isInSessionFamily = isInSessionFamily;
	}

	public SessionFormat Format { get; }

	public string SessionId { get; }

	/// <summary>The parts in order. Empty when the session has no user or assistant text yet.</summary>
	public IReadOnlyList<RedactedSessionDocument> Documents { get; }

	public SessionReadReport Report { get; }

	/// <summary>Replacements across the whole session, by rule name, for a redaction report. Never holds a matched value.</summary>
	public IReadOnlyDictionary<string, int> CountsByRule { get; }

	/// <summary>
	/// True when an existing corpus path belongs to this session but is no longer among its parts.
	/// A session that shrinks, or whose start date changes, leaves such files behind. Overwriting
	/// alone cannot remove them, so re-ingest should delete every existing path this accepts.
	/// </summary>
	public bool IsSupersededPath(string existingPath)
	{
		ArgumentNullException.ThrowIfNull(existingPath);
		return _isInSessionFamily(existingPath)
			&& !Documents.Any(document => string.Equals(document.Path, existingPath, StringComparison.Ordinal));
	}
}
