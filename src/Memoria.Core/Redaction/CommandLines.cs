namespace Memoria.Core.Redaction;

/// <summary>
/// Reads the shell command a flag belongs to, looking back over a bounded window and reading only as many
/// words as it needs, so judging stays linear however many flags a line holds. Lines ending in a backslash
/// a PowerShell backtick, or cmd's caret continue the command.
/// </summary>
internal static class CommandLines
{
	private const int Window = 1024;

	private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal) { "sudo", "doas", "env", "time", "exec", "nohup", "command", "$", "#", ">", "PS>", "%", "\\", "`", "^" };

	private static readonly System.Buffers.SearchValues<char> SeparatorChars = System.Buffers.SearchValues.Create(";|(&`\"\n");

	private static readonly HashSet<string> Containers = new(StringComparer.Ordinal) { "docker", "podman", "kubectl", "nerdctl" };

	/// <summary>True when the command holding the position is one of <paramref name="names"/>, directly or run inside a container with <c>exec</c>, or quoted mid-sentence.</summary>
	public static bool IsCommand(string input, int position, params string[] names)
	{
		return Place(input, position, names) != CommandPlace.None;
	}

	/// <summary>Where the command holding the position stands, when it is one of <paramref name="names"/>.</summary>
	public static CommandPlace Place(string input, int position, params string[] names)
	{
		if (!MentionsAny(input, position, names))
		{
			return CommandPlace.None;
		}

		int start = SegmentStart(input, position);
		int cursor = start;
		string? command = NextCommandWord(input, ref cursor, position);
		if (command is null)
		{
			return CommandPlace.None;
		}

		string name = BaseName(command);
		if (names.Contains(name, StringComparer.Ordinal) || (Containers.Contains(name) && RunsInContainer(input, cursor, position, names)))
		{
			return CommandPlace.SegmentStart;
		}

		return EndsInArguments(input, start, position, names) ? CommandPlace.MidSentence : CommandPlace.None;
	}

	/// <summary>
	/// A command quoted mid-sentence, as in <c>then I ran curl -u …</c> or <c>ssh box mysql -u app …</c>: one of the names
	/// is a word of the segment, and every word from it to the position is an argument of it. An argument is a flag, a URL, or the
	/// word straight after a flag, so <c>after mysql finished I ran tar -pxf</c> is not a MySQL command. A name that is itself a
	/// flag's value, as in <c>journalctl -u mysql</c> or <c>--name mongo</c>, names a service, not the command. Read back from the
	/// position over at most <see cref="MaxArguments"/> words, so a line of many flags costs no more than a short one.
	/// </summary>
	private static bool EndsInArguments(string input, int start, int position, string[] names)
	{
		int cursor = position;
		string? word = PreviousWord(input, ref cursor, start);
		for (int read = 0; word is not null && read < MaxArguments; read++)
		{
			string? before = PreviousWord(input, ref cursor, start);
			bool afterFlag = before is not null && IsFlag(before) && !before.Contains('=', StringComparison.Ordinal);
			if (!afterFlag && names.Contains(BaseName(word), StringComparer.Ordinal))
			{
				return true;
			}

			if (!IsFlag(word) && !afterFlag && !word.Contains("://", StringComparison.Ordinal))
			{
				return false;
			}

			word = before;
		}

		return false;
	}

	/// <summary>More arguments than this between a command and its password flag is not a command quoted in a sentence.</summary>
	private const int MaxArguments = 24;

	private static bool IsFlag(string word)
	{
		return word.Length > 1 && word[0] == '-';
	}

	private static string? PreviousWord(string input, ref int cursor, int start)
	{
		while (cursor > start && char.IsWhiteSpace(input[cursor - 1]))
		{
			cursor--;
		}

		int end = cursor;
		while (cursor > start && !char.IsWhiteSpace(input[cursor - 1]))
		{
			cursor--;
		}

		return end > cursor ? input[cursor..end] : null;
	}

	/// <summary>Most flags are not on such a command at all, and this costs far less than finding the command.</summary>
	private static bool MentionsAny(string input, int position, string[] names)
	{
		ReadOnlySpan<char> window = input.AsSpan(Math.Max(0, position - Window), Math.Min(position, Window));
		foreach (string name in names)
		{
			if (window.Contains(name, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The first word that is not a prompt, a wrapper such as <c>sudo</c>, or a leading <c>NAME=value</c> assignment.</summary>
	private static string? NextCommandWord(string input, ref int cursor, int end)
	{
		while (NextWord(input, ref cursor, end) is { } word)
		{
			if (!Wrappers.Contains(word) && !IsEnvAssignment(word))
			{
				return word;
			}
		}

		return null;
	}

	private static bool RunsInContainer(string input, int cursor, int end, string[] names)
	{
		bool sawExec = false;
		int position = cursor;
		while (NextWord(input, ref position, end) is { } word)
		{
			sawExec |= word == "exec";
			if (sawExec && names.Contains(BaseName(word), StringComparer.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	private static string? NextWord(string input, ref int cursor, int end)
	{
		while (cursor < end && char.IsWhiteSpace(input[cursor]))
		{
			cursor++;
		}

		int start = cursor;
		while (cursor < end && !char.IsWhiteSpace(input[cursor]))
		{
			cursor++;
		}

		return cursor > start ? input[start..cursor] : null;
	}

	/// <summary>Walks back to the start of the command: a line start that is not a continuation, or a shell separator.</summary>
	private static int SegmentStart(string input, int position)
	{
		int limit = Math.Max(0, position - Window);
		ReadOnlySpan<char> window = input.AsSpan(limit, position - limit);
		int found = window.LastIndexOfAny(SeparatorChars);
		while (found >= 0)
		{
			if (IsSeparator(input, limit + found))
			{
				return limit + found + 1;
			}

			found = window[..found].LastIndexOfAny(SeparatorChars);
		}

		return limit;
	}

	private static bool IsSeparator(string input, int i)
	{
		char c = input[i];
		if (c is ';' or '|' or '(' or '&' or '"')
		{
			return true;
		}

		return c == '`' ? !IsLineEnd(input, i + 1) : c == '\n' && !IsContinuedLine(input, i);
	}

	private static bool IsLineEnd(string input, int i)
	{
		return i < input.Length && input[i] is '\n' or '\r';
	}

	private static bool IsContinuedLine(string input, int newline)
	{
		int before = newline - 1;
		if (before >= 0 && input[before] == '\r')
		{
			before--;
		}

		return before >= 0 && input[before] is '\\' or '`' or '^';
	}

	private static bool IsEnvAssignment(string word)
	{
		int equals = word.IndexOf('=');
		return equals > 0 && word.AsSpan(0, equals).IndexOfAnyExcept(EnvNameChars) < 0;
	}

	private static readonly System.Buffers.SearchValues<char> EnvNameChars =
		System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_");

	private static string BaseName(string word)
	{
		int slash = word.LastIndexOfAny(['/', '\\']);
		string name = slash >= 0 ? word[(slash + 1)..] : word;
		return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
	}
}

/// <summary>Where a known command stands on its line, which says how much its arguments are to be trusted.</summary>
internal enum CommandPlace
{
	/// <summary>Not that command.</summary>
	None,

	/// <summary>The first word of a shell segment, after any prompt or wrapper, or run inside a container.</summary>
	SegmentStart,

	/// <summary>After other words, as a command quoted in a sentence. A flag's value there could as well be a word of prose.</summary>
	MidSentence,
}
