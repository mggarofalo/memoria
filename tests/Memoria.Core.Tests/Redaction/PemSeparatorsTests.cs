using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>Direct, exact tests of what can sit between two lines of a key, with one case on each side of each comparison.</summary>
public sealed class PemSeparatorsTests
{
	private static readonly Redactor Redactor = new();

	[Theory]
	[InlineData("12\tX", 3)]
	[InlineData("12→X", 3)]
	[InlineData("12:X", 3)]
	[InlineData("12-X", 3)]
	[InlineData("12.X", 3)]
	[InlineData("12)X", 3)]
	[InlineData("12|X", 3)]
	[InlineData("12: X", 4)]
	[InlineData("12:\t X", 5)]
	[InlineData("  12:X", 5)]
	[InlineData("12", 0)]
	[InlineData("12X", 0)]
	[InlineData("12 X", 0)]
	[InlineData("12,X", 0)]
	[InlineData("X12:", 0)]
	[InlineData("", 0)]
	public void Line_number_prefix_is_exact(string text, int expected)
	{
		Assert.Equal(expected, PemSeparators.SkipLinePrefix(text, 0));
	}

	[Theory]
	[InlineData("//X", 2)]
	[InlineData("// X", 3)]
	[InlineData("/X", 0)]
	[InlineData("/", 0)]
	[InlineData("-X", 1)]
	[InlineData("- X", 2)]
	[InlineData("--X", 0)]
	[InlineData("-", 1)]
	[InlineData("*X", 1)]
	[InlineData("• X", 2)]
	[InlineData(";X", 1)]
	[InlineData("#X", 1)]
	[InlineData(">X", 1)]
	[InlineData("=X", 0)]
	public void Mark_prefix_is_exact(string text, int expected)
	{
		Assert.Equal(expected, PemSeparators.SkipLinePrefix(text, 0));
	}

	[Theory]
	[InlineData("key.pem:X", 8)]
	[InlineData("key.pem-X", 8)]
	[InlineData("a/b.pem:X", 8)]
	[InlineData("a\\b.pem:X", 8)]
	[InlineData("a_b.pem:X", 8)]
	[InlineData("key.pem:12:X", 11)]
	[InlineData("key.pem", 0)]
	[InlineData("key.pem X", 0)]
	[InlineData("nodot:X", 0)]
	[InlineData(".:X", 2)]
	public void File_location_prefix_is_exact(string text, int expected)
	{
		Assert.Equal(expected, PemSeparators.SkipLinePrefix(text, 0));
	}

	[Fact]
	public void Prefix_starts_from_the_given_position()
	{
		Assert.Equal(5, PemSeparators.SkipLinePrefix("xx12:X", 2));
	}

	[Theory]
	[InlineData("A", 0, 1)]
	[InlineData("9", 0, 1)]
	[InlineData("+", 0, 1)]
	[InlineData("/", 0, 1)]
	[InlineData("=", 0, 1)]
	[InlineData("%2B", 0, 3)]
	[InlineData("%2b", 0, 3)]
	[InlineData("%2F", 0, 3)]
	[InlineData("%2f", 0, 3)]
	[InlineData("%3D", 0, 3)]
	[InlineData("%3d", 0, 3)]
	[InlineData("%2D", 0, 0)]
	[InlineData("%3B", 0, 0)]
	[InlineData("%4D", 0, 0)]
	[InlineData("%2", 0, 0)]
	[InlineData("%", 0, 0)]
	[InlineData("\\/", 0, 2)]
	[InlineData("\\\\/", 0, 3)]
	[InlineData("\\u002B", 0, 6)]
	[InlineData("\\u003d", 0, 6)]
	[InlineData("\\u004B", 0, 0)]
	[InlineData("\\u102B", 0, 0)]
	[InlineData("\\v002B", 0, 0)]
	[InlineData("\\u002", 0, 0)]
	[InlineData("\\", 0, 0)]
	[InlineData("-", 0, 0)]
	[InlineData(" ", 0, 0)]
	public void Base64_unit_length_is_exact(string text, int position, int expected)
	{
		Assert.Equal(expected, PemSeparators.TokenUnitLength(text, position));
	}

	[Theory]
	[InlineData("\n", 1, 1)]
	[InlineData("\n\n", 2, 2)]
	[InlineData("\r\n", 2, 1)]
	[InlineData("\r", 1, 1)]
	[InlineData("\r\r", 2, 1)]
	[InlineData(" ", 1, 0)]
	[InlineData("\t", 1, 0)]
	[InlineData("\"", 1, 0)]
	[InlineData("'", 1, 0)]
	[InlineData(",", 1, 0)]
	[InlineData(">", 1, 0)]
	[InlineData("|", 1, 0)]
	[InlineData("#", 1, 0)]
	[InlineData("\\n", 2, 1)]
	[InlineData("\\\\n", 3, 1)]
	[InlineData("\\r", 2, 1)]
	[InlineData("\\t", 2, 0)]
	[InlineData("\\\"", 2, 0)]
	[InlineData("\\'", 2, 0)]
	[InlineData("\\\n", 2, 1)]
	[InlineData("\\\r", 2, 1)]
	[InlineData("\\", 1, 0)]
	[InlineData("\\q", 0, 0)]
	[InlineData(" +\n", 3, 1)]
	[InlineData(" +\r", 3, 1)]
	[InlineData(" + ", 3, 0)]
	[InlineData(" +\t", 3, 0)]
	[InlineData(" +\"", 3, 0)]
	[InlineData(" +'", 3, 0)]
	[InlineData(" +", 1, 0)]
	[InlineData("+A", 0, 0)]
	[InlineData("<br>", 4, 1)]
	[InlineData("<BR>", 4, 1)]
	[InlineData("<br/>", 5, 1)]
	[InlineData("<br />", 6, 1)]
	[InlineData("<b>", 0, 0)]
	[InlineData("&#10;", 5, 1)]
	[InlineData("&#xA;", 5, 1)]
	[InlineData("&#xa;", 5, 1)]
	[InlineData("&#13;", 5, 1)]
	[InlineData("&#xD;", 5, 1)]
	[InlineData("&#xd;", 5, 1)]
	[InlineData("&amp;", 0, 0)]
	[InlineData("\\x0a", 4, 1)]
	[InlineData("\\x0A", 4, 1)]
	[InlineData("\\x0d", 4, 1)]
	[InlineData("\\x0D", 4, 1)]
	[InlineData("\\012", 4, 1)]
	[InlineData("\\015", 4, 1)]
	[InlineData("\\u000a", 6, 1)]
	[InlineData("\\u000A", 6, 1)]
	[InlineData("\\u000d", 6, 1)]
	[InlineData("\\u000D", 6, 1)]
	[InlineData("`n", 2, 1)]
	[InlineData("`r", 2, 1)]
	[InlineData("`t", 2, 0)]
	[InlineData("`x", 0, 0)]
	[InlineData("%0A", 3, 1)]
	[InlineData("%0a", 3, 1)]
	[InlineData("%0D", 3, 1)]
	[InlineData("%0d", 3, 1)]
	[InlineData("%20", 3, 0)]
	[InlineData("%22", 3, 0)]
	[InlineData("\\x09", 4, 0)]
	[InlineData("\\011", 4, 0)]
	[InlineData("%21", 0, 0)]
	[InlineData("%", 0, 0)]
	[InlineData("A", 0, 0)]
	public void Separator_length_and_line_feeds_are_exact(string text, int length, int feeds)
	{
		int position = 0;

		Assert.Equal(feeds, PemSeparators.Skip(text, ref position));
		Assert.Equal(length, position);
	}

	[Fact]
	public void Message_boundary_counts_as_one_break_however_many_newlines_surround_it()
	{
		int position = 0;

		Assert.Equal(1, PemSeparators.Skip(Redactor.MessageSeparator, ref position));
		Assert.Equal(Redactor.MessageSeparator.Length, position);
	}

	[Fact]
	public void A_line_feed_beside_a_return_counts_once_and_two_feeds_count_twice()
	{
		int position = 0;

		Assert.Equal(2, PemSeparators.Skip("\r\n\r\n", ref position));
		Assert.Equal(4, position);
	}

	[Theory]
	[InlineData("abc\n", 4, 3)]
	[InlineData("abc\r\n", 5, 3)]
	[InlineData("abc \t", 5, 3)]
	[InlineData("abc\"'", 5, 3)]
	[InlineData("abc|>", 5, 3)]
	[InlineData("abc→", 4, 3)]
	[InlineData("abc:;", 5, 3)]
	[InlineData("abc*#-", 6, 3)]
	[InlineData("abc\\", 4, 3)]
	[InlineData("abc\\n", 5, 3)]
	[InlineData("abc\\r", 5, 3)]
	[InlineData("abcn", 4, 4)]
	[InlineData("abcr", 4, 4)]
	[InlineData("abc!", 4, 4)]
	[InlineData("\n", 1, 0)]
	[InlineData("", 0, 0)]
	public void Walking_back_over_separators_is_exact(string text, int from, int expected)
	{
		Assert.Equal(expected, PemSeparators.SkipBack(text, from));
	}

	[Theory]
	[InlineData("abc 12", 6, 3)]
	[InlineData("abc\n12", 6, 3)]
	[InlineData("abc\r12", 6, 3)]
	[InlineData("abc\t12", 6, 3)]
	[InlineData("12", 2, 0)]
	[InlineData("abc12", 5, 5)]
	public void Digits_starting_a_line_are_a_line_number_when_walking_back(string text, int from, int expected)
	{
		Assert.Equal(expected, PemSeparators.SkipBack(text, from));
	}

	[Theory]
	[InlineData("abc", 3, 0)]
	[InlineData("a+/=", 4, 0)]
	[InlineData("x-abc", 5, 2)]
	[InlineData("x abc", 5, 2)]
	[InlineData("abc", 0, 0)]
	public void Token_start_is_exact(string text, int from, int expected)
	{
		Assert.Equal(expected, PemSeparators.TokenStartBefore(text, from));
	}

	[Theory]
	[InlineData("\\\\\\x", 0, 3)]
	[InlineData("x", 0, 0)]
	[InlineData("", 0, 0)]
	[InlineData("a\\", 1, 2)]
	public void Backslashes_are_counted_exactly(string text, int from, int expected)
	{
		Assert.Equal(expected, PemSeparators.SkipBackslashes(text, from));
	}
}
