namespace Memoria.Core.Redaction;

/// <summary>
/// Reads the shell command a flag belongs to, looking back over a bounded window so judging stays linear
/// however long the line is. Lines ending in a backslash or a PowerShell backtick continue the command.
/// </summary>
internal static class CommandLines
{
	private const int Window = 4096;

	private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal) { "sudo", "doas", "env", "time", "exec", "nohup", "command", "$", "#", ">", "PS>", "%" };

	private static readonly HashSet<string> Containers = new(StringComparer.Ordinal) { "docker", "podman", "kubectl", "nerdctl" };

	/// <summary>True when the command holding the position is one of <paramref name="names"/>, directly or run inside a container with <c>exec</c>.</summary>
	public static bool IsCommand(string input, int position, params string[] names)
	{
		// Most flags are not on such a command at all, and this check costs far less than reading the words.
		ReadOnlySpan<char> window = input.AsSpan(Math.Max(0, position - Window), Math.Min(position, Window));
		bool mentioned = false;
		foreach (string name in names)
		{
			mentioned |= window.Contains(name, StringComparison.Ordinal);
		}

		if (!mentioned)
		{
			return false;
		}

		List<string> words = WordsBefore(input, position);
		int first = FirstCommandWord(words);
		if (first >= words.Count)
		{
			return false;
		}

		string command = BaseName(words[first]);
		if (names.Contains(command, StringComparer.Ordinal))
		{
			return true;
		}

		return Containers.Contains(command) && words.Contains("exec") && words.Skip(first + 1).Any(word => names.Contains(BaseName(word), StringComparer.Ordinal));
	}

	private static List<string> WordsBefore(string input, int position)
	{
		int start = SegmentStart(input, position);
		string text = input[start..position].Replace("\\\r\n", " ", StringComparison.Ordinal).Replace("\\\n", " ", StringComparison.Ordinal);
		text = text.Replace("`\r\n", " ", StringComparison.Ordinal).Replace("`\n", " ", StringComparison.Ordinal);
		return [.. text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)];
	}

	/// <summary>Walks back to the start of the command: a line start that is not a continuation, or a shell separator.</summary>
	private static int SegmentStart(string input, int position)
	{
		int limit = Math.Max(0, position - Window);
		for (int i = position - 1; i >= limit; i--)
		{
			if (IsSeparator(input, i))
			{
				return i + 1;
			}
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

	/// <summary>Skips a prompt, wrappers such as <c>sudo</c>, and leading <c>NAME=value</c> assignments.</summary>
	private static int FirstCommandWord(List<string> words)
	{
		int i = 0;
		while (i < words.Count && (Wrappers.Contains(words[i]) || IsEnvAssignment(words[i])))
		{
			i++;
		}

		return i;
	}

	private static bool IsEnvAssignment(string word)
	{
		int equals = word.IndexOf('=');
		return equals > 0 && word[..equals].All(static c => char.IsAsciiLetterOrDigit(c) || c == '_');
	}

	private static string BaseName(string word)
	{
		int slash = word.LastIndexOfAny(['/', '\\']);
		string name = slash >= 0 ? word[(slash + 1)..] : word;
		return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
	}
}
