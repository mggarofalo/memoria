using System.Diagnostics;
using System.Text.Json;
using Memoria.Core.Redaction;

namespace Memoria.Core.Sessions;

/// <summary>
/// Turns a raw session transcript into the markdown corpus's documents. Pure: it reads the
/// stream and nothing else, not the clock, the host's home directory, or the filesystem, so
/// the same input always yields the same paths and bytes and re-ingest overwrites.
/// Redaction is part of rendering and cannot be skipped: every metadata string and all the message
/// text are redacted before anything is derived from them. Each finished part is then checked again,
/// and a field or message that still holds a credential is replaced and reported, while the rest is
/// indexed. A field or message that redaction cannot finish is replaced and reported the same way, so hostile
/// text cannot keep a session out of the index. Only a blown overall budget or an internal fault throws, with
/// the read report attached.
/// </summary>
public static class SessionRenderer
{
	/// <summary>Renders one session. The stream is read to its end and is not disposed.</summary>
	/// <exception cref="ArgumentException">The session id is blank or an option is out of range.</exception>
	/// <exception cref="RedactionException">The overall budget ran out or an internal fault made the output untrustworthy. Refuse the session.</exception>
	public static async Task<RedactedSession> RenderAsync(
		Stream payload,
		SessionSource source,
		SessionRenderOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(payload);
		ArgumentNullException.ThrowIfNull(source);
		options ??= SessionRenderOptions.Default;
		Validate(source, options);

		SessionReadReport report = new();
		SessionTranscript transcript = source.Format switch
		{
			SessionFormat.ClaudeCode => new ClaudeTranscript(source, options, report),
			SessionFormat.Codex => new CodexTranscript(source, options, report),
			_ => throw new ArgumentException("Unknown session format.", nameof(source)),
		};
		try
		{
			await JsonlReader.ReadAsync(payload, options.MaxLineBytes, report, transcript.Add, cancellationToken);
			return transcript.Render();
		}
		catch (RedactionException exception)
		{
			throw new RedactionException(exception.RuleName, exception.Failure, exception.Part) { ReadReport = report };
		}
	}

	private static void Validate(SessionSource source, SessionRenderOptions options)
	{
		if (string.IsNullOrWhiteSpace(source.SessionId))
		{
			throw new ArgumentException("A session id is required.", nameof(source));
		}
		if (options.MaxPartChars < 1 || options.MaxLineBytes < 1 || options.MaxRetainedChars < 1)
		{
			throw new ArgumentException("Render limits must be positive.", nameof(options));
		}
	}
}

/// <summary>The state shared by both formats: time bounds, the message list, and the memory budget.</summary>
internal abstract class SessionTranscript(SessionSource source, SessionRenderOptions options, SessionReadReport report)
{
	/// <summary>Per-entry overhead charged to the budget, so many tiny entries cannot evade it.</summary>
	protected const int EntryOverhead = 64;

	/// <summary>Rounds of replacing messages before every remaining message is replaced. Each round's cost is about one redaction per doubling of the message count.</summary>
	private const int MaxMessageRounds = 8;

	private readonly Dictionary<string, string> _redactedMetadata = new(StringComparer.Ordinal);
	private readonly Dictionary<int, string> _refusedMessages = [];
	private long _retained;
	private bool _overBudget;

	protected SessionSource Source { get; } = source;

	protected SessionRenderOptions Options { get; } = options;

	protected SessionReadReport Report { get; } = report;

	protected List<SessionMessage> Messages { get; } = [];

	private SessionTimestamp? First { get; set; }

	public abstract void Add(JsonElement record);

	/// <summary>Replacements made so far, by rule name, across metadata and messages.</summary>
	protected Dictionary<string, int> CountsByRule { get; } = [];

	/// <summary>Fields and messages the check on a finished part replaced.</summary>
	protected List<SessionFieldRefusal> Refusals { get; } = [];

	public abstract RedactedSession Render();

	/// <summary>The first record's offset is kept on a tie, as the exporter does, because the date comes from it.</summary>
	protected void NoteTime(SessionTimestamp? time)
	{
		if (time is { } value && (First is not { } first || value.Instant < first.Instant))
		{
			First = value;
		}
	}

	protected SessionTimestamp? ReadTime(JsonElement holder, string name)
	{
		if (!holder.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
		{
			return null;
		}
		SessionTimestamp? parsed = value.ValueKind == JsonValueKind.String
			? SessionTimestamp.TryParse(JsonValues.Decode(value, Report))
			: null;
		Report.InvalidTimestamps += parsed is null ? 1 : 0;
		return parsed;
	}

	protected void AddMessage(string role, string text, SessionTimestamp? time, string? turnId = null)
	{
		if (!TryRetain(text.Length + (turnId?.Length ?? 0) + EntryOverhead))
		{
			Report.MessagesOverLimit++;
			return;
		}
		Messages.Add(new SessionMessage(role, text, time, turnId, PythonText.CodePointLength(text)));
		Report.Messages++;
	}

	/// <summary>Once anything is refused, everything after it is too, so the kept text stays a true prefix.</summary>
	protected bool TryRetain(long cost)
	{
		if (_overBudget || _retained + cost > Options.MaxRetainedChars)
		{
			_overBudget = true;
			return false;
		}
		_retained += cost;
		return true;
	}

	/// <summary>Redacts, then cuts. In the other order a cut token's head would be too short for any rule to recognize.</summary>
	protected string Clip(string value)
	{
		return SessionFrontmatter.Clip(RedactMetadata(value), Report);
	}

	/// <summary>Redacts one metadata string. The same few values repeat on every record, so each is judged once.</summary>
	protected string RedactMetadata(string value)
	{
		if (_redactedMetadata.TryGetValue(value, out string? known))
		{
			return known;
		}
		string redacted = RedactMetadataOrRefuse(value);
		_redactedMetadata[value] = redacted;
		return redacted;
	}

	/// <summary>A value redaction cannot finish becomes a marker, reported against part 0 because it belongs to the whole session.</summary>
	private string RedactMetadataOrRefuse(string value)
	{
		try
		{
			RedactionResult result = Redactor.Shared.RedactFragment(value);
			AddCounts(result);
			return result.Text;
		}
		catch (RedactionException exception) when (Redactor.IsCausedByText(exception.Failure))
		{
			Refusals.Add(new SessionFieldRefusal(0, "metadata", exception.RuleName));
			return PartGate.FieldMarker;
		}
	}

	/// <summary>A title is cut from redacted text, and the cut can change how the remainder reads, so it is judged again.</summary>
	protected string? RedactTitle(string? title)
	{
		return title is null ? null : RedactMetadata(title);
	}

	/// <summary>
	/// Redacts all message text as one string, so a key spread across messages is one key. Runs before
	/// the title, digest, sizing, and split, which therefore only ever see redacted text.
	/// </summary>
	protected void RedactMessages()
	{
		string[] texts = [.. Messages.Select(RepairBoundary)];
		Stopwatch clock = Stopwatch.StartNew();
		for (int round = 0; round < MaxMessageRounds; round++)
		{
			RedactionException? failure = TryRedactRange(texts, 0, texts.Length, clock, out RedactionResult? result);
			if (failure is null)
			{
				ApplyRedacted(result!);
				return;
			}

			RefuseFailing(texts, 0, texts.Length, failure, clock);
		}

		RefuseEveryMessage(texts);
		ApplyRedacted(Redactor.Shared.RedactFragment(string.Join(Redactor.MessageSeparator, texts)));
	}

	private void ApplyRedacted(RedactionResult result)
	{
		AddCounts(result);
		string[] texts = SplitMessages(result.Text);
		if (texts.Length != Math.Max(Messages.Count, 1))
		{
			throw new RedactionException(RedactionException.WholeDocument, RedactionFailure.RuleFailed);
		}
		for (int i = 0; i < Messages.Count; i++)
		{
			Messages[i] = Messages[i] with { Text = texts[i], CodePoints = PythonText.CodePointLength(texts[i]) };
		}
	}

	/// <summary>Returns the failure when the text caused it. Anything else, and a search that has gone on too long, refuses the session.</summary>
	private static RedactionException? TryRedactRange(string[] texts, int start, int count, Stopwatch clock, out RedactionResult? result)
	{
		result = null;
		if (clock.Elapsed > Redactor.Shared.Budget)
		{
			throw new RedactionException(RedactionException.WholeDocument, RedactionFailure.TimedOut);
		}

		try
		{
			result = Redactor.Shared.RedactFragment(string.Join(Redactor.MessageSeparator, texts, start, count));
			return null;
		}
		catch (RedactionException exception) when (Redactor.IsCausedByText(exception.Failure))
		{
			return exception;
		}
	}

	/// <summary>
	/// Halves a range known to fail until it finds the messages that fail alone. When both halves pass, the two together are what
	/// fails, so the messages either side of the split go. Nothing here is kept: the whole text is redacted again as one string
	/// afterwards, so a key spread across messages is still one key.
	/// </summary>
	private void RefuseFailing(string[] texts, int start, int count, RedactionException failure, Stopwatch clock)
	{
		if (count <= 1)
		{
			RefuseMessage(texts, start, failure.RuleName);
			return;
		}

		int half = count / 2;
		RedactionException? left = TryRedactRange(texts, start, half, clock, out _);
		RedactionException? right = TryRedactRange(texts, start + half, count - half, clock, out _);
		if (left is not null)
		{
			RefuseFailing(texts, start, half, left, clock);
		}
		if (right is not null)
		{
			RefuseFailing(texts, start + half, count - half, right, clock);
		}
		if (left is null && right is null)
		{
			RefuseMessage(texts, start + half - 1, failure.RuleName);
			RefuseMessage(texts, start + half, failure.RuleName);
		}
	}

	private void RefuseMessage(string[] texts, int index, string rule)
	{
		// A session with no messages still redacts one empty string, and has nothing to replace.
		if (index < texts.Length)
		{
			texts[index] = PartGate.MessageMarker;
			_refusedMessages[index] = rule;
		}
	}

	private void RefuseEveryMessage(string[] texts)
	{
		for (int i = 0; i < texts.Length; i++)
		{
			if (!_refusedMessages.ContainsKey(i))
			{
				RefuseMessage(texts, i, RedactionException.WholeDocument);
			}
		}
	}

	private void ReportRefusedMessages(SessionPart part)
	{
		for (int i = 0; i < part.Messages.Count; i++)
		{
			int index = part.MessageStart + i;
			if (_refusedMessages.TryGetValue(index, out string? rule))
			{
				Refusals.Add(new SessionFieldRefusal(part.Number, $"message:{index}", rule));
			}
		}
	}

	/// <summary>Splits on the boundary itself, then takes off the separator's own newlines, so a span that ended beside a boundary cannot change the count.</summary>
	private static string[] SplitMessages(string joined)
	{
		string[] pieces = joined.Split(Redactor.MessageBoundary);
		for (int i = 0; i < pieces.Length; i++)
		{
			string piece = pieces[i];
			piece = i > 0 && piece.StartsWith('\n') ? piece[1..] : piece;
			pieces[i] = i + 1 < pieces.Length && piece.EndsWith('\n') ? piece[..^1] : piece;
		}

		return pieces;
	}

	/// <summary>The id a path is built from. When redaction changed the id, a digest of the raw id keeps two such sessions apart. A digest reveals nothing.</summary>
	protected string PathId(string rawId)
	{
		return RedactMetadata(rawId) == rawId ? rawId : SessionPaths.Digest(rawId);
	}

	private string RepairBoundary(SessionMessage message)
	{
		if (!message.Text.Contains(Redactor.MessageBoundary))
		{
			return message.Text;
		}
		Report.RepairedStrings++;
		return message.Text.Replace(Redactor.MessageBoundary, '�');
	}

	private void AddCounts(RedactionResult result)
	{
		foreach ((string rule, int count) in result.CountsByRule)
		{
			CountsByRule[rule] = CountsByRule.GetValueOrDefault(rule) + count;
		}
	}

	/// <summary>Greedy and prefix-stable: appending to a session only changes its last part.</summary>
	protected List<ArraySegment<SessionMessage>> SplitIntoParts()
	{
		SessionMessage[] all = [.. Messages];
		List<ArraySegment<SessionMessage>> parts = [];
		int start = 0;
		long size = 0;
		for (int i = 0; i < all.Length; i++)
		{
			long messageSize = all[i].Role.Length + all[i].CodePoints + 16L;
			if (i > start && size + messageSize > Options.MaxPartChars)
			{
				parts.Add(new ArraySegment<SessionMessage>(all, start, i - start));
				start = i;
				size = 0;
			}
			size += messageSize;
		}
		if (start < all.Length)
		{
			parts.Add(new ArraySegment<SessionMessage>(all, start, all.Length - start));
		}
		return parts;
	}

	protected List<RedactedSessionDocument> BuildDocuments(
		List<ArraySegment<SessionMessage>> segments,
		Func<SessionPart, string> render,
		Func<string, int, string> path)
	{
		SessionTimestamp start = First ?? SessionTimestamp.FromFallback(Source.FallbackStart);
		List<RedactedSessionDocument> documents = new(segments.Count);
		int messageStart = 0;
		for (int i = 0; i < segments.Count; i++)
		{
			SessionPart part = new(segments[i], i + 1, messageStart, start);
			ReportRefusedMessages(part);
			documents.Add(new RedactedSessionDocument(
				path(start.DateText, part.Number),
				part.Number,
				messageStart,
				segments[i].Count,
				PartGate.Clean(render(part), part, Refusals)));
			messageStart += segments[i].Count;
		}
		return documents;
	}
}

internal sealed record SessionMessage(string Role, string Text, SessionTimestamp? Time, string? TurnId, int CodePoints)
{
	public const string User = "user";
	public const string Assistant = "assistant";

	public bool IsUser => Role == User;
}

/// <summary>One part being rendered. A message with no time takes the previous bound, as the exporters do.</summary>
internal readonly record struct SessionPart(ArraySegment<SessionMessage> Messages, int Number, int MessageStart, SessionTimestamp SessionStart)
{
	public SessionTimestamp FirstTime => Messages[0].Time ?? SessionStart;

	public SessionTimestamp LastTime => Messages[^1].Time ?? FirstTime;

	public SessionMessage? FirstUserMessage => Messages.FirstOrDefault(static message => message.IsUser);
}
