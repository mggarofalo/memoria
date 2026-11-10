using System.Text;
using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>Credentials written as hex, or with <c>\uXXXX</c> escapes spelled out, are decoded and replaced. Hex that only looks random stays.</summary>
public sealed class HexAndUnicodeEscapeTests
{
	public static TheoryData<string> Tokens() =>
	[
		Synthetic.GitHubClassic('p'),
		Synthetic.GitHubFineGrained,
		Synthetic.SlackBot,
		Synthetic.StripeLive,
		Synthetic.Npm,
		Synthetic.Anthropic,
		Synthetic.AwsAccessKey,
		Synthetic.GoogleApiKey,
	];

	[Theory]
	[MemberData(nameof(Tokens))]
	public void A_token_as_hex_is_replaced_in_either_case(string token)
	{
		string lower = Convert.ToHexStringLower(Encoding.ASCII.GetBytes(token));
		string upper = Convert.ToHexString(Encoding.ASCII.GetBytes(token));

		Assert.Equal("h=[REDACTED:hex-token] end", Redactor.Shared.RedactFragment("h=" + lower + " end").Text);
		Assert.Equal("\"[REDACTED:hex-token]\"", Redactor.Shared.RedactFragment("\"" + upper + "\"").Text);
	}

	[Theory]
	[MemberData(nameof(Tokens))]
	public void A_token_with_escaped_separators_is_replaced(string token)
	{
		string escaped = string.Concat(token.Select(static c => char.IsAsciiLetterOrDigit(c) ? c.ToString() : $@"\u{(int)c:x4}"));

		// A token with no separator has nothing to escape, and its own rule takes it.
		Assert.Matches(@"^\{""v"":""\[REDACTED:[a-z0-9-]+\]""\}$", Redactor.Shared.RedactFragment("{\"v\":\"" + escaped + "\"}").Text);
	}

	[Fact]
	public void A_fully_escaped_token_is_replaced()
	{
		string escaped = string.Concat(Synthetic.GitHubClassic('p').Select(static c => $@"\u{(int)c:X4}"));

		Assert.Equal("[REDACTED:unicode-escaped-token]", Redactor.Shared.RedactFragment(escaped).Text);
	}

	[Fact]
	public void Hex_inside_hex_and_base64_inside_hex_are_looked_through()
	{
		string token = Synthetic.GitHubClassic('p');
		string twice = Convert.ToHexStringLower(Encoding.ASCII.GetBytes(Convert.ToHexStringLower(Encoding.ASCII.GetBytes(token))));
		string base64Then = Convert.ToHexStringLower(Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.ASCII.GetBytes(token))));

		Assert.Equal("[REDACTED:hex-token]", Redactor.Shared.RedactFragment(twice).Text);
		Assert.Equal("[REDACTED:hex-token]", Redactor.Shared.RedactFragment(base64Then).Text);
	}

	[Theory]
	[InlineData("commit fd9b778c57587e23e883c887350ea537710bee7c")]
	[InlineData("sha256:5f2b9c1d0e8a7b6c4d3e2f1a0b9c8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c")]
	[InlineData("\"id\": \"919657fff59d4e275172791f4e06d65f\"")]
	[InlineData("68656c6c6f20776f726c64")]
	public void Hex_that_decodes_to_no_token_stays(string text)
	{
		Assert.Equal(text, Redactor.Shared.RedactFragment(text).Text);
	}

	/// <summary>A JSON string's <c>\n</c>, PostgreSQL's <c>\x</c> and a <c>0x</c> put a letter before the run, and the run is still read.</summary>
	[Theory]
	[InlineData("{\"out\":\"line one\\n{S}\"}", "{\"out\":\"line one\\n[REDACTED:hex-token]\"}")]
	[InlineData("{\"out\":\"a\\t{S}\"}", "{\"out\":\"a\\t[REDACTED:hex-token]\"}")]
	[InlineData("select '\\x{S}'::bytea", "select '\\x[REDACTED:hex-token]'::bytea")]
	[InlineData("0x{S}", "0x[REDACTED:hex-token]")]
	public void Hex_after_an_escape_or_a_prefix_is_replaced(string template, string expected)
	{
		string hex = Convert.ToHexStringLower(Encoding.ASCII.GetBytes(Synthetic.GitHubClassic('p')));

		Assert.Equal(expected, Redactor.Shared.RedactFragment(template.Replace("{S}", hex, StringComparison.Ordinal)).Text);
	}

	[Fact]
	public void Hex_of_odd_length_or_glued_to_more_hex_is_not_decoded()
	{
		string hex = Convert.ToHexStringLower(Encoding.ASCII.GetBytes(Synthetic.GitHubClassic('p')));

		Assert.Equal(hex[..^1], Redactor.Shared.RedactFragment(hex[..^1]).Text);
		Assert.Equal("a" + hex, Redactor.Shared.RedactFragment("a" + hex).Text);
	}

	[Theory]
	[InlineData("caf\\u00e9 au lait")]
	[InlineData("\\u003cdiv class=\\u0022x\\u0022\\u003e")]
	[InlineData("emoji \\ud83d\\ude00 here, it\\u2019s fine")]
	[InlineData("C:\\users\\u0041dmin\\notes")]
	public void Escapes_in_ordinary_text_stay(string text)
	{
		Assert.Equal(text, Redactor.Shared.RedactFragment(text).Text);
	}

	[Fact]
	public void An_escaped_token_after_a_json_newline_keeps_the_newline_escape()
	{
		string escaped = string.Concat(Synthetic.GitHubClassic('p').Select(static c => $"\\u{(int)c:X4}"));

		Assert.Equal("{\"x\":\"a\\n[REDACTED:unicode-escaped-token]\"}", Redactor.Shared.RedactFragment("{\"x\":\"a\\n" + escaped + "\"}").Text);
	}

	/// <summary>After a backslash that is not an escape's start, as in a Windows path or regex text, the run is still read.</summary>
	[Theory]
	[InlineData("C:\\\\keys\\\\{S}")]
	[InlineData("C:\\keys\\{S}")]
	[InlineData("\\s{S}")]
	[InlineData("x\\a{S}")]
	public void An_escaped_token_after_another_backslash_is_replaced(string template)
	{
		string token = Synthetic.GitHubClassic('p');
		string escaped = token.Replace("_", "\\u005f", StringComparison.Ordinal);

		Assert.DoesNotContain(token[4..], Redactor.Shared.RedactFragment(template.Replace("{S}", escaped, StringComparison.Ordinal)).Text, StringComparison.Ordinal);
	}

	[Fact]
	public void A_mask_written_as_hex_stays()
	{
		string mask = Convert.ToHexStringLower(Encoding.ASCII.GetBytes("gh" + "p_" + new string('x', 36)));

		Assert.Equal(mask, Redactor.Shared.RedactFragment(mask).Text);
	}
}
