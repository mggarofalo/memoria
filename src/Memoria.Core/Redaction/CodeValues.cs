namespace Memoria.Core.Redaction;

/// <summary>
/// Values that are code or markup around a secret-sounding name, not a secret. Each shape came from source and docs a session
/// quotes, where redacting it destroyed text and protected nothing: a version range in a lockfile, a template literal, a
/// collection literal, a call cut at the end of its line, and the doc-comment markup glued to a value.
/// </summary>
internal static class CodeValues
{
	private const int LongestTagName = 8;

	/// <summary>
	/// The value without doc-comment or HTML markup glued to it: <c>&lt;c&gt;</c> before, and everything from a closing tag
	/// such as <c>&lt;/c&gt;</c> on. In <c>&lt;c&gt;password: admin&lt;/c&gt;</c> the value is <c>admin</c>.
	/// </summary>
	public static string WithoutMarkup(string value)
	{
		string text = value;
		if (OpeningTagLength(text) is int open and > 0)
		{
			text = text[open..];
		}

		int close = text.IndexOf("</", StringComparison.Ordinal);
		return close >= 0 && close + 2 < text.Length && char.IsAsciiLetter(text[close + 2]) ? text[..close] : text;
	}

	/// <summary>True for a value that only code or a version could be. <paramref name="unquoted"/> allows the shapes a quoted string can also hold.</summary>
	public static bool IsCode(string value, bool unquoted)
	{
		return IsVersionRange(value)
			|| HasInterpolation(value)
			|| (unquoted && (IsCollectionLiteral(value) || IsCallCutAtLineEnd(value) || IsRegexLiteral(value)));
	}

	/// <summary><c>^5.2.5</c>, <c>~1.2</c>, <c>&gt;=2.0.1</c>: a package version range, as in <c>"@inquirer/password": "^5.2.5"</c>.</summary>
	public static bool IsVersionRange(string value)
	{
		ReadOnlySpan<char> text = value.AsSpan().TrimStart("^~>=<v");
		int dash = text.IndexOf('-');
		ReadOnlySpan<char> numbers = dash < 0 ? text : text[..dash];
		return numbers.Length > 2 && char.IsAsciiDigit(numbers[0]) && numbers.Contains('.') && numbers.IndexOfAnyExcept("0123456789.x*") < 0;
	}

	/// <summary>
	/// A template literal or a command substitution: <c>${name}</c>, <c>${"0".repeat(16)}</c>, <c>$(cat file)</c>. What is
	/// stored is the code that builds the value, never the value. It must close, or run into a call or a member, as
	/// <c>${randomBytes(32).toString(</c> does where a quote cut the value: <c>${Secret123</c> with nothing after is a password.
	/// </summary>
	public static bool HasInterpolation(string value)
	{
		for (int i = value.IndexOf('$'); i >= 0 && i + 2 < value.Length; i = value.IndexOf('$', i + 1))
		{
			if (value[i + 1] is '{' or '(' && IsExpression(value, i + 2, value[i + 1] == '{' ? '}' : ')'))
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsExpression(string value, int start, char close)
	{
		if (!char.IsAsciiLetter(value[start]) && value[start] is not ('_' or '"' or '\'' or '('))
		{
			return false;
		}

		int name = start;
		while (name < value.Length && (char.IsAsciiLetterOrDigit(value[name]) || value[name] is '_' or '#'))
		{
			name++;
		}

		return value.IndexOf(close, start) > start || (name > start && ContinuesAsCode(value, name));
	}

	/// <summary>A call, a member, or a shell expansion such as <c>${NAME:?message}</c> or <c>${NAME:-default}</c> after a name.</summary>
	private static bool ContinuesAsCode(string value, int at)
	{
		if (at >= value.Length)
		{
			return false;
		}

		return value[at] is '(' or '.' or '?' || (value[at] == ':' && at + 1 < value.Length && value[at + 1] is '?' or '-' or '=' or '+');
	}

	/// <summary><c>/^mem_[0-9a-f]{16}$/</c>: a JavaScript regex literal, which describes a format and holds none.</summary>
	public static bool IsRegexLiteral(string value)
	{
		int close = value.LastIndexOf('/');
		return value.Length > 3 && value[0] == '/' && value[1] is '^' or '(' or '[' or '\\' && close > 1
			&& value.AsSpan(close + 1).IndexOfAnyExcept("dgimsuyv") < 0;
	}

	/// <summary><c>["password", "passwd"]</c> and <c>{ token: x }</c>: an array or object literal. A password does not open with a bracket and a quote.</summary>
	public static bool IsCollectionLiteral(string value)
	{
		return value.Length > 1 && value[0] is '[' or '{' && value[1] is '"' or '\'' or ' ' or ']' or '}' or '\n';
	}

	/// <summary><c>new(</c> and <c>Build(</c>: a call whose arguments start on the next line.</summary>
	public static bool IsCallCutAtLineEnd(string value)
	{
		if (value.Length < 2 || value[^1] != '(')
		{
			return false;
		}

		ReadOnlySpan<char> name = value.AsSpan(0, value.Length - 1);
		return char.IsAsciiLetter(name[0]) && name.IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.") < 0;
	}

	/// <summary>
	/// True when a plain word is followed by a command-line flag, as in <c>$env:X = node -e "..."</c>, where the value is a command
	/// that prints the secret.
	/// </summary>
	public static bool IsCommandWord(string input, string value, int end)
	{
		return value.Length > 1 && value.All(char.IsAsciiLetterLower)
			&& end + 2 < input.Length && input[end] == ' ' && input[end + 1] == '-' && char.IsAsciiLetter(input[end + 2]);
	}

	private static int OpeningTagLength(string text)
	{
		if (text.Length < 3 || text[0] != '<')
		{
			return 0;
		}

		int close = text.IndexOf('>', 1, Math.Min(LongestTagName + 1, text.Length - 1));
		return close > 1 && text.AsSpan(1, close - 1).IndexOfAnyExceptInRange('a', 'z') < 0 ? close + 1 : 0;
	}
}
