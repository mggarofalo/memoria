namespace Memoria.Core.Redaction;

/// <summary>
/// Recognizes a line break that cuts one token in two, as a terminal or an email client does at a
/// fixed column. Used only to build a detection view, so a wrong guess can cost a little
/// over-redaction of the next line's start but can never change text that no rule matches.
/// </summary>
internal static class LineWraps
{
	/// <summary>The characters a token wrapped across lines is made of. Excludes symbols, so prose and paths never join.</summary>
	private const int WrappedRunLength = 16;

	private const int WholeLineLength = 20;

	private const int MinimumContinuation = 8;

	private const int CommonPrefixLength = 4;

	private static ReadOnlySpan<int> WrapColumns => [64, 72, 76, 80, 100, 120];

	/// <summary>True when some line break in the text wraps a token. Allocates nothing, so a text with none costs one scan.</summary>
	public static bool Any(string input)
	{
		int newline = input.IndexOf('\n');
		while (newline >= 0)
		{
			int breakStart = newline > 0 && input[newline - 1] == '\r' ? newline - 1 : newline;
			if (JoinLength(input, breakStart) > 0)
			{
				return true;
			}

			newline = input.IndexOf('\n', newline + 1);
		}

		return false;
	}

	/// <summary>The length of the line break at the position when it wraps a token, otherwise 0.</summary>
	public static int JoinLength(string input, int breakStart)
	{
		int breakEnd = SkipBreak(input, breakStart);
		int lineStart = LineStartBefore(input, breakStart);
		int indent = SkipIndent(input, breakEnd);
		if (breakStart == lineStart || indent >= input.Length || !IsTokenChar(input[indent]))
		{
			return 0;
		}

		return IsWrap(input, lineStart, breakStart, indent) ? indent - breakStart : 0;
	}

	private static bool IsWrap(string input, int lineStart, int lineEnd, int nextStart)
	{
		int run = TrailingRunLength(input, lineStart, lineEnd);
		int nextRun = LeadingRunLength(input, nextStart);
		if (run < WrappedRunLength || StartsAlike(input, lineEnd - run, nextStart))
		{
			return false;
		}

		// The next line must look like a token's tail. Otherwise a token that merely ends at a wrap column would swallow the word after it.
		bool fixedColumn = IsWrapColumn(lineEnd - lineStart);
		bool wholeLine = run == lineEnd - lineStart && run >= WholeLineLength && nextRun >= MinimumContinuation;
		return (fixedColumn || wholeLine) && LooksRandom(input, nextStart, nextRun);
	}

	/// <summary>Two tokens listed on consecutive lines usually share a prefix, and one wrapped token does not.</summary>
	private static bool StartsAlike(string input, int first, int second)
	{
		return second + CommonPrefixLength <= input.Length
			&& string.CompareOrdinal(input, first, input, second, CommonPrefixLength) == 0;
	}

	private static bool IsWrapColumn(int length)
	{
		foreach (int column in WrapColumns)
		{
			if (column == length)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>A plain word on the next line is the next line, while a wrapped token's tail has digits or mixed case.</summary>
	private static bool LooksRandom(string input, int start, int length)
	{
		ReadOnlySpan<char> run = input.AsSpan(start, length);
		bool hasDigit = run.IndexOfAnyInRange('0', '9') >= 0;
		bool hasUpper = run.IndexOfAnyInRange('A', 'Z') >= 0;
		bool hasLower = run.IndexOfAnyInRange('a', 'z') >= 0;
		return hasDigit || (hasUpper && hasLower);
	}

	private static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '+' or '/' or '=' or '.';

	private static int SkipBreak(string input, int position)
	{
		bool crlf = input[position] == '\r' && position + 1 < input.Length && input[position + 1] == '\n';
		return position + (crlf ? 2 : 1);
	}

	private static int SkipIndent(string input, int position)
	{
		while (position < input.Length && input[position] is ' ' or '\t')
		{
			position++;
		}

		return position;
	}

	private static int LineStartBefore(string input, int lineEnd)
	{
		int newline = lineEnd == 0 ? -1 : input.LastIndexOfAny(['\n', '\r'], lineEnd - 1);
		return newline + 1;
	}

	private static int TrailingRunLength(string input, int lineStart, int lineEnd)
	{
		int cursor = lineEnd;
		while (cursor > lineStart && IsTokenChar(input[cursor - 1]))
		{
			cursor--;
		}

		return lineEnd - cursor;
	}

	private static int LeadingRunLength(string input, int start)
	{
		int cursor = start;
		while (cursor < input.Length && IsTokenChar(input[cursor]))
		{
			cursor++;
		}

		return cursor - start;
	}
}
