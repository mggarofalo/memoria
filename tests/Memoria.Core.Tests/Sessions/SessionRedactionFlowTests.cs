using System.Security.Cryptography;
using System.Text;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;
using Memoria.Core.Tests.Redaction;
using YamlDotNet.RepresentationModel;
using static Memoria.Core.Tests.Sessions.SessionTestKit;

namespace Memoria.Core.Tests.Sessions;

/// <summary>Redaction runs before anything is derived from the text, and a finished part must pass a second check.</summary>
public sealed class SessionRedactionFlowTests
{
	private const string Begin = "-----BEGIN " + Synthetic.PrivateWord + " KEY-----";
	private const string End = "-----END " + Synthetic.PrivateWord + " KEY-----";

	private static string Token => Synthetic.GitHubClassic('p');

	[Fact]
	public async Task Key_spread_over_two_messages_is_gone_from_both()
	{
		string first = Synthetic.Base64(64, 920);
		string second = Synthetic.Base64(64, 921);
		string jsonl = ClaudeLine("user", $"{Begin}\n{first}") + ClaudeLine("assistant", $"{second}\n{End}");

		RedactedSession session = await Claude(jsonl);

		string text = Assert.Single(session.Documents).Text();
		Assert.DoesNotContain(first, text, StringComparison.Ordinal);
		Assert.DoesNotContain(second, text, StringComparison.Ordinal);
		Assert.Contains("## User\n\n[REDACTED:private-key]\n", text, StringComparison.Ordinal);
		Assert.Contains("## Assistant\n\n[REDACTED:private-key]\n", text, StringComparison.Ordinal);
		Assert.Equal(2, Assert.Single(session.Documents).MessageCount);
	}

	[Fact]
	public async Task Next_message_starts_after_a_key_cut_off_in_the_previous_one()
	{
		string jsonl = ClaudeLine("user", $"{Begin}\n{Synthetic.Base64(64, 922)}") + ClaudeLine("assistant", "Please do not paste keys.");

		string text = Assert.Single((await Claude(jsonl)).Documents).Text();

		Assert.Contains("## Assistant\n\nPlease do not paste keys.\n", text, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(95)]
	[InlineData(99)]
	[InlineData(100)]
	public async Task Token_cut_by_the_title_limit_leaves_no_head_in_the_heading(int leadingChars)
	{
		string token = Token;
		string message = new string('w', leadingChars) + " " + token + " more";

		string text = Assert.Single((await Claude(ClaudeLine("user", message))).Documents).Text();

		Assert.DoesNotContain(token[..4], text, StringComparison.Ordinal);
		Assert.Contains("[REDACTED:github-token] more", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Codex_title_is_cut_from_redacted_text()
	{
		string token = Token;
		string jsonl = CodexMeta() + CodexMessage("user", new string('w', 95) + " " + token);

		string text = Assert.Single((await Codex(jsonl)).Documents).Text();

		Assert.DoesNotContain(token[..4], text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Title_cut_that_changes_how_a_value_reads_does_not_refuse_the_document()
	{
		string message = "Password: " + new string('w', 90) + " is what the prompt said";

		RedactedSession session = await Claude(ClaudeLine("user", message));

		Assert.Single(session.Documents);
	}

	[Fact]
	public async Task Every_claude_metadata_string_is_redacted()
	{
		string token = Token;
		string jsonl = ClaudeLine("user", "hello", cwd: $"/srv/{token}/app", branch: $"fix/{token}", slug: $"slug-{token}")
			+ "{\"type\":\"file-history-snapshot\",\"snapshot\":{\"trackedFileBackups\":{\"src/" + token + ".txt\":{}}}}\n";

		RedactedSession session = await Claude(jsonl);

		string text = Assert.Single(session.Documents).Text();
		Assert.DoesNotContain(token, text, StringComparison.Ordinal);
		Assert.True(session.CountsByRule["github-token"] >= 4);
		Assert.NotEmpty(Frontmatter(text).Keys());
	}

	[Fact]
	public async Task Every_codex_metadata_string_is_redacted()
	{
		string token = Token;
		string meta = CodexRecord("session_meta", new Dictionary<string, object?>
		{
			["id"] = CodexId,
			["cwd"] = $"/srv/{token}",
			["session_id"] = token,
			["parent_thread_id"] = token,
			["forked_from_id"] = token,
			["agent_path"] = token,
			["agent_role"] = token,
			["source"] = token,
		});

		string text = Assert.Single((await Codex(meta + CodexMessage("user", "hello", token))).Documents).Text();

		Assert.DoesNotContain(token, text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Metadata_is_redacted_before_it_is_clipped()
	{
		string token = Token;
		string branch = new string('b', SessionFrontmatter.MaxFieldBytes - 10) + "/" + token;

		string text = Assert.Single((await Claude(ClaudeLine("user", "hello", branch: branch))).Documents).Text();

		Assert.DoesNotContain(token[..8], text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Session_id_with_a_token_does_not_reach_the_path()
	{
		string token = Token;

		RedactedSession session = await Claude(ClaudeLine("user", "hello"), sessionId: token);

		RedactedSessionDocument document = Assert.Single(session.Documents);
		Assert.DoesNotContain(token[..8], document.Path, StringComparison.Ordinal);
		Assert.DoesNotContain(token, document.Text(), StringComparison.Ordinal);
		Assert.Null(SidecarRefusal(document.Path));
	}

	[Fact]
	public async Task Envelope_field_is_redacted_before_it_is_clipped()
	{
		string token = Token;
		string title = new string('t', SessionFrontmatter.MaxFieldBytes - 10) + " " + token;

		string text = new DocumentEnvelope("s", "i", Title: title).Render("").ExposeForRedaction();

		Assert.DoesNotContain(token[..8], text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Digest_is_over_redacted_text()
	{
		string secret = "password=hunter2";
		const string Redacted = "password=[REDACTED:secret-assignment]";

		string text = Assert.Single((await Codex(CodexMeta() + CodexMessage("user", secret))).Documents).Text();

		string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("user\0" + Redacted + "\0")));
		string rawDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("user\0" + secret + "\0")));
		YamlMappingNode frontmatter = Frontmatter(text);
		Assert.Equal(digest, frontmatter.Scalar("message_sha256"));
		Assert.DoesNotContain(rawDigest, text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Sizing_uses_redacted_length()
	{
		SessionRenderOptions options = new() { MaxPartChars = 2000 };
		string jsonl = ClaudeLine("user", Synthetic.Body(4000, 923).Insert(0, "token=")) + ClaudeLine("assistant", "short reply");

		RedactedSession session = await Claude(jsonl, options);

		// Once redacted, the first message is short, so both fit in one part.
		Assert.Single(session.Documents);
	}

	[Fact]
	public async Task Parts_still_split_between_messages()
	{
		SessionRenderOptions options = new() { MaxPartChars = 60 };
		string jsonl = ClaudeLine("user", new string('a', 50)) + ClaudeLine("assistant", new string('b', 50));

		RedactedSession session = await Claude(jsonl, options);

		Assert.Equal(2, session.Documents.Count);
		Assert.Equal(1, session.Documents[0].MessageCount);
		Assert.Equal(1, session.Documents[1].MessageStart);
	}

	[Fact]
	public async Task Frontmatter_closes_inside_the_window_when_every_value_is_a_placeholder()
	{
		string token = Token;
		string files = string.Concat(Enumerable.Range(0, 400).Select(i => $"\"{token}/{i}.txt\":{{}},")).TrimEnd(',');
		string jsonl = ClaudeLine("user", "hello", branch: token, slug: token, cwd: "/srv/" + token)
			+ "{\"type\":\"file-history-snapshot\",\"snapshot\":{\"trackedFileBackups\":{" + files + "}}}\n";

		string text = Assert.Single((await Claude(jsonl)).Documents).Text();

		int close = text.IndexOf("\n---\n", 3, StringComparison.Ordinal);
		Assert.InRange(Encoding.UTF8.GetByteCount(text[..(close + 5)]), 1, SessionFrontmatter.SidecarHeadWindow);
		Assert.DoesNotContain(token, text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Non_character_used_as_the_boundary_is_repaired_in_input()
	{
		RedactedSession session = await Claude(ClaudeLine("user", "a﷐b") + ClaudeLine("assistant", "c"));

		string text = Assert.Single(session.Documents).Text();
		Assert.Contains("a�b", text, StringComparison.Ordinal);
		Assert.Equal(1, session.Report.RepairedStrings);
		Assert.Equal(2, session.Documents[0].MessageCount);
	}

	[Fact]
	public void Gate_refuses_a_finished_document_that_still_holds_a_credential()
	{
		string token = Token;

		RedactionException exception = Assert.Throws<RedactionException>(() => Redactor.Shared.Seal("# Title\n\n" + token));

		Assert.Equal("github-token", exception.RuleName);
		Assert.Equal(RedactionFailure.FoundAfterRedaction, exception.Failure);
		Assert.Null(exception.InnerException);
		Assert.DoesNotContain(token, exception.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void Gate_passes_a_clean_document_through_unchanged()
	{
		const string Document = "---\ntitle: \"[REDACTED:github-token]\"\n---\n\nBody.\n";

		RedactedMarkdown sealedDocument = Redactor.Shared.Seal(Document);

		Assert.Equal(Document, sealedDocument.Text);
		Assert.Empty(sealedDocument.CountsByRule);
	}

	[Fact]
	public async Task A_session_with_no_messages_renders_no_documents()
	{
		RedactedSession session = await Claude("{\"type\":\"progress\"}\n");

		Assert.Empty(session.Documents);
		Assert.Empty(session.CountsByRule);
	}

	[Fact]
	public void Renderer_exposes_only_redacted_results()
	{
		Assert.Equal(typeof(Task<RedactedSession>), typeof(SessionRenderer).GetMethod(nameof(SessionRenderer.RenderAsync))!.ReturnType);
		Assert.Equal(typeof(RedactedMarkdown), typeof(RedactedSessionDocument).GetProperty(nameof(RedactedSessionDocument.Content))!.PropertyType);
	}
}
