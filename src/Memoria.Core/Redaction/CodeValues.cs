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
	/// upper-case names such as <c>MEMORIA_WORKER_TOKEN_FILE</c> are not words here, because they name things. Words that are a path's
	/// segments, as in <c>memoria/${env}/api-key</c>, are a place, so they are code too; see <see cref="IsPathOfWords"/>.
	/// </remarks>
	public static bool IsInterpolationOnly(string value)
	{
		return Remainder(value) is string rest && !HoldsSecretLikePiece(rest) && (!HoldsWord(rest) || IsPathOfWords(rest));
	}

	/// <summary>
	/// A template's text, its spans taken out, that divides at <c>/</c> into segments of words, such as <c>memoria/ /api-key</c>: a path, a
	/// key's name in a vault, or a route. Each segment's parts, between <c>-</c>, <c>_</c>, <c>.</c> and <c>~</c>, must be words, maybe
	/// capitalised, with at most two digits after them, or a number of at most four, and at most 20 characters, so a random segment such
	/// as <c>kd8sj2nx…</c> or <c>Pr0dP4ss</c> beside a span is still taken for a secret.
	/// </summary>
	internal static bool IsPathOfWords(string rest)
	{
		if (!rest.Contains('/', StringComparison.Ordinal))
		{
			return false;
		}

		// One word after a slash, as in ${PREFIX}/Summerpassword, is no path: it takes two segments with words in them.
		string[] segments = rest.Split('/');
		if (segments.Count(static segment => segment.Trim().Length > 0) < 2)
		{
			return false;
		}

		foreach (string segment in segments)
		{
			foreach (string part in segment.Split(['-', '_', '.', '~', ' '], StringSplitOptions.RemoveEmptyEntries))
			{
				if (part.Length > 20 || !PathWord().IsMatch(part))
				{
					return false;
				}
			}
		}

		return true;
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

	/// <summary>
	/// The data a span's code carries: <c>Tr0ub4dor</c> in <c>$(printf 'Tr0ub4dor')</c> and in <c>$(echo Tr0ub4dor)</c>, <c>Pr0dP4ss</c> in
	/// <c>${DB_PASSWORD:-Pr0dP4ss}</c>, and <c>a8F3…</c> in <c>${prefix + "a8F3…"}</c>, where a template joins a literal to code.
	/// </summary>
	private static string DataIn(string body, bool command)
	{
		string literals = command ? PrintedText(body) : JoinedLiterals(body);
		Match fallback = ShellDefault().Match(body);
		return fallback.Success ? literals + " " + fallback.Groups["text"].Value : literals;
	}

	/// <summary>
	/// What a command substitution carries: its quoted literals. <c>printf</c> and <c>echo</c> print their words, bare or quoted, but not a
	/// flag, a format such as <c>%s</c> or a variable. Piped on, as in <c>$(echo $RANDOM | md5sum)</c>, what they print is input to code.
	/// </summary>
	private static string PrintedText(string body)
	{
		string unquoted = QuotedLiteral().Replace(body, " ");
		string[] words = unquoted.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (words.Length == 0 || !IsPrintCommand(words[0]))
		{
			return string.Join(' ', QuotedLiteral().Matches(body).Select(static match => match.Groups["text"].Value));
		}

		if (unquoted.Contains('|', StringComparison.Ordinal))
		{
			return string.Empty;
		}

		IEnumerable<string> quoted = QuotedLiteral().Matches(body).Select(static match => match.Groups["text"].Value);
		return string.Join(' ', quoted.Concat(words.Skip(1)).Where(static word => word.Length > 0 && word[0] != '-' && !word.AsSpan().ContainsAny('$', '%')));
	}

	/// <summary><c>printf</c> or <c>echo</c>, by name or by path, as <c>/bin/echo</c>.</summary>
	private static bool IsPrintCommand(string word)
	{
		string name = word[(word.LastIndexOf('/') + 1)..];
		return name is "printf" or "echo";
	}

	/// <summary>
	/// The literals of a value that is one template span joining them to code, as <c>a8F3…</c> in <c>${prefix + "a8F3…"}</c>, or null. Judged
	/// one by one, since the span's code around them has spaces in it and is not the secret.
	/// </summary>
	public static string[]? JoinedLiteralsOf(string value)
	{
		if (value.Length < 4 || !value.StartsWith("${", StringComparison.Ordinal) || CloseOf(value, 2, '{', '}') != value.Length)
		{
			return null;
		}

		string literals = JoinedLiterals(value[2..^1]);
		return literals.Length == 0 ? null : literals.Split(' ', StringSplitOptions.RemoveEmptyEntries);
	}

	/// <summary>A command substitution that prints a literal, as <c>$(printf 'x')</c> or <c>$(echo x)</c>. It holds that text, and refers to nothing.</summary>
	public static bool PrintsLiteral(string value)
	{
		string body = value.Length > 3 && value.StartsWith("$(", StringComparison.Ordinal) && value[^1] == ')' ? value[2..^1].Trim() : string.Empty;
		int space = body.IndexOf(' ');
		string printed = space > 0 && IsPrintCommand(body[..space]) ? PrintedText(body) : string.Empty;
		return HoldsSecretLikePiece(printed) || HoldsWord(printed);
	}

	/// <summary>The quoted literals a template joins with <c>+</c>. A literal passed to a call, as in <c>toString("base64url")</c>, is an argument.</summary>
	private static string JoinedLiterals(string body)
	{
		IEnumerable<string> joined = JoinableLiteral().Matches(body)
			.Where(match => body.AsSpan(0, match.Index).TrimEnd().EndsWith('+') || body.AsSpan(match.Index + match.Length).TrimStart().StartsWith('+'))
			.Select(static match => match.Groups["text"].Value);
		return string.Join(' ', joined);
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

	/// <summary>A path's word: letters, maybe capitalised, with at most two digits after them, or a number of at most four.</summary>
	[GeneratedRegex(@"^(?:[A-Za-z]?[a-z]+[0-9]{0,2}|[0-9]{1,4})$")]
	private static partial Regex PathWord();

	[GeneratedRegex(@"^(?:\^|~|[<>]=?|=)v?[0-9]+(?:\.(?:[0-9]+|x|\*)){1,3}(?:-[a-z]+(?:\.?[0-9]+)?)?$", RegexOptions.CultureInvariant)]
	private static partial Regex VersionRange();

	[GeneratedRegex(@"(?:</[a-z]{1,8}>?[,.;:)]*)+$", RegexOptions.CultureInvariant)]
	private static partial Regex ClosingTagAtEnd();

	[GeneratedRegex(@"^</[a-z]{1,8}>", RegexOptions.CultureInvariant)]
	private static partial Regex ClosingTagAtStart();

	[GeneratedRegex(@"""(?<text>[^""]*)""|'(?<text>[^']*)'", RegexOptions.CultureInvariant)]
	private static partial Regex QuotedLiteral();

	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?::?[-=+]|/{1,2}[^/]*/)(?<text>.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
	private static partial Regex ShellDefault();

	[GeneratedRegex(@"[A-Za-z0-9]+", RegexOptions.CultureInvariant)]
	private static partial Regex Pieces();

	/// <summary>A quoted literal in a template's code, where a backtick quotes too: <c>${prefix + `x`}</c>.</summary>
	[GeneratedRegex(@"""(?<text>[^""]*)""|'(?<text>[^']*)'|`(?<text>[^`$]*)`", RegexOptions.CultureInvariant)]
	private static partial Regex JoinableLiteral();

	[GeneratedRegex(@"[A-Za-z0-9]+(?:_[A-Za-z0-9]+)*", RegexOptions.CultureInvariant)]
	private static partial Regex Words();

	[GeneratedRegex(@"</?[a-z]{1,8}>", RegexOptions.CultureInvariant)]
	private static partial Regex Tag();
}
