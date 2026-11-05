using System.Text.RegularExpressions;
using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>Exact thresholds and every spelling of a separator, one on each side of the line, so a shifted boundary fails a test.</summary>
public sealed class RedactionBoundaryTests
{
	private const string Placeholder = "[REDACTED:private-key]";
	private const string Begin = "-----BEGIN " + Synthetic.PrivateWord + " KEY-----";
	private const string End = "-----END " + Synthetic.PrivateWord + " KEY-----";

	private static readonly Redactor Redactor = new();

	private static string Line(int seed = 700) => Synthetic.Body(64, seed);

	[Theory]
	[InlineData(" ")]
	[InlineData("\t")]
	[InlineData("\n")]
	[InlineData("\r\n")]
	[InlineData("\"")]
	[InlineData("'")]
	[InlineData(",")]
	[InlineData(">")]
	[InlineData("|")]
	[InlineData("#")]
	[InlineData("\\n")]
	[InlineData("\\r")]
	[InlineData("\\t")]
	[InlineData("\\\"")]
	[InlineData("\\'")]
	[InlineData("\\\\n")]
	[InlineData("\\\n")]
	[InlineData("\\\r\n")]
	[InlineData("%0A")]
	[InlineData("%0a")]
	[InlineData("%0D")]
	[InlineData("%0d")]
	[InlineData("%20")]
	[InlineData("%22")]
	[InlineData(" + ")]
	[InlineData(" +\t")]
	[InlineData(" +\n")]
	[InlineData(" +\r\n")]
	[InlineData(" +\"")]
	[InlineData(" +'")]
	public void Separator_between_body_lines_is_consumed(string separator)
	{
		string input = Begin + separator + Line() + separator + Line(701);

		Assert.Equal(Placeholder, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("%2B")]
	[InlineData("%2b")]
	[InlineData("%2F")]
	[InlineData("%2f")]
	[InlineData("%3D")]
	[InlineData("%3d")]
	[InlineData("\\/")]
	[InlineData("\\\\/")]
	[InlineData("\\u002B")]
	[InlineData("\\u002b")]
	[InlineData("\\\\u003D")]
	public void Encoded_base64_symbol_stays_inside_the_token(string symbol)
	{
		string input = $"{Begin}\n{Line()}{symbol}{Line(702)}";

		Assert.Equal(Placeholder, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("%2G")]
	[InlineData("%3F")]
	[InlineData("%0B")]
	[InlineData("%21")]
	[InlineData("\\u012B")]
	[InlineData("\\u102B")]
	[InlineData("\\uZZ2B")]
	[InlineData("\\u00ZZ")]
	[InlineData("\\u00Z1")]
	[InlineData("\\u001Z")]
	public void Near_miss_encoding_ends_the_token_exactly_there(string tail)
	{
		string input = $"{Begin}\n{Line()}{tail}";

		Assert.Equal(Placeholder + tail, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData(15, false)]
	[InlineData(16, true)]
	public void Token_on_a_shared_line_counts_from_the_minimum_body(int length, bool isBody)
	{
		string input = $"{Begin} {Synthetic.Body(length, 703)} and words";

		Assert.Equal(isBody ? $"{Placeholder} and words" : input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData(38, false)]
	[InlineData(39, true)]
	public void Key_body_mostly_of_x_is_a_mask_at_sixty_percent(int xs, bool isMask)
	{
		string body = new string('x', xs) + Synthetic.Digits(65 - xs, 704);
		string input = $"{Begin}\n{body}\n{End}";

		Assert.Equal(isMask ? input : Placeholder, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Exactly_sixty_percent_x_is_a_mask()
	{
		string input = $"{Begin}\n{new string('x', 36)}{Synthetic.Digits(24, 705)}\n{End}";

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Two_dashes_make_an_end_marker()
	{
		Assert.Equal(Placeholder, Redactor.Redact($"{Begin}\n{Line()}\n--END {Synthetic.PrivateWord} KEY--").Text);
	}

	[Fact]
	public void One_dash_makes_an_end_marker_only_when_it_names_a_private_key()
	{
		Assert.Equal(Placeholder, Redactor.Redact($"{Begin}\n{Line()}\n-END {Synthetic.PrivateWord} KEY-").Text);
		Assert.Equal(Placeholder + "\n-END of file", Redactor.Redact($"{Begin}\n{Line()}\n-END of file").Text);
	}

	[Fact]
	public void Short_body_directly_after_the_marker_counts_as_a_line()
	{
		Assert.Equal(Placeholder + "\n", Redactor.Redact($"{Begin}{Synthetic.Body(20, 706)}\n").Text);
	}

	[Fact]
	public void Short_body_after_a_header_and_blank_line_counts_as_a_line()
	{
		string input = $"{Begin}\nProc-Type: 4,ENCRYPTED\n\n{Synthetic.Body(20, 707)}\n";

		Assert.Equal(Placeholder + "\n", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Short_final_line_before_a_single_quote_is_body()
	{
		Assert.Equal($"'{Placeholder}'", Redactor.Redact($"'{Begin}\\n{Line()}\\n{Synthetic.Body(20, 708)}'").Text);
	}

	[Fact]
	public void Short_line_before_a_next_begin_marker_is_not_lost()
	{
		string input = $"{Begin}\n{Line()}\n{Begin}\n{Line(709)}";

		Assert.Equal($"{Placeholder}\n{Placeholder}", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Judged_length_is_inclusive_at_the_cap()
	{
		string atCap = "s" + "k-" + string.Concat(Enumerable.Repeat("word-", 900))[..(RedactionRule.LongCandidate - 3)];
		string overCap = atCap + "more-words-past-the-cap";

		Assert.Equal(atCap, Redactor.Redact(atCap).Text);
		Assert.Equal(overCap, Redactor.Redact(overCap).Text);
	}

	[Fact]
	public void Change_in_an_early_rule_forces_another_pass_even_if_the_last_rule_is_idle()
	{
		RedactionRule first = new("first", Compile(@"Y\[REDACTED:second\]"));
		RedactionRule second = new("second", Compile("Q"));
		RedactionRule idle = new("idle", Compile("NEVER"));
		Redactor redactor = new([first, second, idle], TimeSpan.FromMinutes(1));

		RedactionResult result = redactor.Redact("YQ");

		Assert.Equal("[REDACTED:first]", result.Text);
		Assert.Equal(new Dictionary<string, int> { ["first"] = 1, ["second"] = 1 }, result.CountsByRule);
	}

	[Fact]
	public void Counts_add_up_across_passes()
	{
		RedactionRule first = new("first", Compile(@"Y\[REDACTED:second\]|Z"));
		RedactionRule second = new("second", Compile("Q"));
		Redactor redactor = new([first, second], TimeSpan.FromMinutes(1));

		RedactionResult result = redactor.Redact("Z YQ");

		Assert.Equal("[REDACTED:first] [REDACTED:first]", result.Text);
		Assert.Equal(2, result.CountsByRule["first"]);
	}

	[Theory]
	[InlineData(80, true)]
	[InlineData(81, true)]
	[InlineData(300, true)]
	public void Long_key_name_is_judged_by_its_end(int length, bool redacted)
	{
		string key = new string('k', length - 6) + "_token";
		string input = key + "=a1b2c3d4e5f6a7b8";

		Assert.Equal(redacted ? key + "=[REDACTED:secret-assignment]" : input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("Abcde", true)]
	[InlineData("abCd", false)]
	[InlineData("abcDef", true)]
	[InlineData("abcDe", false)]
	public void Wordy_counts_humps_from_the_first_letter(string value, bool expected)
	{
		Assert.Equal(expected, ValueShapes.IsWordy(value));
	}

	[Theory]
	[InlineData("password=abcd", "password=[REDACTED:secret-assignment]")]
	[InlineData("Token=abc123", "Token=abc123")]
	[InlineData("TOKEN_a=abc123", "TOKEN_a=abc123")]
	[InlineData("TOKEN_1=abc123", "TOKEN_1=[REDACTED:secret-assignment]")]
	[InlineData("%22password%22%3A%22adminx%22", "%22password%22%3A%22[REDACTED:secret-assignment]%22")]
	[InlineData("%27password%27%3A%27adminx%27", "%27password%27%3A%27[REDACTED:secret-assignment]%27")]
	[InlineData("%20password%3A%20adminx", "%20password%3A%20adminx")]
	[InlineData("%2password: adminx yes", "%2password: adminx yes")]
	[InlineData("x%22password: adminx yes", "x%22password: adminx yes")]
	[InlineData("password: 'adminx'", "password: '[REDACTED:secret-assignment]'")]
	[InlineData("password: `adminx`", "password: `[REDACTED:secret-assignment]`")]
	[InlineData("| *token* | a1b2c3d4 |", "| *token* | [REDACTED:secret-assignment] |")]
	[InlineData("|\ttoken | a1b2c3d4 |", "|\ttoken | [REDACTED:secret-assignment] |")]
	[InlineData("token | a1b2c3d4", "token | a1b2c3d4")]
	[InlineData("api_key: a/b/cdef.tsx", "api_key: a/b/cdef.tsx")]
	[InlineData("api_key: .cs", "api_key: .cs")]
	[InlineData("token: a1b2c3d4.cs:", "token: [REDACTED:secret-assignment]")]
	[InlineData("secret:\n  name: x", "secret:\n  name: x")]
	[InlineData("api_key: /tmp/key", "api_key: /tmp/key")]
	[InlineData("api_key: /tmp1a2b3c4d", "api_key: [REDACTED:secret-assignment]")]
	[InlineData("api_key: ~/k1a2b3c4d", "api_key: ~/k1a2b3c4d")]
	[InlineData("api_key: ./k1a2b3c4d", "api_key: ./k1a2b3c4d")]
	[InlineData("api_key: ../k1a2b3c4", "api_key: ../k1a2b3c4")]
	[InlineData("password=abc[REDACTED:a]def[REDACTED:b]g", "password=[REDACTED:secret-assignment]")]
	[InlineData("password=[REDACTED:a][REDACTED:b]", "password=[REDACTED:a][REDACTED:b]")]
	[InlineData("password=%a1b2c3", "password=[REDACTED:secret-assignment]")]
	[InlineData("password=%a", "password=%a")]
	[InlineData("password=%(x", "password=%(x")]
	[InlineData("password=$a$b$c1", "password=[REDACTED:secret-assignment]")]
	public void Assignment_boundaries_give_exact_output(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("a$b.c", true)]
	[InlineData("a_b.c", true)]
	[InlineData("a-b.c", false)]
	[InlineData("a1_b", false)]
	[InlineData("a_1", false)]
	[InlineData("a1b", false)]
	[InlineData("a_b", true)]
	[InlineData("A", true)]
	public void Identifier_shapes_are_exact(string value, bool expected)
	{
		Assert.Equal(expected, ValueShapes.IsCodeReference(value));
	}

	[Theory]
	[InlineData("---\n- {S}\n---\n", "---\n- '[REDACTED:github-token]'\n---\n")]
	[InlineData("---\na: 1\nb: {S}\nc: {S}\n---\nd: {S}\n", "---\na: 1\nb: '[REDACTED:github-token]'\nc: '[REDACTED:github-token]'\n---\nd: [REDACTED:github-token]\n")]
	[InlineData("---\n{S} x\n---\n", "---\n[REDACTED:github-token] x\n---\n")]
	[InlineData("---\n {S}\n---\n", "---\n [REDACTED:github-token]\n---\n")]
	[InlineData("---\nx {S}\n---\n", "---\nx [REDACTED:github-token]\n---\n")]
	[InlineData("---", "---")]
	[InlineData("---\n", "---\n")]
	public void Frontmatter_quoting_boundaries_are_exact(string template, string expected)
	{
		string input = template.Replace("{S}", Synthetic.GitHubClassic('p'), StringComparison.Ordinal);

		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Webhook_placeholder_is_judged_on_its_last_segment_only()
	{
		string real = "https://hooks.slack.com/services/T" + new string('X', 10) + "/B" + new string('X', 10) + "/" + Synthetic.Body(24, 710);
		string expected = "https://hooks.slack.com/services/[REDACTED:slack-webhook]";

		Assert.Equal(expected, Redactor.Redact(real).Text);
	}

	[Fact]
	public void A_provider_prefix_at_length_is_redacted_even_if_the_rest_reads_as_words()
	{
		// The price of never storing a real token that happens to have no digit. A sample has to say it is one.
		const string Slack = "xoxb-only-plain-words-in-this";
		const string GitLab = "gl" + "pat-only-plain-words-in-this";

		Assert.Equal("[REDACTED:slack-token]", Redactor.Redact(Slack).Text);
		Assert.Equal("[REDACTED:gitlab-token]", Redactor.Redact(GitLab).Text);
	}

	[Theory]
	[InlineData("your-token-goes-here-now")]
	[InlineData("example-token-for-the-docs")]
	[InlineData("placeholder-value-goes-in-here")]
	public void A_provider_prefix_on_a_recognizable_sample_survives(string sample)
	{
		string text = "gl" + "pat-" + sample;

		Assert.Equal(text, Redactor.Redact(text).Text);
	}

	[Fact]
	public void A_marker_word_inside_a_random_token_does_not_make_it_a_sample()
	{
		string token = "gl" + "pat-" + "kWqZ-BaR-mXvT-pLdR-nHsY";

		Assert.Equal("[REDACTED:gitlab-token]", Redactor.Redact(token).Text);
	}

	[Fact]
	public void A_dotted_code_name_is_not_taken_for_a_token_with_no_prefix()
	{
		const string Name = "ModelContextProtocolServer.Server.AspNetCoreHttpTransportExtensions";

		Assert.Equal(Name, Redactor.Redact(Name).Text);
	}

	[Fact]
	public void Long_letters_as_a_bare_password_are_redacted()
	{
		string password = new string('q', 6) + "wfpgjluyarstdhneiozxcv";

		Assert.Equal("app:[REDACTED:userinfo-password]@db.internal", Redactor.Redact("app:" + password + "@db.internal").Text);
	}

	[Theory]
	[InlineData("--api-key", "KQWZMXVTPLDRNHSYBGFJCEAU")]
	[InlineData("--api-key", "kWqZ_mXvT_pLdR_nHsY_bGfJ")]
	public void An_upper_case_or_snake_run_that_reads_as_no_name_is_redacted(string flag, string value)
	{
		Assert.Equal($"tool {flag} [REDACTED:secret-flag]", Redactor.Redact($"tool {flag} {value}").Text);
	}

	[Theory]
	[InlineData("tool --api-key MY_DEPLOY_TOKEN")]
	[InlineData("tool --api-key deploy_token_name")]
	[InlineData("tool --api-key DEPLOYTOKEN")]
	public void A_constant_or_a_snake_case_name_is_still_a_name(string text)
	{
		Assert.Equal(text, Redactor.Redact(text).Text);
	}

	[Theory]
	[InlineData("sk-" + "proj-" + "only-plain-words-in-this-key")]
	[InlineData("sk-" + "svcacct-" + "only-plain-words-in-this-key")]
	[InlineData("sk-" + "admin-" + "only-plain-words-in-this-key")]
	public void A_named_openai_prefix_is_redacted_even_if_the_rest_reads_as_words(string key)
	{
		Assert.Equal("[REDACTED:openai-key]", Redactor.Redact(key).Text);
	}

	[Fact]
	public void A_bare_sk_name_with_plain_words_still_survives()
	{
		const string Name = "sk-learn-intelex-2024-01-extras";

		Assert.Equal(Name, Redactor.Redact(Name).Text);
	}

	[Fact]
	public void A_discord_webhook_is_redacted_whatever_its_token_reads_as()
	{
		string text = "https://discord.com/api/webhooks/123/" + "only-plain-words-in-this-webhook-path";

		Assert.Equal("https://discord.com/api/webhooks/123/[REDACTED:discord-webhook]", Redactor.Redact(text).Text);
	}

	private static Regex Compile(string pattern) => new(pattern, RegexOptions.None, TimeSpan.FromSeconds(5));
}
