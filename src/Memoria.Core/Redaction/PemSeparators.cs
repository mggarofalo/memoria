namespace Memoria.Core.Redaction;

/// <summary>
/// Everything that can sit between two lines of a key in a transcript: raw newlines, every escaped
/// spelling of one, the punctuation of quoted line-by-line strings, and the per-line prefixes added
/// by <c>cat -n</c>, line-numbered readers, <c>grep -n</c>, lists, diffs, and comments.
/// </summary>
internal static class PemSeparators
{
	/// <summary>Spellings of a line feed, longest first where one is a prefix of another.</summary>
	private static readonly string[] s_lineFeeds =
	[
		"<br />", "<br/>", "<br>", "<BR>", "&#10;", "&#xA;", "&#xa;", "\\x0a", "\\x0A", "\\012", "\\u000a", "\\u000A", "`n", "%0A", "%0a",
	];

	/// <summary>Spellings of a carriage return. Half of a CRLF, so they do not count as a second line feed.</summary>
	private static readonly string[] s_carriageReturns =
	[
		"&#13;", "&#xD;", "&#xd;", "\\x0d", "\\x0D", "\\015", "\\u000d", "\\u000D", "`r", "%0D", "%0d",
	];

	/// <summary>What <see cref="Length"/> reports for a carriage return: half of a CRLF, so it does not count as a second line feed.</summary>
	private const int ReturnOnly = 2;

	private static readonly string[] s_blanks = ["%20", "%22", "`t", "\\x09", "\\011"];

	private static readonly char[] s_prefixMarkers = ['-', '*', '•', ';', '#', '>'];

	/// <summary>Skips what separates two lines. Returns the line feeds crossed, in any spelling.</summary>
	public static int Skip(string input, ref int position)
	{
		int lineFeeds = 0;
		bool sawReturn = false;
		bool crossedMessage = false;
		while (position < input.Length)
		{
			int length = Length(input, position, out int feeds);
			if (length == 0)
			{
				break;
			}

			crossedMessage |= input[position] == Redactor.MessageBoundary;
			sawReturn |= feeds == ReturnOnly;
			lineFeeds += feeds == 1 ? 1 : 0;
			position += length;
		}

		// The newlines around a message boundary are the separator's own, not a blank line.
		// A carriage return with no line feed beside it is a line break all the same.
		return crossedMessage || (lineFeeds == 0 && sawReturn) ? 1 : lineFeeds;
	}

	public static int SkipBackslashes(string input, int position)
	{
		while (position < input.Length && input[position] == '\\')
		{
			position++;
		}

		return position;
	}

	/// <summary>Skips a per-line prefix: a line number with its delimiter, a <c>file:12:</c> location, a list or diff mark, or a comment.</summary>
	public static int SkipLinePrefix(string input, int position)
	{
		int cursor = SkipBlanks(input, position);
		cursor = SkipFileLocation(input, cursor);
		cursor = SkipLineNumber(input, cursor);
		if (cursor + 1 < input.Length && input[cursor] == '/' && input[cursor + 1] == '/')
		{
			return SkipBlanks(input, cursor + 2);
		}

		// A marker's own dashes are not a list mark.
		bool dashRun = cursor + 1 < input.Length && input[cursor] == '-' && input[cursor + 1] == '-';
		if (cursor < input.Length && !dashRun && Array.IndexOf(s_prefixMarkers, input[cursor]) >= 0)
		{
			return SkipBlanks(input, cursor + 1);
		}

		return cursor;
	}

	/// <summary>Length of one base64 unit at the position, in any spelling JSON and URL encoders emit, or 0.</summary>
	public static int TokenUnitLength(string input, int position)
	{
		char c = input[position];
		if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')
		{
			return 1;
		}

		return c switch
		{
			'%' => EncodedSymbolLength(input, position),
			'\\' => EscapedSymbolLength(input, position),
			_ => 0,
		};
	}

	/// <summary>Walks back over what can sit between an END marker's body lines.</summary>
	public static int SkipBack(string input, int position)
	{
		int cursor = position;
		while (cursor > 0)
		{
			int moved = BackLength(input, cursor);
			if (moved == 0)
			{
				break;
			}

			cursor -= moved;
		}

		return cursor;
	}

	public static int TokenStartBefore(string input, int position)
	{
		int cursor = position;
		while (cursor > 0 && (char.IsAsciiLetterOrDigit(input[cursor - 1]) || input[cursor - 1] is '+' or '/' or '='))
		{
			cursor--;
		}

		return cursor;
	}

	private static int BackLength(string input, int cursor)
	{
		char c = input[cursor - 1];
		if (c is '\n' or '\r' or ' ' or '\t' or '"' or '\'' or '|' or '>' or '→' or ':' or ';' or '*' or '#' or '-' or Redactor.MessageBoundary)
		{
			return 1;
		}

		bool escapedBreak = c is 'n' or 'r' && cursor >= 2 && input[cursor - 2] == '\\';
		if (escapedBreak || c == '\\')
		{
			return escapedBreak ? 2 : 1;
		}

		return IsLineNumberBefore(input, cursor);
	}

	/// <summary>A run of digits at the very start of a line is a line number, not the end of a body line.</summary>
	private static int IsLineNumberBefore(string input, int cursor)
	{
		int start = cursor;
		while (start > 0 && char.IsAsciiDigit(input[start - 1]))
		{
			start--;
		}

		bool atLineStart = start == 0 || input[start - 1] is '\n' or '\r' or ' ' or '\t';
		return start < cursor && atLineStart ? cursor - start : 0;
	}

	private static int Length(string input, int position, out int lineFeeds)
	{
		lineFeeds = 0;
		char c = input[position];
		switch (c)
		{
			case '\n':
				lineFeeds = 1;
				return 1;
			case '\r':
				lineFeeds = ReturnOnly;
				return 1;
			case ' ' or '\t' or '"' or '\'' or ',' or '>' or '|' or '#' or Redactor.MessageBoundary:
				return 1;
			case '+':
				return IsConcatenationPlus(input, position) ? 1 : 0;
			case '\\':
				return Spelled(input, position, ref lineFeeds) is var spelled and > 0 ? spelled : EscapedBreakLength(input, position, out lineFeeds);
			case '%' or '<' or '&' or '`':
				return Spelled(input, position, ref lineFeeds);
			default:
				return 0;
		}
	}

	private static int Spelled(string input, int position, ref int lineFeeds)
	{
		int feed = MatchAny(input, position, s_lineFeeds);
		if (feed > 0)
		{
			lineFeeds = 1;
			return feed;
		}

		int carriageReturn = MatchAny(input, position, s_carriageReturns);
		if (carriageReturn > 0)
		{
			lineFeeds = ReturnOnly;
			return carriageReturn;
		}

		return MatchAny(input, position, s_blanks);
	}

	private static int MatchAny(string input, int position, string[] spellings)
	{
		foreach (string spelling in spellings)
		{
			if (string.CompareOrdinal(input, position, spelling, 0, spelling.Length) == 0)
			{
				return spelling.Length;
			}
		}

		return 0;
	}

	/// <summary>A <c>+</c> joining string literals across lines, as opposed to one inside base64.</summary>
	private static bool IsConcatenationPlus(string input, int position)
	{
		return position + 1 < input.Length && input[position + 1] is ' ' or '\t' or '\r' or '\n' or '"' or '\'';
	}

	private static int EscapedBreakLength(string input, int position, out int lineFeeds)
	{
		lineFeeds = 0;
		int next = SkipBackslashes(input, position);
		if (next >= input.Length)
		{
			return next - position;
		}

		lineFeeds = input[next] switch
		{
			'n' or '\n' => 1,
			'r' or '\r' => ReturnOnly,
			_ => 0,
		};
		return lineFeeds > 0 || input[next] is 't' or '"' or '\'' ? next + 1 - position : 0;
	}

	private static int SkipBlanks(string input, int position)
	{
		while (position < input.Length && input[position] is ' ' or '\t')
		{
			position++;
		}

		return position;
	}

	/// <summary>Skips <c>12</c> and its delimiter. Base64 contains none of these delimiters, so digits before one are never key text.</summary>
	private static int SkipLineNumber(string input, int position)
	{
		int cursor = position;
		while (cursor < input.Length && char.IsAsciiDigit(input[cursor]))
		{
			cursor++;
		}

		if (cursor == position || cursor >= input.Length || input[cursor] is not ('\t' or '→' or ':' or '-' or '.' or ')' or '|'))
		{
			return position;
		}

		return SkipBlanks(input, cursor + 1);
	}

	/// <summary>Skips the <c>key.pem:</c> or <c>key.pem-</c> of grep output. Requires a dot, which base64 never contains.</summary>
	private static int SkipFileLocation(string input, int position)
	{
		int cursor = position;
		bool sawDot = false;
		while (cursor < input.Length && (char.IsAsciiLetterOrDigit(input[cursor]) || input[cursor] is '.' or '/' or '\\' or '_'))
		{
			sawDot |= input[cursor] == '.';
			cursor++;
		}

		bool delimited = cursor < input.Length && input[cursor] is ':' or '-';
		return sawDot && delimited ? cursor + 1 : position;
	}

	private static int EncodedSymbolLength(string input, int position)
	{
		if (position + 2 >= input.Length)
		{
			return 0;
		}

		char high = input[position + 1];
		char low = input[position + 2];
		bool symbol = (high == '2' && low is 'B' or 'b' or 'F' or 'f') || (high == '3' && low is 'D' or 'd');
		return symbol ? 3 : 0;
	}

	/// <summary>Accepts <c>\/</c>, and <c>+</c> style escapes, which System.Text.Json writes for <c>+</c> by default.</summary>
	private static int EscapedSymbolLength(string input, int position)
	{
		int next = SkipBackslashes(input, position);
		if (next < input.Length && input[next] == '/')
		{
			return next + 1 - position;
		}

		return IsUnicodeEscape(input, next) ? next + 5 - position : 0;
	}

	/// <summary>A base64 symbol escaped as hex: <c>+</c>, <c>/</c>, or <c>=</c>. A line feed spelled this way is a separator, not a symbol.</summary>
	private static bool IsUnicodeEscape(string input, int position)
	{
		return position + 4 < input.Length
			&& input[position] == 'u'
			&& input[position + 1] == '0'
			&& input[position + 2] == '0'
			&& input[position + 3] is '2' or '3'
			&& char.IsAsciiHexDigit(input[position + 4]);
	}
}
