using System.Buffers;
using System.Text.RegularExpressions;

namespace Memoria.Core.Redaction;

/// <summary>
/// Judges whether a candidate value is a credential. Every method here is a
/// pure function of the value and returns only a verdict, so nothing about the
/// value can escape through it.
/// </summary>
internal static class SecretHeuristics
{
	private const int MaxKeyLength = 80;
	private const int StrictTokenLength = 16;
	private const int ShortTokenLength = 8;
	private const int EnvTokenLength = 6;
	private const int PasswordLength = 4;
	private const int HighInformationLength = 20;
	private const int SessionCookieLength = 12;
	private const double StrictEntropy = 3.0;
	private const double ShortEntropy = 2.5;
	private const double HighEntropy = 3.5;
	private const double UserInfoEntropy = 2.0;
	private const int QuoteLookback = 512;

	private static readonly HashSet<string> AddressNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"email", "e-mail", "mail", "mailto", "author", "from", "to", "cc", "bcc", "reply-to", "committer", "contact", "npm", "pkg", "pip", "gem", "cargo", "nuget", "go", "image", "docker",
	};

	/// <summary>Accounts that exist to log in to a database or server. After one of these, what follows the colon is a password even if it is a plain word.</summary>
	private static readonly HashSet<string> ServerLogins = new(StringComparer.OrdinalIgnoreCase)
	{
		"root", "admin", "administrator", "postgres", "mysql", "mariadb", "sa", "dba", "dbadmin", "dbuser",
	};

	private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		"cs", "ts", "tsx", "js", "jsx", "py", "go", "md", "json", "yaml", "yml", "rs", "java", "rb", "sh", "txt", "html", "css", "sql", "toml",
	};

	private static readonly SearchValues<char> LineNumberChars = SearchValues.Create("0123456789,");

	/// <summary>Bare words that stand where a value would in code, types, schemas, and prose.</summary>
	private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
	{
		"string", "str", "bytes", "number", "int", "integer", "bool", "boolean", "float", "double", "object", "any", "void", "char",
		"function", "func", "def", "async", "await", "lambda", "class", "type", "const", "var", "let", "new", "this", "self",
		"undefined", "true", "false", "yes", "no", "on", "off",
		"required", "optional", "empty", "unset", "unknown", "default", "invalid", "valid", "missing", "present", "absent",
		"value", "val", "input", "password", "passwd", "pwd", "pass", "passphrase", "secret", "secrets", "token", "tokens", "key", "keys", "apikey", "api_key", "api-key",
		"credential", "credentials", "auth", "inherit", "include", "omit", "bearer", "basic", "oauth", "jwt", "user", "username",
	};

	/// <summary>Bits per character over the value's own character distribution.</summary>
	public static double ShannonEntropy(ReadOnlySpan<char> value)
	{
		Dictionary<char, int> frequency = [];
		foreach (char c in value)
		{
			frequency[c] = frequency.GetValueOrDefault(c) + 1;
		}

		double entropy = 0;
		foreach (int count in frequency.Values)
		{
			double probability = (double)count / value.Length;
			entropy -= probability * Math.Log2(probability);
		}

		return entropy;
	}

	/// <summary>
	/// For a value carrying a provider's own prefix, at that provider's length: it has earned its match, so it need not look
	/// random as well. A real token can be all letters, or split by the provider's own separators into short pieces. Only a
	/// value recognizably a sample or a mask gets out. Redacting a name that happens to start with a prefix costs little,
	/// and storing a live credential does not.
	/// </summary>
	public static bool IsPrefixedToken(SecretCandidate candidate)
	{
		return !ValueShapes.IsPlaceholder(candidate.JudgedValue);
	}

	/// <summary>The value after an access key id: token-like, and not a commit or other hex.</summary>
	public static bool IsAwsSecretNearId(SecretCandidate candidate)
	{
		string value = candidate.Value;
		return !value.All(char.IsAsciiHexDigit) && !ValueShapes.IsPlaceholder(value) && IsTokenValue(value);
	}

	/// <summary>For a value with no prefix of its own, whose format ordinary text can also fit: it must not read as words.</summary>
	public static bool IsRealToken(SecretCandidate candidate)
	{
		string value = candidate.JudgedValue;
		return !ValueShapes.IsPlaceholder(value) && !ValueShapes.IsWordy(value);
	}

	/// <summary>Decides a <c>key = value</c> candidate by the key's meaning and the value's shape.</summary>
	public static bool IsSecretAssignment(SecretCandidate candidate)
	{
		return Judge(candidate, SecretKeyNames.Classify);
	}

	/// <summary>Decides a command line flag and its value.</summary>
	public static bool IsSecretFlag(SecretCandidate candidate)
	{
		return Judge(candidate, SecretKeyNames.ClassifyFlag);
	}

	/// <summary>A token-like value: long and varied, not a word, not a count, not a version.</summary>
	public static bool IsTokenValue(string value, bool environmentAssignment = false)
	{
		if (value.All(char.IsAsciiDigit) || value.Any(char.IsWhiteSpace) || IsVersion(value))
		{
			return false;
		}

		if (environmentAssignment)
		{
			// SECRET_KEY=correct-horse-battery-staple: a long passphrase in a real assignment is a value, not a name.
			int needed = ValueShapes.IsWordy(value) ? StrictTokenLength : EnvTokenLength;
			return value.Length >= needed;
		}

		if (ValueShapes.IsWordy(value) || ValueShapes.ReadsAsWordList(value))
		{
			return false;
		}

		return value.Length >= StrictTokenLength ? ShannonEntropy(value) >= StrictEntropy : IsShortToken(value);
	}

	/// <summary>A value that could not be a description, a name, or a path, whatever the key calls it.</summary>
	public static bool IsHighInformation(string value)
	{
		return value.Length >= HighInformationLength
			&& !value.Any(char.IsWhiteSpace)
			&& !value.All(char.IsAsciiDigit)
			&& !ValueShapes.IsWordy(value)
			&& !ValueShapes.ReadsAsWordList(value)
			&& ShannonEntropy(value) >= HighEntropy;
	}

	/// <summary>The value of an explicit credential header: secret unless it is a reference or a name.</summary>
	public static bool IsExplicitCredential(string value)
	{
		return value.Length >= ShortTokenLength
			&& !ValueShapes.IsPlaceholder(value)
			&& !Keywords.Contains(value)
			&& !ValueShapes.IsCodeReference(value);
	}

	/// <summary>True for a password in a URL. Documentation words stay, and so does anything an earlier rule already fully replaced.</summary>
	public static bool IsUrlPassword(string value)
	{
		string remainder = NormalizeRemainder(value);
		return remainder.Length > 0 && !ValueShapes.IsPlaceholder(remainder) && !Keywords.Contains(remainder);
	}

	/// <summary>
	/// A URL password, ruling out what only looks like one in a web URL: <c>host:8080@x</c> is a port, and
	/// <c>host:8080/p@x</c> is a port and a path. Under a database scheme, five digits are a password.
	/// </summary>
	public static bool IsUrlPasswordCandidate(SecretCandidate candidate)
	{
		string value = candidate.JudgedValue;
		int slash = value.IndexOf('/');
		ReadOnlySpan<char> beforeSlash = slash < 0 ? value : value.AsSpan(0, slash);
		bool numeric = beforeSlash.Length > 0 && beforeSlash.Length <= 5 && !beforeSlash.ContainsAnyExceptInRange('0', '9');
		bool webScheme = candidate.Match.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase);
		bool isPort = numeric && (webScheme || slash >= 0);
		return !isPort && IsUrlPassword(value);
	}

	/// <summary>The password in <c>user:password@host</c> with no scheme, which also describes versions and image digests.</summary>
	public static bool IsBareUserInfoPassword(SecretCandidate candidate)
	{
		string value = candidate.JudgedValue;
		int hostStart = candidate.End + 1;
		if (IsDsnHost(candidate.Input, hostStart))
		{
			return IsUrlPassword(value);
		}

		if (IsVersionOrDigestHost(candidate.Input, hostStart))
		{
			return false;
		}

		string name = UserInfoName(candidate);
		return ServerLogins.Contains(name)
			? IsServerLoginPassword(value)
			: HasPasswordShape(value) && !IsAddressName(name) && IsUrlPassword(value);
	}

	/// <summary><c>image:tag@sha256:...</c> and <c>npm:name@1.2.3</c>. An address or a port after the <c>@</c> is a host, so what comes before it is a password.</summary>
	private static bool IsVersionOrDigestHost(string input, int hostStart)
	{
		return string.CompareOrdinal(input, hostStart, "sha", 0, 3) == 0 || IsThreePartVersion(input, hostStart);
	}

	private static bool IsThreePartVersion(string input, int start)
	{
		int cursor = SkipDigits(input, start);
		if (cursor == start || cursor >= input.Length || input[cursor] != '.')
		{
			return false;
		}

		int second = cursor + 1;
		cursor = SkipDigits(input, second);
		if (cursor == second || cursor >= input.Length || input[cursor] != '.')
		{
			return false;
		}

		int third = cursor + 1;
		cursor = SkipDigits(input, third);
		return cursor > third && !IsAddressOrPortNext(input, cursor);
	}

	private static int SkipDigits(string input, int start)
	{
		int cursor = start;
		while (cursor < input.Length && char.IsAsciiDigit(input[cursor]))
		{
			cursor++;
		}

		return cursor;
	}

	/// <summary>A fourth number makes an address and a colon makes a port. A dot on its own ends a sentence.</summary>
	private static bool IsAddressOrPortNext(string input, int position)
	{
		bool port = position < input.Length && input[position] == ':';
		bool fourthPart = position + 1 < input.Length && input[position] == '.' && char.IsAsciiDigit(input[position + 1]);
		return port || fourthPart;
	}

	/// <summary>A capitalized word such as <c>Password</c> is what a person picks. All lower case, it is how documentation writes the slot.</summary>
	private static bool IsServerLoginPassword(string value)
	{
		return value.Length >= PasswordLength
			&& (IsUrlPassword(value) || (Keywords.Contains(value) && char.IsAsciiLetterUpper(value[0])));
	}

	private static bool HasPasswordShape(string value)
	{
		// A run of letters this long is not a name, so it needs no digit or symbol to be a password.
		bool enoughShape = value.Length >= HighInformationLength || value.Any(char.IsAsciiDigit) || value.Any(static c => !char.IsAsciiLetterOrDigit(c) && c != '_');
		return enoughShape
			&& value.Length >= PasswordLength
			&& !value.Contains('\\')
			&& !IsVersion(value)
			&& ShannonEntropy(value) >= UserInfoEntropy;
	}

	/// <summary>The name before the colon says this is an address or a package, as in <c>email:me2@x</c>; a clock time such as <c>12:30pm@home</c> has a number there.</summary>
	private static bool IsAddressName(string name)
	{
		return AddressNames.Contains(name) || name.All(char.IsAsciiDigit);
	}

	private static string UserInfoName(SecretCandidate candidate)
	{
		string input = candidate.Input;
		int colon = candidate.Secret.Index - 1;
		int start = colon;
		while (start > 0 && (char.IsAsciiLetterOrDigit(input[start - 1]) || input[start - 1] is '_' or '-' or '.'))
		{
			start--;
		}

		return input[start..colon];
	}

	/// <summary>Go's <c>tcp(</c> and <c>unix(</c> say plainly that what came before the <c>@</c> is a login.</summary>
	private static bool IsDsnHost(string input, int position)
	{
		return string.CompareOrdinal(input, position, "tcp(", 0, 4) == 0 || string.CompareOrdinal(input, position, "unix(", 0, 5) == 0;
	}

	/// <summary>A password on a known command line or in a known call. The surrounding text already says what it is.</summary>
	public static bool IsCommandCredential(SecretCandidate candidate)
	{
		string value = CodeValues.WithoutMarkup(candidate.JudgedValue);
		return value.Length >= PasswordLength
			&& value[0] is not ('/' or '<')
			&& !ValueShapes.IsPlaceholder(value)
			&& !Keywords.Contains(value)
			&& !ValueShapes.IsCodeReference(value)
			&& !CodeValues.HasInterpolation(value);
	}

	/// <summary><c>curl -u user:password</c>. The flag is common, so the line must be a curl command.</summary>
	public static bool IsCurlUserPassword(SecretCandidate candidate)
	{
		return IsUrlPassword(candidate.JudgedValue) && !ValueShapes.IsCodeReference(candidate.JudgedValue) && CommandLines.IsCommand(candidate.Input, candidate.Match.Index, "curl");
	}

	/// <summary><c>-b</c> and <c>--cookie</c> with a value of <c>name=value</c>. A file name has no equals sign.</summary>
	public static bool IsCurlCookie(SecretCandidate candidate)
	{
		return IsSessionCookie(candidate.JudgedValue) && CommandLines.IsCommand(candidate.Input, candidate.Match.Index, "curl");
	}

	/// <summary><c>mysql -pSECRET</c>. A flag with the value attached, so the line must be a MySQL client.</summary>
	public static bool IsMysqlPassword(SecretCandidate candidate)
	{
		string value = candidate.JudgedValue;
		bool port = value.All(static c => char.IsAsciiDigit(c) || c == ':');
		return !port && !ValueShapes.IsPlaceholder(value) && CommandLines.IsCommand(candidate.Input, candidate.Match.Index, "mysql", "mysqldump", "mysqladmin", "mariadb", "mariadb-dump");
	}

	/// <summary>A cookie header carrying at least one value long and varied enough to be a session id.</summary>
	public static bool IsSessionCookie(string value)
	{
		foreach (string pair in value.Split(';'))
		{
			string cookieValue = pair[(pair.IndexOf('=') + 1)..].Trim();
			if (pair.Contains('=') && IsSessionValue(cookieValue))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>A value under a session cookie's own name, such as <c>PHPSESSID</c>.</summary>
	public static bool IsSessionValue(string value)
	{
		return value.Length >= SessionCookieLength && !ValueShapes.IsPlaceholder(value) && ShannonEntropy(value) >= StrictEntropy;
	}

	/// <summary>OpenAI keys: a named prefix is distinctive, as a provider's prefix is. A bare <c>sk-</c> needs a random-looking body to rule out <c>sk-learn-style</c> names.</summary>
	public static bool IsOpenAiKey(string value)
	{
		if (ValueShapes.IsPlaceholder(value))
		{
			return false;
		}

		bool named = value.StartsWith("sk-proj-", StringComparison.Ordinal)
			|| value.StartsWith("sk-svcacct-", StringComparison.Ordinal)
			|| value.StartsWith("sk-admin-", StringComparison.Ordinal);
		ReadOnlySpan<char> body = value.AsSpan(3);
		if (named || !body.ContainsAny('-', '_'))
		{
			return true;
		}

		return !ValueShapes.IsWordy(value) && ShannonEntropy(body) >= StrictEntropy && !IsPackageName(value);
	}

	/// <summary>Memoria keys are <c>mem_&lt;id&gt;_&lt;secret&gt;</c>, with a 64 character secret. A long unbroken segment separates them from names like <c>mem_total_bytes_per_node</c>.</summary>
	public static bool IsMemoriaKey(string value)
	{
		return !ValueShapes.IsPlaceholder(value) && value.Split('_', '-').Max(segment => segment.Length) >= 12;
	}

	/// <summary>
	/// Runs an unquoted value on to the end when it fills the whole line or a whole quoted string, since
	/// <c>, ; &amp;</c> are then part of it. A delimiter followed by <c>name=</c> is a real separator, which keeps query strings intact.
	/// </summary>
	public static int ExtendAssignment(SecretCandidate candidate)
	{
		return GluedMarkupStart(candidate) ?? ExtendValue(candidate);
	}

	/// <summary>A closing tag glued to the value, as in <c>&lt;c&gt;password: x&lt;/c&gt;</c>, is markup, so the replacement stops before it.</summary>
	private static int? GluedMarkupStart(SecretCandidate candidate)
	{
		int markup = candidate.Value.IndexOf("</", StringComparison.Ordinal);
		return markup > 0 && CodeValues.WithoutMarkup(candidate.Value).Length == markup ? candidate.Secret.Index + markup : null;
	}

	private static int ExtendValue(SecretCandidate candidate)
	{
		string input = candidate.Input;
		int end = ExtendPastStrayQuote(input, candidate.Secret.Index, candidate.End);
		if (end > candidate.End)
		{
			return end;
		}

		bool delimited = end < input.Length && input[end] is ',' or ';' or '&';
		if (!delimited || IsQuoted(input, candidate.Secret.Index) || StartsNameAssignment(input, end + 1))
		{
			return end;
		}

		int keyStart = candidate.Match.Groups["key"].Index;
		int runEnd = RunEnd(input, end);

		// A lone trailing delimiter ends the value: password: string,
		bool hasMore = runEnd > end + 1;
		return hasMore && (FillsLine(input, keyStart, runEnd) || FillsString(input, keyStart, runEnd)) ? runEnd : end;
	}

	private static bool FillsLine(string input, int keyStart, int runEnd)
	{
		return IsAtLineStart(input, keyStart) && (runEnd == input.Length || input[runEnd] is '\r' or '\n');
	}

	private static bool FillsString(string input, int keyStart, int runEnd)
	{
		return keyStart > 0 && input[keyStart - 1] is '"' or '\'' && runEnd < input.Length && input[runEnd] == input[keyStart - 1];
	}

	/// <summary>
	/// A quote is a delimiter only if it pairs up. An unquoted value stops at one only when another follows on the line, as in
	/// <c>password=it's9x</c> it does not. A closing quote with text straight after it did not close anything, as in <c>"ab"9x"</c>.
	/// </summary>
	private static int ExtendPastStrayQuote(string input, int start, int end)
	{
		if (end >= input.Length || input[end] is not ('"' or '\''))
		{
			return end;
		}

		char quote = input[end];
		if (OpensBeforeValue(input, start, quote))
		{
			return end;
		}

		int close = input.IndexOfAny([quote, '\n', '\r'], end + 1);
		bool closes = close >= 0 && input[close] == quote;
		if (start > 0 && input[start - 1] == quote)
		{
			return closes && IsTextAt(input, end + 1) ? close : end;
		}

		return closes ? end : RunEnd(input, end + 1);
	}

	private static bool IsTextAt(string input, int position)
	{
		return position < input.Length && char.IsAsciiLetterOrDigit(input[position]);
	}

	/// <summary>An unquoted value with an unpaired quote earlier on its line sits inside that string, as in <c>f("password=x")</c>, so the next quote closes it.</summary>
	private static bool OpensBeforeValue(string input, int start, char quote)
	{
		int limit = Math.Max(0, start - QuoteLookback);
		int count = 0;
		for (int i = start - 1; i >= limit && input[i] is not ('\n' or '\r'); i--)
		{
			count += input[i] == quote ? 1 : 0;
		}

		return count % 2 == 1 && input[start - 1] != quote;
	}

	private static bool Judge(SecretCandidate candidate, Func<string, KeyKind> classify)
	{
		Group keyGroup = candidate.Match.Groups["key"];
		KeyKind kind = classify(KeyTail(keyGroup.Value));
		if (kind == KeyKind.NotSecret)
		{
			return false;
		}

		string value = CodeValues.WithoutMarkup(NormalizeRemainder(WholeValue(candidate)));
		return !IsBenignCandidate(candidate, keyGroup, value) && JudgeByKind(candidate, keyGroup, kind, value);
	}

	/// <summary>
	/// The end of a long name, starting at a word. A run with no spaces can run straight into a key, as in
	/// <c>word-word-...-password=x</c>, and only its end names the value. One unbroken word this long is data, not a name.
	/// </summary>
	private static string KeyTail(string key)
	{
		if (key.Length <= MaxKeyLength)
		{
			return key;
		}

		string tail = key[^MaxKeyLength..];
		int separator = tail.IndexOfAny(['_', '-', '.', ' ']);
		return separator < 0 ? "" : tail[(separator + 1)..];
	}

	/// <summary>What will be replaced if the candidate is accepted, so the judgement covers all of it.</summary>
	private static string WholeValue(SecretCandidate candidate)
	{
		int end = ExtendAssignment(candidate);
		return end > candidate.End ? candidate.Input[candidate.Secret.Index..end] : candidate.JudgedValue;
	}

	private static bool IsBenignCandidate(SecretCandidate candidate, Group keyGroup, string value)
	{
		string input = candidate.Input;
		int start = candidate.Secret.Index;

		// Backticks mark code in markdown, so a call inside them is still a call. Quotes make a literal.
		bool readAsCode = !IsQuoted(input, start) || input[start - 1] == '`';
		bool literalDollar = input[start - 1] == '\'' && IsLiteralDollar(value);
		return (!literalDollar && IsBenignValue(value, keyGroup.Value, readAsCode))
			|| IsNestedYamlKey(input, keyGroup, start, value)
			|| IsHostAfterPlaceholder(candidate.Value, value);
	}

	/// <summary>In <c>secret:\n  name: x</c> the next line is a nested key, not the secret's value.</summary>
	private static bool IsNestedYamlKey(string input, Group keyGroup, int valueStart, string value)
	{
		int keyEnd = keyGroup.Index + keyGroup.Length;
		return value.EndsWith(':') && input.AsSpan(keyEnd, valueStart - keyEnd).ContainsAny('\n', '\r');
	}

	/// <summary>Single quotes stop expansion, so <c>'$abc123'</c> is the password itself. An upper-case name in them is still far more often a reference.</summary>
	private static bool IsLiteralDollar(string value)
	{
		return value.StartsWith('$') && !IsUpperSnake(value[1..]);
	}

	private static bool JudgeByKind(SecretCandidate candidate, Group keyGroup, KeyKind kind, string value)
	{
		int keyEnd = keyGroup.Index + keyGroup.Length;
		ReadOnlySpan<char> separator = candidate.Input.AsSpan(keyEnd, candidate.Secret.Index - keyEnd);
		if (separator.Contains('|') && !separator.ContainsAny('\n', '\r') && !IsTableRow(candidate.Input, keyGroup.Index))
		{
			// A shell pipeline, as in cat token | sha256sum. The next word is a command.
			return value.Length >= StrictTokenLength && IsTokenValue(value);
		}

		bool compact = separator.Contains('=') && !separator.ContainsAny(' ', '\t');
		return kind switch
		{
			KeyKind.Password => IsPasswordValue(value, compact || IsExplicitPasswordForm(candidate, keyGroup.Index, separator)),
			KeyKind.KeyFamily => value.Length >= StrictTokenLength && !ValueShapes.ReadsAsIdentifierWithDigits(value) && IsTokenValue(value),
			KeyKind.Descriptive => IsHighInformation(value),
			_ => IsTokenValue(value, compact && IsUpperSnake(keyGroup.Value)),
		};
	}

	/// <summary>A quoted literal, or <c>password: value</c> filling its whole line, says plainly that the value is the password.</summary>
	private static bool IsExplicitPasswordForm(SecretCandidate candidate, int keyStart, ReadOnlySpan<char> separator)
	{
		return IsQuoted(candidate.Input, candidate.Secret.Index)
			|| (separator.Contains(':') && IsWholeYamlLine(candidate.Input, keyStart, candidate.End));
	}

	/// <summary>What a value holds once earlier rules' placeholders are gone. <c>%20Bearer%20[REDACTED:x]</c> leaves the word Bearer, not token-like text.</summary>
	private static string NormalizeRemainder(string value)
	{
		return StripPlaceholders(value).Replace("%20", " ", StringComparison.Ordinal).Trim();
	}

	/// <summary>In <c>user:[REDACTED:x]@host/path</c> the name before the colon is URL userinfo, and what remains is the host.</summary>
	private static bool IsHostAfterPlaceholder(string original, string remainder)
	{
		return remainder.Length != original.Length && remainder.StartsWith('@');
	}

	private static bool IsShortToken(string value)
	{
		return value.Length >= ShortTokenLength
			&& value.Any(char.IsAsciiLetter)
			&& value.Any(char.IsAsciiDigit)
			&& ShannonEntropy(value) >= ShortEntropy;
	}

	/// <summary><c>v1.2.3-rc1</c> and <c>1.2.3</c>: dotted numbers with an optional prefix and suffix.</summary>
	private static bool IsVersion(string value)
	{
		ReadOnlySpan<char> text = value.AsSpan().TrimStart('v');
		int dash = text.IndexOf('-');
		ReadOnlySpan<char> numbers = dash < 0 ? text : text[..dash];
		return numbers.Contains('.') && numbers.IndexOfAnyExcept(VersionChars) < 0 && numbers.Length > 0 && char.IsAsciiDigit(numbers[0]);
	}

	private static readonly SearchValues<char> VersionChars = SearchValues.Create("0123456789.");

	/// <summary>All plain words and plain numbers, as in <c>sk-learn-intelex-2024-01</c>. A key's segments mix letters and digits.</summary>
	private static bool IsPackageName(string value)
	{
		return value.Split('-', '_').All(static segment => segment.All(char.IsAsciiLetter) || segment.All(char.IsAsciiDigit));
	}

	private static bool IsBenignValue(string value, string key, bool readAsCode)
	{
		return ValueShapes.IsPlaceholder(value)
			|| Keywords.Contains(value)
			|| string.Equals(value, key, StringComparison.OrdinalIgnoreCase)
			|| IsLocator(value)
			|| IsSourceLocation(value)
			|| IsFileName(value)
			|| CodeValues.IsCode(value, readAsCode)
			|| (readAsCode && ValueShapes.IsCodeReference(value));
	}

	/// <summary>A compiler-style reference such as <c>Program.cs:12</c> or <c>Program.cs:12,34</c>.</summary>
	private static bool IsSourceLocation(string value)
	{
		int colon = value.LastIndexOf(':');
		if (colon <= 0 || colon == value.Length - 1)
		{
			return false;
		}

		return value.AsSpan(colon + 1).IndexOfAnyExcept(LineNumberChars) < 0 && IsFileName(value[..colon]);
	}

	/// <summary>A file name or relative path with a recognized source extension, built only from name characters.</summary>
	private static bool IsFileName(string value)
	{
		int dot = value.LastIndexOf('.');
		return dot > 0
			&& SourceExtensions.Contains(value[(dot + 1)..])
			&& value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/' or '\\');
	}

	private static bool IsPasswordValue(string value, bool explicitlyAssigned)
	{
		return value.Length >= PasswordLength && (explicitlyAssigned || value.Any(c => !char.IsAsciiLetter(c)));
	}

	/// <summary>True when only a table's opening pipe precedes the key on its line.</summary>
	private static bool IsTableRow(string input, int keyStart)
	{
		int i = keyStart - 1;
		while (i >= 0 && input[i] is ' ' or '\t' or '`' or '*')
		{
			i--;
		}

		return i >= 0 && input[i] == '|';
	}

	/// <summary>A bare word is a password in <c>password: admin</c> on its own line, but not in <c>password: the one you chose</c>.</summary>
	private static bool IsWholeYamlLine(string input, int keyStart, int valueEnd)
	{
		return IsAtLineStart(input, keyStart) && IsLineBlankFrom(input, valueEnd);
	}

	private static bool IsAtLineStart(string input, int position)
	{
		for (int i = position - 1; i >= 0 && input[i] is not ('\n' or '\r'); i--)
		{
			if (input[i] is not (' ' or '\t' or '-'))
			{
				return LineStartsWithExport(input, i);
			}
		}

		return true;
	}

	/// <summary>True when what precedes on the line is the word <c>export</c> or <c>set</c>.</summary>
	private static bool LineStartsWithExport(string input, int wordEnd)
	{
		int start = wordEnd;
		while (start > 0 && char.IsAsciiLetter(input[start - 1]))
		{
			start--;
		}

		ReadOnlySpan<char> word = input.AsSpan(start, wordEnd + 1 - start);
		return IsBlankBack(input, start) && (word.SequenceEqual("export") || word.SequenceEqual("set"));
	}

	/// <summary>True when only indentation stands between the position and the start of its line.</summary>
	private static bool IsBlankBack(string input, int position)
	{
		int i = position - 1;
		while (i >= 0 && input[i] is ' ' or '\t')
		{
			i--;
		}

		return i < 0 || input[i] is '\n' or '\r';
	}

	private static bool IsLineBlankFrom(string input, int position)
	{
		for (int i = position; i < input.Length && input[i] is not ('\n' or '\r'); i++)
		{
			if (input[i] is not (' ' or '\t'))
			{
				return false;
			}
		}

		return true;
	}

	private static bool StartsNameAssignment(string input, int position)
	{
		int cursor = position;
		while (cursor < input.Length && (char.IsAsciiLetterOrDigit(input[cursor]) || input[cursor] is '_' or '-' or '.'))
		{
			cursor++;
		}

		return cursor > position && cursor < input.Length && input[cursor] == '=';
	}

	private static int RunEnd(string input, int position)
	{
		int cursor = position;
		while (cursor < input.Length && !char.IsWhiteSpace(input[cursor]) && input[cursor] is not ('"' or '\'' or '`' or '\\'))
		{
			cursor++;
		}

		return cursor;
	}

	private static bool IsQuoted(string input, int valueStart)
	{
		char before = input[valueStart - 1];
		if (before is '"' or '\'' or '`')
		{
			return true;
		}

		return valueStart >= 3 && input[valueStart - 3] == '%' && input[valueStart - 2] == '2' && before is '2' or '7';
	}

	private static bool IsUpperSnake(string key)
	{
		return key.TrimStart('-').All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');
	}

	/// <summary>A path or URL under a secret-sounding name, such as <c>token: /var/run/secrets/token</c>. Credentials in URLs have their own rules.</summary>
	private static bool IsLocator(string value)
	{
		if (value.Contains("://", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
		{
			return true;
		}

		if (!IsRootedPath(value))
		{
			return false;
		}

		// Base64 can start with a slash too. A path's segments read as names, and /wJa.../K7... do not.
		return (value.Count(c => c is '/' or '\\') >= 2 || value[0] is '.' or '~') && SegmentsReadAsNames(value);
	}

	private static bool SegmentsReadAsNames(string value)
	{
		foreach (string segment in value.Split('/', '\\'))
		{
			if (!SegmentReadsAsName(segment))
			{
				return false;
			}
		}

		return true;
	}

	private static bool SegmentReadsAsName(string segment)
	{
		if (segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':')
		{
			return true;
		}

		int dot = segment.LastIndexOf('.');
		string stem = dot > 0 ? segment[..dot] : segment;
		bool lowerAndDigits = stem.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-' or '_' or ':' or '~');
		return stem.Length == 0 || (lowerAndDigits && stem.Length <= 32) || ValueShapes.IsWordy(stem);
	}

	private static bool IsRootedPath(string value)
	{
		return value.StartsWith('/')
			|| value.StartsWith("./", StringComparison.Ordinal)
			|| value.StartsWith("../", StringComparison.Ordinal)
			|| value.StartsWith("~/", StringComparison.Ordinal)
			|| (value.Length > 2 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/');
	}

	private static string StripPlaceholders(string value)
	{
		const string Open = "[REDACTED:";
		string result = value;
		int start = result.IndexOf(Open, StringComparison.Ordinal);
		while (start >= 0)
		{
			int close = result.IndexOf(']', start);
			if (close < 0)
			{
				// A placeholder cut off by a title's length limit, or else not a placeholder at all.
				return ValueShapes.IsPlaceholderName(result.AsSpan(start + Open.Length)) ? result[..start] : result;
			}

			result = result.Remove(start, close - start + 1);
			start = result.IndexOf(Open, StringComparison.Ordinal);
		}

		return result;
	}
}
