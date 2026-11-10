using System.Text.RegularExpressions;
using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction.Gitleaks;

/// <summary>Every imported rule, through the whole engine: its secret goes, nothing else does, and its mask survives.</summary>
public sealed partial class ImportedRuleTests
{
	[Fact]
	public void Every_imported_rule_has_a_sampled_case()
	{
		Assert.True(ImportedCases.Unsampled.Count == 0, "No case for: " + string.Join(", ", ImportedCases.Unsampled));
		Assert.Equal(GitleaksRules.All.Count, ImportedCases.Forms.Count);
	}

	[Theory]
	[MemberData(nameof(ImportedCases.All), MemberType = typeof(ImportedCases))]
	public void Its_secret_becomes_one_placeholder_and_the_rest_stays(ImportedCases.Form form)
	{
		string output = Redactor.Shared.RedactFragment(form.Text).Text;
		string kept = s_hostKeptByOurRule.TryGetValue(form.Rule, out string? marker) ? form.Secret[..(form.Secret.IndexOf(marker, StringComparison.Ordinal) + marker.Length)] : "";

		// What a rule of ours takes after the token, as grafana-token takes a / its alphabet allows, is that rule's own, tested with it.
		string after = output.Contains("[REDACTED:" + GitleaksRules.Prefix, StringComparison.Ordinal) ? Regex.Escape(form.After) + "$" : "";
		Assert.Matches("^" + Regex.Escape(form.Before + kept) + @"\[REDACTED:[a-z0-9-]+\]" + after, output);
		Assert.DoesNotContain(form.Secret, output, StringComparison.Ordinal);
	}

	/// <summary>
	/// Where a rule of ours takes the same secret first and keeps its start on purpose, this is the end of what it keeps: a webhook's
	/// host, a URL's user name, and the <c>xoxe.</c> before a Slack token, none of which is the credential.
	/// </summary>
	private static readonly Dictionary<string, string> s_hostKeptByOurRule = new(StringComparer.Ordinal)
	{
		["gitleaks-microsoft-teams-webhook"] = "/webhookb2/",
		["gitleaks-sidekiq-sensitive-url"] = ":",
		["gitleaks-slack-config-access-token"] = "xoxe.",
	};

	/// <summary>A secret group drawn short of the token would leave the token's rest touching the placeholder.</summary>
	[Theory]
	[MemberData(nameof(ImportedCases.All), MemberType = typeof(ImportedCases))]
	public void Nothing_token_like_is_left_touching_the_secret(ImportedCases.Form form)
	{
		Assert.False(TokenRunAtEnd().Match(form.Before).Length >= 8, $"{form.Rule} leaves the token's start before its secret");
		Assert.False(TokenRunAtStart().Match(form.After).Length >= 8, $"{form.Rule} leaves the token's end after its secret");
	}

	[Theory]
	[MemberData(nameof(ImportedCases.All), MemberType = typeof(ImportedCases))]
	public void A_mask_in_the_secret_s_place_survives(ImportedCases.Form form)
	{
		Assert.Equal(form.Negative, Redactor.Shared.RedactFragment(form.Negative).Text);
	}

	/// <summary>Where a transcript puts a token. Each replaces the sampled boundary itself, so the token touches the escape or the punctuation.</summary>
	private static readonly string[] s_tightContexts =
	[
		"{\"out\":\"line1\\n{S}\\nline3\"}",
		"{\"out\":\"token {S}\\tnext\"}",
		"{\"cmd\":\"{\\\"t\\\":\\\"{S}\\\"}\"}",
		"my key is {S}.",
		"keys: {S}, and more",
		"[key]({S})",
		"line1\r\n{S}\r\nline3",
	];

	/// <summary>A rule that knows a token by its own format, so the token can stand anywhere, with its sampled context only a boundary.</summary>
	public static TheoryData<ImportedCases.Form> StandaloneForms()
	{
		TheoryData<ImportedCases.Form> data = [];
		foreach (ImportedCases.Form form in Standalone())
		{
			data.Add(form);
		}

		return data;
	}

	private static IEnumerable<ImportedCases.Form> Standalone()
	{
		return ImportedCases.Forms.Where(static form => !form.Before.Any(char.IsAsciiLetterOrDigit) && !form.After.Any(char.IsAsciiLetterOrDigit));
	}

	[Theory]
	[MemberData(nameof(StandaloneForms))]
	public void A_token_touching_an_escape_or_punctuation_goes_and_nothing_else_does(ImportedCases.Form form)
	{
		List<string> wrong = [];
		foreach (string context in s_tightContexts)
		{
			string[] sides = context.Split("{S}");
			string output = Redactor.Shared.RedactFragment(sides[0] + form.Secret + sides[1]).Text;

			// A rule of ours may take the token first. What it keeps or takes around the token is that rule's own, tested with it.
			bool imported = output.Contains("[REDACTED:" + GitleaksRules.Prefix, StringComparison.Ordinal);
			bool exact = Regex.IsMatch(output, "^" + Regex.Escape(sides[0]) + @"\[REDACTED:[a-z0-9-]+\]" + Regex.Escape(sides[1]) + "$");
			if (output.Contains(form.Secret, StringComparison.Ordinal) || (imported && !exact))
			{
				wrong.Add(context + " => " + output.Replace(form.Secret, "<secret>", StringComparison.Ordinal));
			}
		}

		Assert.True(wrong.Count == 0, string.Join('\n', wrong));
	}

	/// <summary>A long token wrapped at a terminal's width goes whole, apart from a short tail that reads as a word, as for Memoria's own rules.</summary>
	public static TheoryData<ImportedCases.Form> LongStandaloneForms()
	{
		TheoryData<ImportedCases.Form> data = [];
		foreach (ImportedCases.Form form in Standalone().Where(static form => form.Secret.Length > 80))
		{
			data.Add(form);
		}

		return data;
	}

	[Theory]
	[MemberData(nameof(LongStandaloneForms))]
	public void A_long_token_wrapped_at_eighty_columns_goes(ImportedCases.Form form)
	{
		string wrapped = string.Join('\n', form.Secret.Chunk(80).Select(static chunk => new string(chunk)));
		string output = Redactor.Shared.RedactFragment("see\n" + wrapped + "\nend").Text;
		string credential = s_hostKeptByOurRule.TryGetValue(form.Rule, out string? marker)
			? form.Secret[(form.Secret.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]
			: form.Secret;

		Assert.False(LongestCommonRun(output, credential) > 12, output);
		Assert.EndsWith("\nend", output, StringComparison.Ordinal);
	}

	/// <summary>Without its keyword an assignment rule has no context, so the same value under another name is left to Memoria's own judges.</summary>
	[Theory]
	[MemberData(nameof(ImportedCases.All), MemberType = typeof(ImportedCases))]
	public void Without_its_keyword_the_rule_does_not_fire(ImportedCases.Form form)
	{
		RedactionRule rule = GitleaksRules.All.Single(rule => rule.Name == form.Rule);
		string text = form.Text;
		foreach (string keyword in rule.Keywords)
		{
			text = Regex.Replace(text, Regex.Escape(keyword), new string('q', keyword.Length), RegexOptions.IgnoreCase);
		}

		Assert.False(rule.TryFindNext(text, 0, out _, out _));
	}

	private static int LongestCommonRun(string text, string secret)
	{
		int longest = 0;
		for (int length = 8; length <= secret.Length; length++)
		{
			bool any = false;
			for (int i = 0; i + length <= secret.Length && !any; i++)
			{
				any = text.Contains(secret.Substring(i, length), StringComparison.Ordinal);
			}

			if (!any)
			{
				break;
			}

			longest = length;
		}

		return longest;
	}

	[Fact]
	public void Imported_rules_run_after_every_rule_of_ours()
	{
		int firstImported = RedactionRules.All.ToList().FindIndex(static rule => rule.Backstop);
		int lastOurs = RedactionRules.All.ToList().FindLastIndex(static rule => !rule.Backstop && rule.Name is not ("hex-token" or "unicode-escaped-token" or "percent-encoded-token" or "base64-token"));

		Assert.True(firstImported > lastOurs);
		Assert.All(RedactionRules.All.Where(static rule => rule.Backstop), static rule => Assert.StartsWith(GitleaksRules.Prefix, rule.Name, StringComparison.Ordinal));
		Assert.Equal(GitleaksRules.All.Count, RedactionRules.All.Count(static rule => rule.Backstop));
	}

	/// <summary>An imported boundary takes the separator, and the engine resumes at the secret's end, so the next token is still found.</summary>
	[Theory]
	[InlineData(" ")]
	[InlineData(",")]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public void Tokens_one_separator_apart_are_each_replaced(string separator)
	{
		string first = "AK" + "Cp" + Synthetic.Body(69, 4104);
		string second = "AK" + "Cp" + Synthetic.Body(69, 4105);

		Assert.Equal(
			"[REDACTED:gitleaks-artifactory-api-key]" + separator + "[REDACTED:gitleaks-artifactory-api-key]",
			Redactor.Shared.RedactFragment(first + separator + second).Text);
	}

	[Fact]
	public void A_token_after_a_non_ascii_letter_is_replaced()
	{
		string token = "AK" + "Cp" + Synthetic.Body(69, 4106);

		Assert.Equal("é[REDACTED:gitleaks-artifactory-api-key]é", Redactor.Shared.RedactFragment("é" + token + "é").Text);
	}

	[Theory]
	[InlineData("-X")]
	[InlineData("abc")]
	public void Text_glued_to_an_imported_match_goes_with_it(string glued)
	{
		string head = "FLWSECK" + "_TEST-" + Synthetic.Hex(12, 4107).Replace('9', 'a');

		Assert.Equal("[REDACTED:gitleaks-flutterwave-encryption-key] next", Redactor.Shared.RedactFragment(head + glued + " next").Text);
	}

	/// <summary>An assignment rule knows a value by its name, so it does not look inside decoded text, where encoded prose would read as one.</summary>
	[Fact]
	public void Encoded_prose_naming_a_product_stays()
	{
		const string Text = "q=asana%20task%3A%201203456789012345%20is%20blocked%20by%20review";

		Assert.Equal(Text, Redactor.Shared.RedactFragment(Text).Text);
		Assert.DoesNotContain(RedactionRules.SelfEvident, static rule => rule.Name == "gitleaks-asana-client-id");
		Assert.Contains(RedactionRules.SelfEvident, static rule => rule.Name == "gitleaks-artifactory-api-key");
	}

	[Fact]
	public void Words_after_a_token_s_short_suffix_stay()
	{
		string head = "FLWSECK" + "_TEST-" + Synthetic.Hex(12, 4109).Replace('9', 'a');

		Assert.Equal("[REDACTED:gitleaks-flutterwave-encryption-key]-backup-copy and more", Redactor.Shared.RedactFragment(head + "-backup-copy and more").Text);
	}

	/// <summary>A token longer than its rule allows stops the match at an inner separator. What follows still reads as random, so it goes too.</summary>
	[Fact]
	public void The_rest_of_an_overlong_token_goes_with_it()
	{
		string body = Synthetic.Body(60, 4112) + "-" + Synthetic.Body(20, 4113);
		string output = Redactor.Shared.RedactFragment("x pscale" + "_pw_" + body + " y").Text;

		Assert.Equal("x [REDACTED:gitleaks-planetscale-password] y", output);
	}

	/// <summary>A complete token keeps what follows a separator its alphabet lacks: <c>rdme_</c> tokens hold no <c>/</c>, <c>-</c> or <c>.</c>.</summary>
	[Theory]
	[InlineData("token {R}/{H} done", "token [REDACTED:gitleaks-readme-api-token]/{H} done")]
	[InlineData("token {R}-{H} done", "token [REDACTED:gitleaks-readme-api-token]-{H} done")]
	[InlineData("see {R}.{H} done", "see [REDACTED:gitleaks-readme-api-token].{H} done")]
	[InlineData("GET /v1/{R}/items/a8F3kQ9zX2mP7wLc done", "GET /v1/[REDACTED:gitleaks-readme-api-token]/items/a8F3kQ9zX2mP7wLc done")]
	public void A_complete_token_keeps_the_text_after_a_separator(string template, string expected)
	{
		string token = "rdme" + "_" + Synthetic.Hex(70, 4114);
		string hex = Synthetic.Hex(40, 4115);

		string Fill(string text) => text.Replace("{R}", token, StringComparison.Ordinal).Replace("{H}", hex, StringComparison.Ordinal);

		Assert.Equal(Fill(expected), Redactor.Shared.RedactFragment(Fill(template)).Text);
	}

	[Fact]
	public void A_period_ending_the_sentence_stays()
	{
		string token = "pscale" + "_oauth_" + Synthetic.Body(43, 4110);

		Assert.Equal("my key is [REDACTED:gitleaks-planetscale-oauth-token].", Redactor.Shared.RedactFragment("my key is " + token + ".").Text);
	}

	/// <summary>An unclosed placeholder opening before many candidates once made each check read to the far end of the text.</summary>
	[Fact]
	public void Many_candidates_after_an_unclosed_placeholder_cost_linear_time()
	{
		string token = "AK" + "Cp" + Synthetic.Body(69, 4111);
		string text = string.Concat(Enumerable.Repeat("see [REDACTED:ab " + token + " ok then. ", 20_000)) + "]";
		TimeSpan cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;

		RedactionResult result = Redactor.Shared.RedactFragment(text);

		Assert.Equal(20_000, result.CountsByRule["gitleaks-artifactory-api-key"]);
		Assert.InRange(System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpu, TimeSpan.Zero, TimeSpan.FromSeconds(60));
	}

	[Fact]
	public void A_rule_of_ours_labels_a_token_both_could_take()
	{
		Assert.Equal("[REDACTED:github-token]", Redactor.Shared.RedactFragment(Synthetic.GitHubClassic('p')).Text);
	}

	/// <summary>A rule that would take <c>user:pass</c> whole must not take one whose user is a placeholder another rule wrote.</summary>
	[Theory]
	[InlineData("login [REDACTED:url-password]:hunter2xyz", false)]
	[InlineData("login [REDACTED:x]", false)]
	[InlineData("login admin:hunter2xyz", true)]
	public void An_imported_rule_does_not_split_or_swallow_a_placeholder(string text, bool found)
	{
		GitleaksRuleSpec spec = new("pair", "a test rule", @"login (?<secret>[^ ]+)", "", ["login"], 0, []);
		RedactionRule rule = GitleaksRules.Build(GitleaksRuleData.BuiltIn with { Rules = [spec] }).Single();

		Assert.Equal(found, rule.TryFindNext(text, 0, out _, out _));
	}

	/// <summary>
	/// A keyword is context and may sit before every match, as "sgp" sat in a function name above a column of commit hashes. Checked
	/// only ahead of the cursor, it vanished after the first hit, so each pass took one more and a long column never settled.
	/// </summary>
	[Fact]
	public void A_keyword_behind_the_cursor_still_counts_for_every_later_match()
	{
		GitleaksRuleSpec spec = new("column", "a test rule", @"(?<secret>tok[0-9]{6})", "", ["kw"], 0, []);
		Redactor redactor = new(GitleaksRules.Build(GitleaksRuleData.BuiltIn with { Rules = [spec] }), TimeSpan.FromMinutes(1));
		string text = "kw\n" + string.Join('\n', Enumerable.Range(100000, 12).Select(static n => "tok" + n));

		RedactionResult result = redactor.RedactFragment(text);

		Assert.Equal(12, result.CountsByRule["gitleaks-column"]);
		Assert.DoesNotContain("tok", result.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void A_keyword_is_needed_in_any_case()
	{
		RedactionRule rule = GitleaksRules.All.Single(static rule => rule.Name == "gitleaks-adafruit-api-key");
		string value = Synthetic.Body(32, 4101).ToLowerInvariant();

		Assert.True(rule.TryFindNext("ADAFRUIT_KEY=" + value, 0, out _, out _));
		Assert.False(rule.TryFindNext("SOME_KEY=" + value, 0, out _, out _));
	}

	[Theory]
	[InlineData("aaaaaaaa", 0.0)]
	[InlineData("abcd", 2.0)]
	[InlineData("aabb", 1.0)]
	public void Entropy_is_bits_per_character(string value, double bits)
	{
		Assert.Equal(bits, GitleaksRules.ShannonEntropy(value), 6);
	}

	[GeneratedRegex("[A-Za-z0-9+/_-]+$")]
	private static partial Regex TokenRunAtEnd();

	[GeneratedRegex("^[A-Za-z0-9+/_-]+")]
	private static partial Regex TokenRunAtStart();
}
