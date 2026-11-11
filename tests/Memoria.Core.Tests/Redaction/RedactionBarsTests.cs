using Memoria.Core.Redaction;
using Memoria.Core.Tests.Redaction.Gitleaks;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// The two bars beside the import's: real-format tokens of the providers known through gitleaks's rules must all be caught, in every
/// context a transcript puts them in, and ordinary transcript lines must all come through unchanged.
/// </summary>
public sealed class RedactionBarsTests
{
	public static TheoryData<string, string, string> ProviderTokensInContext()
	{
		TheoryData<string, string, string> data = [];
		foreach ((string provider, string token) in ProviderFormats.Tokens)
		{
			foreach (string context in ProviderFormats.Contexts)
			{
				data.Add(provider, context, token);
			}
		}

		return data;
	}

	[Theory]
	[MemberData(nameof(ProviderTokensInContext))]
	public void A_real_format_provider_token_is_caught_in_each_context(string provider, string context, string token)
	{
		string text = context.Replace("{T}", token, StringComparison.Ordinal);
		string redacted = Redactor.Shared.RedactFragment(text).Text;
		Assert.False(redacted.Contains(token, StringComparison.Ordinal), $"{provider} survived in: {context}");
	}

	[Theory]
	[MemberData(nameof(BenignLines.Data), MemberType = typeof(BenignLines))]
	public void An_ordinary_transcript_line_comes_through_unchanged(string line)
	{
		Assert.Equal(line, Redactor.Shared.RedactFragment(line).Text);
	}

	[Fact]
	public void The_fixture_has_lines_and_every_placeholder_is_filled()
	{
		Assert.True(BenignLines.All.Count >= 40, BenignLines.All.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
		Assert.DoesNotContain(BenignLines.All, static line => line.Contains("{hex", StringComparison.Ordinal) || line.Contains("{uuid}", StringComparison.Ordinal) || line.Contains("{digits", StringComparison.Ordinal));
	}
}
