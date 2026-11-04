namespace Memoria.Core.Redaction;

/// <summary>
/// Reads the shell command a flag belongs to, looking back over a bounded window and reading only as many
/// words as it needs, so judging stays linear however many flags a line holds. Lines ending in a backslash
/// or a PowerShell backtick continue the command.
/// </summary>
internal static class CommandLines
{
	private const int Window = 1024;

	private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal) { "sudo", "doas", "env", "time", "exec", "nohup", "command", "$", "#", ">", "PS>", "%", "\\", "`" };

	private static readonly System.Buffers.SearchValues<char> SeparatorChars = System.Buffers.SearchValues.Create(";|(&`\"\n");

	private static readonly HashSet<string> Containers = new(StringComparer.Ordinal) { "docker", "podman", "kubectl", "nerdctl" };

	/// <summary>True when the command holding the position is one of <paramref name="names"/>, directly or run inside a container with <c>exec</c>.</summary>
	public static bool IsCommand(string input, int position, params string[] names)
	{
		if (!MentionsAny(input, position, names))
		{
			return false;
		}

		int start = SegmentStart(input, position);
		int cursor = start;
		string? command = NextCommandWord(input, ref cursor, position);
		if (command is null)
		{
			return false;
		}

		string name = BaseName(command);
		if (names.Contains(name, StringComparer.Ordinal))
		{
			return true;
		}

		return Containers.Contains(name) && RunsInContainer(input, cursor, position, names);
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

		return before >= 0 && input[before] is '\\' or '`';
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
