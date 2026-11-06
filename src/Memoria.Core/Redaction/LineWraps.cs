using System.Text.RegularExpressions;

namespace Memoria.Core.Redaction;

/// <summary>
/// Recognizes a line break that cuts one token in two, as a terminal or an email client does at a
/// fixed column. Used only to build a detection view, but a wrong guess here costs real text: the rule that
/// matches the joined token replaces the next line's start along with it, and that word is gone for good.
/// So a whole token only joins what cannot be ordinary text. The cost is that a wrapped token whose tail
/// happens to read like a word keeps that short tail, with the rest of the secret removed.
/// </summary>
internal static class LineWraps
{
	/// <summary>The characters a token wrapped across lines is made of. Excludes symbols, so prose and paths never join.</summary>
	private const int WrappedRunLength = 16;

	private const int WholeLineLength = 20;

	private const int MinimumContinuation = 8;

	private const int CommonPrefixLength = 4;

	/// <summary>How far short of a token a run can be and still take any tail. Further off, it may not be a token at all.</summary>
	private const int MostMissing = 8;

	private static ReadOnlySpan<int> WrapColumns => [64, 72, 76, 78, 80, 90, 100, 120, 132];

	/// <summary>
	/// A run starting with one of these is a token at any column, so a line ending in one is cut however wide the terminal was. The
	/// length is the shortest its rule matches: below it the run is not a secret yet, and only a join can make it one.
	/// </summary>
	private static readonly (string Prefix, int Shortest)[] s_tokenPrefixes =
	[
		("AKIA", 20), ("ASIA", 20), ("ghp_", 24), ("gho_", 24), ("ghu_", 24), ("ghs_", 24), ("ghr_", 24), ("github_pat_", 31), ("sk-", 23), ("xox", 15), ("xapp-", 17),
		("eyJ", 18), ("glpat-", 26), ("gldt-", 25), ("glrt-", 25), ("glptt-", 26), ("glsoat-", 27), ("glcbt-", 26), ("gloas-", 26), ("glagent-", 28), ("glimt-", 26), ("AIza", 39), ("ya29.", 24), ("hf_", 33), ("npm_", 40), ("hvs.", 24), ("SG.", 37), ("dop_v1_", 39), ("vcp_", 28), ("sbp_", 36), ("pypi-", 37),
		("dckr_pat_", 29), ("ATATT3", 38), ("ATCTT3", 38), ("gsk_", 36), ("xai-", 36), ("lsv2_", 37), ("ops_", 36), ("plane_api_", 34), ("lin_api_", 40), ("mem_", 23), ("sk_live_", 24),
		("rk_live_", 24), ("sntrys_", 39), ("glsa_", 37),
	];

	/// <summary>
	/// A piece of ordinary text: a number, or a word in any usual case with at most a short number after it, such as The, 2024, README,
	/// JavaScript, OpenAI, HTTP2 or x86. Random text of any length rarely fits, since it mixes digits, capitals and lower case within a piece.
	/// </summary>
	private static readonly Regex s_ordinaryPiece = new(
		@"^(?:[0-9]{1,8}|(?:[A-Z]?[a-z]+|[A-Z]+|[a-z]*(?:[A-Z][a-z]{2,}|[A-Z]{2,})+)[0-9]{0,4})$",
		RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

	private static ReadOnlySpan<char> PieceSeparators => "._-+/=";

	/// <summary>The prefixes this knows, so a test can cover each.</summary>
	internal static IEnumerable<string> TokenPrefixes => s_tokenPrefixes.Select(static entry => entry.Prefix);

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
		int lineStart = LineStartBefore(input, breakStart);
		if (breakStart == lineStart)
		{
			return 0;
		}

		int run = TrailingRunLength(input, lineStart, breakStart);
		if (run > 0 && StartsWithTokenPrefix(input, breakStart - run))
		{
			return PrefixedJoinLength(input, breakStart, run);
		}

		int indent = SkipIndent(input, SkipBreak(input, breakStart));
		if (indent >= input.Length || !IsTokenChar(input[indent]))
		{
			return 0;
		}

		return IsWrap(input, lineStart, breakStart, indent) ? indent - breakStart : 0;
	}

	/// <summary>
	/// A run that opens with a token prefix is a token however short the piece before the break, and the break may be a blank
	/// line or a message boundary, which is how a token split across content blocks or messages looks. The next piece must still
	/// look like a tail. A run already long enough to be a whole token needs a tail that cannot be ordinary text, so it never
	/// swallows the word after it. A shorter run is no secret without its tail, so that is not asked of it, and one just short of
	/// a token takes whatever completes it, or the whole token would stay.
	/// </summary>
	private static int PrefixedJoinLength(string input, int breakStart, int run)
	{
		int next = SkipBreaksAndBoundaries(input, breakStart);
		if (next >= input.Length || !IsTokenChar(input[next]) || StartsAlike(input, breakStart - run, next))
		{
			return 0;
		}

		int tail = LeadingRunLength(input, next);
		int shortest = ShortestToken(input, breakStart - run);
		bool joins = IsWholeToken(input, breakStart - run, run, shortest)
			? CannotBeOrdinaryText(input, next, tail)
			: LooksRandom(input, next, tail) || IsAwsTail(input, breakStart - run, next, tail) || CompletesToken(run, tail, shortest);
		return joins ? next - breakStart : 0;
	}

	/// <summary>An AWS key is only capitals and digits, so half of one can be all capitals. Its fixed length means a word cannot be swallowed by accident.</summary>
	private static bool IsAwsTail(string input, int runStart, int next, int tail)
	{
		return input.AsSpan(runStart, 4) is "AKIA" or "ASIA"
			&& input.AsSpan(next, tail).IndexOfAnyExceptInRange('A', 'Z') < 0;
	}

	private static int SkipBreaksAndBoundaries(string input, int position)
	{
		while (position < input.Length && (input[position] is '\n' or '\r' or ' ' or '\t' || (input[position] == Redactor.MessageBoundary && DetectionView.IsSeparatorBoundary(input, position))))
		{
			position++;
		}

		return position;
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
		bool fixedColumn = IsWrapColumn(lineEnd - lineStart) || StartsWithTokenPrefix(input, lineEnd - run);
		bool wholeLine = run == lineEnd - lineStart && run >= WholeLineLength && nextRun >= MinimumContinuation;
		return (fixedColumn || wholeLine) && CannotBeOrdinaryText(input, nextStart, nextRun);
	}

	/// <summary>A run is a whole token once it is as long as its rule's shortest match. A JWT also needs the dot before its payload.</summary>
	private static bool IsWholeToken(string input, int runStart, int run, int shortest)
	{
		return run >= shortest && (input[runStart] != 'e' || input.AsSpan(runStart, run).Contains('.'));
	}

	private static int ShortestToken(string input, int runStart)
	{
		foreach ((string prefix, int shortest) in s_tokenPrefixes)
		{
			if (string.CompareOrdinal(input, runStart, prefix, 0, prefix.Length) == 0)
			{
				return shortest;
			}
		}

		return 0;
	}

	private static bool CompletesToken(int run, int tail, int shortest)
	{
		return shortest - run <= MostMissing && run + tail >= shortest;
	}

	/// <summary>True when some piece of the run, between its separators, is not a word or a number. README.md, 2026-01-15 and src/App2.tsx all fail this.</summary>
	private static bool CannotBeOrdinaryText(string input, int start, int length)
	{
		ReadOnlySpan<char> rest = input.AsSpan(start, length);
		while (!rest.IsEmpty)
		{
			int end = rest.IndexOfAny(PieceSeparators);
			ReadOnlySpan<char> piece = end < 0 ? rest : rest[..end];
			if (!piece.IsEmpty && !s_ordinaryPiece.IsMatch(piece))
			{
				return true;
			}

			rest = end < 0 ? [] : rest[(end + 1)..];
		}

		return false;
	}

	private static bool StartsWithTokenPrefix(string input, int runStart)
	{
		return ShortestToken(input, runStart) > 0;
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

	/// <summary>For a run too short to be a secret alone: an all-one-case word on the next line is the next line, and anything else could be its tail.</summary>
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
