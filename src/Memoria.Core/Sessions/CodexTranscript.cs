using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Memoria.Core.Sessions;

/// <summary>
/// Folds Codex rollout records and renders them. Only user and assistant message text is
/// kept, and compaction does not remove earlier messages.
/// </summary>
internal sealed partial class CodexTranscript : SessionTranscript
{
	private static readonly HashSet<string> s_knownTypes =
		new(["session_meta", "turn_context", "event_msg", "compacted", "response_item"], StringComparer.Ordinal);

	private string _sessionId;
	private string _pathId;
	private string _rootThreadId = "";
	private string _parentThreadId = "";
	private string _forkedFromId = "";
	private string _agentPath = "";
	private string _agentRole = "";
	private string _sourceKind = "";
	private string _currentTurnId = "";
	private string _cwd = "";

	public CodexTranscript(SessionSource source, SessionRenderOptions options, SessionReadReport report)
		: base(source, options, report)
	{
		Match uuid = UuidPattern().Match(source.SessionId);
		string id = uuid.Success ? uuid.Value : source.SessionId;
		_sessionId = Clip(id);
		_pathId = PathId(id);
	}

	public override void Add(JsonElement record)
	{
		SessionTimestamp? time = ReadTime(record, "timestamp");
		NoteTime(time);
		string type = JsonValues.String(record, "type", Report) ?? "";
		if (!s_knownTypes.Contains(type))
		{
			Report.UnrenderedRecords++;
			return;
		}
		if (record.TryGetProperty("payload", out JsonElement payload) && payload.ValueKind == JsonValueKind.Object)
		{
			Apply(type, payload, time);
		}
	}

	public override RedactedSession Render()
	{
		RedactMessages();
		string safeId = SessionPaths.SafeSegment(_pathId);
		string project = SessionProject.FromCodexCwd(_cwd, Source.HomeDirectory);
		List<RedactedSessionDocument> documents = BuildDocuments(
			SplitIntoParts(),
			part => RenderPart(project, part),
			(date, part) => SessionPaths.CodexPart(date, safeId, part));
		return new RedactedSession(
			SessionFormat.Codex,
			_sessionId,
			documents,
			Report,
			CountsByRule,
			Refusals,
			path => SessionPaths.IsCodexFamily(path, safeId),
			SessionPaths.CodexGlob(safeId));
	}

	private void Apply(string type, JsonElement payload, SessionTimestamp? time)
	{
		string? payloadType = JsonValues.String(payload, "type", Report);
		if (type == "turn_context" || (type == "event_msg" && payloadType is "task_started" or "turn_started"))
		{
			NoteTurn(payload);
		}
		else if (type == "session_meta" && _cwd.Length == 0)
		{
			ApplyMeta(payload);
		}
		else if (type == "response_item" && payloadType == "message")
		{
			AddResponse(payload, time);
		}
	}

	private void NoteTurn(JsonElement payload)
	{
		_currentTurnId = MetaOr(payload, "turn_id", _currentTurnId);
	}

	private void ApplyMeta(JsonElement payload)
	{
		string? rawId = JsonValues.String(payload, "id", Report);
		_pathId = string.IsNullOrEmpty(rawId) ? _pathId : PathId(rawId);
		_sessionId = MetaOr(payload, "id", _sessionId);
		_rootThreadId = MetaOr(payload, "session_id", _sessionId);
		_parentThreadId = MetaOr(payload, "parent_thread_id", "");
		_forkedFromId = MetaOr(payload, "forked_from_id", "");
		_agentPath = MetaOr(payload, "agent_path", "");
		_agentRole = MetaOr(payload, "agent_role", "");
		_sourceKind = Clip(SourceKind(payload));
		_cwd = MetaOr(payload, "cwd", "");
	}

	private string MetaOr(JsonElement payload, string name, string fallback)
	{
		string? value = JsonValues.String(payload, name, Report);
		return string.IsNullOrEmpty(value) ? fallback : Clip(value);
	}

	/// <summary>The source is either a plain string or a single-key object such as <c>{"subagent": {...}}</c>.</summary>
	private string SourceKind(JsonElement payload)
	{
		if (!payload.TryGetProperty("source", out JsonElement source))
		{
			return "";
		}
		if (source.ValueKind == JsonValueKind.String)
		{
			return JsonValues.Decode(source, Report);
		}
		if (source.ValueKind != JsonValueKind.Object)
		{
			return "";
		}
		foreach (JsonProperty property in source.EnumerateObject())
		{
			return JsonValues.Name(property, Report) ?? "";
		}
		return "";
	}

	private void AddResponse(JsonElement payload, SessionTimestamp? time)
	{
		string? role = JsonValues.String(payload, "role", Report);
		if (role is not (SessionMessage.User or SessionMessage.Assistant))
		{
			return;
		}
		string text = JsonValues.BlockText(payload, Report, static type => type is "input_text" or "output_text" or "text");
		if (text.Length > 0)
		{
			AddMessage(role, text, time, TurnOf(payload));
		}
	}

	private string TurnOf(JsonElement payload)
	{
		string fallback = _currentTurnId.Length > 0 ? _currentTurnId : "unassigned";
		return payload.TryGetProperty("internal_chat_message_metadata_passthrough", out JsonElement metadata)
			&& metadata.ValueKind == JsonValueKind.Object
				? MetaOr(metadata, "turn_id", fallback)
				: fallback;
	}

	private string RenderPart(string project, SessionPart part)
	{
		StringBuilder output = new();
		output.Append("---\n");
		SessionFrontmatter.Scalar(output, "session_id", _sessionId);
		SessionFrontmatter.Scalar(output, "root_thread_id", _rootThreadId.Length > 0 ? _rootThreadId : _sessionId);
		SessionFrontmatter.Scalar(output, "parent_thread_id", _parentThreadId);
		SessionFrontmatter.Scalar(output, "forked_from_id", _forkedFromId);
		SessionFrontmatter.Scalar(output, "agent", "codex");
		SessionFrontmatter.Scalar(output, "agent_path", _agentPath);
		SessionFrontmatter.Scalar(output, "agent_role", _agentRole);
		SessionFrontmatter.Scalar(output, "source_kind", _sourceKind);
		SessionFrontmatter.Scalar(output, "date", part.SessionStart.DateText);
		SessionFrontmatter.Scalar(output, "start_time", part.FirstTime.ToIsoString());
		SessionFrontmatter.Scalar(output, "end_time", part.LastTime.ToIsoString());
		SessionFrontmatter.Scalar(output, "project", project);
		SessionFrontmatter.Scalar(output, "cwd", _cwd);
		SessionFrontmatter.Scalar(output, "git_branch", "");
		SessionFrontmatter.Number(output, "chunk_index", part.Number);
		SessionFrontmatter.Number(output, "message_start", part.MessageStart);
		SessionFrontmatter.Number(output, "message_end", part.MessageStart + part.Messages.Count - 1);
		SessionFrontmatter.Number(output, "message_count", part.Messages.Count);
		SessionFrontmatter.Scalar(output, "message_sha256", Digest(part.Messages));
		AppendTurnIds(output, part);
		output.Append("---\n");

		SessionFrontmatter.Body(output, RedactTitle(Title(part.FirstUserMessage)), part.Messages);
		return output.ToString();
	}

	/// <summary>Everything filterable is already written, so only this list can run out of room.</summary>
	private void AppendTurnIds(StringBuilder output, SessionPart part)
	{
		List<string> turnIds = part.Messages.Select(static message => message.TurnId!).Distinct(StringComparer.Ordinal).ToList();
		int kept = SessionFrontmatter.CountFitting(output, turnIds, ", ".Length);
		if (kept < turnIds.Count)
		{
			SessionFrontmatter.Number(output, "turn_ids_truncated", turnIds.Count - kept);
			Report.TurnIdsTruncated += turnIds.Count - kept;
		}
		SessionFrontmatter.FlowSequence(output, "turn_ids", turnIds.Take(kept));
	}

	private static string? Title(SessionMessage? firstUser)
	{
		if (firstUser is null)
		{
			return null;
		}
		string prefix = PythonText.Prefix(PythonText.FirstSplitLine(firstUser.Text), 100);
		return PythonText.Strip(prefix.Replace("#", "", StringComparison.Ordinal));
	}

	/// <summary>
	/// Over redacted text, because messages are redacted before this runs. A digest of the raw text would
	/// survive into the corpus and let anyone confirm a guess at a short message or a secret in it. It
	/// therefore equals the Python exporter's only for a part in which nothing was redacted.
	/// </summary>
	private static string Digest(ArraySegment<SessionMessage> messages)
	{
		using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		byte[] separator = [0];
		foreach (SessionMessage message in messages)
		{
			hash.AppendData(Encoding.UTF8.GetBytes(message.Role));
			hash.AppendData(separator);
			hash.AppendData(Encoding.UTF8.GetBytes(message.Text));
			hash.AppendData(separator);
		}
		return Convert.ToHexStringLower(hash.GetHashAndReset());
	}

	[GeneratedRegex("[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex UuidPattern();
}
