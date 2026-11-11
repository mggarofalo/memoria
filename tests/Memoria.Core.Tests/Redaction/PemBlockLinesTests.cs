using System.Text.RegularExpressions;
using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// Key lines with no marker: when <see cref="PemBlock.ExtendLines"/> takes a final short line into the run, and when
/// <see cref="PemBlock.IsUnmarkedKeyBody"/> takes lines of one width for a key. Every key is synthetic.
/// </summary>
public sealed class PemBlockLinesTests
{
	private static string L1 => Synthetic.Body(64, 630);

	private static string L2 => Synthetic.Body(64, 631);

	private static string L3 => Synthetic.Body(64, 632);

	/// <summary>A candidate whose secret is <paramref name="length"/> characters from <paramref name="start"/>, as a rule's match gives it.</summary>
	private static SecretCandidate Candidate(string input, int start, int length)
	{
		Match match = new Regex($@"\G(?<secret>[\s\S]{{{length}}})").Match(input, start);
		Assert.True(match.Success);
		return new SecretCandidate(input, match, match.Groups["secret"]);
	}

	/// <summary>The extended end for a run from <paramref name="first"/> through <paramref name="last"/>.</summary>
	private static int Extend(string input, string first, string last)
	{
		int start = input.IndexOf(first, StringComparison.Ordinal);
		int end = input.IndexOf(last, StringComparison.Ordinal) + last.Length;
		return PemBlock.ExtendLines(Candidate(input, start, end - start));
	}

	private static int After(string input, string piece)
	{
		return input.LastIndexOf(piece, StringComparison.Ordinal) + piece.Length;
	}

	private static bool IsKeyBody(string input)
	{
		return PemBlock.IsUnmarkedKeyBody(Candidate(input, 0, input.Length));
	}

	[Theory]
	[InlineData("Ab+c/d9XyZ==")]
	[InlineData("Ab3dEf7hIj")]
	[InlineData("9abcdefghij")]
	[InlineData("Abcdefghijk")]
	public void A_final_short_line_joins_the_run(string tail)
	{
		string input = $"{L1}\n{L2}\n{L3}\n{tail}\nafter";

		Assert.Equal(After(input, tail), Extend(input, L1, L3));
	}

	[Theory]
	[InlineData("abcdefghijk")]
	[InlineData("Ab3dEf7")]
	public void A_word_or_a_short_token_on_the_next_line_does_not_join(string tail)
	{
		string input = $"{L1}\n{L2}\n{L3}\n{tail}\nafter";

		Assert.Equal(After(input, L3), Extend(input, L1, L3));
	}

	[Fact]
	public void A_final_short_line_joins_before_a_closing_bracket()
	{
		string input = $"{L1}\n{L2}\n{L3}\nAb3dEf7hIj)\nafter";

		Assert.Equal(After(input, "Ab3dEf7hIj"), Extend(input, L1, L3));
	}

	[Fact]
	public void Quoted_lines_join_past_their_closing_punctuation()
	{
		string input = $"\"{L1}\",\n\"{L2}\",\n\"{L3}\",\n\"Ab3dEf7hIj==\"\n";

		Assert.Equal(After(input, "Ab3dEf7hIj=="), Extend(input, L1, L3));
	}

	[Fact]
	public void Five_closing_characters_are_more_than_a_line_ends_with()
	{
		string input = $"\"{L1}\"}},;!\n\"{L2}\"}},;!\n\"{L3}\"}},;!\n\"Ab3dEf7hIj==\"\n";

		Assert.Equal(After(input, L3), Extend(input, L1, L3));
	}

	[Fact]
	public void A_word_after_the_last_full_line_keeps_the_next_line_out()
	{
		string input = $"{L1}\n{L2}\n{L3} x9Kq8Zr7Lm=\n";

		Assert.Equal(After(input, L3), Extend(input, L1, L3));
	}

	[Fact]
	public void A_prefixed_final_line_joins_after_the_same_prefix()
	{
		string input = $"> {L1}\n> {L2}\n> {L3}\n> Ab3dEf7hIj==\n";

		Assert.Equal(After(input, "Ab3dEf7hIj=="), Extend(input, L1, L3));
	}

	[Fact]
	public void A_line_no_longer_than_the_prefix_does_not_join()
	{
		string input = $"> {L1}\n> {L2}\n> {L3}\nx=\nafter";

		Assert.Equal(After(input, L3), Extend(input, L1, L3));
	}

	[Fact]
	public void Lines_of_one_width_are_a_key_body()
	{
		Assert.True(IsKeyBody($"{L1}\n{L2}\n{L3}"));
	}

	[Fact]
	public void Lines_of_different_widths_are_not_a_key_body()
	{
		Assert.False(IsKeyBody($"{L1}\n{Synthetic.Body(48, 633)}\n{L3}"));
	}

	[Fact]
	public void A_line_with_no_base64_is_not_a_key_body_line()
	{
		Assert.False(IsKeyBody($"!!\n{Synthetic.Body(10, 634)}\n{Synthetic.Body(10, 635)}"));
	}

	[Fact]
	public void An_open_bracket_without_a_close_is_part_of_the_line()
	{
		Assert.True(IsKeyBody("x(Ab3dEf7hIjKl\nAb3dEf7hIjKm\nAb3dEf7hIjKn"));
	}

	[Fact]
	public void A_line_that_is_all_note_has_no_base64()
	{
		Assert.False(IsKeyBody("(Ab3dEf7hIj)\nAb3dEf7hIk\nAb3dEf7hIm"));
	}

	[Fact]
	public void A_note_of_22_characters_is_set_aside()
	{
		Assert.True(IsKeyBody("Ab3dEf7hIjKl(aa bb cc dd ee ff gg)\nAb3dEf7hIjKm\nAb3dEf7hIjKn"));
	}

	[Fact]
	public void Four_closing_characters_are_set_aside_and_a_fifth_is_not()
	{
		string four = string.Join('\n', Synthetic.Body(12, 636), Synthetic.Body(12, 637), Synthetic.Body(12, 638)).Replace("\n", "\"},;\n") + "\"},;";
		string five = four.Replace("\"},;", "\"},;!");

		Assert.True(IsKeyBody(four));
		Assert.False(IsKeyBody(five));
	}

	[Fact]
	public void A_trailing_slash_is_base64_not_punctuation()
	{
		Assert.True(IsKeyBody("Ab3dEf7hIjK/\nAb3dEf7hIjKm\nAb3dEf7hIjKn"));
	}

	[Fact]
	public void Only_a_data_uri_header_on_the_run_s_own_line_rules_it_out()
	{
		string split = $"data:image/png\nx;base64,{L1}\n{L2}\n{L3}";
		string joined = $"x data:image/png;base64,{L1}\n{L2}\n{L3}";

		Assert.True(PemBlock.IsUnmarkedKeyBody(Candidate(split, split.IndexOf(L1, StringComparison.Ordinal), split.Length - split.IndexOf(L1, StringComparison.Ordinal))));
		Assert.False(PemBlock.IsUnmarkedKeyBody(Candidate(joined, joined.IndexOf(L1, StringComparison.Ordinal), joined.Length - joined.IndexOf(L1, StringComparison.Ordinal))));
	}
}
