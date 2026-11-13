using System.Text.RegularExpressions;

namespace Memoria.Core.Redaction;

/// <summary>
/// The one judgement our rules make about a value their pattern found: whether it is a value at all, or something that stands
/// where one would, such as a reference, a type or a placeholder already written. It does not ask whether the value looks
/// random. When in doubt the value is replaced.
/// </summary>
internal static partial class PlainValues
{
	/// <summary>Under a name alone, shorter than this is a flag, a count or a letter of code rather than a credential.</summary>
	private const int MinAssignedLength = 3;

	/// <summary>Words that stand where a value would in code, types, schemas, and documentation.</summary>
	private static readonly HashSet<string> StandIns = new(StringComparer.OrdinalIgnoreCase)
	{
		"string", "str", "bytes", "number", "int", "integer", "bool", "boolean", "object", "any", "void", "char",
		"null", "nil", "none", "undefined", "true", "false", "required", "optional", "empty", "unset", "include", "inherit", "omit",
		"password", "passwd", "secret", "token", "key", "value", "bearer", "basic", "redacted",
	};

	/// <summary>
	/// For a value known only by the name before it: replaced unless it is under three characters, a stand-in word such as
	/// <c>string</c> or <c>required</c>, or not a value at all (see <see cref="IsLiteral"/>).
	/// </summary>
	public static bool IsReplaceable(string value)
	{
		string trimmed = Trimmed(value);
		return trimmed.Length >= MinAssignedLength && IsLiteral(trimmed) && !StandIns.Contains(trimmed);
	}

	/// <summary>
	/// For a value whose place already says it is a password, as after <c>mysql -p</c> or in a URL's user info: any literal goes,
	/// a word such as <c>password</c> too.
	/// </summary>
	public static bool IsPassword(string value)
	{
		string trimmed = Trimmed(value);
		return trimmed.Length > 0 && IsLiteral(trimmed);
	}

	/// <summary><c>8080:80</c> or <c>127.0.0.1:5432:5432</c>, which a port flag takes. Never a password.</summary>
	public static bool IsPortMapping(string value)
	{
		return PortMapping().IsMatch(value);
	}

	private static string Trimmed(string value)
	{
		return value.Trim().Trim('*', '`', '"', '\'', ',', ';', '.');
	}

	/// <summary>
	/// A literal holds a letter or digit, and is not a mask such as <c>xxxx</c>, a reference, or a placeholder. A value that starts with a
	/// placeholder has already lost its secret, and what follows it, such as the host after a URL's password, is context. A placeholder
	/// later in a value does not save it, so no piece of a secret around one survives.
	/// </summary>
	private static bool IsLiteral(string trimmed)
	{
		return !trimmed.StartsWith("[REDACTED:", StringComparison.Ordinal)
			&& HasLetterOrDigit(Placeholder().Replace(trimmed, ""))
			&& !IsMask(trimmed)
			&& !IsReference(trimmed);
	}

	/// <summary>One character over and over, as documentation writes <c>xxxx</c> where a secret goes.</summary>
	private static bool IsMask(string value)
	{
		return value.AsSpan().IndexOfAnyExcept(value[0]) < 0;
	}

	/// <summary>
	/// A whole variable or template (<c>$TOKEN</c>, <c>${TOKEN}</c>, <c>$(cat f)</c>, <c>%TOKEN%</c>, <c>{{ token }}</c>), a placeholder in
	/// angle brackets (<c>&lt;token&gt;</c>, whose closing bracket ends an unquoted value), an environment read, a call
	/// (<c>getPassword()</c>), or where a value is kept: a file path or a URL. A URL holding a password is
	/// <see cref="RedactionRules.UrlPassword"/>'s. A variable with a literal joined to it, as in <c>${PREFIX}abc123</c>, is a value.
	/// </summary>
	private static bool IsReference(string value)
	{
		return IsVariable(value) || EnvironmentRead().IsMatch(value) || Call().IsMatch(value) || Location().IsMatch(value);
	}

	private static bool IsVariable(string value)
	{
		return Variable().IsMatch(value)
			|| (value[0] == '%' && value[^1] == '%')
			|| (value[0] == '<' && (value[^1] == '>' || !value.Contains('>', StringComparison.Ordinal)));
	}

	private static bool HasLetterOrDigit(string text)
	{
		foreach (char c in text)
		{
			if (char.IsAsciiLetterOrDigit(c))
			{
				return true;
			}
		}

		return false;
	}

	[GeneratedRegex(@"\[REDACTED:[a-z0-9\-]+\]?", RegexOptions.CultureInvariant)]
	private static partial Regex Placeholder();

	/// <summary>An unquoted value ends before a closing brace, so <c>${TOKEN}</c> is read as <c>${TOKEN</c>, and both are whole.</summary>
	[GeneratedRegex(@"^(?:\$[A-Za-z_][A-Za-z0-9_]*|\$\{[^{}]*\}?|\$\(.*|\{\{.*)$", RegexOptions.CultureInvariant)]
	private static partial Regex Variable();

	[GeneratedRegex(@"^(?:[0-9.]+:)?[0-9]+:[0-9]+$", RegexOptions.CultureInvariant)]
	private static partial Regex PortMapping();

	[GeneratedRegex(@"^(?:process\.env\b|os\.environ\b|os\.getenv\b|ENV\[|env\.|System\.getenv\b|Environment\.GetEnvironmentVariable\b)", RegexOptions.CultureInvariant)]
	private static partial Regex EnvironmentRead();

	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.]*\(.*\)?$", RegexOptions.CultureInvariant)]
	private static partial Regex Call();

	[GeneratedRegex(@"^(?:/|\./|\.\./|~/|[A-Za-z]:\\|[A-Za-z][A-Za-z0-9+.\-]*://)", RegexOptions.CultureInvariant)]
	private static partial Regex Location();
}
