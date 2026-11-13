namespace Memoria.Core.Redaction;

/// <summary>
/// How far a value after a credential's name or flag runs, and whether it is a secret. The pattern ends an unquoted value at a
/// space, a quote or a bracket. A few shapes run on: a list, a braced value or a command substitution goes to its closer on the
/// line, an <c>Authorization</c> header's value goes to the end of its line, whatever scheme leads it, and a value in a
/// <c>;</c>-separated connection string goes to the next <c>;</c>, spaces and all.
/// </summary>
internal static class AssignedValues
{
	/// <summary>How far a balanced value is looked through. A real one is short, and a line of nested code is not a value.</summary>
	private const int MaxBalanced = 256;

	/// <summary>The end of the value the candidate starts.</summary>
	public static int End(SecretCandidate candidate)
	{
		string input = candidate.Input;
		int start = candidate.Secret.Index;
		if (IsQuoted(input, start))
		{
			return candidate.End;
		}

		if (Opener(input, start) is int open)
		{
			int close = CloserOf(input, open);
			return close > candidate.End ? close : candidate.End;
		}

		bool header = Last(SecretKeyNames.SplitWords(candidate.Match.Groups["key"].Value)).Equals("authorization", StringComparison.OrdinalIgnoreCase);
		return header || InConnectionString(input, candidate.Match.Groups["key"].Index) ? RunEnd(input, candidate.End, header) : candidate.End;
	}

	/// <summary>True when the value, as far as <see cref="End"/> runs it, is a secret.</summary>
	public static bool IsSecret(SecretCandidate candidate)
	{
		string input = candidate.Input;
		int start = candidate.Secret.Index;
		string value = input[start..End(candidate)];
		if (value.StartsWith("$(", StringComparison.Ordinal))
		{
			return PrintedLiteral(value) is { } printed && PlainValues.IsPassword(printed);
		}

		if (!IsQuoted(input, start) && Opener(input, start) is not null)
		{
			return value[1..].TrimEnd(']', '}').Split(',').Any(static item => PlainValues.IsReplaceable(item.Trim().Trim('"', '\'', '`')));
		}

		bool singleQuoted = start > 0 && input[start - 1] == '\'';
		return PlainValues.IsReplaceable(value, shellExpands: !singleQuoted);
	}

	/// <summary>What <c>$(echo x)</c> or <c>$(printf 'x')</c> prints, which is a literal. Null for any other command, whose output is not in the text.</summary>
	private static string? PrintedLiteral(string value)
	{
		string inner = value[2..].TrimEnd(')').Trim();
		foreach (string command in (string[])["echo ", "printf "])
		{
			if (inner.StartsWith(command, StringComparison.Ordinal))
			{
				return inner[command.Length..].Trim().Trim('"', '\'');
			}
		}

		return null;
	}

	private static bool IsQuoted(string input, int start)
	{
		return start > 0 && input[start - 1] is '"' or '\'' or '`';
	}

	/// <summary>The index of the bracket a value opens with: <c>[</c>, <c>{</c> but not a template's <c>{{</c>, or the <c>(</c> of <c>$(</c>.</summary>
	private static int? Opener(string input, int start)
	{
		char first = input[start];
		bool next = start + 1 < input.Length;
		if (string.CompareOrdinal(input, start, "[REDACTED:", 0, 10) == 0)
		{
			return null;
		}

		if (first == '$' && next && input[start + 1] == '(')
		{
			return start + 1;
		}

		return first is '[' or '{' && !(next && input[start + 1] == '{') ? start : null;
	}

	/// <summary>The index just past the closer that matches the opener, skipping quoted text, or -1 when the line or the window ends first.</summary>
	private static int CloserOf(string input, int open)
	{
		char opener = input[open];
		char closer = opener switch { '[' => ']', '{' => '}', _ => ')' };
		int depth = 0;
		char quote = '\0';
		int limit = Math.Min(input.Length, open + MaxBalanced);
		for (int i = open; i < limit && input[i] is not ('\n' or '\r'); i++)
		{
			char c = input[i];
			if (quote != '\0')
			{
				quote = c == quote ? '\0' : quote;
				continue;
			}

			quote = c is '"' or '\'' or '`' ? c : '\0';
			depth += c == opener ? 1 : c == closer ? -1 : 0;
			if (depth == 0)
			{
				return i + 1;
			}
		}

		return -1;
	}

	/// <summary>A name straight after a <c>;</c>, as in <c>Server=db;Password=x y;</c>.</summary>
	private static bool InConnectionString(string input, int keyStart)
	{
		int i = keyStart - 1;
		while (i >= 0 && input[i] is ' ' or '\t')
		{
			i--;
		}

		return i >= 0 && input[i] == ';';
	}

	/// <summary>
	/// The end of the run of text from <paramref name="end"/>: the line's end, a quote, an escaped line break, and in a connection
	/// string the next <c>;</c>. Trailing spaces stay.
	/// </summary>
	private static int RunEnd(string input, int end, bool header)
	{
		int i = end;
		while (i < input.Length && !EndsRun(input, i, header))
		{
			i++;
		}

		while (i > end && input[i - 1] is ' ' or '\t')
		{
			i--;
		}

		return i;
	}

	private static bool EndsRun(string input, int i, bool header)
	{
		char c = input[i];
		return c is '\n' or '\r' or '"' or '\'' or '`' || (c == ';' && !header) || IsEscapedBreak(input, i);
	}

	private static bool IsEscapedBreak(string input, int i)
	{
		return input[i] == '\\' && i + 1 < input.Length && input[i + 1] is 'n' or 'r';
	}

	private static string Last(List<string> words)
	{
		return words.Count == 0 ? "" : words[^1];
	}
}
