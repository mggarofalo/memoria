using System.Text.Json;

namespace Memoria.Core.Sessions;

/// <summary>
/// Turns a raw session transcript into the markdown corpus's documents. Pure: it reads the
/// stream and nothing else, not the clock, the host's home directory, or the filesystem, so
/// the same input always yields the same paths and bytes and re-ingest overwrites.
/// </summary>
public static class SessionRenderer
{
	/// <summary>Renders one session. The stream is read to its end and is not disposed.</summary>
	/// <exception cref="ArgumentException">The session id is blank or an option is out of range.</exception>
	public static async Task<UnredactedSession> RenderAsync(
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
		await JsonlReader.ReadAsync(payload, options.MaxLineBytes, report, transcript.Add, cancellationToken);
		return transcript.Render();
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

	private long _retained;
	private bool _overBudget;

	protected SessionSource Source { get; } = source;

	protected SessionRenderOptions Options { get; } = options;

	protected SessionReadReport Report { get; } = report;

	protected List<SessionMessage> Messages { get; } = [];

	private SessionTimestamp? First { get; set; }

	public abstract void Add(JsonElement record);

	public abstract UnredactedSession Render();

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

	protected string Clip(string value)
	{
		return SessionFrontmatter.Clip(value, Report);
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

	protected List<UnredactedSessionDocument> BuildDocuments(
		List<ArraySegment<SessionMessage>> segments,
		Func<SessionPart, string> render,
		Func<string, int, string> path)
	{
		SessionTimestamp start = First ?? SessionTimestamp.FromFallback(Source.FallbackStart);
		List<UnredactedSessionDocument> documents = new(segments.Count);
		int messageStart = 0;
		for (int i = 0; i < segments.Count; i++)
		{
			SessionPart part = new(segments[i], i + 1, messageStart, start);
			documents.Add(new UnredactedSessionDocument(
				path(start.DateText, part.Number),
				part.Number,
				messageStart,
				segments[i].Count,
				new UnredactedMarkdown(render(part))));
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
