using System.Text;
using System.Text.RegularExpressions;

namespace Memoria.Core.Redaction;

/// <summary>
/// Values that are code or markup around a secret-sounding name, not a secret. Each shape came from source and docs a session
/// quotes, where redacting it destroyed text and protected nothing: a version range in a lockfile, a template literal, a
/// collection literal, a call cut at the end of its line, and the doc-comment markup glued to a value. Each is narrow on
/// purpose: code that holds a literal secret beside it, as in <c>${PREFIX}Tr0ub4dor-x9Q</c>, is still a secret.
/// </summary>
internal static partial class CodeValues
{
	private const int LongestTagName = 8;

	private const int SecretLikePiece = 6;

	private const int MinWord = 4;

	/// <summary>Commands whose output is the value, as in <c>$env:X = node -e "..."</c>.</summary>
	private static readonly HashSet<string> s_commands = new(StringComparer.Ordinal)
	{
		"node", "python", "python3", "openssl", "head", "cat", "printf", "echo", "base64", "pwgen", "uuidgen", "op", "pass", "gopass", "vault", "aws", "gcloud", "az", "kubectl", "security", "curl", "jq",
	};

	/// <summary>
	/// The value without doc-comment or HTML markup glued to it: a short <c>&lt;c&gt;</c> tag before, and a closing tag such as
	/// <c>&lt;/c&gt;</c> after. In <c>&lt;c&gt;password: admin&lt;/c&gt;</c> the value is <c>admin</c>. <c>Tr0ub4dor&lt;/x9Q</c> holds no tag.
	/// </summary>
	public static string WithoutMarkup(string value)
	{
		// A value that starts by closing a tag, as in <c>sshpass -p</c>, held nothing before the markup.
		if (ClosingTagAtStart().IsMatch(value))
		{
			return string.Empty;
		}

		string text = value;
		if (OpeningTagLength(text) is int open and > 0)
		{
			text = text[open..];
		}

		Match close = ClosingTagAtEnd().Match(text);
		return close.Success ? text[..close.Index] : text;
	}

	/// <summary>True for a value that only code or a version range could be. <paramref name="unquoted"/> allows the shapes a quoted string can also hold.</summary>
	public static bool IsCode(string value, bool unquoted)
	{
		return IsVersionRange(value)
			|| IsInterpolationOnly(value)
			|| (unquoted && (IsCollectionLiteral(value) || IsCallCutAtLineEnd(value) || IsRegexLiteral(value)));
	}

	/// <summary>
	/// <c>^5.2.5</c>, <c>~1.2</c>, <c>&gt;=2.0.1-beta.1</c>: a package version range, as in <c>"@inquirer/password": "^5.2.5"</c>. It
	/// needs the range operator, because <c>2024.11-Winter!</c> and <c>10.20.30</c> can be passwords, and a pre-release suffix of words.
	/// </summary>
	public static bool IsVersionRange(string value)
	{
		return VersionRange().IsMatch(value);
	}

	/// <summary>
	/// A value built only from template literals or command substitutions, with nothing that reads as a secret beside them:
	/// <c>${name}</c>, <c>mem_${"0".repeat(16)}</c>, <c>$(xml "$file")</c>, or <c>${randomBytes(32).toString(</c> where a quote cut it.
	/// A span that only wraps a quoted literal, as <c>${"a8F3…"}</c>, is the literal.
	/// </summary>
	/// <remarks>
	/// Beside a span, any word of four letters or digits is taken for the secret, as <c>Summerpassword</c> in
	/// <c>${PREFIX}Summerpassword</c>. A secret-sounding name with a literal beside its span is rarely clean code. Markup tags and
	/// upper-case names such as <c>MEMORIA_WORKER_TOKEN_FILE</c> are not words here, because they name things.
	/// </remarks>
	public static bool IsInterpolationOnly(string value)
	{
		return Remainder(value) is string rest && !HoldsSecretLikePiece(rest) && !HoldsWord(rest);
	}

	/// <summary>
	/// The value with each span replaced by the data it carries, or null when it has no span or a span is only a literal. The data is a
	/// shell default after :- or :=, and the quoted literals a command substitution prints. A literal passed to a call in a template,
	/// as in toString("base64url"), is an argument. Built in one pass, so many spans cost no more than one long one.
	/// </summary>
	private static string? Remainder(string value)
	{
		StringBuilder rest = new();
		int from = 0;
		while (NextSpan(value, from) is (int start, int end))
		{
			string body = value[(start + 2)..Math.Min(value.Length, Math.Max(start + 2, end - 1))];
			if (IsBareLiteral(body))
			{
				return null;
			}

			rest.Append(value, from, start - from).Append(' ').Append(DataIn(body, value[start + 1] == '(')).Append(' ');
			from = Math.Min(end, value.Length);
		}

		// Every span appends a space, so an empty builder means there was none.
		return rest.Length == 0 ? null : rest.Append(value, from, value.Length - from).ToString();
	}

	private static bool HoldsWord(string text)
	{
		string words = Tag().Replace(text, " ");
		foreach (ValueMatch piece in Words().EnumerateMatches(words))
		{
			ReadOnlySpan<char> word = words.AsSpan(piece.Index, piece.Length);
			bool name = word.Contains('_') && word.IndexOfAnyInRange('a', 'z') < 0;
			if (word.Length >= MinWord && !name)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>True when the value holds a span whose code is visible, closed or not. For rules that judge the value as a whole token.</summary>
	public static bool HasInterpolation(string value)
	{
		return IsInterpolationOnly(value);
	}

	/// <summary><c>["password", "passwd"]</c> and <c>{ token: x }</c>: an array or object literal of names, with no secret-like element.</summary>
	public static bool IsCollectionLiteral(string value)
	{
		return value.Length > 1 && value[0] is '[' or '{' && value[1] is '"' or '\'' or ' ' or ']' or '}' or '\n' && !HoldsSecretLikePiece(value);
	}

	/// <summary><c>new(</c> and <c>Build(</c>: a call whose arguments start on the next line. A name with digits, as <c>Summer2024(</c>, is a password.</summary>
	public static bool IsCallCutAtLineEnd(string value)
	{
		if (value.Length < 2 || value[^1] != '(')
		{
			return false;
		}

		ReadOnlySpan<char> name = value.AsSpan(0, value.Length - 1);
		return char.IsAsciiLetter(name[0]) && name.IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_.") < 0;
	}

	/// <summary><c>/^mem_[0-9a-f]{16}$/</c>: a JavaScript regex literal, which describes a format. It must use regex syntax beyond an anchor.</summary>
	public static bool IsRegexLiteral(string value)
	{
		int close = value.LastIndexOf('/');
		return value.Length > 3 && value[0] == '/' && close > 1
			&& value.AsSpan(close + 1).IndexOfAnyExcept("dgimsuyv") < 0
			&& value.AsSpan(1, close - 1).IndexOfAny("[]{}()\\|*+?") >= 0;
	}

	/// <summary>A known command followed by a flag, as in <c>$env:X = node -e "..."</c>, where the value is the command that prints the secret.</summary>
	public static bool IsCommandWord(string input, string value, int end)
	{
		return s_commands.Contains(value)
			&& end + 2 < input.Length && input[end] == ' ' && input[end + 1] == '-' && char.IsAsciiLetter(input[end + 2]);
	}

	/// <summary>A piece of letters and digits mixed, at least six long, or one that reads as random: what a secret beside code looks like.</summary>
	private static bool HoldsSecretLikePiece(string text)
	{
		foreach (ValueMatch piece in Pieces().EnumerateMatches(text))
		{
			ReadOnlySpan<char> part = text.AsSpan(piece.Index, piece.Length);
			bool mixed = part.IndexOfAnyInRange('0', '9') >= 0 && part.IndexOfAnyExceptInRange('0', '9') >= 0;
			if ((mixed && part.Length >= SecretLikePiece) || ValueShapes.IsRandomSegment(part))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The next <c>${…}</c> or <c>$(…)</c> whose body is code, as start and end, closed or running to the end of the text.</summary>
	private static (int Start, int End)? NextSpan(string text, int from)
	{
		for (int i = text.IndexOf('$', from); i >= 0 && i + 2 < text.Length; i = text.IndexOf('$', i + 1))
		{
			char close = text[i + 1] switch { '{' => '}', '(' => ')', _ => '\0' };
			if (close != '\0' && StartsExpression(text, i + 2))
			{
				int end = CloseOf(text, i + 2, text[i + 1], close);
				return end > 0 ? (i, end) : RunsIntoCode(text, i + 2, text[i + 1] == '(') ? (i, text.Length + 1) : null;
			}
		}

		return null;
	}

	private static bool StartsExpression(string text, int start)
	{
		return char.IsAsciiLetter(text[start]) || text[start] is '_' or '"' or '\'' or '(' or '#';
	}

	/// <summary>The index just past the matching closer, counting nesting, or -1 when it never closes.</summary>
	private static int CloseOf(string text, int start, char open, char close)
	{
		int depth = 1;
		for (int i = start; i < text.Length; i++)
		{
			depth += text[i] == open ? 1 : text[i] == close ? -1 : 0;
			if (depth == 0)
			{
				return i + 1;
			}
		}

		return -1;
	}

	/// <summary>
	/// An unclosed span still reads as code when a name runs into a call or a member, a shell expansion such as <c>${NAME:-</c>, or,
	/// in a command substitution, a command's arguments, as in <c>$(xml "$file")</c> cut at its quote.
	/// </summary>
	private static bool RunsIntoCode(string text, int start, bool command)
	{
		int name = NameEnd(text, start);
		if (name == start)
		{
			return false;
		}

		if (command && IsCutCommand(text, start, name))
		{
			return true;
		}

		return name < text.Length
			&& (text[name] is '(' or '.' || (text[name] == ':' && name + 1 < text.Length && text[name + 1] is '?' or '-' or '=' or '+'));
	}

	private static int NameEnd(string text, int start)
	{
		int name = start;
		while (name < text.Length && (char.IsAsciiLetterOrDigit(text[name]) || text[name] is '_' or '#'))
		{
			name++;
		}

		return name;
	}

	/// <summary>
	/// A command name cut by a quote, as <c>$(xml </c> in <c>"$(xml "$file")"</c>: lower-case letters, then a space or the end. <c>$(Secret123</c>
	/// is not one. printf and echo print their argument, so a cut one may have cut off the secret itself.
	/// </summary>
	private static bool IsCutCommand(string text, int start, int name)
	{
		ReadOnlySpan<char> command = text.AsSpan(start, name - start);
		return (name == text.Length || text[name] == ' ')
			&& command.IndexOfAnyExceptInRange('a', 'z') < 0
			&& command is not ("printf" or "echo");
	}

	/// <summary>The data a span's code carries: <c>Tr0ub4dor</c> in <c>$(printf 'Tr0ub4dor')</c>, and <c>Pr0dP4ss</c> in <c>${DB_PASSWORD:-Pr0dP4ss}</c>.</summary>
	private static string DataIn(string body, bool command)
	{
		string literals = command ? string.Join(' ', QuotedLiteral().Matches(body).Select(static match => match.Groups["text"].Value)) : string.Empty;
		Match fallback = ShellDefault().Match(body);
		return fallback.Success ? literals + " " + fallback.Groups["text"].Value : literals;
	}

	/// <summary>The literal a template span only wraps, as <c>a8F3…</c> in <c>${"a8F3…"}</c>, or the value itself.</summary>
	public static string UnwrapLiteral(string value)
	{
		if (value.Length > 4 && value[0] == '$' && value[1] == '{' && value[^1] == '}' && IsBareLiteral(value[2..^1]))
		{
			string body = value[2..^1].Trim();
			return body[1..^1];
		}

		return value;
	}

	/// <summary>A body that is only a quoted string, as in <c>${"a8F3…"}</c>: the literal is the value.</summary>
	private static bool IsBareLiteral(string body)
	{
		string trimmed = body.Trim();
		return trimmed.Length >= 2 && trimmed[0] is '"' or '\'' && trimmed[^1] == trimmed[0] && trimmed.IndexOf(trimmed[0], 1) == trimmed.Length - 1;
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

	[GeneratedRegex(@"^(?:\^|~|[<>]=?|=)v?[0-9]+(?:\.(?:[0-9]+|x|\*)){1,3}(?:-[a-z]+(?:\.?[0-9]+)?)?$", RegexOptions.CultureInvariant)]
	private static partial Regex VersionRange();

	[GeneratedRegex(@"(?:</[a-z]{1,8}>?[,.;:)]*)+$", RegexOptions.CultureInvariant)]
	private static partial Regex ClosingTagAtEnd();

	[GeneratedRegex(@"^</[a-z]{1,8}>", RegexOptions.CultureInvariant)]
	private static partial Regex ClosingTagAtStart();

	[GeneratedRegex(@"""(?<text>[^""]*)""|'(?<text>[^']*)'", RegexOptions.CultureInvariant)]
	private static partial Regex QuotedLiteral();

	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*:?[-=+](?<text>.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
	private static partial Regex ShellDefault();

	[GeneratedRegex(@"[A-Za-z0-9]+", RegexOptions.CultureInvariant)]
	private static partial Regex Pieces();

	[GeneratedRegex(@"[A-Za-z0-9]+(?:_[A-Za-z0-9]+)*", RegexOptions.CultureInvariant)]
	private static partial Regex Words();

	[GeneratedRegex(@"</?[a-z]{1,8}>", RegexOptions.CultureInvariant)]
	private static partial Regex Tag();
}
