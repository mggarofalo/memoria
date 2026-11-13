using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>Per-rule positive and negative cases, asserting exact output and exact counts.</summary>
public sealed class RedactorRuleTests
{
	private const string Slot = "{S}";

	private static readonly Redactor Redactor = new();

	public static TheoryData<string, string, string> TokenInContext()
	{
		TheoryData<string, string, string> data = [];
		foreach ((string rule, string secret) in RedactionCases.Tokens)
		{
			foreach (string context in RedactionCases.Contexts)
			{
				data.Add(rule, context, secret);
			}
		}

		return data;
	}

	public static TheoryData<string, string, string> ContextualCases()
	{
		TheoryData<string, string, string> data = [];
		foreach ((string rule, string template, string secret) in RedactionCases.Contextual)
		{
			data.Add(rule, template, secret);
		}

		return data;
	}

	public static TheoryData<string, string> NegativeCases()
	{
		TheoryData<string, string> data = [];
		foreach ((string rule, string text) in RedactionCases.Negatives)
		{
			data.Add(rule, text);
		}

		return data;
	}

	public static TheoryData<string> SurvivorCases() => [.. RedactionCases.Survivors];

	[Theory]
	[MemberData(nameof(TokenInContext))]
	public void Token_in_any_context_becomes_exactly_its_placeholder(string rule, string context, string secret)
	{
		AssertRedacted(rule, context, secret);
	}

	[Theory]
	[MemberData(nameof(ContextualCases))]
	public void Contextual_secret_becomes_exactly_its_placeholder(string rule, string template, string secret)
	{
		AssertRedacted(rule, template, secret);
	}

	[Theory]
	[MemberData(nameof(NegativeCases))]
	public void Negative_survives_unchanged(string rule, string text)
	{
		Assert.False(string.IsNullOrEmpty(rule));
		RedactionResult result = Redactor.Redact(text);

		Assert.Equal(text, result.Text);
		Assert.Empty(result.CountsByRule);
		Assert.False(result.WasRedacted);
	}

	[Theory]
	[MemberData(nameof(SurvivorCases))]
	public void General_content_survives_unchanged(string text)
	{
		RedactionResult result = Redactor.Redact(text);

		Assert.Equal(text, result.Text);
		Assert.Empty(result.CountsByRule);
	}

	[Fact]
	public void Every_rule_has_positive_and_negative_cases()
	{
		HashSet<string> positive =
		[
			.. RedactionCases.Tokens.Select(t => t.Rule),
			.. RedactionCases.Contextual.Select(c => c.Rule),
			"private-key",
			.. Gitleaks.ImportedCases.Forms.Select(static form => form.Rule),
		];
		HashSet<string> negative = [.. RedactionCases.Negatives.Select(n => n.Rule), .. Gitleaks.ImportedCases.Forms.Select(static form => form.Rule)];

		// A rule with no format-specific negative still needs one: this names any gap.
		Assert.Empty(Redactor.RuleNames.Except(positive));
		Assert.Empty(Redactor.RuleNames.Except(negative));
		Assert.Empty(positive.Except(Redactor.RuleNames));
		Assert.Empty(negative.Except(Redactor.RuleNames));
	}

	[Fact]
	public void Token_inside_a_header_keeps_its_more_specific_label()
	{
		RedactionResult result = Redactor.Redact("Authorization: Bearer " + Synthetic.Jwt);

		Assert.Equal("Authorization: Bearer [REDACTED:jwt]", result.Text);
		Assert.Equal(new Dictionary<string, int> { ["jwt"] = 1 }, result.CountsByRule);
	}

	[Fact]
	public void Token_as_a_url_password_is_replaced_once()
	{
		string token = Synthetic.GitHubClassic('s');

		RedactionResult result = Redactor.Redact("https://x-access-token:" + token + "@github.com/o/r.git");

		Assert.Equal("https://x-access-token:[REDACTED:url-password]@github.com/o/r.git", result.Text);
		Assert.Equal(new Dictionary<string, int> { ["url-password"] = 1 }, result.CountsByRule);
	}

	[Fact]
	public void Candidate_rejected_by_its_rule_does_not_hide_a_secret_inside_it()
	{
		string token = Synthetic.Body(32, 400);

		RedactionResult result = Redactor.Redact("pageToken=api_key=" + token);

		Assert.Equal("pageToken=api_key=[REDACTED:secret-assignment]", result.Text);
	}

	[Fact]
	public void Several_secrets_are_counted_per_rule()
	{
		string input = $"{Synthetic.Jwt} {Synthetic.GitHubClassic('p')} {Synthetic.GitHubClassic('o')} password=hunter2";

		RedactionResult result = Redactor.Redact(input);

		Assert.Equal("[REDACTED:jwt] [REDACTED:gitleaks-github-pat] [REDACTED:gitleaks-github-oauth] password=[REDACTED:secret-assignment]", result.Text);
		Assert.Equal(new Dictionary<string, int> { ["jwt"] = 1, ["gitleaks-github-pat"] = 1, ["gitleaks-github-oauth"] = 1, ["secret-assignment"] = 1 }, result.CountsByRule);
		Assert.True(result.WasRedacted);
	}

	private static void AssertRedacted(string rule, string template, string secret)
	{
		string input = template.Replace(Slot, secret, StringComparison.Ordinal);
		string expected = template.Replace(Slot, $"[REDACTED:{rule}]", StringComparison.Ordinal);

		RedactionResult result = Redactor.Redact(input);

		Assert.Equal(expected, result.Text);
		Assert.Equal(new Dictionary<string, int> { [rule] = 1 }, result.CountsByRule);
		Assert.Equal(expected, Redactor.Redact(result.Text).Text);
		Assert.Empty(Redactor.Redact(result.Text).CountsByRule);
	}
}
