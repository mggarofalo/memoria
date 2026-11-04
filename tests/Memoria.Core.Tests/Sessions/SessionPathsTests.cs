using Memoria.Core.Sessions;
using YamlDotNet.RepresentationModel;
using static Memoria.Core.Tests.Sessions.SessionTestKit;

namespace Memoria.Core.Tests.Sessions;

/// <summary>Hostile ids and project names, checked against a reading of the sidecar's validator.</summary>
public sealed class SessionPathsTests
{
	public static TheoryData<string> HostileIds =>
	[
		"../../../etc/passwd",
		"..\\..\\windows\\system32",
		"/etc/passwd",
		"C:\\Windows\\win.ini",
		"C:evil",
		"a:b",
		"file.md:stream",
		"..",
		".",
		"...",
		"....//....//",
		"trailing.",
		"trailing ",
		" leading",
		".hidden",
		"nul\0byte",
		"new\nline",
		"---\ninjected: true",
		"CON",
		"aux.txt",
		"\u2215\u2216\uFF0F",
		"\U0001F680\U0001F680\U0001F680",
		"%2e%2e%2f",
		"a/b",
		"a\\b",
		new string('x', 5000),
		new string('.', 5000),
		string.Concat(Enumerable.Repeat("../", 2000)),
	];

	[Theory]
	[MemberData(nameof(HostileIds))]
	public async Task A_hostile_claude_id_yields_a_valid_flat_path(string id)
	{
		RedactedSession session = await Claude(ClaudeLine("user", "x"), sessionId: id);

		AssertValid(session);
	}

	[Theory]
	[MemberData(nameof(HostileIds))]
	public async Task A_hostile_codex_id_yields_a_valid_flat_path(string id)
	{
		RedactedSession fromFileName = await Codex(CodexMessage("user", "x"), sessionId: id);
		RedactedSession fromMetadata = await Codex(CodexMeta(id) + CodexMessage("user", "x"));

		AssertValid(fromFileName);
		AssertValid(fromMetadata);
		Assert.Equal(fromFileName.Documents[0].Path, fromMetadata.Documents[0].Path);
	}

	[Theory]
	[MemberData(nameof(HostileIds))]
	public void SafeSegment_output_is_valid_alone(string id)
	{
		string segment = SessionPaths.SafeSegment(id);

		Assert.Null(SidecarRefusal(segment + ".md"));
		Assert.Null(SidecarRefusal(segment + "/x.md"));
		Assert.True(segment.Length <= 64, "a segment must stay short");
		Assert.Equal(segment, SessionPaths.SafeSegment(id));
	}

	[Theory]
	[InlineData("../../evil")]
	[InlineData("a:b")]
	public async Task A_hostile_project_name_never_reaches_a_path(string name)
	{
		RedactedSession fromCwd = await Claude(ClaudeLine("user", "x", cwd: "/x/" + name));
		SessionSource source = new(SessionFormat.ClaudeCode, ClaudeId) { ProjectDirectory = name };
		RedactedSession fromDirectory = await Render(ClaudeLine("user", "x", cwd: null), source);

		Assert.Equal("2026-10-01_abcdef12.md", fromCwd.Documents[0].Path);
		Assert.Equal("2026-10-01_abcdef12.md", fromDirectory.Documents[0].Path);
		Assert.Equal(name, Frontmatter(fromDirectory.Documents[0].Text()).Scalar("project"));
	}

	[Fact]
	public void Clean_ids_keep_the_exporter_names()
	{
		Assert.Equal("abcdef12", SessionPaths.ClaudeShortId(ClaudeId));
		Assert.Equal("abc", SessionPaths.ClaudeShortId("abc"));
		Assert.Equal("agent-a0", SessionPaths.ClaudeShortId("agent-a04bea"));
		Assert.Equal(CodexId, SessionPaths.SafeSegment(CodexId));
		Assert.Equal("2026-10-01_abcdef12.md", SessionPaths.ClaudePart("2026-10-01", "abcdef12", 1));
		Assert.Equal("2026-10-01_abcdef12_part-002.md", SessionPaths.ClaudePart("2026-10-01", "abcdef12", 2));
		Assert.Equal("2026-10-01_abcdef12_part-1000.md", SessionPaths.ClaudePart("2026-10-01", "abcdef12", 1000));
		Assert.Equal("2026-09-09_codex_id_chunk-00002.md", SessionPaths.CodexPart("2026-09-09", "id", 2));
	}

	[Fact]
	public void Ids_that_sanitize_alike_do_not_collide()
	{
		Assert.NotEqual(SessionPaths.SafeSegment("a/b"), SessionPaths.SafeSegment("a:b"));
		Assert.NotEqual(SessionPaths.ClaudeShortId("a/b"), SessionPaths.ClaudeShortId("a:b"));
		Assert.StartsWith("a-b-", SessionPaths.SafeSegment("a/b"));
	}

	[Fact]
	public void A_segment_at_the_length_limit_is_kept_and_one_past_it_is_not()
	{
		string atLimit = new('x', 64);
		string pastLimit = new('x', 65);

		Assert.Equal(atLimit, SessionPaths.SafeSegment(atLimit));
		Assert.NotEqual(pastLimit, SessionPaths.SafeSegment(pastLimit));
		Assert.Equal(new string('x', 32) + "-", SessionPaths.SafeSegment(pastLimit)[..33]);
		Assert.Throws<ArgumentNullException>(() => SessionPaths.SafeSegment(null!));
	}

	[Fact]
	public void An_id_with_nothing_readable_is_only_its_digest()
	{
		Assert.Matches("^[0-9a-f]{8}$", SessionPaths.SafeSegment(""));
		Assert.Matches("^[0-9a-f]{8}$", SessionPaths.SafeSegment("..."));
	}

	[Fact]
	public async Task A_shrunken_claude_session_marks_its_old_parts_as_superseded()
	{
		SessionRenderOptions options = new() { MaxPartChars = 40 };
		string one = ClaudeLine("user", new string('a', 30));

		RedactedSession session = await Claude(one + ClaudeLine("assistant", new string('b', 30)), options);

		Assert.Equal(2, session.Documents.Count);
		Assert.False(session.IsSupersededPath("2026-10-01_abcdef12.md"));
		Assert.False(session.IsSupersededPath("2026-10-01_abcdef12_part-002.md"));
		Assert.True(session.IsSupersededPath("2026-10-01_abcdef12_part-003.md"));
		Assert.True(session.IsSupersededPath("2026-09-30_abcdef12.md"));
		Assert.False(session.IsSupersededPath("2026-10-01_abcdef13.md"));
		Assert.False(session.IsSupersededPath("2026-10-01_abcdef12_part-02.md"));
		Assert.False(session.IsSupersededPath("2026-10-01_abcdef12.txt"));
		Assert.False(session.IsSupersededPath("notes/2026-10-01_abcdef12.md"));
		Assert.False(session.IsSupersededPath("abcdef12.md"));
		Assert.False(session.IsSupersededPath("2026-10-01_codex_abcdef12_chunk-00001.md"));
		Assert.Throws<ArgumentNullException>(() => session.IsSupersededPath(null!));
	}

	[Fact]
	public async Task A_shrunken_codex_session_marks_its_old_parts_as_superseded()
	{
		RedactedSession session = await Codex(CodexMeta() + CodexMessage("user", "x"));

		Assert.False(session.IsSupersededPath($"2026-09-09_codex_{CodexId}_chunk-00001.md"));
		Assert.True(session.IsSupersededPath($"2026-09-09_codex_{CodexId}_chunk-00002.md"));
		Assert.True(session.IsSupersededPath($"2026-09-08_codex_{CodexId}_chunk-00001.md"));
		Assert.False(session.IsSupersededPath($"2026-09-09_codex_{CodexId}x_chunk-00001.md"));
		Assert.False(session.IsSupersededPath($"2026-09-09_codex_{CodexId}_chunk-1.md"));
		Assert.False(session.IsSupersededPath("2026-09-09_11111111.md"));
	}

	[Fact]
	public async Task A_session_emptied_entirely_supersedes_everything_it_had()
	{
		RedactedSession session = await Claude("{\"type\":\"progress\"}\n");

		Assert.True(session.IsSupersededPath("2026-10-01_abcdef12.md"));
	}

	[Fact]
	public async Task The_same_input_gives_the_same_paths_and_bytes()
	{
		SessionRenderOptions options = new() { MaxPartChars = 40 };
		string jsonl = ClaudeLine("user", new string('a', 30)) + ClaudeLine("assistant", new string('b', 30));

		RedactedSession first = await Claude(jsonl, options);
		RedactedSession second = await Claude(jsonl, options);

		Assert.Equal(first.Documents.Select(static d => d.Path), second.Documents.Select(static d => d.Path));
		Assert.Equal(first.Documents.Select(static d => d.Text()), second.Documents.Select(static d => d.Text()));
	}

	[Fact]
	public async Task Appending_only_changes_the_trailing_part()
	{
		SessionRenderOptions options = new() { MaxPartChars = 80 };
		string jsonl = ClaudeLine("user", new string('a', 30)) + ClaudeLine("assistant", new string('b', 30));

		RedactedSession before = await Claude(jsonl, options);
		RedactedSession after = await Claude(jsonl + ClaudeLine("user", "c"), options);

		Assert.Equal(before.Documents[0].Text(), after.Documents[0].Text());
		Assert.NotEqual(before.Documents[1].Text(), after.Documents[1].Text());
		Assert.Equal(2, after.Documents.Count);
		Assert.Equal(0, after.Documents[0].MessageStart);
		Assert.Equal(1, after.Documents[1].MessageStart);
		Assert.Equal(2, after.Documents[1].MessageCount);
		Assert.Equal(2, after.Documents[1].Part);
		YamlMappingNode second = Frontmatter(after.Documents[1].Text());
		Assert.Equal("2", second.Scalar("chunk_index"));
		Assert.Equal("1", second.Scalar("message_start"));
		Assert.Equal("2", second.Scalar("message_count"));
	}

	[Fact]
	public async Task The_part_boundary_is_exact()
	{
		// Each message costs its 4-character role, its text, and 16.
		string text = new('a', 80);
		string jsonl = ClaudeLine("user", text) + ClaudeLine("user", text);

		Assert.Single((await Claude(jsonl, new SessionRenderOptions { MaxPartChars = 200 })).Documents);
		Assert.Equal(2, (await Claude(jsonl, new SessionRenderOptions { MaxPartChars = 199 })).Documents.Count);
	}

	[Fact]
	public async Task An_oversized_message_gets_its_own_part_rather_than_being_cut()
	{
		string text = new('a', 500);

		RedactedSession session = await Claude(ClaudeLine("user", text), new SessionRenderOptions { MaxPartChars = 10 });

		Assert.Contains(text, Assert.Single(session.Documents).Text());
	}

	private static void AssertValid(RedactedSession session)
	{
		string path = Assert.Single(session.Documents).Path;
		Assert.Null(SidecarRefusal(path));
		Assert.DoesNotContain('/', path);
		Assert.DoesNotContain('\\', path);
		Assert.True(path.Length < 128, "the path must stay short");
		Assert.False(session.IsSupersededPath(path));
	}
}
