using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Memoria.Core.Sessions;
using YamlDotNet.RepresentationModel;
using static Memoria.Core.Tests.Sessions.SessionTestKit;

namespace Memoria.Core.Tests.Sessions;

/// <summary>
/// The sidecar reads only a document's first 8192 characters for frontmatter and needs the closing
/// fence among them. These tests hold the renderer to that budget. The same fixtures are read by
/// sidecar/test/session-head.test.ts, which runs them through the real metadata index. Refresh them
/// with MEMORIA_WRITE_SESSION_FIXTURES=1 after an intended change.
/// </summary>
public sealed partial class SessionHeadBudgetTests
{
	private const int TouchedFiles = 10_000;

	/// <summary>Longer than the window, so every fixture is really cut when the head is read.</summary>
	private static readonly string s_longRequest = new('m', 20_000);

	private static readonly string s_hostile = string.Concat(Enumerable.Repeat("\u0001\"\n---\n", 2000));

	public static TheoryData<string> FixtureNames => ["claude-10000-files", "claude-hostile-10000-files", "codex-2500-turns", "codex-hostile", "envelope-longest"];

	[Fact]
	public void The_budget_is_pinned_to_the_sidecars_window()
	{
		Match match = HeadBytes().Match(File.ReadAllText(FindAboveTests("sidecar", "src", "metadata.ts")));

		Assert.True(match.Success, "DEFAULT_HEAD_BYTES is no longer a plain number in sidecar/src/metadata.ts");
		Assert.Equal(SessionFrontmatter.SidecarHeadWindow, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
		Assert.True(SessionFrontmatter.MaxBytes < SessionFrontmatter.SidecarHeadWindow);
	}

	[Theory]
	[MemberData(nameof(FixtureNames))]
	public async Task The_committed_fixture_is_what_the_renderer_produces(string name)
	{
		byte[] rendered = Encoding.UTF8.GetBytes(await RenderFixtureAsync(name));
		string relative = Path.Combine("Sessions", "Fixtures", "head", $"{name}.md");

		if (Environment.GetEnvironmentVariable("MEMORIA_WRITE_SESSION_FIXTURES") == "1")
		{
			string target = Path.Combine(TestsRoot(), relative);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			await File.WriteAllBytesAsync(target, rendered);
			return;
		}
		Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, relative)), rendered);
	}

	[Theory]
	[MemberData(nameof(FixtureNames))]
	public async Task The_frontmatter_closes_inside_the_window_and_the_document_runs_past_it(string name)
	{
		string text = await RenderFixtureAsync(name);

		int fence = text.IndexOf("\n---\n", StringComparison.Ordinal) + "\n---\n".Length;
		Assert.InRange(Encoding.UTF8.GetByteCount(text[..fence]), 1, SessionFrontmatter.MaxBytes);
		Assert.True(text.Length > SessionFrontmatter.SidecarHeadWindow, "the fixture must be cut by the window");
		_ = Frontmatter(text[..SessionFrontmatter.SidecarHeadWindow]);
	}

	[Fact]
	public async Task Ten_thousand_touched_files_are_cut_and_counted_and_keep_every_filter_field()
	{
		UnredactedSession session = await RenderClaudeWithFilesAsync(TouchedFiles);
		YamlMappingNode frontmatter = Frontmatter(session.Documents[0].Text());

		Assert.Equal(ClaudeId, frontmatter.Scalar("session_id"));
		Assert.Equal("2026-10-01", frontmatter.Scalar("date"));
		Assert.Equal("demo-app", frontmatter.Scalar("project"));
		Assert.Equal("feature/parity", frontmatter.Scalar("git_branch"));
		Assert.Equal(["project/demo-app", "branch/feature-parity"], frontmatter.Sequence("tags"));

		string[] kept = frontmatter.Sequence("files_touched");
		long truncated = long.Parse(frontmatter.Scalar("files_touched_truncated"), System.Globalization.CultureInfo.InvariantCulture);
		Assert.InRange(kept.Length, 1, TouchedFiles - 1);
		Assert.Equal(TouchedFiles, kept.Length + truncated);
		Assert.Equal(truncated, session.Report.FilesTruncated);
		Assert.Equal("f/00000.cs", kept[0]);
		Assert.Equal($"f/{kept.Length - 1:D5}.cs", kept[^1]);
		Assert.True(session.Report.HasSkippedContent);
	}

	[Fact]
	public async Task Truncating_files_leaves_the_body_untouched()
	{
		string truncated = (await RenderClaudeWithFilesAsync(TouchedFiles)).Documents[0].Text();
		string untruncated = (await RenderClaudeWithFilesAsync(3)).Documents[0].Text();

		Assert.Equal(Body(untruncated), Body(truncated));
	}

	[Fact]
	public async Task A_short_file_list_is_not_cut_and_comes_after_the_tags()
	{
		UnredactedSession session = await RenderClaudeWithFilesAsync(3);
		string text = session.Documents[0].Text();

		Assert.Equal(0, session.Report.FilesTruncated);
		Assert.False(session.Report.HasSkippedContent);
		Assert.DoesNotContain("files_touched_truncated", text);
		string[] keys = Frontmatter(text).Keys();
		Assert.Equal(keys.Length - 1, Array.IndexOf(keys, "files_touched"));
		Assert.Equal(keys.Length - 2, Array.IndexOf(keys, "tags"));
	}

	[Fact]
	public async Task Many_turn_ids_are_cut_and_counted_without_losing_the_fence()
	{
		UnredactedSession session = await RenderManyTurnsAsync();
		YamlMappingNode frontmatter = Frontmatter(session.Documents[0].Text());

		int kept = frontmatter.Sequence("turn_ids").Length;
		Assert.InRange(kept, 1, 2499);
		Assert.Equal(2500 - kept, int.Parse(frontmatter.Scalar("turn_ids_truncated"), System.Globalization.CultureInfo.InvariantCulture));
		Assert.Equal(2500 - kept, session.Report.TurnIdsTruncated);
		Assert.Equal("turn-00000", frontmatter.Sequence("turn_ids")[0]);
		Assert.Equal("2500", frontmatter.Scalar("message_count"));
		Assert.Equal("demo-app", frontmatter.Scalar("project"));
		Assert.True(session.Report.HasSkippedContent);
	}

	[Fact]
	public async Task A_few_turn_ids_are_not_cut()
	{
		UnredactedSession session = await Codex(CodexMessage("user", "x", "t1"));

		Assert.Equal(0, session.Report.TurnIdsTruncated);
		Assert.DoesNotContain("turn_ids_truncated", session.Documents[0].Text());
	}

	[Fact]
	public void The_list_budget_admits_exactly_what_fits()
	{
		// 64 bytes are reserved. Each entry below costs its 8 characters, two quotes, and no overhead.
		StringBuilder frontmatter = new(new string('x', SessionFrontmatter.MaxBytes - 64 - 30));
		string[] values = ["aaaaaaaa", "bbbbbbbb", "cccccccc", "dddddddd"];

		Assert.Equal(3, SessionFrontmatter.CountFitting(frontmatter, values, 0));
		Assert.Equal(2, SessionFrontmatter.CountFitting(frontmatter, values, 1));
		Assert.Equal(0, SessionFrontmatter.CountFitting(new StringBuilder(new string('x', SessionFrontmatter.MaxBytes)), values, 0));
		Assert.Equal(0, SessionFrontmatter.CountFitting(frontmatter, [], 0));
	}

	[Fact]
	public void Clipping_counts_the_written_form_not_the_characters()
	{
		// A control character is written as six bytes, a quote as two, and U+65E5 as three.
		Assert.Equal(new string('\u0001', 2), SessionFrontmatter.Clip(new string('\u0001', 3), maxBytes: 12));
		Assert.Equal(new string('"', 3), SessionFrontmatter.Clip(new string('"', 4), maxBytes: 7));
		Assert.Equal("日", SessionFrontmatter.Clip("日日", maxBytes: 5));
		Assert.Equal("", SessionFrontmatter.Clip("\u0001", maxBytes: 5));
	}

	[Fact]
	public async Task A_hostile_project_directory_is_clipped_too()
	{
		SessionSource source = new(SessionFormat.ClaudeCode, ClaudeId) { ProjectDirectory = new string('\u0001', 5000) };

		UnredactedSession session = await Render(ClaudeLine("user", "x", cwd: null), source);

		Assert.Equal(1, session.Report.ClippedFields);
		Assert.Equal(85, Frontmatter(session.Documents[0].Text()).Scalar("project").Length);
	}

	private static async Task<string> RenderFixtureAsync(string name)
	{
		return name switch
		{
			"claude-10000-files" => (await RenderClaudeWithFilesAsync(TouchedFiles)).Documents[0].Text(),
			"claude-hostile-10000-files" => (await RenderHostileClaudeAsync()).Documents[0].Text(),
			"codex-2500-turns" => (await RenderManyTurnsAsync()).Documents[0].Text(),
			"codex-hostile" => (await RenderHostileCodexAsync()).Documents[0].Text(),
			_ => RenderLongestEnvelope(),
		};
	}

	private static Task<UnredactedSession> RenderClaudeWithFilesAsync(int count)
	{
		string jsonl = ClaudeLine("user", s_longRequest, cwd: "/Users/synth/Source/demo-app", branch: "feature/parity")
			+ Snapshot(Enumerable.Range(0, count).Select(static i => $"f/{i:D5}.cs"));
		return Claude(jsonl);
	}

	private static Task<UnredactedSession> RenderHostileClaudeAsync()
	{
		string jsonl = ClaudeLine("user", s_longRequest, cwd: "/srv/" + s_hostile, branch: s_hostile, slug: s_hostile)
			+ Snapshot(Enumerable.Range(0, TouchedFiles).Select(static i => $"日日/{i:D5}"));
		return Claude(jsonl, sessionId: s_hostile);
	}

	private static Task<UnredactedSession> RenderManyTurnsAsync()
	{
		StringBuilder jsonl = new(CodexMeta(cwd: "/Users/synth/Source/demo-app"));
		for (int i = 0; i < 2500; i++)
		{
			jsonl.Append(CodexMessage("user", "x", $"turn-{i:D5}"));
		}
		return Codex(jsonl.ToString());
	}

	private static Task<UnredactedSession> RenderHostileCodexAsync()
	{
		string meta = CodexRecord("session_meta", new Dictionary<string, object>
		{
			["id"] = s_hostile,
			["session_id"] = s_hostile,
			["parent_thread_id"] = s_hostile,
			["forked_from_id"] = s_hostile,
			["agent_path"] = s_hostile,
			["agent_role"] = s_hostile,
			["cwd"] = "/srv/" + s_hostile,
			["source"] = s_hostile,
		});
		StringBuilder jsonl = new(meta);
		for (int i = 0; i < 500; i++)
		{
			jsonl.Append(CodexMessage("user", "x", s_hostile + i));
		}
		return Codex(jsonl.ToString());
	}

	/// <summary>The longest values of every envelope field, a pathological title and tag list included, all escapes.</summary>
	private static string RenderLongestEnvelope()
	{
		string[] tags = Enumerable.Repeat(s_hostile, 1000).ToArray();
		DateTimeOffset updated = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
		return new DocumentEnvelope(s_hostile, s_hostile, s_hostile, s_hostile, updated, s_hostile, tags).Render(s_longRequest).ExposeForRedaction();
	}

	private static string Body(string markdown)
	{
		return markdown[(markdown.IndexOf("\n---\n", StringComparison.Ordinal) + "\n---\n".Length)..];
	}

	private static string Snapshot(IEnumerable<string> files)
	{
		return JsonSerializer.Serialize(new Dictionary<string, object>
		{
			["type"] = "file-history-snapshot",
			["snapshot"] = new Dictionary<string, object> { ["trackedFileBackups"] = files.ToDictionary(static file => file, static _ => new object()) },
		}) + "\n";
	}

	private static string FindAboveTests(params string[] relative)
	{
		string? directory = AppContext.BaseDirectory;
		while (directory is not null)
		{
			string candidate = Path.Combine([directory, .. relative]);
			if (File.Exists(candidate))
			{
				return candidate;
			}
			directory = Path.GetDirectoryName(directory);
		}
		throw new FileNotFoundException("not found above the test output: " + string.Join('/', relative));
	}

	private static string TestsRoot([CallerFilePath] string thisFile = "")
	{
		return Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
	}

	[GeneratedRegex(@"export const DEFAULT_HEAD_BYTES = ([0-9]+);")]
	private static partial Regex HeadBytes();
}
