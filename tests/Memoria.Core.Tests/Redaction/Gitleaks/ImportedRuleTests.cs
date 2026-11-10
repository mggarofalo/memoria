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

		Assert.Matches("^" + Regex.Escape(form.Before + kept) + @"\[REDACTED:[a-z0-9-]+\]" + Regex.Escape(form.After) + "$", output);
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

	[Fact]
	public void Imported_rules_run_after_every_rule_of_ours()
	{
		int firstImported = RedactionRules.All.ToList().FindIndex(static rule => rule.Backstop);
		int lastOurs = RedactionRules.All.ToList().FindLastIndex(static rule => !rule.Backstop && rule.Name is not ("percent-encoded-token" or "base64-token"));

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
