using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction.Gitleaks;

/// <summary>
/// Where an imported token ends: at an underscore its alphabet cannot hold, at its format's exact length or its padding, and never
/// cut short by a rule that runs first and knows only part of it.
/// </summary>
public sealed class ImportedBoundaryTests
{
	private static string Redact(string text) => Redactor.Shared.RedactFragment(text).Text;

	private static string RubyGems => "rubygems" + "_" + Synthetic.Hex(48, 6001);

	private static string Readme => "rdme" + "_" + Synthetic.Hex(70, 6002).Replace('f', 'z');

	private static string Bedrock => "AB" + "SK" + Synthetic.Base64(118, 6003) + "==";

	private static string Heroku => "HRKU" + "-AA" + Synthetic.Body(58, 6004);

	[Theory]
	[InlineData("export RUBYGEMS_{R}_backup", "export RUBYGEMS_[REDACTED:gitleaks-rubygems-api-token]_backup")]
	[InlineData("ENV_{R}_BACKUP", "ENV_[REDACTED:gitleaks-rubygems-api-token]_BACKUP")]
	[InlineData("a {R}_ b", "a [REDACTED:gitleaks-rubygems-api-token]_ b")]
	[InlineData("a {R}_", "a [REDACTED:gitleaks-rubygems-api-token]_")]
	[InlineData("a {R}_2 b", "a [REDACTED:gitleaks-rubygems-api-token]_2 b")]
	[InlineData("see {R}_old.txt", "see [REDACTED:gitleaks-rubygems-api-token]_old.txt")]
	public void An_underscore_the_alphabet_cannot_hold_ends_the_token(string template, string expected)
	{
		Assert.Equal(expected, Redact(template.Replace("{R}", RubyGems, StringComparison.Ordinal)));
	}

	/// <summary>
	/// Across every sampled token known by its format, an underscore after it ends it, except for the five whose alphabet holds one, and the
	/// six whose upstream pattern ends only at a quote, a space or a semicolon, so a token glued to an underscore is not theirs at all.
	/// </summary>
	[Fact]
	public void Every_format_token_ends_at_an_underscore_unless_its_alphabet_holds_one()
	{
		HashSet<string> selfEvident = [.. GitleaksRules.SelfEvident.Select(static rule => rule.Name)];
		string[] alphabetHoldsUnderscore =
		[
			"gitleaks-gcp-api-key", "gitleaks-heroku-api-key-v2", "gitleaks-openshift-user-token", "gitleaks-scalingo-api-token", "gitleaks-sendgrid-api-token",
			"gitleaks-anthropic-admin-api-key", "gitleaks-anthropic-api-key", "gitleaks-azure-ad-client-secret", "gitleaks-facebook-access-token", "gitleaks-openai-api-key",
			"gitleaks-perplexity-api-key",
		];
		List<string> missed = [];
		foreach (ImportedCases.Form form in ImportedCases.Forms.Where(form => selfEvident.Contains(form.Rule) && !alphabetHoldsUnderscore.Contains(form.Rule)))
		{
			if (Redact(form.Before + form.Secret + "_backup" + form.After).Contains(form.Secret, StringComparison.Ordinal))
			{
				missed.Add(form.Rule);
			}
		}

		Assert.True(missed.Count == 0, string.Join(", ", missed));
	}

	/// <summary>Heroku's alphabet holds an underscore, so the run may go on past it: that is a longer value, not this token.</summary>
	[Fact]
	public void An_underscore_inside_the_alphabet_still_continues_the_run()
	{
		string text = "key " + Heroku + "_backup";

		Assert.Equal(text, Redact(text));
	}

	/// <summary>A rule known by the name before its value keeps upstream's ending, since that name is all it has to go by.</summary>
	[Fact]
	public void An_assignment_rule_keeps_upstream_s_ending()
	{
		string text = "adafruit = " + Synthetic.Hex(32, 6005) + "_x";

		Assert.Equal(text, Redact(text));
	}

	[Theory]
	[InlineData("{R}-Kq9xZ7mLpW3vB8 next")]
	[InlineData("{R}.Kq9xZ7mLpW3vB8 next")]
	public void A_token_at_its_format_s_exact_length_keeps_the_random_text_after_it(string template)
	{
		string output = Redact(template.Replace("{R}", Readme, StringComparison.Ordinal));

		Assert.Equal(template.Replace("{R}", "[REDACTED:gitleaks-readme-api-token]", StringComparison.Ordinal), output);
	}

	[Theory]
	[InlineData("{B}/Kq9xZ7mLpW3vB8 next")]
	[InlineData("{B}+Kq9xZ7mLpW3vB8 next")]
	[InlineData("{B}%2FOEpr2P8rv0N3COE7sWmePhzLKka9ah next")]
	public void A_padded_token_keeps_the_random_text_after_it(string template)
	{
		string output = Redact(template.Replace("{B}", Bedrock, StringComparison.Ordinal));

		Assert.Equal(template.Replace("{B}", "[REDACTED:gitleaks-aws-amazon-bedrock-api-key-long-lived]", StringComparison.Ordinal), output);
	}

	/// <summary>Without padding, a token of a variable length may run past its rule's limit, so random text after a separator is still its rest.</summary>
	[Fact]
	public void An_unpadded_token_of_variable_length_still_takes_its_rest()
	{
		string unpadded = Bedrock.TrimEnd('=');

		Assert.Equal("[REDACTED:gitleaks-aws-amazon-bedrock-api-key-long-lived] next", Redact(unpadded + "/Kq9xZ7mLpW3vB8 next"));
	}

	[Fact]
	public void Rules_that_know_a_token_by_its_format_run_before_rules_that_know_it_by_a_name()
	{
		bool[] isTemplate = [.. GitleaksRules.All.Select(rule => GitleaksRuleData.BuiltIn.Rules.Single(spec => GitleaksRules.Prefix + spec.Id == rule.Name)).Select(GitleaksRules.IsAssignmentTemplate)];
		int firstTemplate = Array.IndexOf(isTemplate, true);

		Assert.True(firstTemplate > 0);
		Assert.DoesNotContain(false, isTemplate[firstTemplate..]);
		Assert.Equal(GitleaksRuleData.BuiltIn.Rules.Count, GitleaksRules.All.Count);
	}

	/// <summary>A URL-encoded + / or = inside a token's run is more of the token, so a rule that stops at the escape takes the rest too.</summary>
	[Theory]
	[InlineData("%2B")]
	[InlineData("%2b")]
	[InlineData("%2F")]
	[InlineData("%2f")]
	[InlineData("%3D")]
	[InlineData("%3d")]
	public void An_encoded_base64_symbol_glues_the_rest_of_the_token(string escape)
	{
		string tail = Synthetic.Body(60, 6006);
		string text = "twitter: " + new string('A', 22) + "Mlh" + escape + tail + " next";

		Assert.Equal("twitter: [REDACTED:gitleaks-twitter-api-key] next", Redact(text));
	}

	[Theory]
	[InlineData("%20")]
	[InlineData("%2C")]
	[InlineData("%2D")]
	[InlineData("%3E")]
	public void Another_escape_ends_the_token(string escape)
	{
		string text = "twitter: " + new string('A', 22) + "Mlh" + escape + "word";

		Assert.Equal("twitter: [REDACTED:gitleaks-twitter-api-key]" + escape + "word", Redact(text));
	}

	[Fact]
	public void An_encoded_symbol_at_the_end_of_the_text_is_kept()
	{
		Assert.Equal("twitter: [REDACTED:gitleaks-twitter-api-key]%2B", Redact("twitter: " + new string('A', 22) + "Mlh%2B"));
	}

	/// <summary>A token whose alphabet holds + / or = goes whole, padding and all.</summary>
	[Fact]
	public void A_longer_token_goes_whole()
	{
		string onePassword = "ops" + "_eyJ" + Synthetic.Base64(260, 6007) + "==";
		string sendGrid = "SG." + Synthetic.Body(22, 6008) + "." + Synthetic.Body(43, 6009);

		Assert.Equal("t [REDACTED:gitleaks-1password-service-account-token] n", Redact("t " + onePassword + " n"));
		Assert.Equal("t [REDACTED:gitleaks-sendgrid-api-token] n", Redact("t " + sendGrid + " n"));
		Assert.Equal("t [REDACTED:gitleaks-sendgrid-api-token].", Redact("t SG." + Synthetic.Body(22, 6012) + "." + Synthetic.Body(43, 6013) + "."));
	}

	/// <summary>A Stripe key followed by an underscore still goes, and the word after it stays.</summary>
	[Fact]
	public void A_stripe_key_before_an_underscore_goes()
	{
		string key = "sk" + "_test_" + Synthetic.Body(18, 6025) + "4" + Synthetic.Body(4, 6026) + "7";

		Assert.Equal("x [REDACTED:gitleaks-stripe-access-token]_old y", Redact("x " + key + "_old y"));
	}

	/// <summary>
	/// A token known by its format at its exact length keeps an encoded separator and what follows it, as it keeps a plain one. A value
	/// known by the name before it takes the rest of its run, since the whole run is the value.
	/// </summary>
	[Fact]
	public void A_token_at_its_exact_length_keeps_an_encoded_separator()
	{
		RedactionRule readme = GitleaksRules.All.Single(static rule => rule.Name == "gitleaks-readme-api-token");
		string text = "k " + Readme + "%2Fdocs%2Fpage";

		Assert.True(readme.TryFindNext(text, 0, out int start, out int end));
		Assert.Equal(Readme, text[start..end]);
	}

	[Theory]
	[InlineData("ansible/ops_playbooks/roles/common/tasks/main.yml")]
	[InlineData("cd /srv/ops_automation/terraform/modules/network")]
	public void A_path_through_an_ops_folder_stays(string text)
	{
		Assert.Equal(text, Redact(text));
	}

	[Theory]
	[InlineData(".txt")]
	[InlineData(".Extensions;")]
	public void A_dotted_part_after_a_sendgrid_key_stays(string after)
	{
		string key = "SG." + Synthetic.Body(22, 6015) + "." + Synthetic.Body(43, 6016);

		Assert.Equal("[REDACTED:gitleaks-sendgrid-api-token]" + after, Redact(key + after));
	}

	/// <summary>A value known by the name before it is all of its run, so random text after a separator is more of it, as on main.</summary>
	[Fact]
	public void A_named_value_at_its_count_still_takes_its_random_rest()
	{
		string text = "adafruit = " + Synthetic.Hex(32, 6020) + "-" + Synthetic.Body(20, 6021) + " next";

		Assert.Equal("adafruit = [REDACTED:gitleaks-adafruit-api-key] next", Redact(text));
	}

	/// <summary>A path after an encoded slash is not more of a token: only a run with a random piece in it is glued on.</summary>
	[Fact]
	public void An_encoded_path_after_a_value_stays()
	{
		string value = Synthetic.Body(25, 6022).ToLowerInvariant();

		Assert.Equal("twitter: [REDACTED:gitleaks-twitter-api-key]%2Fhome%2Fuser%2Fdocuments%2Freport next", Redact("twitter: " + value + "%2Fhome%2Fuser%2Fdocuments%2Freport next"));
	}

	/// <summary>An = that the alphabet holds is not padding, so a token cut after one still takes its random rest.</summary>
	[Fact]
	public void An_equals_sign_inside_the_alphabet_is_not_padding()
	{
		string text = "x pscale" + "_pw_" + Synthetic.Body(50, 6023) + "=-" + Synthetic.Body(40, 6024) + " y";

		Assert.Equal("x [REDACTED:gitleaks-planetscale-password] y", Redact(text));
	}

	[Theory]
	[InlineData("(?<secret>rdme_[a-z0-9]{70})", true)]
	[InlineData("glpat-[0-9A-Za-z_\\-]{20}", true)]
	[InlineData("(?<secret>ABSK[A-Za-z0-9+/]{109,269}={0,2})", false)]
	[InlineData("pscale_pw_[0-9A-Za-z_=\\.-]{32,64}", false)]
	[InlineData("xoxb-[0-9]{10,13}-[a-zA-Z0-9-]*", false)]
	[InlineData("a\\{3}", false)]
	[InlineData("a{}", false)]
	[InlineData("{3}", false)]
	[InlineData("abc", false)]
	[InlineData("(?:hvs\\.[a-z]{90,120}|s\\.[a-z]{24})", false)]
	public void A_fixed_length_ending_is_known(string secret, bool expected)
	{
		Assert.Equal(expected, GitleaksRules.EndsAtFixedLength(secret));
	}

	[Theory]
	[InlineData("(?<secret>x)(?:[^0-9A-Za-z_]|\\z)", "(?<secret>x)(?:[^0-9A-Za-z]|\\z)")]
	[InlineData("(?<secret>x)(?:[^0-9A-Za-z_]|\\z)y", "(?<secret>x)(?:[^0-9A-Za-z_]|\\z)y")]
	[InlineData("glpat-[0-9A-Za-z_\\-]{20}", "glpat-[0-9A-Za-z_\\-]{20}")]
	public void The_word_ending_is_widened_to_an_underscore(string pattern, string expected)
	{
		Assert.Equal(expected, GitleaksRules.EndingAtUnderscore(pattern));
	}
}
