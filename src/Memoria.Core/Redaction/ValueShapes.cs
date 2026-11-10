namespace Memoria.Core.Redaction;

/// <summary>
/// Recognizes values that merely refer to a secret: templates, references, masks, samples, and code.
/// Every test requires the shape to be complete, because a real password can start with any
/// character: <c>${Secret123</c> has no closing brace, and <c>env.Secret123</c> is not a name.
/// </summary>
internal static partial class ValueShapes
{
	private const int ShortTokenLength = 16;
	private const int LongestMarkerSegment = 12;
	private const int SegmentsPerMarker = 6;
	private const int MaxCloserDistance = 200;
	private const int LongSegment = 20;
	private const int MediumSegment = 12;
	private const double RandomEntropy = 3.0;

	private static readonly HashSet<string> MarkerWords = new(StringComparer.OrdinalIgnoreCase)
	{
		"your", "yours", "placeholder", "changeme", "example", "sample", "dummy", "fake", "redacted", "removed",
		"hidden", "masked", "todo", "tbd", "foo", "bar", "baz", "none", "null", "nil", "here", "insert", "replace",
	};

	private static readonly string[] SquashedMarkers =
	[
		"yourkey", "yourtoken", "yoursecret", "yourpassword", "yourapikey", "apikeyhere", "tokenhere", "keyhere", "passwordhere", "secrethere", "examplekey",
	];

	private static readonly string[] ReferencePrefixes =
	[
		"process.env", "os.environ", "os.getenv", "env.", "secrets.", "vault:", "op://", "ssm:", "arn:aws:", "ref+", "secretref:", "$env:",
	];

	/// <summary>The opening and closing spellings of a template, longest opener first.</summary>
	private static readonly (string Open, string Close)[] Templates =
	[
		("${{", "}}"), ("{{", "}}"), ("#{", "}"), ("${", "}"), ("<", ">"), ("{", "}"),
	];

	/// <summary>True for <c>&lt;your-key&gt;</c>, <c>${TOKEN}</c>, <c>$API_KEY</c>, <c>xxxx</c>, <c>your_api_key_here</c>, <c>[REDACTED:x]</c>, masks, and truncated samples.</summary>
	public static bool IsPlaceholder(string value)
	{
		string text = value.Trim();
		return text.Length == 0
			|| IsTemplateReference(text)
			|| IsReference(text)
			|| IsRedactedForm(text)
			|| IsMaskOrSample(text)
			|| SquashedMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase))
			|| ReadsAsSample(text)
			|| IsFormatWithPlaceholders(text);
	}

	/// <summary>
	/// A format written with angle-bracket names, as in <c>mem_&lt;id&gt;_&lt;secret&gt;</c> or <c>ghp_&lt;token&gt;</c>, and no random
	/// text besides. It describes a credential's shape, and holds none.
	/// </summary>
	private static bool IsFormatWithPlaceholders(string text)
	{
		string rest = AngleName().Replace(text, "_");
		return rest.Length < text.Length && !rest.AsSpan().ContainsAny('<', '>') && rest.Split(['_', '-', '.', '/', ':', ' ']).All(static part => !IsRandomSegment(part));
	}

	[System.Text.RegularExpressions.GeneratedRegex("<[a-z][a-z0-9_-]{0,23}>", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
	private static partial System.Text.RegularExpressions.Regex AngleName();

	/// <summary>True for <c>[REDACTED]</c>, <c>[REDACTED:name]</c>, and a placeholder cut off at the end of the text, as a title can be.</summary>
	public static bool IsRedactedForm(string text)
	{
		const string Open = "[REDACTED:";
		if (text.Equals("[REDACTED]", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (!text.StartsWith(Open, StringComparison.Ordinal))
		{
			return false;
		}

		ReadOnlySpan<char> name = text.AsSpan(Open.Length);
		return IsPlaceholderName(name.EndsWith("]") ? name[..^1] : name);
	}

	public static bool IsPlaceholderName(ReadOnlySpan<char> name)
	{
		foreach (char c in name)
		{
			if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
			{
				return false;
			}
		}

		// The longest rule name is an imported one, gitleaks- and a 43-character id.
		return name.Length <= 64;
	}

	/// <summary>The value plus the closer of its last unclosed opener, when one follows on the same line. A quoted value is already whole.</summary>
	public static string ExtendToCloser(string input, int start, int end, string value)
	{
		bool quoted = start > 0 && input[start - 1] is '"' or '\'' or '`';
		string? closer = quoted ? null : UnclosedCloser(value);
		if (closer is null)
		{
			return value;
		}

		int limit = Math.Min(input.Length, end + MaxCloserDistance);
		int found = input.IndexOf(closer, end, limit - end, StringComparison.Ordinal);
		int newline = input.IndexOfAny(['\n', '\r'], end, limit - end);
		bool sameLine = found >= 0 && (newline < 0 || found < newline);
		return sameLine ? value + input[end..(found + closer.Length)] : value;
	}

	/// <summary>True for an unquoted value that reads as code: <c>getPassword()</c>, <c>config.token</c>, <c>user_password</c>, <c>args[0]</c>.</summary>
	public static bool IsCodeReference(string value)
	{
		return value.Length > 0 && (IsCallOrMember(value) || HasIdentifierShape(value));
	}

	/// <summary>The structural subset of <see cref="IsCodeReference"/>, for places where a bare identifier-shaped word is more likely a password.</summary>
	public static bool IsCallOrMember(string value)
	{
		return IsCallPrefix(value)
			|| IsParameterList(value)
			|| IsNegatedName(value)
			|| (value.Contains('.') && IsIdentifierPath(value.Replace("?.", ".", StringComparison.Ordinal)));
	}

	/// <summary>Plain words joined by punctuation, such as a storage key <c>app:recent-items</c> or a path <c>src-auth/components</c>.</summary>
	public static bool ReadsAsWordList(string value)
	{
		return value.AsSpan().ContainsAny(":/\\") && ReadsAsNames(value.AsSpan().Trim('"'));
	}

	/// <summary>A name in test or code style with a number on the end, such as <c>TestFoo_Works2</c>. Not a generated value.</summary>
	public static bool ReadsAsIdentifierWithDigits(string value)
	{
		return value.Contains('_') && value.Split('_').All(static part => part.Length > 0 && IsWordy(part.AsSpan().TrimEnd("0123456789").ToString()));
	}

	/// <summary>True when every part is a plain word: <c>my-token-name</c>, <c>TokenValue</c>, <c>session.key</c>. A long unbroken run is not treated as a word.</summary>
	public static bool IsWordy(string value)
	{
		if (value.Any(c => !char.IsAsciiLetter(c) && c is not ('-' or '_' or '.')))
		{
			return false;
		}

		// Each part has to read as a word by itself: a short random run of mixed case does not become one by having a dash beside it.
		return value.Split('-', '_', '.').All(ReadsAsWord);
	}

	private static bool ReadsAsWord(string segment)
	{
		int uppers = segment.Count(char.IsAsciiLetterUpper);
		if (uppers == 0 || uppers == segment.Length)
		{
			return segment.Length < LongSegment;
		}

		// Natural camelCase averages several letters per hump; random mixed case averages about two. A random run can
		// reach that average by chance, but then it still has many humps of a letter or two, which names rarely do.
		int humps = char.IsAsciiLetterUpper(segment[0]) ? uppers : uppers + 1;
		return (double)segment.Length / humps >= 3 && ShortHumps(segment) <= 1 + (humps / 5);
	}

	private static int ShortHumps(string segment)
	{
		int shortHumps = 0;
		int length = 0;
		foreach (char c in segment)
		{
			if (char.IsAsciiLetterUpper(c) && length > 0)
			{
				shortHumps += length < 3 ? 1 : 0;
				length = 0;
			}

			length++;
		}

		return shortHumps + (length < 3 ? 1 : 0);
	}

	/// <summary>A segment that reads as random text and so can never be an identifier: long and mixed, or medium with digits and high entropy.</summary>
	public static bool IsRandomSegment(ReadOnlySpan<char> part)
	{
		bool hasDigit = part.IndexOfAnyInRange('0', '9') >= 0;
		bool hasUpper = part.IndexOfAnyInRange('A', 'Z') >= 0;
		bool hasLower = part.IndexOfAnyInRange('a', 'z') >= 0;
		bool mixed = hasDigit || (hasUpper && hasLower);
		return (part.Length >= LongSegment && mixed && !IsWordy(part.ToString()))
			|| (hasDigit && (hasUpper || hasLower) && IsMediumRandom(part));
	}

	/// <summary>A word with a number on the end is still a name: getPassword1.</summary>
	private static bool IsMediumRandom(ReadOnlySpan<char> part)
	{
		return part.Length >= MediumSegment
			&& !IsWordy(part.TrimEnd("0123456789").ToString())
			&& SecretHeuristics.ShannonEntropy(part) >= RandomEntropy;
	}

	private static string? UnclosedCloser(string value)
	{
		foreach ((string open, string close) in Templates)
		{
			if (value.StartsWith(open, StringComparison.Ordinal) && !value.EndsWith(close, StringComparison.Ordinal))
			{
				return close;
			}
		}

		if (value.StartsWith("$(", StringComparison.Ordinal) || HasUnclosed(value, '(', ')'))
		{
			return ")";
		}

		return HasUnclosed(value, '[', ']') ? "]" : null;
	}

	private static bool HasUnclosed(string value, char open, char close)
	{
		return value.Count(c => c == open) > value.Count(c => c == close);
	}

	private static bool IsTemplateReference(string text)
	{
		foreach ((string open, string close) in Templates)
		{
			if (text.StartsWith(open, StringComparison.Ordinal))
			{
				return IsClosedTemplate(text, open, close);
			}
		}

		return text[0] switch
		{
			'$' => IsDollarReference(text),
			'%' => IsPercentReference(text),
			_ => false,
		};
	}

	private static bool IsClosedTemplate(string text, string open, string close)
	{
		int bodyLength = text.Length - open.Length - close.Length;
		if (bodyLength < 0 || !text.EndsWith(close, StringComparison.Ordinal))
		{
			return false;
		}

		return IsTemplateBody(text.AsSpan(open.Length, bodyLength));
	}

	/// <summary><c>$NAME</c>, or <c>$(command args)</c> with a closing parenthesis and a command name first.</summary>
	private static bool IsDollarReference(string text)
	{
		if (!text.StartsWith("$(", StringComparison.Ordinal))
		{
			return IsNameBody(text.AsSpan(1));
		}

		return text.EndsWith(')') && IsTemplateBody(text.AsSpan(2, text.Length - 3).Trim().ToString().Split(' ')[0]);
	}

	/// <summary><c>%s</c>, <c>%(name)s</c>, and <c>%NAME%</c>. A leading percent alone proves nothing.</summary>
	private static bool IsPercentReference(string text)
	{
		if (text.Length <= 2)
		{
			return true;
		}

		if (text[1] == '(')
		{
			return text.EndsWith(")s", StringComparison.Ordinal) && IsNameBody(text.AsSpan(2, text.Length - 4));
		}

		if (text[^1] == '%' && IsNameBody(text.AsSpan(1, text.Length - 2)))
		{
			return true;
		}

		// A Windows path under a variable, as in %ProgramData%\service\secret\.
		int close = text.IndexOf('%', 1);
		return close > 1 && close + 1 < text.Length && text[close + 1] is '\\' or '/' && IsNameBody(text.AsSpan(1, close - 1));
	}

	/// <summary>Words, dotted paths, and simple filters, with nothing that reads as random.</summary>
	private static bool IsTemplateBody(ReadOnlySpan<char> body)
	{
		foreach (char c in body)
		{
			if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.' or ' ' or ':' or '|' or '?' or '\'' or '"' or '[' or ']' or '(' or ')' or '/'))
			{
				return false;
			}
		}

		foreach (Range range in body.SplitAny(" .:|/"))
		{
			if (IsRandomSegment(body[range]))
			{
				return false;
			}
		}

		return true;
	}

	private static bool IsNameBody(ReadOnlySpan<char> body)
	{
		foreach (char c in body)
		{
			if (!char.IsAsciiLetterOrDigit(c) && c != '_')
			{
				return false;
			}
		}

		return !IsRandomSegment(body);
	}

	/// <summary>A reference prefix counts only when what follows reads as names. <c>vault:&lt;random&gt;</c> and <c>env.Secret123</c> do not.</summary>
	private static bool IsReference(string text)
	{
		foreach (string prefix in ReferencePrefixes)
		{
			if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				return ReadsAsNames(text.AsSpan(prefix.Length));
			}
		}

		return false;
	}

	private static bool ReadsAsNames(ReadOnlySpan<char> remainder)
	{
		foreach (Range range in remainder.SplitAny(".:/\\[]'\"()_- "))
		{
			if (!IsNamePart(remainder[range]))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>A word or a plain number. Letters and digits mixed in one part read as a value, not a name.</summary>
	private static bool IsNamePart(ReadOnlySpan<char> part)
	{
		bool hasDigit = part.IndexOfAnyInRange('0', '9') >= 0;
		bool hasLetter = part.ContainsAnyExceptInRange('0', '9');
		return !(hasDigit && hasLetter) && part.Length <= 32 && (hasDigit || part.IsEmpty || IsWordy(part.ToString()));
	}

	/// <summary>All punctuation, mostly the letter x, or an ellipsis standing in for the middle of a short sample.</summary>
	private static bool IsMaskOrSample(string text)
	{
		int alphanumeric = text.Count(char.IsLetterOrDigit);
		int xs = text.Count(c => c is 'x' or 'X');
		if (alphanumeric == 0 || (alphanumeric >= 3 && xs * 10 >= alphanumeric * 6))
		{
			return true;
		}

		bool elided = text.Contains("...", StringComparison.Ordinal) || text.Contains('…');
		return elided && alphanumeric < ShortTokenLength;
	}

	/// <summary>
	/// A marker counts only when the whole value reads as a sample: short plain words and nothing else.
	/// <c>your_api_key_here</c> does, while <c>Hidden-Here-42</c> and <c>Foo!9921</c> are passwords. Every part has to read as a
	/// word and markers have to be common, because a long random token can hold <c>BaR</c> between two separators by chance.
	/// </summary>
	private static bool ReadsAsSample(string text)
	{
		if (text.Any(c => !char.IsLetter(c) && c is not ('-' or '_' or '.' or ' ')))
		{
			return false;
		}

		string[] segments = text.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
		return segments.All(segment => segment.Length <= LongestMarkerSegment && ReadsAsWord(segment))
			&& segments.Count(MarkerWords.Contains) * SegmentsPerMarker >= segments.Length;
	}

	/// <summary>A name before <c>(...)</c> or <c>[...]</c>, closed, with generic arguments allowed: <c>table.Column&lt;string&gt;(type: "x")</c>.</summary>
	private static bool IsCallPrefix(string value)
	{
		int open = value.IndexOfAny(['(', '[']);
		if (open <= 0 || !value.AsSpan(open).ContainsAny(')', ']'))
		{
			return false;
		}

		// An optional call, apiKeyFor?.(role), is a call too.
		ReadOnlySpan<char> prefix = value.AsSpan(0, open).TrimEnd("?.");
		int generic = prefix.IndexOf('<');
		return !prefix.IsEmpty && IsIdentifierPath(generic > 0 ? prefix[..generic] : prefix);
	}

	/// <summary><c>(data: Input)</c>: an opening parenthesis, a plain name, then a delimiter. <c>(Secret123</c> has none of that.</summary>
	private static bool IsParameterList(string value)
	{
		if (value[0] != '(')
		{
			return false;
		}

		int end = value.IndexOfAny([':', ',', ')']);
		return end > 1 && IsWordy(value[1..end]);
	}

	/// <summary><c>!isValid</c> and <c>*ptr</c>. Digits rule it out: <c>!Secret123</c> is a password.</summary>
	private static bool IsNegatedName(string value)
	{
		return value[0] is '!' or '*' && value.Length > 1 && IsWordy(value[1..]);
	}

	private static bool IsIdentifierPath(ReadOnlySpan<char> text)
	{
		foreach (Range range in text.Split('.'))
		{
			if (!IsIdentifierPart(text[range]))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>A name part, with JavaScript's <c>#</c> for a private field allowed first, as in <c>this.#routes</c>.</summary>
	private static bool IsIdentifierPart(ReadOnlySpan<char> part)
	{
		part = part.StartsWith('#') ? part[1..] : part;
		if (part.IsEmpty || char.IsAsciiDigit(part[0]) || IsRandomSegment(part))
		{
			return false;
		}

		foreach (char c in part)
		{
			if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '$'))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>camelCase, snake_case, and UPPER_SNAKE names without digits. A plain lowercase or Capitalized word does not qualify: it could be a weak password.</summary>
	private static bool HasIdentifierShape(string value)
	{
		if (value.Any(c => !char.IsAsciiLetter(c) && c != '_'))
		{
			return false;
		}

		bool hasLower = value.Any(char.IsAsciiLetterLower);
		bool hasUpper = value.Any(char.IsAsciiLetterUpper);
		if (value.Contains('_') || !hasLower)
		{
			return IsWordy(value);
		}

		return hasUpper && char.IsAsciiLetterLower(value[0]) && IsWordy(value);
	}
}
