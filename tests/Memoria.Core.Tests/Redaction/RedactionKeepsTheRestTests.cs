using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// Redaction removes the secret and nothing else. Stored text is the only copy once a local index is retired, so a word lost after
/// a token is lost for good. For every rule and form, in every context, the output must be the input with the secret replaced.
/// </summary>
public sealed class RedactionKeepsTheRestTests
{
	/// <summary>What follows a secret. Each is ordinary text, and starts in a way a token's tail could also start.</summary>
	private static readonly (string Name, string Text)[] s_after =
	[
		("blank line, sentence", "\n\nThe next sentence is here."),
		("next line, sentence", "\nThe next sentence is here."),
		("heading", "\n\n# A Heading\n\nSome text."),
		("list", "\n- First item\n- Second item"),
		("numbered list", "\n1. First item\n2. Second item"),
		("code", "\n\n```sh\nRunTheThing --flag value2\n```"),
		("end of file", ""),
		("mid-line", " and Some more text."),
		("crlf", "\r\n\r\nThe next sentence is here."),
		("camel case word", "\n\nJavaScript runs here."),
		("capitals", "\n\nNOTE this."),
		("a year", "\n\n2024 was the year."),
		("a file name", "\nREADME.md says so."),
		("a version", "\nv1.2.3 shipped."),
		("a date", "\n2026-01-15 was the day."),
		("a path", "\nsrc/App2.tsx changed."),
		("a short word", "\n\nIs this it."),
	];

	private static readonly (string Name, string Text)[] s_before =
	[
		("no text", ""),
		("line above", "The previous line\n"),
		("blank line above", "The previous line.\n\n"),
		("long word above", "a_long_identifier_name_on_its_own\n"),
	];

	private const int Seed = 20261105;

	/// <summary>One form names an access key id just before its secret. That id is a secret of its own, and goes too.</summary>
	private static readonly string s_awsIdInTemplate = "AK" + "IAABCDEFGHIJKLMNOP";

	private const int LongestTailThatStays = 12;

	private const string Begin = "-----BEGIN " + Synthetic.PrivateWord + " KEY-----";

	private const string End = "-----END " + Synthetic.PrivateWord + " KEY-----";

	/// <summary>Rules the shapes table leaves out, as they judge no shape: key blocks, and what is only found encoded.</summary>
	private static readonly (string Rule, string Template, string Secret)[] s_handForms =
	[
		("private-key", "{S}", Begin + "\n" + Synthetic.PemBody(3, 4001) + "\n" + End),
		("private-key-end", "{S}", Synthetic.PemBody(3, 4002) + "\n" + End),
		("private-key-lines", "{S}", Synthetic.PemBody(3, 4003)),
		("private-key-der", "{S}", "b3Blbn" + "NzaC1rZXktdjE" + Synthetic.Body(80, 4004)),
		("base64-private-key", "{S}", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(Begin + "\n" + Synthetic.Body(60, 4005)))),
		("putty-private-key", "Private-Lines: 1\n{S}", Synthetic.Base64(64, 4006) + "\nPrivate-MAC: " + Synthetic.Hex(40, 4007)),
		("digest-response", "Authorization: Digest username=\"u\", response=\"{S}\"", Synthetic.Hex(32, 4008)),
		("base64-token", "{S}", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(Synthetic.GitHubClassic('p')))),
		("percent-encoded-token", "{S}", string.Concat(Synthetic.GitHubClassic('p').Select(static c => $"%{(int)c:X2}"))),
		("hex-token", "{S}", Convert.ToHexStringLower(System.Text.Encoding.ASCII.GetBytes(Synthetic.GitHubClassic('p')))),
		("unicode-escaped-token", "{S}", string.Concat(Synthetic.GitHubClassic('p').Select(static c => $@"\u{(int)c:X4}"))),
	];

	/// <summary>Short values after a bare name, which more words on the line turn into prose.</summary>
	private static readonly HashSet<(string Before, string Secret)> s_proseForms = [("PASSWORD=", "ab,cd;ef&gh"), ("  - password: ", "admin"), ("password: ", "admin")];

	/// <summary>Sentences built from a fixed seed, so a failure repeats. They mix the shapes above.</summary>
	private static IEnumerable<string> Generated()
	{
		string[] openers = ["The", "This", "OpenAI", "GitHub", "PostgreSQL", "Next", "Windows11", "x86", "42", "A", "iOS", "Docs/Setup", "user-guide", "foo.bar", "HTTP2", "Then:"];
		string[] breaks = ["\n", "\n\n", "\r\n", "\n\n\n", "\n  ", "\n\t"];
		Random random = new(Seed);
		for (int i = 0; i < 24; i++)
		{
			yield return breaks[random.Next(breaks.Length)] + openers[random.Next(openers.Length)] + " the rest of it.";
		}
	}

	public static TheoryData<string, string> Forms()
	{
		TheoryData<string, string> forms = [];
		foreach ((string rule, string secret) in RedactionCases.Tokens.Concat(ReviewCases.ProviderTokens).DistinctBy(static token => token.Secret))
		{
			forms.Add(rule, secret);
		}

		return forms;
	}

	public static TheoryData<string, string, string> ContextualForms()
	{
		TheoryData<string, string, string> forms = [];
		foreach ((string rule, string template, string secret) in RedactionCases.Contextual.Concat(ReviewCases.Positives).Concat(ShapeForms()).Concat(s_handForms))
		{
			forms.Add(rule, template, secret);
		}

		return forms;
	}

	/// <summary>Each format in the shapes table, filled from a fixed seed.</summary>
	private static IEnumerable<(string Rule, string Template, string Secret)> ShapeForms()
	{
		foreach (TokenSpec spec in TokenShapes.All)
		{
			Random random = new(TokenShapes.CellSeed(Seed, spec, TokenShape.Random));
			string secret = TokenShapes.Generate(spec, TokenShape.Random, random);

			// A cookie's name goes with its value in these two.
			yield return spec.Context.Contains("session={S}", StringComparison.Ordinal)
				? (spec.Rule, spec.Context.Replace("session={S}", "{S}", StringComparison.Ordinal), "session=" + secret)
				: (spec.Rule, spec.Context, secret);
		}
	}

	private static void CheckAll(string rule, string before, string secret, string afterSecret)
	{
		// A token needs no context, so text just before it on its line is one more thing to hold. A contextual rule may read the line's start.
		(string Name, string Text)[] above = before.Length == 0 ? [.. s_before, ("words", "see ")] : s_before;
		string placeholder = $"[REDACTED:{rule}]";
		List<string> wrong = [];
		foreach ((string name, string text) in s_after.Concat(Generated().Select(static text => ("generated", text))))
		{
			foreach ((string beforeName, string prior) in above)
			{
				string input = prior + before + secret + afterSecret + text;
				string kept = before.Replace(s_awsIdInTemplate, "[REDACTED:aws-access-key]", StringComparison.Ordinal);
				string expected = prior + (name == "mid-line" ? ExpectedMidLine(rule, kept, secret, placeholder, afterSecret, text) : kept + placeholder + afterSecret + text);
				string output = Redactor.Shared.RedactFragment(input).Text;
				if (output != expected)
				{
					wrong.Add($"{beforeName} / {name}: {output.Replace(secret, "<secret>", StringComparison.Ordinal)}");
				}
			}
		}

		Assert.True(wrong.Count == 0, $"{rule} over-consumed in {wrong.Count} context(s):\n" + string.Join('\n', wrong));
	}

	/// <summary>
	/// Two rules read the rest of a line, and text after a secret on its line is a different question for them. A cookie header's
	/// whole value goes, as any cookie in it may be the live one, and it stops at the line's end. A short value after a bare name
	/// followed by more words reads as prose and is left, which is a value kept and no text lost. A run of key lines needs whole
	/// lines, so words after its last line leave the run unmatched.
	/// </summary>
	private static string ExpectedMidLine(string rule, string before, string secret, string placeholder, string afterSecret, string text)
	{
		return rule switch
		{
			"cookie-header" when afterSecret.Length == 0 => before + placeholder,
			"secret-assignment" when s_proseForms.Contains((before, secret)) => before + secret + afterSecret + text,
			"private-key-lines" => before + secret + afterSecret + text,
			_ => before + placeholder + afterSecret + text,
		};
	}

	[Theory]
	[InlineData("\n\nThe next sentence is here.")]
	[InlineData("\nThe next sentence is here.")]
	[InlineData("\r\n\r\nThe next sentence is here.")]
	public void The_reported_loss_is_gone(string after)
	{
		Assert.Equal("see [REDACTED:github-token]" + after, Redactor.Shared.RedactFragment("see " + Synthetic.GitHubClassic('p') + after).Text);
	}

	[Fact]
	public void A_value_is_not_joined_to_the_header_below_it()
	{
		string output = Redactor.Shared.RedactFragment("PASSWORD=" + Synthetic.Body(24, 4010) + "\nAuthorization: Bearer " + Synthetic.Body(32, 4011) + "\nend").Text;

		Assert.Equal("PASSWORD=[REDACTED:secret-assignment]\nAuthorization: Bearer [REDACTED:authorization-header]\nend", output);
	}

	[Theory]
	[InlineData("-b", "sid={S} https://h.example.test and more", "sid={S}")]
	[InlineData("--cookie", "sid={S}", "sid={S}")]
	public void A_cookie_argument_with_no_quotes_ends_at_its_space(string flag, string rest, string cookie)
	{
		string value = Synthetic.Body(32, 4012);
		string text = "cu" + "rl " + flag + " " + rest.Replace("{S}", value, StringComparison.Ordinal);

		Assert.Equal("cu" + "rl " + flag + " [REDACTED:cu" + "rl-cookie]" + rest[cookie.Length..], Redactor.Shared.RedactFragment(text).Text);
	}

	/// <summary>A JWT with no dot yet is half a token, so its tail still joins even if it reads as a word. One with its payload is whole.</summary>
	[Fact]
	public void Half_a_token_still_joins_and_a_whole_one_does_not()
	{
		string head = "ey" + "J" + Synthetic.Body(20, 4013);

		Assert.True(LineWraps.JoinLength(head + "\nHello", head.Length) > 0);
		Assert.Equal(0, LineWraps.JoinLength(head + "." + "ey" + "J" + Synthetic.Body(20, 4014) + "\nThe", head.Length + 24));
	}

	[Theory]
	[InlineData(8, "abcdefgh", true)]
	[InlineData(9, "abcdefghij", false)]
	public void A_run_just_short_of_a_token_takes_what_completes_it(int missing, string tail, bool joins)
	{
		string head = "h" + "f_" + Synthetic.Digits(30 - missing, 4015);

		Assert.Equal(joins, LineWraps.JoinLength(head + "\n" + tail, head.Length) > 0);
	}

	[Theory]
	[InlineData("gl" + "dt-")]
	[InlineData("gl" + "rt-")]
	[InlineData("gl" + "ptt-")]
	[InlineData("gl" + "soat-")]
	[InlineData("gl" + "cbt-")]
	[InlineData("gl" + "oas-")]
	[InlineData("gl" + "agent-")]
	[InlineData("gl" + "imt-")]
	public void Every_form_of_a_GitLab_token_is_known_when_it_wraps(string prefix)
	{
		string body = Synthetic.Body(40, 4016);

		Assert.Equal("[REDACTED:gitlab-token]", Redactor.Shared.RedactFragment(prefix + body[..4] + "\n" + body[4..]).Text);
	}

	[Fact]
	public void Every_rule_has_a_form_here()
	{
		IEnumerable<string> covered = RedactionCases.Tokens.Concat(ReviewCases.ProviderTokens).Select(static token => token.Rule)
			.Concat(RedactionCases.Contextual.Concat(ReviewCases.Positives).Concat(ShapeForms()).Concat(s_handForms).Select(static form => form.Rule));
		string missing = string.Join(", ", Redactor.RuleNames.Except(covered));

		Assert.True(missing.Length == 0, missing);
	}

	[Theory]
	[MemberData(nameof(Forms))]
	public void A_token_takes_only_itself(string rule, string secret)
	{
		CheckAll(rule, "", secret, "");
	}

	/// <summary>
	/// A token really wrapped must still go, at every cut. What this gives up is a short tail that reads as ordinary text, so the
	/// bound on what can stay is pinned here: the longest such tail, and never the start of a token.
	/// </summary>
	[Fact]
	public void A_wrapped_token_goes_at_every_cut_apart_from_a_short_tail_that_reads_as_a_word()
	{
		List<string> wrong = [];
		int longest = 0;
		foreach ((string rule, string secret) in RedactionCases.Tokens.Concat(ReviewCases.ProviderTokens).Where(static token => LineWraps.TokenPrefixes.Any(token.Secret.StartsWith)).DistinctBy(static token => token.Secret))
		{
			int after = LineWraps.TokenPrefixes.Where(secret.StartsWith).Max(static prefix => prefix.Length);
			foreach (string lineBreak in new[] { "\n", "\n\n" })
			{
				for (int cut = after + 1; cut < secret.Length; cut++)
				{
					string head = secret[..cut];
					string tail = secret[cut..];
					string output = Redactor.Shared.RedactFragment(head + lineBreak + tail).Text;
					if (output.Contains(head, StringComparison.Ordinal))
					{
						wrong.Add($"{rule} at {cut} of {secret.Length}: its start stayed, before {tail[..Math.Min(tail.Length, 14)]}");
					}
					else if (output.Contains(tail, StringComparison.Ordinal) && tail.Length >= 3)
					{
						longest = Math.Max(longest, tail.Length);
					}
				}
			}
		}

		Assert.True(wrong.Count == 0, string.Join('\n', wrong));
		Assert.InRange(longest, 0, LongestTailThatStays);
	}

	[Theory]
	[MemberData(nameof(ContextualForms))]
	public void A_secret_in_its_context_takes_only_itself(string rule, string template, string secret)
	{
		string[] sides = template.Split("{S}");
		CheckAll(rule, sides[0], secret, sides[1]);
	}
}
