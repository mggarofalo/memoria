using System.Text;
using System.Text.Json;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;
using Memoria.Core.Tests.Redaction;
using static Memoria.Core.Tests.Sessions.SessionTestKit;

namespace Memoria.Core.Tests.Sessions;

/// <summary>Hostile transcripts and tokens that once got past the budget or the rules.</summary>
public sealed class SessionHardeningTests
{
	public static TheoryData<string, string> Tokens() => new()
	{
		{ "github", Synthetic.GitHubClassic('p') },
		{ "github-fine", Synthetic.GitHubFineGrained },
		{ "anthropic", Synthetic.Anthropic },
		{ "openai", Synthetic.OpenAiLegacy },
		{ "aws", Synthetic.AwsAccessKey },
		{ "gitlab", Synthetic.GitLab },
		{ "slack", Synthetic.SlackBot },
		{ "google", Synthetic.GoogleApiKey },
		{ "stripe", Synthetic.StripeLive },
		{ "npm", Synthetic.Npm },
		{ "huggingface", Synthetic.HuggingFace },
		{ "memoria", Synthetic.Memoria },
	};

	private static string Cut(string token, string between)
	{
		int middle = token.Length / 2;
		return string.Concat(token.AsSpan(0, middle), between, token.AsSpan(middle));
	}

	private static string Halves(string token)
	{
		int middle = token.Length / 2;
		return $"{token[..middle]}|{token[middle..]}";
	}

	private static void AssertNeitherHalf(string token, string text)
	{
		int middle = token.Length / 2;
		Assert.DoesNotContain(token[..middle], text, StringComparison.Ordinal);
		Assert.DoesNotContain(token[middle..], text, StringComparison.Ordinal);
	}

	[Theory]
	[MemberData(nameof(Tokens))]
	public void Noise_inside_a_token_does_not_hide_it(string name, string token)
	{
		Assert.NotNull(name);
		foreach (char noise in "\u0000\u0001\u0008\u000b\u000c\u007f\u0085     　")
		{
			string text = new Redactor().Redact(new UnredactedMarkdown($"before {Cut(token, noise.ToString())} after")).Text;
			AssertNeitherHalf(token, text);
			Assert.Contains("before ", text, StringComparison.Ordinal);
			Assert.Contains(" after", text, StringComparison.Ordinal);
		}
	}

	[Theory]
	[MemberData(nameof(Tokens))]
	public void A_line_break_in_the_middle_of_a_token_does_not_hide_it(string name, string token)
	{
		Assert.NotNull(name);
		foreach (string line in new[] { "\n", "\r\n", "\n\n", "\n  " })
		{
			string text = new Redactor().Redact(new UnredactedMarkdown($"before\n{Cut(token, line)}\nafter")).Text;
			AssertNeitherHalf(token, text);
		}
	}

	[Theory]
	[MemberData(nameof(Tokens))]
	public async Task A_token_split_across_messages_is_redacted_in_both(string name, string token)
	{
		Assert.NotNull(name);
		string[] halves = Halves(token).Split('|');

		RedactedSession session = await Claude(ClaudeLine("user", "intro " + halves[0]) + ClaudeLine("assistant", halves[1] + " outro"));

		string text = Assert.Single(session.Documents).Text();
		AssertNeitherHalf(token, text);
		Assert.Contains("intro", text, StringComparison.Ordinal);
		Assert.Contains("outro", text, StringComparison.Ordinal);
		Assert.Equal(2, session.Documents[0].MessageCount);
	}

	[Fact]
	public async Task A_token_split_across_content_blocks_is_redacted()
	{
		string token = Synthetic.GitHubClassic('p');
		int middle = token.Length / 2;
		string line = JsonSerializer.Serialize(new
		{
			type = "user",
			timestamp = "2026-10-01T10:00:00Z",
			cwd = "/srv/demo",
			message = new { role = "user", content = new object[] { new { type = "text", text = "key " + token[..middle] }, new { type = "text", text = token[middle..] + " end" } } },
		}) + "\n";

		string text = Assert.Single((await Claude(line)).Documents).Text();

		AssertNeitherHalf(token, text);
	}

	[Theory]
	[InlineData("AK", "IA")]
	[InlineData("AS", "IA")]
	public void An_aws_key_whose_second_half_is_all_capitals_is_still_joined(string first, string second)
	{
		string head = first + second + "ABCDEFGH";
		const string Tail = "IJKLMNOP";

		string text = new Redactor().Redact(new UnredactedMarkdown($"before {head}\n{Tail} after")).Text;

		Assert.DoesNotContain(head, text, StringComparison.Ordinal);
		Assert.DoesNotContain(Tail, text, StringComparison.Ordinal);
	}

	[Fact]
	public void Another_tokens_prefix_does_not_join_a_following_word_in_capitals()
	{
		string token = Synthetic.GitHubClassic('p');

		string text = new Redactor().Redact(new UnredactedMarkdown(token + "\nHEADING here")).Text;

		Assert.Contains("HEADING here", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Ordinary_text_with_these_characters_is_left_exactly_as_it_was()
	{
		const string Text = "Bonjour ! Prix : 1 000 €.\u0007 Line\u0001two\nghp_ is a prefix\nand this is the next line.";

		Assert.Equal(Text, new Redactor().Redact(new UnredactedMarkdown(Text)).Text);
	}

	[Fact]
	public void A_whole_token_at_the_end_of_a_line_does_not_swallow_the_next_word()
	{
		string token = Synthetic.GitHubClassic('p');

		string text = new Redactor().Redact(new UnredactedMarkdown(token + "\nnextword is here")).Text;

		Assert.Contains("nextword is here", text, StringComparison.Ordinal);
		Assert.DoesNotContain(token, text, StringComparison.Ordinal);
	}

	[Fact]
	public void Holds_credential_says_yes_only_to_what_redaction_would_change()
	{
		Assert.True(Redactor.HoldsCredential(Synthetic.GitHubClassic('p')));
		Assert.False(Redactor.HoldsCredential("claude-sessions"));
	}

	[Fact]
	public void Endless_distinct_metadata_does_not_grow_the_cache_past_its_share()
	{
		SessionRenderOptions options = new() { MaxRetainedChars = 64 * 1024 };
		CodexTranscript transcript = new(new SessionSource(SessionFormat.Codex, CodexId), options, new SessionReadReport());
		string padding = new('t', 200);

		for (int i = 0; i < 10_000; i++)
		{
			using JsonDocument record = JsonDocument.Parse("{\"type\":\"turn_context\",\"timestamp\":\"2026-10-01T10:00:00Z\",\"payload\":{\"turn_id\":\"" + padding + i + "\"}}");
			transcript.Add(record.RootElement);
		}

		Assert.InRange(transcript.MetadataCachedChars, 1, options.MaxRetainedChars / 8);
	}

	[Fact]
	public void A_very_long_metadata_value_is_never_remembered()
	{
		CodexTranscript transcript = new(new SessionSource(SessionFormat.Codex, CodexId), SessionRenderOptions.Default, new SessionReadReport());
		long before = transcript.MetadataCachedChars;
		using JsonDocument record = JsonDocument.Parse("{\"type\":\"turn_context\",\"timestamp\":\"2026-10-01T10:00:00Z\",\"payload\":{\"turn_id\":\"" + new string('t', 5000) + "\"}}");

		transcript.Add(record.RootElement);

		Assert.Equal(before, transcript.MetadataCachedChars);
	}

	[Fact]
	public async Task A_line_of_countless_tiny_values_counts_as_oversized_and_costs_no_parse()
	{
		string wide = "{\"type\":\"user\",\"x\":[" + string.Join(',', Enumerable.Repeat("1", 600_000)) + "]}\n";

		RedactedSession session = await Claude(wide + ClaudeLine("user", "kept"));

		Assert.Equal(1, session.Report.OversizedLines);
		Assert.True(session.Report.HasLostContent);
		Assert.Contains("kept", Assert.Single(session.Documents).Text(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_line_with_many_tokens_under_the_limit_is_still_read()
	{
		string wide = "{\"type\":\"progress\",\"x\":[" + string.Join(',', Enumerable.Repeat("1", 1000)) + "]}\n";

		RedactedSession session = await Claude(wide + ClaudeLine("user", "kept"));

		Assert.Equal(0, session.Report.OversizedLines);
		Assert.Equal(1, session.Report.UnrenderedRecords);
	}

	[Fact]
	public async Task Lost_content_excludes_frontmatter_lists_cut_to_fit()
	{
		string files = string.Concat(Enumerable.Range(0, 400).Select(static i => $"\"{new string('p', 64)}/{i}.txt\":{{}},")).TrimEnd(',');
		string jsonl = ClaudeLine("user", "hello") + "{\"type\":\"file-history-snapshot\",\"snapshot\":{\"trackedFileBackups\":{" + files + "}}}\n";

		RedactedSession session = await Claude(jsonl);

		Assert.True(session.Report.FilesTruncated > 0);
		Assert.True(session.Report.HasSkippedContent);
		Assert.False(session.Report.HasLostContent);
	}

	[Fact]
	public async Task Reading_then_rendering_gives_what_rendering_gives()
	{
		string jsonl = ClaudeLine("user", "hello");
		SessionSource source = new(SessionFormat.ClaudeCode, ClaudeId);

		ReadSessionTranscript read = await SessionRenderer.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(jsonl)), source);
		RedactedSession rendered = await SessionRenderer.RenderAsync(new MemoryStream(Encoding.UTF8.GetBytes(jsonl)), source);

		Assert.Equal(rendered.Documents[0].Content.Text, read.Render().Documents[0].Content.Text);
	}

	[Fact]
	public async Task A_failure_while_rendering_a_read_transcript_still_carries_the_report()
	{
		ReadSessionTranscript read = await SessionRenderer.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(ClaudeLine("user", "hello"))), new SessionSource(SessionFormat.ClaudeCode, ClaudeId));

		Assert.NotNull(read.Render().Report);
	}

	[Fact]
	public async Task Distinct_paths_keep_date_and_part_and_change_the_family()
	{
		string jsonl = ClaudeLine("user", new string('a', 3000)) + ClaudeLine("assistant", new string('b', 3000));
		RedactedSession session = await Claude(jsonl, new SessionRenderOptions { MaxPartChars = 60 });

		RedactedSession distinct = session.WithDistinctPaths()!;

		Assert.Equal(session.Documents.Count, distinct.Documents.Count);
		Assert.Equal(session.PathIdentity, distinct.PathIdentity);
		Assert.Matches("^2026-10-01_abcdef12-[0-9a-f]{8}\\.md$", distinct.Documents[0].Path);
		Assert.Matches("^2026-10-01_abcdef12-[0-9a-f]{8}_part-002\\.md$", distinct.Documents[1].Path);
		Assert.True(distinct.IsSupersededPath(session.Documents[1].Path) is false);
		Assert.False(session.IsSupersededPath(distinct.Documents[1].Path));
	}

	[Fact]
	public async Task Distinct_paths_get_longer_and_then_run_out()
	{
		RedactedSession session = await Claude(ClaudeLine("user", "hello"));

		RedactedSession first = session.WithDistinctPaths()!;
		RedactedSession second = first.WithDistinctPaths()!;
		RedactedSession third = second.WithDistinctPaths()!;

		Assert.Matches("^abcdef12-[0-9a-f]{8}$", first.PathStem);
		Assert.Matches("^abcdef12-[0-9a-f]{16}$", second.PathStem);
		Assert.Matches("^abcdef12-[0-9a-f]{32}$", third.PathStem);
		Assert.Matches("^2026-10-01_abcdef12-[0-9a-f]{32}\\.md$", third.Documents[0].Path);
		Assert.Null(third.WithDistinctPaths());
	}

	[Fact]
	public async Task A_project_folder_cannot_be_chosen_to_produce_a_distinct_path()
	{
		string jsonl = ClaudeLine("user", "hello");
		RedactedSession victim = await Claude(jsonl);
		RedactedSession squatter = await SessionRenderer.RenderAsync(
			new MemoryStream(Encoding.UTF8.GetBytes(jsonl)),
			new SessionSource(SessionFormat.ClaudeCode, "abcdef12-ffff-ffff-ffff-ffffffffffff") { ProjectDirectory = ClaudeId });

		Assert.NotEqual(victim.WithDistinctPaths()!.PathStem, squatter.PathStem);
	}

	[Fact]
	public async Task The_digest_of_the_stored_messages_says_whether_an_upload_starts_with_them()
	{
		string a = ClaudeLine("user", "message A"), b = ClaudeLine("assistant", "message B"), c = ClaudeLine("user", "message C");
		RedactedSession stored = await Claude(a + b);

		string digest = stored.MessageDigestAt(2)!;
		Assert.NotEqual((await Claude(ClaudeLine("assistant", "message A"))).MessageDigestAt(1), (await Claude(a)).MessageDigestAt(1));

		Assert.Equal(digest, (await Claude(a + b + c)).MessageDigestAt(2));
		Assert.Equal(digest, (await Claude(a + "{torn\n" + b)).MessageDigestAt(2));
		Assert.NotEqual(digest, (await Claude(a + c)).MessageDigestAt(2));
		Assert.NotEqual(digest, (await Claude(b + a)).MessageDigestAt(2));
		Assert.Null((await Claude(a)).MessageDigestAt(2));
		Assert.Null(stored.MessageDigestAt(-1));
		Assert.Equal("", stored.MessageDigestAt(0));
		Assert.Equal(digest, stored.WithDistinctPaths()!.MessageDigestAt(2));
	}

	[Fact]
	public async Task The_digest_does_not_care_how_a_session_splits_into_parts()
	{
		string jsonl = ClaudeLine("user", new string('a', 3000)) + ClaudeLine("assistant", new string('b', 3000));

		RedactedSession one = await Claude(jsonl);
		RedactedSession two = await Claude(jsonl, new SessionRenderOptions { MaxPartChars = 60 });

		Assert.Single(one.Documents);
		Assert.Equal(2, two.Documents.Count);
		Assert.Equal(one.MessageDigestAt(2), two.MessageDigestAt(2));
	}

	[Fact]
	public void A_replaced_message_takes_the_digest_of_it_too()
	{
		string token = Synthetic.GitHubClassic('p');
		string document = "---\nmessage_sha256: \"digestoftheoriginal\"\n---\n\n## User\n\n" + token + "\n";
		SessionPart part = new(new ArraySegment<SessionMessage>([new SessionMessage(SessionMessage.User, token, null, null, token.Length)]), 1, 0, SessionTimestamp.FromFallback(null));

		string clean = PartGate.Clean(document, part, []).Text;

		Assert.DoesNotContain("digestoftheoriginal", clean, StringComparison.Ordinal);
		Assert.DoesNotContain(token, clean, StringComparison.Ordinal);
	}

	[Fact]
	public void A_part_with_nothing_replaced_keeps_its_digest()
	{
		string document = "---\nmessage_sha256: \"digestoftheoriginal\"\n---\n\n## User\n\nhello\n";
		SessionPart part = new(new ArraySegment<SessionMessage>([new SessionMessage(SessionMessage.User, "hello", null, null, 5)]), 1, 0, SessionTimestamp.FromFallback(null));

		Assert.Contains("digestoftheoriginal", PartGate.Clean(document, part, []).Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_copy_with_a_hole_says_so()
	{
		string line = ClaudeLine("user", "a");

		Assert.True((await Claude(line + "{torn\n" + line)).Report.HasHoles);
		Assert.False((await Claude(line + line.TrimEnd('\n'))).Report.HasHoles);
	}

	[Fact]
	public async Task A_codex_session_knows_when_its_meta_names_another()
	{
		RedactedSession same = await Codex(CodexMeta() + CodexMessage("user", "hello"));
		RedactedSession other = await Codex(CodexMeta(id: "99999999-2222-4333-8444-555555555555") + CodexMessage("user", "hello"));

		Assert.False(same.NamesAnotherSession);
		Assert.True(other.NamesAnotherSession);
	}

	[Fact]
	public async Task Identity_includes_what_the_path_is_made_of_and_not_what_it_is_not()
	{
		string jsonl = ClaudeLine("user", "hello");

		RedactedSession plain = await Claude(jsonl);
		RedactedSession named = await SessionRenderer.RenderAsync(new MemoryStream(Encoding.UTF8.GetBytes(jsonl)), new SessionSource(SessionFormat.ClaudeCode, ClaudeId) { ProjectDirectory = "-Users-me-Source-demo" });
		RedactedSession moved = await SessionRenderer.RenderAsync(new MemoryStream(Encoding.UTF8.GetBytes(jsonl)), new SessionSource(SessionFormat.ClaudeCode, ClaudeId) { ProjectDirectory = "-somewhere-else" });

		Assert.Equal(plain.PathIdentity, named.PathIdentity);
		Assert.NotEqual(plain.PathIdentity, moved.PathIdentity);
		Assert.Equal(plain.Documents[0].Path, named.Documents[0].Path);
	}
}
