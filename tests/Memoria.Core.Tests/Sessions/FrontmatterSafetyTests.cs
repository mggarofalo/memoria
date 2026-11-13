using System.Reflection;
using System.Text.Json;
using Memoria.Core.Sessions;
using YamlDotNet.RepresentationModel;
using static Memoria.Core.Tests.Sessions.SessionTestKit;

namespace Memoria.Core.Tests.Sessions;

/// <summary>
/// Field values are attacker-influenced. Each hostile value must come back from a real YAML
/// parser as exactly that value, with no extra keys and the fence where it belongs.
/// </summary>
public sealed class FrontmatterSafetyTests
{
	public static TheoryData<string> HostileValues =>
	[
		"plain",
		"\n---\ninjected: true\n---\n",
		"---",
		"\r\n---\r\ninjected: true",
		"\"",
		"\\",
		"\\\"",
		"\"\ninjected: true\n\"",
		"x\": 1\ninjected: \"y",
		"&anchor value",
		"*alias",
		"&a [*a, *a]",
		"<<: *merge",
		"!!python/object:os.system 'rm'",
		"! tag",
		"# comment",
		"key: value",
		"{flow: map}",
		"[flow, seq]",
		"| literal",
		"> folded",
		"%YAML 1.1",
		"...",
		"~",
		"null",
		"true",
		"2026-10-01",
		"0x10",
		"1e3",
		"\u0085---\u0085injected: true",
		"\u2028---\u2028injected: true",
		"\u2029---",
		"\uFEFF---",
		"\u0000nul\u0001ctl\u001Fus\u007Fdel\u009Fc1",
		"\b\f\t\u000B\u001B",
		"\uFFFE\uFFFF",
		"\U0001F680 caf\u00e9 \u65e5\u672c\u8a9e",
		"  padded  ",
	];

	[Theory]
	[MemberData(nameof(HostileValues))]
	public void Quote_round_trips_through_a_real_yaml_parser(string value)
	{
		string document = "---\nkey: " + SessionFrontmatter.Quote(value) + "\nafter: \"ok\"\n---\n\nbody\n";

		YamlMappingNode frontmatter = Frontmatter(document);

		Assert.Equal(value, frontmatter.Scalar("key"));
		Assert.Equal(["key", "after"], frontmatter.Keys());
		Assert.DoesNotContain('\n', SessionFrontmatter.Quote(value));
		Assert.DoesNotContain('\r', SessionFrontmatter.Quote(value));
	}

	[Theory]
	[InlineData("plain text", "\"plain text\"")]
	[InlineData("q\"b\\", "\"q\\\"b\\\\\"")]
	[InlineData("\n\r\t\f\b", "\"\\n\\r\\t\\f\\b\"")]
	[InlineData("\u0001\u001F", "\"\\u0001\\u001f\"")]
	[InlineData("\u007F\u0085\u009F", "\"\\u007f\\u0085\\u009f\"")]
	[InlineData("\u2028\u2029\uFEFF\uFFFE\uFFFF", "\"\\u2028\\u2029\\ufeff\\ufffe\\uffff\"")]
	[InlineData(" \u007E\u00A0\u2027\u202A\uFFFD", "\" \u007E\u00A0\u2027\u202A\uFFFD\"")]
	[InlineData("\U0001F680", "\"\U0001F680\"")]
	public void Quote_writes_exactly_these_bytes(string value, string expected)
	{
		Assert.Equal(expected, SessionFrontmatter.Quote(value));
	}

	[Fact]
	public void An_unpaired_surrogate_is_written_as_the_replacement_escape()
	{
		Assert.Equal("\"\\ufffd\"", SessionFrontmatter.Quote("\uD83D"));
		Assert.Equal("\"\\ufffdx\"", SessionFrontmatter.Quote("\uDE00x"));
		Assert.Equal("\"\\ufffdx\"", SessionFrontmatter.Quote("\uD83Dx"));
	}

	[Theory]
	[MemberData(nameof(HostileValues))]
	public async Task Hostile_claude_metadata_stays_in_its_field(string value)
	{
		string jsonl = JsonSerializer.Serialize(new Dictionary<string, object>
		{
			["type"] = "user",
			["timestamp"] = "2026-10-01T10:00:00Z",
			["gitBranch"] = value,
			["slug"] = value,
			["cwd"] = "/srv/" + value.Replace('/', '_').Replace('\\', '_'),
			["message"] = new Dictionary<string, string> { ["content"] = "---\nsession_id: spoofed\n---" },
		}) + "\n";
		string snapshot = JsonSerializer.Serialize(new Dictionary<string, object>
		{
			["type"] = "file-history-snapshot",
			["snapshot"] = new Dictionary<string, object> { ["trackedFileBackups"] = new Dictionary<string, object> { ["f/" + value.Replace('\\', '/')] = new { } } },
		}) + "\n";
		SessionSource source = new(SessionFormat.ClaudeCode, value + "x");

		RedactedSession session = await Render(jsonl + snapshot, source);
		YamlMappingNode frontmatter = Frontmatter(session.Documents[0].Text());

		Assert.Equal(value + "x", frontmatter.Scalar("session_id"));
		Assert.Equal(value, frontmatter.Scalar("git_branch"));
		Assert.Equal(value, frontmatter.Scalar("slug"));
		Assert.Equal("f/" + value.Replace('\\', '/'), Assert.Single(frontmatter.Sequence("files_touched")));
		Assert.Equal(
			["session_id", "slug", "date", "start_time", "end_time", "project", "git_branch", "chunk_index", "message_start", "message_count", "tags", "files_touched"],
			frontmatter.Keys());
	}

	[Theory]
	[MemberData(nameof(HostileValues))]
	public async Task Hostile_codex_metadata_stays_in_its_field(string value)
	{
		string meta = CodexRecord("session_meta", new Dictionary<string, object>
		{
			["id"] = value + "x",
			["session_id"] = value + "root",
			["parent_thread_id"] = value + "parent",
			["forked_from_id"] = value + "fork",
			["agent_path"] = value + "path",
			["agent_role"] = value + "role",
			["cwd"] = value + "cwd",
			["source"] = value + "kind",
		});
		string jsonl = meta + CodexMessage("user", "---\nsession_id: spoofed\n---", value + "turn");

		YamlMappingNode frontmatter = Frontmatter((await Codex(jsonl)).Documents[0].Text());

		Assert.Equal(value + "x", frontmatter.Scalar("session_id"));
		Assert.Equal(value + "root", frontmatter.Scalar("root_thread_id"));
		Assert.Equal(value + "parent", frontmatter.Scalar("parent_thread_id"));
		Assert.Equal(value + "fork", frontmatter.Scalar("forked_from_id"));
		Assert.Equal(value + "path", frontmatter.Scalar("agent_path"));
		Assert.Equal(value + "role", frontmatter.Scalar("agent_role"));
		Assert.Equal(value + "kind", frontmatter.Scalar("source_kind"));
		Assert.Equal(value + "cwd", frontmatter.Scalar("cwd"));
		Assert.Equal(value + "turn", Assert.Single(frontmatter.Sequence("turn_ids")));
		Assert.Equal(
			["session_id", "root_thread_id", "parent_thread_id", "forked_from_id", "agent", "agent_path", "agent_role", "source_kind", "date", "start_time", "end_time", "project", "cwd", "git_branch", "chunk_index", "message_start", "message_end", "message_count", "message_sha256", "turn_ids"],
			frontmatter.Keys());
	}

	[Fact]
	public async Task A_fence_in_a_message_body_cannot_change_the_metadata()
	{
		const string Spoof = "---\nsession_id: spoofed\nproject: pwned\n---\n";

		RedactedSession session = await Claude(ClaudeLine("user", Spoof));
		YamlMappingNode frontmatter = Frontmatter(session.Documents[0].Text());

		Assert.Equal(ClaudeId, frontmatter.Scalar("session_id"));
		Assert.Equal("demo", frontmatter.Scalar("project"));
	}

	[Fact]
	public async Task Metadata_values_are_clipped_and_counted_so_the_frontmatter_stays_readable()
	{
		string huge = new('h', 100_000);

		RedactedSession session = await Claude(ClaudeLine("user", "x", branch: huge, slug: huge, cwd: "/srv/" + huge));
		string text = session.Documents[0].Text();

		Assert.Equal(3, session.Report.ClippedFields);
		Assert.Equal(512, Frontmatter(text).Scalar("git_branch").Length);
		Assert.Equal(512, Frontmatter(text).Scalar("slug").Length);
		Assert.False(session.Report.HasSkippedContent);
	}

	[Fact]
	public void A_clip_never_splits_a_surrogate_pair()
	{
		string value = new string('a', 3) + "\U0001F680";

		// The rocket is four bytes.
		Assert.Equal("aaa", SessionFrontmatter.Clip(value, maxBytes: 6));
		Assert.Equal(value, SessionFrontmatter.Clip(value, maxBytes: 7));
		Assert.Equal("aaa\U0001F680", SessionFrontmatter.Clip(value + "b", maxBytes: 7));
		Assert.Equal("aa", SessionFrontmatter.Clip(value, maxBytes: 2));
	}

	[Fact]
	public void Clip_counts_only_what_it_cuts()
	{
		SessionReadReport report = new();

		SessionFrontmatter.Clip(new string('a', SessionFrontmatter.MaxFieldBytes), report);
		Assert.Equal(0, report.ClippedFields);

		SessionFrontmatter.Clip(new string('a', SessionFrontmatter.MaxFieldBytes + 1), report);
		Assert.Equal(1, report.ClippedFields);
	}
}

public sealed class DocumentEnvelopeTests
{
	[Fact]
	public void Renders_every_field_in_a_stable_order_and_appends_the_body()
	{
		DocumentEnvelope envelope = new(
			"obsidian",
			"notes/one",
			"https://example.test/one",
			"One",
			new DateTimeOffset(2026, 10, 1, 22, 0, 0, TimeSpan.FromHours(-5)),
			"demo",
			["a", "b"]);

		Assert.Equal(
			"---\nsource: \"obsidian\"\nsource_id: \"notes/one\"\nurl: \"https://example.test/one\"\ntitle: \"One\"\n"
			+ "updated: \"2026-10-01T22:00:00-05:00\"\ndate: \"2026-10-01\"\nproject: \"demo\"\ntags:\n  - \"a\"\n  - \"b\"\n---\n\nbody\n",
			envelope.Render("body\n").ExposeForRedaction());
	}

	[Fact]
	public void Optional_fields_are_omitted_when_absent_or_blank()
	{
		DocumentEnvelope envelope = new("s", "i", "  ", "", null, null, ["", "  "]);

		Assert.Equal("---\nsource: \"s\"\nsource_id: \"i\"\ntags: []\n---\n\n", envelope.Render("").ExposeForRedaction());
		Assert.Equal("---\nsource: \"s\"\nsource_id: \"i\"\ntags: []\n---\n\n", new DocumentEnvelope("s", "i").Render("").ExposeForRedaction());
	}

	[Fact]
	public void Fractional_time_is_kept()
	{
		DocumentEnvelope envelope = new("s", "i", Updated: new DateTimeOffset(2026, 1, 2, 3, 4, 5, 250, TimeSpan.Zero));

		Assert.Contains("updated: \"2026-01-02T03:04:05.25+00:00\"", envelope.Render("").ExposeForRedaction());
	}

	[Theory]
	[MemberData(nameof(FrontmatterSafetyTests.HostileValues), MemberType = typeof(FrontmatterSafetyTests))]
	public void Hostile_values_stay_in_their_fields(string value)
	{
		string marked = "x" + value + "y";
		DocumentEnvelope envelope = new(marked, marked, marked, marked, null, marked, [marked]);

		YamlMappingNode frontmatter = Frontmatter(envelope.Render("---\nsource: spoofed\n---\n").ExposeForRedaction());

		Assert.Equal(["source", "source_id", "url", "title", "project", "tags"], frontmatter.Keys());
		foreach (string key in new[] { "source", "source_id", "url", "title", "project" })
		{
			Assert.Equal(marked, frontmatter.Scalar(key));
		}
		Assert.Equal(marked, Assert.Single(frontmatter.Sequence("tags")));
	}

	[Theory]
	[InlineData("", "id")]
	[InlineData("  ", "id")]
	[InlineData("source", "")]
	[InlineData("source", "  ")]
	public void A_blank_identity_is_refused(string source, string sourceId)
	{
		Assert.Throws<ArgumentException>(() => new DocumentEnvelope(source, sourceId).Render("body"));
	}

	[Fact]
	public void A_null_body_is_refused()
	{
		Assert.Throws<ArgumentNullException>(() => new DocumentEnvelope("s", "i").Render(null!));
	}

	[Fact]
	public void Long_values_and_many_tags_are_cut_so_the_frontmatter_fits_the_metadata_head()
	{
		string huge = new('h', 100_000);
		string[] tags = Enumerable.Range(0, 1000).Select(i => $"{i}-{huge}").ToArray();
		DocumentEnvelope envelope = new(huge, huge, huge, huge, DateTimeOffset.UnixEpoch, huge, tags);

		string text = envelope.Render("").ExposeForRedaction();
		YamlMappingNode frontmatter = Frontmatter(text);

		Assert.Equal(512, frontmatter.Scalar("title").Length);
		Assert.Equal(32, frontmatter.Sequence("tags").Length);
		Assert.Equal(64, frontmatter.Sequence("tags")[0].Length);
		Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) < 8192, "frontmatter must fit the sidecar's metadata head");
	}

	[Fact]
	public void The_tag_limit_counts_only_tags_that_are_kept()
	{
		string[] tags = [.. Enumerable.Repeat("  ", 40), .. Enumerable.Range(0, 32).Select(static i => $"t{i}")];

		string[] kept = Frontmatter(new DocumentEnvelope("s", "i", Tags: tags).Render("").ExposeForRedaction()).Sequence("tags");

		Assert.Equal(32, kept.Length);
		Assert.Equal("t0", kept[0]);
		Assert.Equal("t31", kept[^1]);
	}
}

/// <summary>The wrong call must not compile, serialize, or log.</summary>
public sealed class UnredactedMarkdownTests
{
	private const string Secret = "sk-live-a-real-looking-secret";

	[Fact]
	public void It_converts_to_nothing()
	{
		MethodInfo[] conversions = typeof(UnredactedMarkdown)
			.GetMethods(BindingFlags.Public | BindingFlags.Static)
			.Where(static method => method.Name is "op_Implicit" or "op_Explicit")
			.ToArray();

		Assert.Empty(conversions);
		Assert.False(typeof(IConvertible).IsAssignableFrom(typeof(UnredactedMarkdown)));
		Assert.True(typeof(UnredactedMarkdown).IsSealed);
		Assert.Empty(typeof(UnredactedMarkdown).GetConstructors());
	}

	[Fact]
	public void Formatting_and_logging_withhold_the_text()
	{
		UnredactedMarkdown markdown = new(Secret);

		Assert.Equal("[unredacted markdown withheld]", markdown.ToString());
		Assert.DoesNotContain(Secret, $"{markdown}");
		Assert.Equal(Secret, markdown.ExposeForRedaction());
	}

	[Fact]
	public async Task A_rendered_document_holds_only_redacted_text()
	{
		string token = Memoria.Core.Tests.Redaction.Synthetic.GitHubClassic('p');
		RedactedSession session = await Claude(ClaudeLine("user", token));

		Assert.DoesNotContain(token, session.Documents[0].ToString());
		Assert.DoesNotContain(token, session.Documents[0].Text());
	}

	[Fact]
	public void Serializing_it_throws()
	{
		Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new UnredactedMarkdown(Secret)));
		Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<UnredactedMarkdown>("\"x\""));
	}

	[Fact]
	public void An_envelope_render_is_unredacted_too()
	{
		Assert.IsType<UnredactedMarkdown>(new DocumentEnvelope("s", "i").Render(Secret));
	}
}
