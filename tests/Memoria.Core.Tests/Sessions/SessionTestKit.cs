using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Memoria.Core.Sessions;
using YamlDotNet.RepresentationModel;

namespace Memoria.Core.Tests.Sessions;

internal static partial class SessionTestKit
{
	public const string ClaudeId = "abcdef12-3456-7890-abcd-ef1234567890";
	public const string CodexId = "11111111-2222-4333-8444-555555555555";

	public static Task<RedactedSession> Claude(string jsonl, SessionRenderOptions? options = null, string sessionId = ClaudeId)
	{
		return Render(jsonl, new SessionSource(SessionFormat.ClaudeCode, sessionId), options);
	}

	public static Task<RedactedSession> Codex(string jsonl, SessionRenderOptions? options = null, string sessionId = CodexId)
	{
		return Render(jsonl, new SessionSource(SessionFormat.Codex, sessionId), options);
	}

	public static Task<RedactedSession> Render(string jsonl, SessionSource source, SessionRenderOptions? options = null)
	{
		return SessionRenderer.RenderAsync(new MemoryStream(Encoding.UTF8.GetBytes(jsonl)), source, options);
	}

	/// <summary>One Claude record as a JSONL line, with its values JSON-encoded.</summary>
	public static string ClaudeLine(string type, string text, string timestamp = "2026-10-01T10:00:00Z", string? cwd = "/Users/me/Source/demo", string? branch = "main", string? slug = "a-slug")
	{
		Dictionary<string, object?> record = new()
		{
			["type"] = type,
			["timestamp"] = timestamp,
			["message"] = new Dictionary<string, object?> { ["role"] = type, ["content"] = text },
		};
		AddIfPresent(record, "cwd", cwd);
		AddIfPresent(record, "gitBranch", branch);
		AddIfPresent(record, "slug", slug);
		return JsonSerializer.Serialize(record) + "\n";
	}

	public static string CodexMeta(string id = CodexId, string cwd = "/Users/me/Source/demo", object? source = null)
	{
		return CodexRecord("session_meta", new Dictionary<string, object?> { ["id"] = id, ["cwd"] = cwd, ["source"] = source ?? "cli" });
	}

	public static string CodexMessage(string role, string text, string? metadataTurn = null)
	{
		Dictionary<string, object?> payload = new()
		{
			["type"] = "message",
			["role"] = role,
			["content"] = new object[] { new Dictionary<string, string> { ["type"] = "input_text", ["text"] = text } },
		};
		if (metadataTurn is not null)
		{
			payload["internal_chat_message_metadata_passthrough"] = new Dictionary<string, string> { ["turn_id"] = metadataTurn };
		}
		return CodexRecord("response_item", payload);
	}

	public static string CodexRecord(string type, object payload, string timestamp = "2026-09-09T14:00:00Z")
	{
		return JsonSerializer.Serialize(new Dictionary<string, object?> { ["timestamp"] = timestamp, ["type"] = type, ["payload"] = payload }) + "\n";
	}

	public static string Text(this RedactedSessionDocument document)
	{
		return document.Content.Text;
	}

	/// <summary>Parses frontmatter the way the sidecar finds it: the same fence regex, then a real YAML parser.</summary>
	public static YamlMappingNode Frontmatter(string markdown)
	{
		Match match = FrontmatterFence().Match(markdown);
		Assert.True(match.Success, "frontmatter fence not found");
		YamlStream stream = [];
		stream.Load(new StringReader(match.Groups[1].Value));
		Assert.Single(stream.Documents);
		return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
	}

	public static string Scalar(this YamlMappingNode mapping, string key)
	{
		YamlScalarNode node = Assert.IsType<YamlScalarNode>(mapping.Children[new YamlScalarNode(key)]);
		Assert.True(node.Anchor.IsEmpty, "a value became an anchor");
		return node.Value!;
	}

	public static string[] Sequence(this YamlMappingNode mapping, string key)
	{
		YamlSequenceNode node = Assert.IsType<YamlSequenceNode>(mapping.Children[new YamlScalarNode(key)]);
		return node.Children.Select(static child => Assert.IsType<YamlScalarNode>(child).Value!).ToArray();
	}

	public static string[] Keys(this YamlMappingNode mapping)
	{
		return mapping.Children.Keys.Select(static key => ((YamlScalarNode)key).Value!).ToArray();
	}

	/// <summary>A C# reading of <c>resolveTarget</c> in sidecar/src/ingest.ts. Returns why it would refuse, or null.</summary>
	public static string? SidecarRefusal(string requested)
	{
		string raw = requested.Trim();
		if (raw.Length == 0 || raw.Contains('\0'))
		{
			return "empty or NUL";
		}
		string relative = raw.Replace('\\', '/');
		if (relative.StartsWith('/') || WindowsDrive().IsMatch(relative))
		{
			return "absolute";
		}
		return SegmentRefusal(relative.Split('/')) ?? (relative.EndsWith(".md", StringComparison.Ordinal) ? null : "pattern mismatch");
	}

	private static string? SegmentRefusal(string[] segments)
	{
		if (segments.Any(static s => s is "" or "." or ".."))
		{
			return "traversal";
		}
		if (segments.Any(static s => s.EndsWith('.') || s.EndsWith(' ') || s.Contains(':')))
		{
			return "windows-unsafe segment";
		}
		// picomatch with dot:false refuses a leading dot, and the default pattern needs .md.
		return segments.Any(static s => s.StartsWith('.')) ? "pattern mismatch" : null;
	}

	private static void AddIfPresent(Dictionary<string, object?> record, string key, string? value)
	{
		if (value is not null)
		{
			record[key] = value;
		}
	}

	[GeneratedRegex(@"^(?:\uFEFF)?---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|\z)")]
	private static partial Regex FrontmatterFence();

	[GeneratedRegex("^[a-zA-Z]:")]
	private static partial Regex WindowsDrive();
}
