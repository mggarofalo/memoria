using System.Text;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;
using Memoria.Core.Tests.Redaction;
using YamlDotNet.RepresentationModel;
using static Memoria.Core.Tests.Sessions.SessionTestKit;

namespace Memoria.Core.Tests.Sessions;

/// <summary>A finished part's gate replaces what it finds and reports it, and a transform can no longer make a fresh match.</summary>
public sealed class SessionGateTests
{
	private static string Token => Synthetic.GitHubClassic('p');

	[Fact]
	public void Hit_in_a_scalar_field_replaces_that_field_and_reports_it()
	{
		const string Fence = "---\n";
		string document = Fence + "project: \"demo\"\nslug: \"" + Token + "\"\n" + Fence + "\n## User\n\nhello\n";
		List<SessionFieldRefusal> refusals = [];

		RedactedMarkdown clean = PartGate.Clean(document, Part("hello"), refusals);

		Assert.Equal(Fence + "project: \"demo\"\nslug: \"[REDACTED:refused-field]\"\n" + Fence + "\n## User\n\nhello\n", clean.Text);
		Assert.Equal([new SessionFieldRefusal(1, "slug", "github-token")], refusals);
		Assert.Equal("[REDACTED:refused-field]", Frontmatter(clean.Text).Scalar("slug"));
	}

	[Fact]
	public void Hit_in_a_list_field_replaces_the_whole_list_and_stays_parseable()
	{
		string document = "---\ntags:\n  - \"ok\"\n  - \"" + Token + "\"\nproject: \"demo\"\n---\n\n## User\n\nhello\n";
		List<SessionFieldRefusal> refusals = [];

		RedactedMarkdown clean = PartGate.Clean(document, Part("hello"), refusals);

		Assert.Equal(["[REDACTED:refused-field]"], Frontmatter(clean.Text).Sequence("tags"));
		Assert.Equal("demo", Frontmatter(clean.Text).Scalar("project"));
		Assert.Equal("tags", Assert.Single(refusals).Field);
	}

	[Fact]
	public void Hit_in_a_flow_list_stays_a_list()
	{
		string document = "---\nturn_ids: [\"a\", \"" + Token + "\"]\n---\n\n## User\n\nhello\n";

		RedactedMarkdown clean = PartGate.Clean(document, Part("hello"), []);

		Assert.Equal(["[REDACTED:refused-field]"], Frontmatter(clean.Text).Sequence("turn_ids"));
	}

	[Fact]
	public void Hit_in_a_message_replaces_only_that_message()
	{
		string text = "text with " + Token;
		string document = "---\nproject: \"demo\"\n---\n\n## User\n\nfirst\n\n## Assistant\n\n" + text + "\n\n## User\n\nthird\n";
		List<SessionFieldRefusal> refusals = [];

		RedactedMarkdown clean = PartGate.Clean(document, PartAt(7, "first", text, "third"), refusals);

		Assert.Equal("---\nproject: \"demo\"\n---\n\n## User\n\nfirst\n\n## Assistant\n\n[REDACTED:refused-message]\n\n## User\n\nthird\n", clean.Text);
		Assert.Equal([new SessionFieldRefusal(1, "message:8", "github-token")], refusals);
	}

	[Fact]
	public void Hit_in_a_title_replaces_only_the_title()
	{
		string document = "---\nproject: \"demo\"\n---\n\n# " + Token + "\n\n## User\n\nhello\n";
		List<SessionFieldRefusal> refusals = [];

		RedactedMarkdown clean = PartGate.Clean(document, Part("hello"), refusals);

		Assert.Equal("---\nproject: \"demo\"\n---\n\n# [REDACTED:refused-field]\n\n## User\n\nhello\n", clean.Text);
		Assert.Equal("title", Assert.Single(refusals).Field);
	}

	[Fact]
	public void Several_hits_are_all_replaced_and_reported()
	{
		string document = "---\nslug: \"" + Token + "\"\nproject: \"" + Token + "\"\n---\n\n## User\n\n" + Token + "\n";
		List<SessionFieldRefusal> refusals = [];

		RedactedMarkdown clean = PartGate.Clean(document, Part(Token), refusals);

		Assert.DoesNotContain(Token, clean.Text, StringComparison.Ordinal);
		Assert.Equal(3, refusals.Count);
	}

	[Fact]
	public void Clean_part_passes_through_unchanged()
	{
		const string Document = "---\nproject: \"demo\"\n---\n\n# Title\n\n## User\n\nhello\n";
		List<SessionFieldRefusal> refusals = [];

		Assert.Equal(Document, PartGate.Clean(Document, Part("hello"), refusals).Text);
		Assert.Empty(refusals);
	}

	[Fact]
	public void Report_holds_no_value()
	{
		string token = Token;
		List<SessionFieldRefusal> refusals = [];

		PartGate.Clean("---\nslug: \"" + token + "\"\n---\n\n## User\n\nhello\n", Part("hello"), refusals);

		Assert.DoesNotContain(token, refusals[0].ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Branch_that_a_transform_turns_into_a_token_is_redacted_not_refused()
	{
		string body = Synthetic.Body(40, 1200);
		string branch = "sk/ant/api03/" + body;

		RedactedSession session = await Claude(ClaudeLine("user", "hello", branch: branch));

		string text = Assert.Single(session.Documents).Text();
		Assert.DoesNotContain("sk-ant-", text, StringComparison.Ordinal);
		Assert.DoesNotContain(body + "\"\n  - ", text, StringComparison.Ordinal);
		Assert.Contains("## User\n\nhello\n", text, StringComparison.Ordinal);
		Assert.NotEmpty(Frontmatter(text).Sequence("tags"));
	}

	[Fact]
	public async Task File_path_that_a_transform_turns_into_a_url_password_is_redacted()
	{
		const string Secret = "Hunter2x9";
		string snapshot = "{\"type\":\"file-history-snapshot\",\"snapshot\":{\"trackedFileBackups\":{\"https:\\\\\\\\u:" + Secret + "@h\":{}}}}\n";

		RedactedSession session = await Claude(ClaudeLine("user", "hello") + snapshot);

		Assert.DoesNotContain(Secret, Assert.Single(session.Documents).Text(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Turn_ids_that_join_into_a_flag_and_value_refuse_the_field_only()
	{
		string random = Synthetic.Body(32, 1201);
		string jsonl = CodexMeta() + CodexMessage("user", "hello", "--token") + CodexMessage("assistant", "reply", random);

		RedactedSession session = await Codex(jsonl);

		string text = Assert.Single(session.Documents).Text();
		Assert.DoesNotContain(random, text, StringComparison.Ordinal);
		Assert.Contains("## User\n\nhello\n", text, StringComparison.Ordinal);
		Assert.Contains("## Assistant\n\nreply\n", text, StringComparison.Ordinal);
		Assert.Contains(session.Refusals, static refusal => refusal.Field == "turn_ids");
		Assert.NotEmpty(Frontmatter(text).Keys());
	}

	[Fact]
	public async Task Codex_title_that_forms_a_token_once_hashes_are_removed_is_redacted()
	{
		string body = Synthetic.Body(36, 1202);
		string message = "g#h" + "p_" + body;

		string text = Assert.Single((await Codex(CodexMeta() + CodexMessage("user", message))).Documents).Text();

		Assert.Contains("# [REDACTED:github-token]\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("# gh", text, StringComparison.Ordinal);
		Assert.Contains("## User\n\n" + message + "\n", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Two_sessions_whose_ids_hold_credentials_get_different_paths()
	{
		string first = Synthetic.GitHubClassic('p');
		string second = "gh" + "p_" + Synthetic.Body(36, 1203);

		RedactedSession a = await Claude(ClaudeLine("user", "hello"), sessionId: first);
		RedactedSession b = await Claude(ClaudeLine("user", "hello"), sessionId: second);

		Assert.NotEqual(a.Documents[0].Path, b.Documents[0].Path);
		Assert.DoesNotContain(first[..8], a.Documents[0].Path, StringComparison.Ordinal);
		Assert.DoesNotContain(second[..8], b.Documents[0].Path, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Two_codex_sessions_whose_ids_hold_credentials_get_different_paths()
	{
		string first = Synthetic.GitHubClassic('p');
		string second = "gh" + "p_" + Synthetic.Body(36, 1204);

		RedactedSession a = await Codex(CodexMeta(id: first) + CodexMessage("user", "hello"));
		RedactedSession b = await Codex(CodexMeta(id: second) + CodexMessage("user", "hello"));

		Assert.NotEqual(a.Documents[0].Path, b.Documents[0].Path);
		Assert.DoesNotContain(first, a.Documents[0].Path, StringComparison.Ordinal);
		Assert.Null(SidecarRefusal(a.Documents[0].Path));
	}

	[Theory]
	[InlineData(3)]
	[InlineData(5)]
	public async Task Message_ending_in_full_base64_lines_keeps_every_message(int lines)
	{
		string body = string.Join('\n', Enumerable.Range(0, lines).Select(static i => Synthetic.Body(64, 1205 + i)));
		string jsonl = ClaudeLine("user", "before\n" + body) + ClaudeLine("assistant", "after") + ClaudeLine("user", body);

		RedactedSession session = await Claude(jsonl);

		RedactedSessionDocument document = Assert.Single(session.Documents);
		Assert.Equal(3, document.MessageCount);
		Assert.Empty(session.Refusals);
		Assert.Contains("## Assistant\n\nafter\n", document.Text(), StringComparison.Ordinal);
		Assert.DoesNotContain(Synthetic.Body(64, 1205), document.Text(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Untrustworthy_failure_keeps_the_read_report()
	{
		string huge = new StringBuilder("sk-a-".Length * 2_000_000).Insert(0, "s" + "k-a-", 2_000_000).ToString();

		try
		{
			RedactedSession session = await Claude(ClaudeLine("user", huge), new SessionRenderOptions { MaxPartChars = 65536 });
			Assert.Single(session.Documents);
		}
		catch (RedactionException exception)
		{
			Assert.NotNull(exception.ReadReport);
			Assert.Equal(1, exception.ReadReport.Messages);
		}
	}

	[Fact]
	public async Task Renderer_fuzz_never_fails_on_its_own_output_and_stays_parseable()
	{
		string[] fragments =
		[
			"\n", "\n\n", " ", "/", "\\", "-", "#", "\"", "'", ":", "=", "@", ",", "|", "[REDACTED:x]", "--token", "--password", "password=", "token: ", "curl -u a:", "https:\\\\u:", "sk/ant/api03/", "g#hp_",
			Synthetic.GitHubClassic('p'), Synthetic.Body(64, 1210), Synthetic.Body(64, 1211), Synthetic.Body(64, 1212), Synthetic.Body(40, 1213), "Hunter2x", "-----BEGIN " + Synthetic.PrivateWord + " KEY-----", "-----END " + Synthetic.PrivateWord + " KEY-----", "﷐", "�", "​",
		];
		Random random = new(20261105);
		string Pick(int most) => string.Concat(Enumerable.Range(0, random.Next(1, most)).Select(_ => fragments[random.Next(fragments.Length)]));

		for (int i = 0; i < 1500; i++)
		{
			string jsonl = ClaudeLine("user", "m " + Pick(10), cwd: "/srv/" + Pick(3), branch: Pick(3), slug: Pick(3)) + ClaudeLine("assistant", "m " + Pick(10));
			RedactedSession session = await Claude(jsonl, sessionId: i % 2 == 0 ? ClaudeId : Pick(3).Replace("\n", "", StringComparison.Ordinal) + "x");
			Assert.Single(session.Documents);
			Assert.Equal(2, session.Documents[0].MessageCount);
			Assert.NotEmpty(Frontmatter(session.Documents[0].Text()).Keys());
			Assert.Null(Redactor.Shared.FindFirstHit(session.Documents[0].Text()));
		}
	}

	private static SessionPart Part(params string[] messages)
	{
		return PartAt(0, messages);
	}

	private static SessionPart PartAt(int messageStart, params string[] messages)
	{
		SessionMessage[] all = [.. messages.Select((text, i) => new SessionMessage(i % 2 == 0 ? SessionMessage.User : SessionMessage.Assistant, text, null, null, text.Length))];
		return new SessionPart(new ArraySegment<SessionMessage>(all), 1, messageStart, SessionTimestamp.FromFallback(null));
	}
}
