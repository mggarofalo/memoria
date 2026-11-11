using System.Text.RegularExpressions;
using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// Where <see cref="PemBlock"/>'s walk from a BEGIN marker stops, line by line: the marker's own forms, prose and
/// elisions before and after the body, short last lines, and prefixed lines. Each case asserts the exact end of the
/// span, or that there is none, so a walk that stops one line early or runs one line late fails. Every key is synthetic.
/// </summary>
public sealed class PemBlockWalkTests
{
	private const string Begin = "-----BEGIN " + Synthetic.PrivateWord + " KEY-----";
	private const string End = "-----END " + Synthetic.PrivateWord + " KEY-----";

	/// <summary>Short last lines of a key: random-looking, as base64 is, unlike a word.</summary>
	private const string Six = "Ab3dE7";
	private const string Eight = "Ab3dEf7h";
	private const string SixteenA = "Ab3dEf7hIj9kLm2N";
	private const string SixteenB = "Qr5sTu8vWx1yZa4B";
	private const string TwentyA = "Cd4eFg5hIj6kLm7nOp8q";
	private const string TwentyB = "Rs9tUv0wXy1zAb2cDe3f";

	private static string Line1 => Synthetic.Body(64, 600);

	private static string Line2 => Synthetic.Body(64, 601);

	private static (int Start, int End)? Walk(string input)
	{
		return PemBlock.FindForward(input, Regex.Match(input, Regex.Escape(Begin)));
	}

	private static int After(string input, string piece)
	{
		return input.LastIndexOf(piece, StringComparison.Ordinal) + piece.Length;
	}

	private static void AssertEndsAfter(string input, string piece)
	{
		Assert.Equal((input.IndexOf(Begin, StringComparison.Ordinal), After(input, piece)), Walk(input));
	}

	[Fact]
	public void Two_dashes_make_an_end_marker()
	{
		string input = $"{Begin}\n{Line1}\n--END\nafter";

		AssertEndsAfter(input, "--END");
	}

	[Fact]
	public void Two_dashes_make_an_end_marker_inside_a_line()
	{
		string input = $"{Begin}\n{Line1}\nlog: --END";

		AssertEndsAfter(input, "--END");
	}

	[Fact]
	public void One_dash_makes_an_end_marker_when_the_next_40_characters_name_a_private_key()
	{
		string within = $"{Begin}\n{Line1}\n-END{new string(' ', 33)}{Synthetic.PrivateWord}";
		string beyond = $"{Begin}\n{Line1}\n-END{new string(' ', 34)}{Synthetic.PrivateWord}";

		AssertEndsAfter(within, "-END");
		AssertEndsAfter(beyond, Line1);
	}

	[Fact]
	public void End_marker_glued_to_its_name_ends_after_the_name()
	{
		string input = $"{Begin}\n{Line1}\n-----ENDKEY\nafter";

		AssertEndsAfter(input, "-----ENDKEY");
	}

	[Fact]
	public void Inside_a_line_only_a_dash_starts_an_end_marker()
	{
		string input = $"{Begin}\n{Line1}\nnote: the END of the {Synthetic.PrivateWord} key is missing";

		AssertEndsAfter(input, Line1);
	}

	[Fact]
	public void A_second_begin_ends_a_block_that_has_no_body()
	{
		Assert.Null(Walk($"{Begin}\n{Begin}\n{Line1}\n{End}"));
	}

	[Fact]
	public void An_empty_block_is_a_mention_even_with_base64_after_its_end()
	{
		Assert.Null(Walk($"{Begin}\n{End}\n{Line1}"));
	}

	[Fact]
	public void Short_lines_count_as_a_key_only_when_an_end_marker_closes_them()
	{
		string closed = $"{Begin}\n{Eight}\n{Eight.ToUpperInvariant()}9\n{End}";
		string open = $"{Begin}\n{Eight}\n{Eight.ToUpperInvariant()}9";

		AssertEndsAfter(closed, End);
		Assert.Null(Walk(open));
	}

	[Fact]
	public void Escaped_line_breaks_end_a_line_of_prose_before_the_body()
	{
		string input = $"\"{Begin}\\nsee below\\n{Line1}\\n{End}\"";

		AssertEndsAfter(input, End);
	}

	[Fact]
	public void Backslash_at_the_end_of_the_text_ends_a_line_of_prose()
	{
		Assert.Null(Walk($"{Begin}\nsee below\\"));
	}

	[Fact]
	public void A_short_token_after_prose_is_prose()
	{
		string input = $"{Begin}\nsee below\n{SixteenA}\n{SixteenB}\n\nprose";

		Assert.Null(Walk(input));
	}

	[Fact]
	public void A_short_token_after_prose_is_body_when_the_end_marker_follows()
	{
		string input = $"{Begin}\nsee below\n{SixteenA}\n{End}";

		AssertEndsAfter(input, End);
	}

	[Fact]
	public void A_32_character_line_after_prose_is_body()
	{
		string first = Synthetic.Body(32, 610);
		string second = Synthetic.Body(32, 611);
		string input = $"{Begin}\nsee below\n{first}\n{second}\n\nprose";

		AssertEndsAfter(input, second);
	}

	[Fact]
	public void A_dash_after_the_marker_on_its_line_is_prose_not_a_list_mark()
	{
		Assert.Null(Walk($"{Begin} - {SixteenA}\n{End}"));
	}

	[Fact]
	public void List_marks_before_every_line_are_skipped()
	{
		string input = $"{Begin}\n* {Line1}\n* {TwentyA}\n* {TwentyB}\n* {End}";

		AssertEndsAfter(input, End);
	}

	[Fact]
	public void A_note_after_a_40_character_line_is_read_past()
	{
		string first = Synthetic.Body(40, 612);
		string second = Synthetic.Body(40, 613);
		string input = $"{Begin}\n{first} (line 1)\n{second} (line 2)\n{End}";

		AssertEndsAfter(input, End);
	}

	[Theory]
	[InlineData("[...]")]
	[InlineData("…")]
	[InlineData("<snip>")]
	[InlineData("(truncated)")]
	[InlineData("(3 lines omitted)")]
	[InlineData("[elided]")]
	public void An_elision_between_body_lines_is_read_past(string elision)
	{
		string input = $"{Begin}\n{Line1}\n{elision}\n{Line2}\n{End}";

		AssertEndsAfter(input, End);
	}

	[Fact]
	public void A_line_of_prose_between_body_lines_ends_the_key()
	{
		string input = $"{Begin}\n{Line1}\nnot part of it\n{Line2}\n{End}";

		AssertEndsAfter(input, Line1);
	}

	[Fact]
	public void An_elision_may_be_80_characters_long_and_no_longer()
	{
		string at = $"{Begin}\n{Line1}\n[{new string('.', 78)}]\n{Line2}\n{End}";
		string past = $"{Begin}\n{Line1}\n[{new string('.', 79)}]\n{Line2}\n{End}";

		AssertEndsAfter(at, End);
		AssertEndsAfter(past, Line1);
	}

	[Fact]
	public void A_short_last_line_at_the_end_of_the_text_is_body()
	{
		string input = $"{Begin}\n{Line1}\n{Six}";

		AssertEndsAfter(input, Six);
	}

	[Fact]
	public void A_short_last_line_before_a_blank_line_is_body()
	{
		string input = $"{Begin}\n{Line1}\n{Six}\n\nprose";

		AssertEndsAfter(input, Six);
	}

	[Fact]
	public void A_short_last_line_closed_by_a_quote_is_body()
	{
		string input = $"{{\"k\":\"{Begin}\\n{Line1}\\n{Six}\"}}";

		AssertEndsAfter(input, Six);
	}

	[Fact]
	public void Eight_random_characters_before_prose_are_the_last_line()
	{
		string input = $"{Begin}\n{Line1}\n{Eight} then words";

		AssertEndsAfter(input, Eight);
	}

	[Fact]
	public void Fewer_random_characters_before_prose_are_prose()
	{
		string input = $"{Begin}\n{Line1}\n{Eight[..7]} then words";

		AssertEndsAfter(input, Line1);
	}

	[Theory]
	[InlineData("Ab3d")]
	[InlineData("Ab3dEf7h")]
	public void A_short_last_line_after_a_log_prefix_is_body(string tail)
	{
		string input = $"{Begin}\n[12:00:01] {Line1}\n[12:00:02] {tail}\n{End}";

		AssertEndsAfter(input, End);
	}

	[Fact]
	public void Three_characters_after_a_log_prefix_are_not_body()
	{
		string input = $"{Begin}\n[12:00:01] {Line1}\n[12:00:02] Ab3\n{End}";

		AssertEndsAfter(input, Line1);
	}

	[Fact]
	public void A_short_line_after_a_log_prefix_with_words_after_it_is_prose()
	{
		string input = $"{Begin}\n[12:00:01] {Line1}\n[12:00:02] {Eight} is the end";

		AssertEndsAfter(input, Line1);
	}

	[Fact]
	public void The_prefix_runs_to_the_first_long_run_on_the_line()
	{
		string first = Synthetic.Body(40, 614);
		string second = Synthetic.Body(40, 615);
		string input = $"{Begin}\n[t] {first} {second}\n[t] {Eight}\n{End}";

		AssertEndsAfter(input, End);
	}

	[Fact]
	public void Backward_walk_takes_a_32_character_line_above_the_nearest()
	{
		string top = Synthetic.Body(32, 620);
		string nearest = Synthetic.Body(10, 621);
		string input = $"{top}\n{nearest}\n{End}";

		Assert.Equal((0, input.Length), PemBlock.FindBackward(input, Regex.Match(input, Regex.Escape(End))));
	}

	[Fact]
	public void Backward_walk_stops_at_a_shorter_line_above_the_nearest()
	{
		string top = Synthetic.Body(31, 620);
		string nearest = Synthetic.Body(10, 621);
		string input = $"{top}\n{nearest}\n{End}";

		Assert.Null(PemBlock.FindBackward(input, Regex.Match(input, Regex.Escape(End))));
	}
}
