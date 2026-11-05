using System.Text;
using System.Text.Json;

namespace Memoria.Core.Sessions;

/// <summary>
/// Folds Claude Code JSONL records and renders them. Only user and assistant text is kept;
/// thinking, tool calls and results, and progress events are not. File-history snapshots
/// contribute <c>files_touched</c>.
/// </summary>
internal sealed class ClaudeTranscript(SessionSource source, SessionRenderOptions options, SessionReadReport report)
	: SessionTranscript(source, options, report)
{
	private const string InterruptedMarker = "[Request interrupted by user for tool use]";

	private readonly Dictionary<string, int> _files = new(StringComparer.Ordinal);
	private string _cwd = "";
	private string _rawCwd = "";
	private string _slug = "";
	private string _gitBranch = "";

	public override void Add(JsonElement record)
	{
		_gitBranch = FirstNonEmpty(_gitBranch, record, "gitBranch");
		_slug = FirstNonEmpty(_slug, record, "slug");
		NoteCwd(record);

		switch (JsonValues.String(record, "type", Report))
		{
			case "file-history-snapshot":
				AddSnapshot(record);
				break;
			case SessionMessage.User:
				AddTurn(record, SessionMessage.User);
				break;
			case SessionMessage.Assistant:
				AddTurn(record, SessionMessage.Assistant);
				break;
			default:
				Report.UnrenderedRecords++;
				break;
		}
	}

	public override RedactedSession Render()
	{
		RedactMessages();
		MessageChain chain = MessageChain.Of(Messages);
		string relocated = SessionPaths.RelocatedCopySuffix(_rawCwd, Source.ProjectDirectory);
		string shortId = SessionPaths.ClaudeShortId(PathId(Source.SessionId)) + relocated;
		SessionPathing pathing = Pathing($"claude\0{Source.SessionId}\0{relocated}", shortId);
		string sessionId = Clip(Source.SessionId);
		string project = Clip(_cwd.Length > 0
			? SessionProject.FromClaudeCwd(_cwd, Source.HomeDirectory)
			: SessionProject.FromClaudeDirectory(Source.ProjectDirectory, Source.HomeDirectory));
		List<ArraySegment<SessionMessage>> segments = SplitIntoParts();
		List<string>[] filesByPart = BucketFiles(segments);
		List<RedactedSessionDocument> documents = BuildDocuments(
			segments,
			part => RenderPart(sessionId, project, part, filesByPart[part.Number - 1]),
			(date, part) => SessionPaths.ClaudePart(date, shortId, part));
		return new RedactedSession(
			SessionFormat.ClaudeCode,
			sessionId,
			documents,
			Report,
			CountsByRule,
			Refusals,
			pathing.IsFamily,
			pathing)
		{ MessageChain = chain };
	}

	private SessionPathing Pathing(string identity, string stem)
	{
		return new SessionPathing(identity, stem, path => SessionPaths.IsClaudeFamily(path, stem), next => Pathing(identity, next), Source.SessionId);
	}

	/// <summary>Keeps the raw first cwd too, only to compare with the project folder. It is never written out.</summary>
	private void NoteCwd(JsonElement record)
	{
		if (_cwd.Length > 0)
		{
			return;
		}

		string? value = JsonValues.String(record, "cwd", Report);
		if (!string.IsNullOrEmpty(value))
		{
			_rawCwd = value;
			_cwd = Clip(value);
		}
	}

	private string FirstNonEmpty(string current, JsonElement record, string name)
	{
		if (current.Length > 0)
		{
			return current;
		}
		string? value = JsonValues.String(record, name, Report);
		return value is null ? current : Clip(value);
	}

	private void AddSnapshot(JsonElement record)
	{
		if (!record.TryGetProperty("snapshot", out JsonElement snapshot) || snapshot.ValueKind != JsonValueKind.Object)
		{
			return;
		}
		if (snapshot.TryGetProperty("trackedFileBackups", out JsonElement backups) && backups.ValueKind == JsonValueKind.Object)
		{
			// Attribute a file to the message it was first seen at, so a closed part never changes.
			int anchor = Math.Max(Messages.Count - 1, 0);
			foreach (JsonProperty backup in backups.EnumerateObject())
			{
				AddFile(JsonValues.Name(backup, Report), anchor);
			}
		}
		NoteTime(ReadTime(snapshot, "timestamp"));
	}

	private void AddFile(string? name, int anchor)
	{
		if (name is null)
		{
			return;
		}
		// Redacted after the transform, because turning backslashes into slashes can form a URL.
		string path = Clip(name.Replace('\\', '/'));
		if (_files.ContainsKey(path))
		{
			return;
		}
		if (TryRetain(path.Length + (long)EntryOverhead))
		{
			_files.Add(path, anchor);
			return;
		}
		Report.FilesOverLimit++;
	}

	private void AddTurn(JsonElement record, string role)
	{
		SessionTimestamp? time = ReadTime(record, "timestamp");
		NoteTime(time);
		if (!record.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object)
		{
			return;
		}
		string text = JsonValues.BlockText(message, Report, static type => type == "text");
		if (text.Length > 0 && text != InterruptedMarker)
		{
			AddMessage(role, text, time);
		}
	}

	protected override void MessagesRenumbered(int[] firstPiece)
	{
		foreach (string path in _files.Keys.ToArray())
		{
			_files[path] = firstPiece[_files[path]];
		}
	}

	private List<string>[] BucketFiles(List<ArraySegment<SessionMessage>> segments)
	{
		List<string>[] buckets = new List<string>[segments.Count];
		int[] partOfMessage = new int[Messages.Count];
		int cursor = 0;
		for (int part = 0; part < segments.Count; part++)
		{
			buckets[part] = [];
			partOfMessage.AsSpan(cursor, segments[part].Count).Fill(part);
			cursor += segments[part].Count;
		}
		// Files seen before any message have an anchor only once a message exists to hold them.
		if (segments.Count > 0)
		{
			foreach (KeyValuePair<string, int> file in _files)
			{
				buckets[partOfMessage[file.Value]].Add(file.Key);
			}
		}
		foreach (List<string> bucket in buckets)
		{
			bucket.Sort(PythonText.CompareCodePoints);
		}
		return buckets;
	}

	private string RenderPart(string sessionId, string project, SessionPart part, List<string> files)
	{
		StringBuilder output = new();
		output.Append("---\n");
		SessionFrontmatter.Scalar(output, "session_id", sessionId);
		SessionFrontmatter.Scalar(output, "slug", _slug);
		SessionFrontmatter.Scalar(output, "date", part.SessionStart.DateText);
		SessionFrontmatter.Scalar(output, "start_time", part.FirstTime.ToIsoString());
		SessionFrontmatter.Scalar(output, "end_time", part.LastTime.ToIsoString());
		SessionFrontmatter.Scalar(output, "project", project);
		SessionFrontmatter.Scalar(output, "git_branch", _gitBranch);
		SessionFrontmatter.Number(output, "chunk_index", part.Number);
		SessionFrontmatter.Number(output, "message_start", part.MessageStart);
		SessionFrontmatter.Number(output, "message_count", part.Messages.Count);
		SessionFrontmatter.Sequence(output, "tags", Tags(project));

		// Everything filterable is already written, so only this list can run out of room.
		int kept = SessionFrontmatter.CountFitting(output, files, "  - \n".Length);
		if (kept < files.Count)
		{
			SessionFrontmatter.Number(output, "files_touched_truncated", files.Count - kept);
			Report.FilesTruncated += files.Count - kept;
		}
		SessionFrontmatter.Sequence(output, "files_touched", files[..kept]);
		output.Append("---\n");

		SessionFrontmatter.Body(output, RedactTitle(Title(part.FirstUserMessage)), part.Messages);
		return output.ToString();
	}

	private List<string> Tags(string project)
	{
		List<string> tags = [RedactMetadata($"project/{project}")];
		if (_gitBranch.Length > 0)
		{
			// Redacted after the transform, because turning slashes into dashes can form a token.
			tags.Add(RedactMetadata($"branch/{_gitBranch.Replace('/', '-')}"));
		}
		return tags;
	}

	private static string? Title(SessionMessage? firstUser)
	{
		if (firstUser is null)
		{
			return null;
		}
		int newline = firstUser.Text.IndexOf('\n');
		return PythonText.Prefix(newline < 0 ? firstUser.Text : firstUser.Text[..newline], 100);
	}
}
