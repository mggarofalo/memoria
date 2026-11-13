using System.Text.RegularExpressions;

namespace Memoria.Core.Redaction;

/// <summary>
/// Every detector, in the order they run. Redaction is best effort, so this is a short list on purpose: private-key blocks,
/// JWTs, passwords in URLs, the common command-line credentials, plain <c>password=</c> and <c>token:</c> assignments, and
/// gitleaks's default rules for formatted provider tokens. A new rule needs a real secret that got through, and a test that holds it.
/// All patterns use <see cref="RegexOptions.NonBacktracking"/>, which rules out catastrophic backtracking and also rules out
/// lookaround and backreferences. Context on either side of a secret is therefore consumed by the pattern and excluded from the
/// <c>secret</c> group.
/// </summary>
internal static class RedactionRules
{
	/// <summary>
	/// Per-call cap. The engine is linear, so this only trips on pathological machine load, and tripping refuses the document.
	/// A property, not a field, so <see cref="GitleaksRules"/> can build patterns while this class is still initializing.
	/// </summary>
	private static TimeSpan MatchTimeout => TimeSpan.FromSeconds(120);

	/// <summary>The closing quote of a JSON or shell key, at any escaping depth, or a markdown bold marker.</summary>
	private const string Close = @"(?:\\*[""'`]|%22|%27)?\*{0,2}";

	/// <summary>
	/// Assignment operators, including a type annotation (<c>password: str = "x"</c>), URL-encoded forms, and a
	/// YAML value on the next line, with or without a block scalar indicator (<c>token: |</c>, <c>token: &gt;-</c>).
	/// </summary>
	private const string Operator =
		@"[ \t]{0,4}(?::=|=>|={1,3}|:(?:[ \t]*[A-Za-z_][A-Za-z0-9_.\[\]]*[ \t]*=)?|:[ \t]*(?:[|>][-+0-9]*[ \t]*)?\r?\n[ \t]+|%3[AaDd])[ \t]{0,4}";

	/// <summary>An HTTP authorization scheme before the value, as in <c>Authorization: Bearer x</c>, which is not itself the secret.</summary>
	private const string Scheme = @"(?:(?i:bearer|basic|token|apikey)[ \t]+)?";

	private const string UnquotedRun = @"(?:[^\s""'`,;&|\\)\]}>]|\\[^nrt""'\\\r\n])+";

	/// <summary>
	/// A value, quoted or not. Each escaping depth has its own branch because the
	/// value in <c>\"token\":\"abc\"</c> ends at an escaped quote, whereas the value in
	/// <c>"a\"b"</c> continues past one. An unquoted value stops at an escaped
	/// newline, because an env dump in a JSON string reads <c>A=1\nB=2</c>. A doubled
	/// single quote is YAML's and SQL's escape for one.
	/// </summary>
	private const string Value =
		@"\*{0,2}[ \t]{0,4}(?:"
		+ @"\\+""(?<secret>(?:[^""\\\r\n]|\\+[^""\\\r\n])+)"
		+ @"|\\+'(?<secret>(?:[^'\\\r\n]|\\+[^'\\\r\n])+)"
		+ @"|""(?<secret>(?:[^""\\\r\n]|\\[^\r\n])+)"
		+ @"|'(?<secret>(?:[^'\\\r\n]|\\[^\r\n]|'')+)"
		+ @"|`(?<secret>[^`\r\n]+)"
		+ @"|%2[27](?<secret>(?:[^%&\s""']|%[013-9A-Fa-f][0-9A-Fa-f]|%2[013-689A-Fa-f])+)"
		+ @"|(?<secret>" + UnquotedRun + @"(?:[)\]}>]+" + UnquotedRun + @")*)"
		+ @")";

	/// <summary>A name that suggests a secret. <see cref="SecretKeyNames"/> makes the real decision by splitting it into words.</summary>
	private const string SecretKey =
		@"(?<key>[A-Za-z0-9_.\-]*(?:password|passwd|pwd|passphrase|psk|secret|token|pass|auth|authorization|credentials?|keys?(?:[_.\-]data)?)[A-Za-z0-9_.\-]*"
		+ @"|[A-Za-z0-9_.\-]+[ ]keys?)";

	/// <summary>A line ending in a shell's continuation character: backslash, PowerShell's backtick, or cmd's caret.</summary>
	private const string Continuation = @"[\\`^]\r?\n";

	/// <summary>Space between words of a command, which may run onto the next line.</summary>
	private const string CommandGap = @"(?:\s|" + Continuation + ")+";

	/// <summary>The arguments before a flag, on this line or a continued one.</summary>
	private const string CommandArguments = @"(?:[^\r\n|]|" + Continuation + ")*?";

	/// <summary>What comes before a flag. A plain newline is not here: it ends the command.</summary>
	private const string FlagStart = @"(?:[ \t]|" + Continuation + ")";

	/// <summary>Space between a flag and its value, which may run onto the next line.</summary>
	private const string FlagGap = @"(?:[ ]|" + Continuation + ")+";

	/// <summary>A PEM marker line naming a private key, of any flavour: RSA, EC, OPENSSH, ENCRYPTED, and PGP's key block.</summary>
	private const string KeyMarker = @"[ A-Z0-9]*PRIVATE KEY(?: BLOCK)?-----";

	/// <summary>What a key's body is made of: base64, line breaks real or JSON-escaped, spaces, and an encrypted key's headers.</summary>
	private const string KeyBody = @"(?:[A-Za-z0-9+/=:,\s\\]|-[A-Za-z0-9])";

	/// <summary>The same, without white space, which a body ends on.</summary>
	private const string KeyBodyEnd = @"(?:[A-Za-z0-9+/=:,\\]|-[A-Za-z0-9])";

	/// <summary>
	/// A PEM private key: the BEGIN line, the body, and the END line. When an END line follows, everything up to it goes, whatever
	/// stands between: PGP's comment headers, quote or comment markers before each line, a string split and joined in code. A key
	/// cut off before its END line goes to the end of its body, and perhaps a few words after it, which costs less than leaving the
	/// key. A marker with no key material after it, as when code or prose names one, stays. Certificates and public keys do not match.
	/// </summary>
	public static readonly RedactionRule PrivateKey = new(
		"private-key",
		Pattern(@"-----BEGIN" + KeyMarker + "(?:" + KeyBody + "*" + KeyBodyEnd + @")?(?:\s*-----END" + KeyMarker + ")?"),
		c => HasKeyMaterial(c.Input.AsSpan(c.Secret.Index, KeyEnd(c) - c.Secret.Index)),
		extend: KeyEnd,
		literal: "PRIVATE KEY");

	/// <summary>How far ahead an END line is looked for. A real key, even RSA at 8192 bits, is well inside it.</summary>
	private const int MaxKeyBlock = 16384;

	/// <summary>The end of the END line that closes this block, when the match stopped short of it and no other BEGIN comes first.</summary>
	private static int KeyEnd(SecretCandidate candidate)
	{
		if (candidate.Value.Contains("-----END", StringComparison.Ordinal))
		{
			return candidate.End;
		}

		string input = candidate.Input;
		int limit = Math.Min(input.Length - candidate.End, MaxKeyBlock);
		int end = input.IndexOf("-----END", candidate.End, limit, StringComparison.Ordinal);
		int begin = input.IndexOf("-----BEGIN", candidate.End, limit, StringComparison.Ordinal);
		if (end < 0 || (begin >= 0 && begin < end))
		{
			return candidate.End;
		}

		Match marker = EndMarker().Match(input, end);
		return marker.Success && marker.Index == end ? marker.Index + marker.Length : candidate.End;
	}

	private static Regex EndMarker()
	{
		return s_endMarker.Value;
	}

	private static readonly Lazy<Regex> s_endMarker = new(() => Pattern("-----END" + KeyMarker));

	/// <summary>The shortest run of base64 that shows a body is a key. A real key's lines are 64 characters, and prose has no such run.</summary>
	private const int MinKeyRun = 32;

	private static bool HasKeyMaterial(ReadOnlySpan<char> block)
	{
		int run = 0;
		foreach (char c in block["-----BEGIN".Length..])
		{
			run = char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' ? run + 1 : 0;
			if (run >= MinKeyRun)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// JSON Web Tokens and JWEs. A JWT has a base64url JSON header and payload, with or without a signature.
	/// A JWE's second part is random or empty, so it is recognized by having five parts.
	/// </summary>
	public static readonly RedactionRule Jwt = new(
		"jwt",
		Pattern(@"eyJ[A-Za-z0-9_\-]{6,}\.(?:e[wy][A-Za-z0-9_\-]{6,}(?:\.[A-Za-z0-9_\-]*){0,3}|[A-Za-z0-9_\-]*(?:\.[A-Za-z0-9_\-]+){3})"),
		literal: "eyJ");

	/// <summary>
	/// The password in a URL's user info, for any scheme: <c>postgres://app:x@db</c>, <c>redis://:x@cache</c>, <c>https://u:x@host</c>,
	/// and in Go's <c>user:x@tcp(host)</c>. The user name and the host stay.
	/// </summary>
	public static readonly RedactionRule UrlPassword = new(
		"url-password",
		Pattern(
			@"[A-Za-z][A-Za-z0-9+.\-]*://[^\s/:@""'`<>\[]*:(?<secret>[^\s/""'`<>]+)@"
			+ @"|(?:^|[\s""'`=(])[A-Za-z0-9_.\-]+:(?<secret>[^\s:@/""'`]+)@(?:tcp|unix)\("),
		c => PlainValues.IsPassword(c.Value),
		literal: "@");

	/// <summary>The password in <c>curl -u user:password</c>. Anchored on the flag, so every occurrence on a line is found and <c>--user-agent</c> is not one.</summary>
	public static readonly RedactionRule CurlUserPassword = new(
		"curl-user-password",
		Pattern(@"(?:^|[\s""'`])(?:-[A-Za-z]*u|--user)[ =]?[""']?[^\s:""'=]*:(?<secret>[^\s""'`]+)"),
		c => PlainValues.IsPassword(c.Value) && CommandLines.IsCommand(c.Input, c.Match.Index, "curl"));

	/// <summary>Passwords in the conventional places of common tools: <c>sshpass -p</c>, <c>docker login -p</c>, <c>redis-cli -a</c>, <c>htpasswd -b</c>, <c>az login -p</c>, <c>ssh-keygen -N</c> and <c>-P</c>, <c>cmdkey /pass:</c>, <c>net use</c>, <c>setx</c>.</summary>
	public static readonly RedactionRule CommandPassword = new(
		"command-password",
		Pattern(
			@"(?:\bsshpass" + CommandGap + "-p(?:" + FlagGap + @")?|\bdocker" + CommandGap + @"login\b" + CommandArguments + FlagStart + "-p" + FlagGap + @"|\bredis-cli\b" + CommandArguments + FlagStart + "-a" + FlagGap + @"|\baz\s+login\b" + CommandArguments + FlagStart + "(?:-p|--password)" + FlagGap
			+ @"|\bhtpasswd\s+-[a-mo-zA-Z]*b[a-zA-Z]*\s+\S+\s+\S+\s+|\bhtpasswd\s+-[a-zA-Z]*n[a-zA-Z]*b[a-zA-Z]*\s+\S+\s+"
			+ @"|\bssh-keygen\b[^\r\n|]*?\s-[NP][ ]+|\bcmdkey\b[^\r\n|]*?\s/pass:|\bnet\s+use\s+(?:[A-Za-z]:\s+)?\\\\\S+\s+|\bsetx\s+\S*(?i:pass|secret|token|key)\S*\s+)"
			+ @"(?:""(?<secret>[^""\r\n]+)|'(?<secret>[^'\r\n]+)|(?<secret>[^\s""'`|;&]+))"),
		c => PlainValues.IsPassword(c.Value));

	/// <summary><c>mysql -pSECRET</c>, <c>mysqldump</c>, <c>mysqladmin</c>, and <c>mariadb</c>, where the password is attached to the flag.</summary>
	public static readonly RedactionRule MysqlPassword = new(
		"mysql-password",
		Pattern(@"\s-p(?:'(?<secret>[^'\r\n]+)|""(?<secret>[^""\r\n]+)|(?<secret>[^\s""'`|;&]+))"),
		c => PlainValues.IsPassword(c.Value) && !PlainValues.IsPortMapping(c.Value)
			&& CommandLines.IsCommand(c.Input, c.Match.Index, "mysql", "mysqldump", "mysqladmin", "mariadb", "mariadb-dump"),
		literal: "-p");

	/// <summary>
	/// The Mongo shell's <c>-p</c>, as in <c>mongosh -u admin -p x</c> or <c>mongo -px</c>. The value may follow a space, so a value
	/// that starts with a dash is the next flag, and <c>-p</c> alone prompts for the password.
	/// </summary>
	public static readonly RedactionRule MongoPassword = new(
		"mongo-password",
		Pattern(@"\s-p[ \t]*(?:'(?<secret>[^'\r\n]+)'|""(?<secret>[^""\r\n]+)""|(?<secret>[^\s""'`|;&\-][^\s""'`|;&]*))"),
		c => PlainValues.IsPassword(c.Value) && !PlainValues.IsPortMapping(c.Value)
			&& CommandLines.IsCommand(c.Input, c.Match.Index, "mongosh", "mongo", "mongodump", "mongorestore", "mongoexport", "mongoimport", "mongostat", "mongotop", "mongofiles"),
		literal: "-p");

	/// <summary>Command line flags with the value after a space or as the next array item: <c>--password x</c>, <c>--api-key x</c>, <c>["--token", "x"]</c>.</summary>
	public static readonly RedactionRule SecretFlag = new(
		"secret-flag",
		Pattern(@"(?:^|[\s""'`\[,])(?<key>-{1,2}[A-Za-z0-9\-]*(?i:password|passwd|pwd|passphrase|pass|token|secret|auth|key|api-?key)[A-Za-z0-9\-]*)(?:[""'][ \t]*,[ \t]*|[ \t]+)" + Value),
		c => SecretKeyNames.IsSecretFlag(c.Match.Groups["key"].Value) && AssignedValues.IsSecret(c),
		extend: AssignedValues.End);

	/// <summary>
	/// The catch-all: <c>password=</c>, <c>api_key:</c>, <c>API key:</c>, <c>token":"..."</c>, <c>.env</c> lines,
	/// <c>export FOO_SECRET=</c>, <c>Authorization: Bearer</c>, markdown table rows, and YAML values on the next line. The name
	/// decides, through <see cref="SecretKeyNames"/>, so <c>max_tokens: 4096</c> stays; the value need only not be a reference such
	/// as <c>$TOKEN</c> or <c>getPassword()</c>. <see cref="AssignedValues"/> says how far an unquoted value runs.
	/// </summary>
	public static readonly RedactionRule SecretAssignment = new(
		"secret-assignment",
		Pattern(@"(?i:" + SecretKey + @")" + Close + @"(?:" + Operator + @"|[ \t]{0,4}\|[ \t]{0,4})" + Scheme + Value),
		c => SecretKeyNames.IsSecretName(c.Match.Groups["key"].Value) && AssignedValues.IsSecret(c),
		extend: AssignedValues.End);

	/// <summary>The rules, in execution order. The imported rules run last, on what ours left.</summary>
	public static readonly IReadOnlyList<RedactionRule> All =
	[
		PrivateKey,
		Jwt,
		UrlPassword,
		CurlUserPassword,
		CommandPassword,
		MysqlPassword,
		MongoPassword,
		SecretFlag,
		SecretAssignment,
		.. GitleaksRules.All,
	];

	/// <summary>How every rule's pattern is built, imported ones included.</summary>
	internal static Regex Pattern(string pattern)
	{
		return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout);
	}
}
