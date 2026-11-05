using System.Security.Cryptography;
using System.Text;
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

	/// <summary>
	/// A message longer than this, in UTF-8 bytes, is split so its pieces land in separate parts. It is the sidecar's 2 MiB
	/// document cap less room for the frontmatter (<see cref="SessionFrontmatter.MaxBytes"/>), the title and the headings.
	/// </summary>
	public int MaxMessageBytes { get; init; } = (2 * 1024 * 1024) - (16 * 1024);
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

	/// <summary>
	/// True when transcript content did not make it: lines not read, or messages left out. What is stored is then not the whole session.
	/// Unlike <see cref="HasSkippedContent"/> this leaves out frontmatter lists cut to fit, as the messages behind them are all kept.
	/// </summary>
	public bool HasLostContent =>
		MalformedLines + NonObjectLines + OversizedLines + UnterminatedLines + InvalidKeys + MessagesOverLimit + FilesOverLimit > 0;

	/// <summary>True when lines were lost from somewhere other than the end, so the messages kept are not a true start of the session.</summary>
	public bool HasHoles => MalformedLines + NonObjectLines + OversizedLines + InvalidKeys > 0;

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

/// <summary>A field or message the check on a finished part replaced with a marker. Names only, so it is safe to log.</summary>
/// <param name="Part">The 1-based part number.</param>
/// <param name="Field">The frontmatter key, <c>title</c>, or <c>message:N</c> with N the message's index in the session.</param>
/// <param name="Rule">The rule that found it.</param>
public sealed record SessionFieldRefusal(int Part, string Field, string Rule);

/// <summary>A rendered session: every part, in order, with what was skipped along the way.</summary>
public sealed class RedactedSession
{
	private readonly Func<string, bool> _isInSessionFamily;
	private readonly SessionPathing _pathing;

	internal RedactedSession(
		SessionFormat format,
		string sessionId,
		IReadOnlyList<RedactedSessionDocument> documents,
		SessionReadReport report,
		IReadOnlyDictionary<string, int> countsByRule,
		IReadOnlyList<SessionFieldRefusal> refusals,
		Func<string, bool> isInSessionFamily,
		SessionPathing pathing)
	{
		_pathing = pathing;
		Format = format;
		SessionId = sessionId;
		Documents = documents;
		Report = report;
		CountsByRule = countsByRule;
		Refusals = refusals;
		_isInSessionFamily = isInSessionFamily;
	}

	public SessionFormat Format { get; }

	/// <summary>
	/// What this session's paths are made from, for keying anything that orders or locks them. Two uploads share this exactly
	/// when they are the same session, whatever else they say. Holds raw ids, so digest it before storing it.
	/// </summary>
	public string PathIdentity => _pathing.Identity;

	/// <summary>The short part of the file names, which another session can share. Goes with <see cref="ExistingPathsGlob"/>.</summary>
	public string PathStem => _pathing.Stem;

	/// <summary>
	/// A digest of this session's first <paramref name="messages"/> messages, as redacted, or null when it has fewer. A later upload that starts with the
	/// same messages gives the same digest, whatever follows and however it splits into parts, so comparing these says whether stored text would be lost.
	/// </summary>
	public string? MessageDigestAt(long messages)
	{
		return messages < 0 || messages > MessageChain.Count ? null : MessageChain.DigestAt((int)messages);
	}

	internal MessageChain MessageChain { get; init; } = MessageChain.Empty;

	/// <summary>
	/// Whether this session may replace documents exported under its id. False unless it is known to be the same copy: exports may be the only record of a fork, so a double is better than a loss.
	/// </summary>
	public bool TakesOverExports { get; init; }

	/// <summary>True when the transcript names a different session from the one the caller said. Its parts would land on the other session's files, so refuse it.</summary>
	public bool NamesAnotherSession => _pathing.NamesAnotherSession;

	public string SessionId { get; }

	/// <summary>The parts in order. Empty when the session has no user or assistant text yet.</summary>
	public IReadOnlyList<RedactedSessionDocument> Documents { get; }

	public SessionReadReport Report { get; }

	/// <summary>Replacements across the whole session, by rule name, for a redaction report. Never holds a matched value.</summary>
	public IReadOnlyDictionary<string, int> CountsByRule { get; }

	/// <summary>Fields and messages replaced by the check on finished parts. A hit here means assembly made a match, so it is worth an operator's attention.</summary>
	public IReadOnlyList<SessionFieldRefusal> Refusals { get; }

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

	/// <summary>
	/// The same session under paths carrying a digest of its id, for when another session already has its short stem. Each call goes
	/// one longer. Null when there is nothing longer to try.
	/// </summary>
	public RedactedSession? WithDistinctPaths()
	{
		if (_pathing.Distinct() is not { } distinct)
		{
			return null;
		}

		RedactedSessionDocument[] documents = [.. Documents.Select(document => document with { Path = SessionPaths.ReplaceStem(document.Path, _pathing.Stem, distinct.Stem) })];
		return new RedactedSession(Format, SessionId, documents, Report, CountsByRule, Refusals, distinct.IsFamily, distinct) { MessageChain = MessageChain, TakesOverExports = TakesOverExports };
	}
}

/// <summary>How one session's paths are built, kept together so everything that decides who owns a path agrees.</summary>
internal sealed record SessionPathing(string Identity, string Stem, Func<string, bool> IsFamily, Func<string, SessionPathing> WithStem, string FullId, bool NamesAnotherSession = false)
{
	/// <summary>Longer each time, as each one tried was taken.</summary>
	private static readonly int[] s_digestChars = [8, 16, 32];

	private string BaseStem { get; init; } = Stem;

	private int Level { get; init; }

	/// <summary>The next stem to try, or null when even the longest is taken, which takes someone squatting on purpose.</summary>
	public SessionPathing? Distinct()
	{
		return Level >= s_digestChars.Length
			? null
			: WithStem($"{BaseStem}-{SessionPaths.DistinctDigest(FullId, s_digestChars[Level])}") with { BaseStem = BaseStem, Level = Level + 1 };
	}
}

/// <summary>One digest per message, each over the one before and the message's text, so a digest stands for everything up to it.</summary>
internal sealed class MessageChain
{
	private const int DigestBytes = 32;

	private readonly byte[] _digests;

	private MessageChain(byte[] digests)
	{
		_digests = digests;
	}

	public static MessageChain Empty { get; } = new([]);

	public int Count => _digests.Length / DigestBytes;

	public static MessageChain Of(IReadOnlyList<SessionMessage> messages)
	{
		byte[] digests = new byte[messages.Count * DigestBytes];
		for (int i = 0; i < messages.Count; i++)
		{
			using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			hash.AppendData(digests, Math.Max(0, (i - 1) * DigestBytes), i == 0 ? 0 : DigestBytes);
			hash.AppendData(Encoding.UTF8.GetBytes(messages[i].Role + "\0"));
			hash.AppendData(Encoding.UTF8.GetBytes(messages[i].Text));
			hash.GetHashAndReset(digests.AsSpan(i * DigestBytes, DigestBytes));
		}

		return new MessageChain(digests);
	}

	public string DigestAt(int messages)
	{
		return messages == 0 ? "" : Convert.ToHexStringLower(_digests, (messages - 1) * DigestBytes, DigestBytes);
	}
}
