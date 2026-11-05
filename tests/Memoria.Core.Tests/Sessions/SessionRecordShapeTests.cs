using Memoria.Core.Sessions;
using YamlDotNet.RepresentationModel;
using static Memoria.Core.Tests.Sessions.SessionTestKit;

namespace Memoria.Core.Tests.Sessions;

/// <summary>Fields of the wrong type are absent rather than fatal, and unpaired surrogates stay encodable.</summary>
public sealed class SessionRecordShapeTests
{
	private const string Good = "{\"type\":\"user\",\"timestamp\":\"2026-10-01T10:00:00Z\",\"message\":{\"content\":\"kept\"}}\n";

	[Theory]
	[InlineData("{\"type\":7}")]
	[InlineData("{\"type\":\"user\"}")]
	[InlineData("{\"type\":\"user\",\"message\":\"not an object\"}")]
	[InlineData("{\"type\":\"user\",\"message\":{}}")]
	[InlineData("{\"type\":\"user\",\"message\":{\"content\":7}}")]
	[InlineData("{\"type\":\"user\",\"message\":{\"content\":[7,{\"type\":\"image\"},{\"type\":\"text\"},{\"type\":\"text\",\"text\":7},{\"type\":\"text\",\"text\":\"  \"}]}}")]
	[InlineData("{\"type\":\"user\",\"message\":{\"content\":\"   \"}}")]
	[InlineData("{\"type\":\"file-history-snapshot\"}")]
	[InlineData("{\"type\":\"file-history-snapshot\",\"snapshot\":3}")]
	[InlineData("{\"type\":\"file-history-snapshot\",\"snapshot\":{\"trackedFileBackups\":3}}")]
	[InlineData("{\"gitBranch\":7,\"slug\":[],\"cwd\":{}}")]
	public async Task A_claude_record_of_the_wrong_shape_adds_no_message(string line)
	{
		RedactedSession session = await Claude(line + "\n" + Good);

		Assert.Equal(1, session.Report.Messages);
		YamlMappingNode frontmatter = Frontmatter(session.Documents[0].Text());
		Assert.Equal("", frontmatter.Scalar("git_branch"));
		Assert.Equal("", frontmatter.Scalar("slug"));
	}

	[Theory]
	[InlineData("{\"timestamp\":\"2026-09-09T00:00:00Z\",\"type\":\"response_item\",\"payload\":3}")]
	[InlineData("{\"type\":7,\"payload\":{}}")]
	[InlineData("{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"developer\",\"content\":\"x\"}}")]
	[InlineData("{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":7,\"content\":\"x\"}}")]
	[InlineData("{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[]}}")]
	[InlineData("{\"type\":\"response_item\",\"payload\":{\"type\":\"reasoning\"}}")]
	[InlineData("{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"turn_id\":\"nope\"}}")]
	[InlineData("{\"type\":\"turn_context\",\"payload\":{\"turn_id\":7}}")]
	public async Task A_codex_record_of_the_wrong_shape_adds_no_message(string line)
	{
		RedactedSession session = await Codex(line + "\n" + CodexMessage("user", "kept"));

		Assert.Equal(1, session.Report.Messages);
		Assert.Equal("[unassigned]", TurnIds(session.Documents[0]));
	}

	[Fact]
	public async Task A_null_timestamp_is_absent_and_a_bad_one_is_counted()
	{
		string jsonl = "{\"type\":\"user\",\"timestamp\":null,\"message\":{\"content\":\"a\"}}\n"
			+ "{\"type\":\"user\",\"timestamp\":7,\"message\":{\"content\":\"b\"}}\n"
			+ "{\"type\":\"user\",\"timestamp\":\"garbage\",\"message\":{\"content\":\"c\"}}\n"
			+ "{\"type\":\"user\",\"message\":{\"content\":\"d\"}}\n";

		RedactedSession session = await Claude(jsonl);

		Assert.Equal(2, session.Report.InvalidTimestamps);
		Assert.Equal(4, session.Report.Messages);
		Assert.StartsWith("1970-01-01_", session.Documents[0].Path);
	}

	[Fact]
	public async Task Text_blocks_are_stripped_and_joined_by_a_blank_line()
	{
		string line = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\" one \"},{\"type\":\"thinking\",\"text\":\"hidden\"},{\"type\":\"text\",\"text\":\"\\ntwo\\n\"}]}}\n";

		RedactedSession session = await Claude(line);

		Assert.EndsWith("## Assistant\n\none\n\ntwo\n", session.Documents[0].Text());
	}

	[Fact]
	public async Task The_interrupted_marker_is_not_a_message_but_its_time_still_counts()
	{
		string line = "{\"type\":\"user\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"message\":{\"content\":\"[Request interrupted by user for tool use]\"}}\n";

		RedactedSession session = await Claude(line + ClaudeLine("assistant", "later"));

		Assert.Equal(1, session.Report.Messages);
		Assert.StartsWith("2026-01-01_", session.Documents[0].Path);
		Assert.Equal("2026-10-01T10:00:00+00:00", Frontmatter(session.Documents[0].Text()).Scalar("start_time"));
	}

	[Theory]
	[InlineData("\\ud83d", "\uFFFD")]
	[InlineData("\\ude00", "\uFFFD")]
	[InlineData("a\\ud83d", "a\uFFFD")]
	[InlineData("\\ud83d\\u0041", "\uFFFDA")]
	[InlineData("\\ud83dx", "\uFFFDx")]
	[InlineData("\\ud83d\\ud83d\\ude00", "\uFFFD\U0001F600")]
	[InlineData("\\b\\f\\n\\r\\t\\/\\\"\\\\\\ud83d", "\b\f\n\r\t/\"\\\uFFFD")]
	[InlineData("\\u00e9\\ud83d", "\u00e9\uFFFD")]
	public async Task A_lone_surrogate_is_replaced_and_the_rest_of_the_string_survives(string escaped, string expected)
	{
		string line = "{\"type\":\"user\",\"message\":{\"content\":\"[" + escaped + "]\"}}\n";

		RedactedSession session = await Claude(line);

		Assert.Equal(1, session.Report.RepairedStrings);
		Assert.EndsWith($"## User\n\n[{expected}]\n", session.Documents[0].Text());
		// Strict UTF-8 refuses an unpaired surrogate, so this proves the whole document can be stored.
		_ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetByteCount(session.Documents[0].Text());
	}

	[Fact]
	public async Task A_valid_surrogate_pair_is_not_repaired()
	{
		RedactedSession session = await Claude("{\"type\":\"user\",\"message\":{\"content\":\"\\ud83d\\ude00\"}}\n");

		Assert.Equal(0, session.Report.RepairedStrings);
		Assert.EndsWith("\U0001F600\n", session.Documents[0].Text());
	}

	[Fact]
	public async Task A_touched_file_key_with_a_lone_surrogate_is_skipped_and_counted()
	{
		string snapshot = "{\"type\":\"file-history-snapshot\",\"snapshot\":{\"trackedFileBackups\":{\"bad\\ud83d\":{},\"good.txt\":{}}}}\n";

		RedactedSession session = await Claude(Good + snapshot);

		Assert.Equal(1, session.Report.InvalidKeys);
		Assert.True(session.Report.HasSkippedContent);
		Assert.Equal(["good.txt"], Frontmatter(session.Documents[0].Text()).Sequence("files_touched"));
	}

	[Fact]
	public async Task Touched_files_use_forward_slashes_are_unique_and_sort_by_code_point()
	{
		string snapshot = "{\"type\":\"file-history-snapshot\",\"snapshot\":{\"trackedFileBackups\":{"
			+ "\"dir\\\\b.txt\":{},\"dir/b.txt\":{},\"\\uFFFD.txt\":{},\"\\ud83d\\ude00.txt\":{},\"a.txt\":{}}}}\n";

		RedactedSession session = await Claude(Good + snapshot);

		Assert.Equal(["a.txt", "dir/b.txt", "\uFFFD.txt", "\U0001F600.txt"], Frontmatter(session.Documents[0].Text()).Sequence("files_touched"));
	}

	[Fact]
	public async Task A_touched_file_stays_with_the_message_it_was_first_seen_at()
	{
		SessionRenderOptions options = new() { MaxPartChars = 40 };
		string Snapshot(string file) => "{\"type\":\"file-history-snapshot\",\"snapshot\":{\"trackedFileBackups\":{\"" + file + "\":{}}}}\n";
		string jsonl = Snapshot("before-any.txt") + ClaudeLine("user", new string('a', 30)) + Snapshot("one.txt")
			+ ClaudeLine("assistant", new string('b', 30)) + Snapshot("two.txt") + Snapshot("one.txt");

		RedactedSession session = await Claude(jsonl, options);

		Assert.Equal(2, session.Documents.Count);
		Assert.Equal(["before-any.txt", "one.txt"], Frontmatter(session.Documents[0].Text()).Sequence("files_touched"));
		Assert.Equal(["two.txt"], Frontmatter(session.Documents[1].Text()).Sequence("files_touched"));
	}

	[Fact]
	public async Task A_snapshot_timestamp_can_set_the_session_start()
	{
		string snapshot = "{\"type\":\"file-history-snapshot\",\"snapshot\":{\"timestamp\":\"2026-02-03T00:00:00Z\"}}\n";

		RedactedSession session = await Claude(snapshot + ClaudeLine("user", "x"));

		Assert.Equal("2026-02-03", Frontmatter(session.Documents[0].Text()).Scalar("date"));
	}

	[Fact]
	public async Task The_earliest_instant_sets_the_date_in_its_own_offset_and_ties_keep_the_first()
	{
		string jsonl = ClaudeLine("user", "b", "2026-10-02T02:00:00Z")
			+ ClaudeLine("user", "a", "2026-10-01T20:00:00-05:00")
			+ ClaudeLine("user", "c", "2026-10-02T01:00:00Z");

		RedactedSession session = await Claude(jsonl);

		Assert.Equal("2026-10-01", Frontmatter(session.Documents[0].Text()).Scalar("date"));
		Assert.StartsWith("2026-10-01_", session.Documents[0].Path);
	}

	[Fact]
	public async Task A_message_without_a_time_takes_the_previous_bound()
	{
		string jsonl = ClaudeLine("user", "timed", "2026-05-05T05:05:05Z") + "{\"type\":\"assistant\",\"message\":{\"content\":\"untimed\"}}\n";

		YamlMappingNode frontmatter = Frontmatter((await Claude(jsonl)).Documents[0].Text());
		Assert.Equal("2026-05-05T05:05:05+00:00", frontmatter.Scalar("start_time"));
		Assert.Equal("2026-05-05T05:05:05+00:00", frontmatter.Scalar("end_time"));

		YamlMappingNode reversed = Frontmatter((await Claude("{\"type\":\"user\",\"message\":{\"content\":\"untimed\"}}\n" + ClaudeLine("assistant", "timed", "2026-05-05T05:05:05Z"))).Documents[0].Text());
		Assert.Equal("2026-05-05T05:05:05+00:00", reversed.Scalar("start_time"));
		Assert.Equal("2026-05-05T05:05:05+00:00", reversed.Scalar("end_time"));
	}

	[Fact]
	public async Task The_fallback_start_applies_only_without_any_timestamp()
	{
		DateTimeOffset fallback = new(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(-5));
		SessionSource source = new(SessionFormat.ClaudeCode, ClaudeId) { FallbackStart = fallback };

		RedactedSession untimed = await Render("{\"type\":\"user\",\"message\":{\"content\":\"x\"}}\n", source);
		RedactedSession timed = await Render(ClaudeLine("user", "x"), source);

		Assert.Equal("2026-03-04T05:06:07-05:00", Frontmatter(untimed.Documents[0].Text()).Scalar("start_time"));
		Assert.StartsWith("2026-10-01_", timed.Documents[0].Path);
	}

	[Fact]
	public async Task A_part_with_no_user_message_has_no_title()
	{
		RedactedSession session = await Claude(ClaudeLine("assistant", "only me"));

		Assert.DoesNotContain("\n# ", session.Documents[0].Text());
		Assert.Contains("---\n\n## Assistant\n\nonly me\n", session.Documents[0].Text());
	}

	[Fact]
	public async Task A_claude_title_is_the_first_line_cut_to_100_code_points()
	{
		string rockets = string.Concat(Enumerable.Repeat("\U0001F680", 150));

		RedactedSession session = await Claude(ClaudeLine("user", rockets + "\nsecond"));

		Assert.Contains("\n# " + string.Concat(Enumerable.Repeat("\U0001F680", 100)) + "\n\n## User", session.Documents[0].Text());
	}

	[Fact]
	public async Task A_claude_session_with_no_branch_has_only_a_project_tag()
	{
		YamlMappingNode frontmatter = Frontmatter((await Claude(ClaudeLine("user", "x", branch: null))).Documents[0].Text());

		Assert.Equal(["project/demo"], frontmatter.Sequence("tags"));
	}

	[Fact]
	public async Task Branch_slashes_become_dashes_in_the_tag_only()
	{
		YamlMappingNode frontmatter = Frontmatter((await Claude(ClaudeLine("user", "x", branch: "feat/a/b"))).Documents[0].Text());

		Assert.Equal("feat/a/b", frontmatter.Scalar("git_branch"));
		Assert.Equal(["project/demo", "branch/feat-a-b"], frontmatter.Sequence("tags"));
	}

	[Fact]
	public async Task The_first_non_empty_metadata_value_wins()
	{
		string jsonl = ClaudeLine("user", "a", branch: "", slug: "", cwd: "") + ClaudeLine("user", "b", branch: "first", slug: "first", cwd: "/x/first") + ClaudeLine("user", "c", branch: "second", slug: "second", cwd: "/x/second");

		YamlMappingNode frontmatter = Frontmatter((await Claude(jsonl)).Documents[0].Text());

		Assert.Equal("first", frontmatter.Scalar("git_branch"));
		Assert.Equal("first", frontmatter.Scalar("slug"));
		Assert.Equal("first", frontmatter.Scalar("project"));
	}

	[Fact]
	public async Task A_session_with_no_cwd_or_directory_has_an_unknown_project()
	{
		YamlMappingNode frontmatter = Frontmatter((await Claude(ClaudeLine("user", "x", cwd: null))).Documents[0].Text());

		Assert.Equal("unknown", frontmatter.Scalar("project"));
	}

	[Fact]
	public async Task Codex_metadata_is_read_only_until_a_cwd_is_known()
	{
		string jsonl = CodexRecord("session_meta", new Dictionary<string, object?> { ["id"] = "first", ["cwd"] = "", ["agent_role"] = "one" })
			+ CodexRecord("session_meta", new Dictionary<string, object?> { ["id"] = "second", ["cwd"] = "/srv/work", ["agent_role"] = "two" })
			+ CodexRecord("session_meta", new Dictionary<string, object?> { ["id"] = "third", ["cwd"] = "/srv/other", ["agent_role"] = "three" })
			+ CodexMessage("user", "x");

		RedactedSession session = await Codex(jsonl);
		YamlMappingNode frontmatter = Frontmatter(session.Documents[0].Text());

		Assert.Equal("second", frontmatter.Scalar("session_id"));
		Assert.Equal("second", session.SessionId);
		Assert.Equal("two", frontmatter.Scalar("agent_role"));
		Assert.Equal("work", frontmatter.Scalar("project"));
	}

	[Fact]
	public async Task A_codex_id_comes_from_the_uuid_in_the_file_name_until_metadata_says_otherwise()
	{
		string stem = $"rollout-2026-09-09T10-00-00-{CodexId.ToUpperInvariant()}";

		RedactedSession withoutMeta = await Codex(CodexMessage("user", "x"), sessionId: stem);
		RedactedSession plainStem = await Codex(CodexMessage("user", "x"), sessionId: "not-a-uuid");

		Assert.Equal(CodexId.ToUpperInvariant(), withoutMeta.SessionId);
		Assert.Equal("not-a-uuid", plainStem.SessionId);
		Assert.Equal(CodexId.ToUpperInvariant(), Frontmatter(withoutMeta.Documents[0].Text()).Scalar("root_thread_id"));
	}

	[Theory]
	[InlineData("7", "")]
	[InlineData("{}", "")]
	[InlineData("{\"subagent\":{\"depth\":1},\"other\":1}", "subagent")]
	[InlineData("\"cli\"", "cli")]
	[InlineData("{\"bad\\ud83d\":1}", "")]
	public async Task The_codex_source_kind_comes_from_a_string_or_the_first_key(string source, string expected)
	{
		string meta = "{\"type\":\"session_meta\",\"payload\":{\"cwd\":\"/x\",\"source\":" + source + "}}\n";

		YamlMappingNode frontmatter = Frontmatter((await Codex(meta + CodexMessage("user", "x"))).Documents[0].Text());

		Assert.Equal(expected, frontmatter.Scalar("source_kind"));
	}

	[Fact]
	public async Task A_codex_meta_with_no_source_has_an_empty_kind()
	{
		string meta = "{\"type\":\"session_meta\",\"payload\":{\"cwd\":\"/x\"}}\n";

		YamlMappingNode frontmatter = Frontmatter((await Codex(meta + CodexMessage("user", "x"))).Documents[0].Text());

		Assert.Equal("", frontmatter.Scalar("source_kind"));
	}

	[Fact]
	public async Task Codex_turns_follow_context_events_and_message_metadata()
	{
		string jsonl = CodexMessage("user", "before any turn")
			+ CodexRecord("turn_context", new Dictionary<string, string> { ["turn_id"] = "t1" })
			+ CodexMessage("assistant", "in t1")
			+ CodexRecord("event_msg", new Dictionary<string, string> { ["type"] = "task_started", ["turn_id"] = "t2" })
			+ CodexMessage("assistant", "in t2")
			+ CodexRecord("event_msg", new Dictionary<string, string> { ["type"] = "turn_started", ["turn_id"] = "t3" })
			+ CodexMessage("assistant", "metadata wins", "t9")
			+ CodexMessage("assistant", "again in t3")
			+ CodexRecord("response_item", new Dictionary<string, object> { ["type"] = "message", ["role"] = "assistant", ["content"] = "no metadata object", ["internal_chat_message_metadata_passthrough"] = 3 })
			+ CodexRecord("response_item", new Dictionary<string, object> { ["type"] = "message", ["role"] = "assistant", ["content"] = "empty metadata turn", ["internal_chat_message_metadata_passthrough"] = new Dictionary<string, string> { ["turn_id"] = "" } });

		Assert.Equal("[unassigned, t1, t2, t9, t3]", TurnIds((await Codex(jsonl)).Documents[0]));
	}

	[Fact]
	public async Task A_codex_digest_covers_the_part_text_and_counts_are_exact()
	{
		YamlMappingNode frontmatter = Frontmatter((await Codex(CodexMessage("user", "hello"))).Documents[0].Text());

		// sha256 of "user\0hello\0"
		Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("user\0hello\0"u8)), frontmatter.Scalar("message_sha256"));
		Assert.Equal("0", frontmatter.Scalar("message_start"));
		Assert.Equal("0", frontmatter.Scalar("message_end"));
		Assert.Equal("1", frontmatter.Scalar("message_count"));
	}

	[Fact]
	public async Task A_codex_part_with_no_user_message_has_no_title_and_a_title_loses_its_hashes()
	{
		RedactedSession untitled = await Codex(CodexMessage("assistant", "only me"));
		RedactedSession titled = await Codex(CodexMessage("user", "  ## #Fix # it\u2028next line"));

		Assert.DoesNotContain("\n# ", untitled.Documents[0].Text());
		Assert.Contains("\n# Fix  it\n\n## User", titled.Documents[0].Text());
	}

	[Fact]
	public async Task A_codex_record_with_an_unknown_type_still_contributes_its_time()
	{
		string jsonl = "{\"type\":\"future\",\"timestamp\":\"2026-01-01T00:00:00Z\"}\n" + CodexMessage("user", "x");

		RedactedSession session = await Codex(jsonl);

		Assert.Equal(1, session.Report.UnrenderedRecords);
		Assert.Equal("2026-01-01", Frontmatter(session.Documents[0].Text()).Scalar("date"));
	}

	[Fact]
	public async Task Empty_sessions_have_no_documents()
	{
		Assert.Empty((await Claude("{\"type\":\"progress\"}\n")).Documents);
		Assert.Empty((await Codex(CodexMeta())).Documents);
		Assert.Empty((await Claude("")).Documents);
	}

	private static string TurnIds(RedactedSessionDocument document)
	{
		return "[" + string.Join(", ", Frontmatter(document.Text()).Sequence("turn_ids")) + "]";
	}
}
