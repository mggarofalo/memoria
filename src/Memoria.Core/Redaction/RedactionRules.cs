using System.Text.RegularExpressions;

namespace Memoria.Core.Redaction;

/// <summary>
/// Every detector, in the order they run. To add one: declare a field with a doc
/// comment, add it to <see cref="All"/>, and add positive and negative cases to
/// <c>RedactionCases</c> in the tests, which fail for a rule that has neither. A token with
/// a distinctive prefix needs only a line in <see cref="ProviderTokens"/>.
/// Order matters: distinctive formats go first so they get the most specific
/// label, and contextual rules such as <see cref="SecretAssignment"/> go last.
/// All patterns use <see cref="RegexOptions.NonBacktracking"/>, which rules out
/// catastrophic backtracking and also rules out lookaround and backreferences.
/// Context on either side of a secret is therefore consumed by the pattern and
/// excluded from the <c>secret</c> group.
/// </summary>
internal static class RedactionRules
{
	/// <summary>
	/// Per-call cap. The engine is linear, so this only trips on pathological machine load, and tripping refuses the document.
	/// A property, not a field, so <see cref="GitleaksRules"/> can build patterns while this class is still initializing.
	/// </summary>
	private static TimeSpan MatchTimeout => TimeSpan.FromSeconds(120);

	/// <summary>
	/// What may precede a token. Plain <c>\b</c> would miss the most common
	/// transcript shapes: in a JSON string <c>\nghp_...</c> the character before
	/// the token is the letter <c>n</c>, and in <c>token%3Dghp_...</c> it is <c>D</c>.
	/// </summary>
	private const string Left = @"(?:^|[^A-Za-z0-9]|\\+[nrtfbv0]|\\u[0-9A-Fa-f]{4}|%[0-9A-Fa-f]{2})";

	/// <summary>A left boundary for a prefix that is also ordinary text, such as <c>key-</c> in <c>cache-key-abc</c>: no dash may touch it.</summary>
	private const string StrictLeft = @"(?:^|[^A-Za-z0-9\-]|\\+[nrtfbv0]|\\u[0-9A-Fa-f]{4}|%[0-9A-Fa-f]{2})";

	/// <summary>What may follow a fixed-length token, so a run of base64 that happens to contain the prefix is not cut.</summary>
	private const string Right = @"(?:$|[^A-Za-z0-9])";

	/// <summary>The closing quote of a JSON or shell key, at any escaping depth, or a markdown bold marker.</summary>
	private const string Close = @"(?:\\*[""'`]|%22|%27)?\*{0,2}";

	/// <summary>
	/// Assignment operators, including a type annotation (<c>password: str = "x"</c>), URL-encoded forms, and a
	/// YAML value on the next line, with or without a block scalar indicator (<c>token: |</c>, <c>token: &gt;-</c>).
	/// </summary>
	private const string Operator =
		@"[ \t]{0,4}(?::=|=>|={1,3}|:(?:[ \t]*[A-Za-z_][A-Za-z0-9_.\[\]]*[ \t]*=)?|:[ \t]*(?:[|>][-+0-9]*[ \t]*)?\r?\n[ \t]+|%3[AaDd])[ \t]{0,4}";

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

	private const string Slash = @"(?:\\*/|%2[Ff])";

	private const string Space = @"(?:[ +]|%20)";

	private const string KeyMarkerName = @"(?:[a-z0-9]+" + Space + @"){0,4}private" + Space + @"key(?:" + Space + @"block)?";

	/// <summary>A line ending in a long base64 run, with anything before the run.</summary>
	private const string RunOnLine = @"[A-Za-z0-9+/]{40,}={0,2}";

	/// <summary>What a log or a list may leave after a body line: closing punctuation and a short note in brackets, but no words.</summary>
	private const string AfterRun = @"[ \t]*[^A-Za-z0-9\s]{0,4}[ \t]*(?:\([^()\r\n]{0,20}\)[ \t]*)?";

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

	/// <summary>
	/// PEM private keys of every flavor, including PGP blocks and keys cut off with no END line. The marker
	/// may be lower case or have a single dash. <see cref="PemBlock"/> finds the end. A marker named in code or
	/// prose with no key material behind it is left alone. Certificates and public keys do not match.
	/// </summary>
	public static readonly RedactionRule PrivateKey = new(
		"private-key",
		Pattern(@"(?i:-*[ ]?begin" + Space + KeyMarkerName + @"[ ]?-*)"),
		findSpan: PemBlock.FindForward,
		linesMatter: true);

	/// <summary>An END marker with its BEGIN cut off, which leaves the end of a real key above it.</summary>
	public static readonly RedactionRule PrivateKeyEnd = new(
		"private-key-end",
		Pattern(@"(?i:-+[ ]?end" + Space + KeyMarkerName + @"[ ]?-*)"),
		findSpan: PemBlock.FindBackward,
		linesMatter: true);

	/// <summary>
	/// A run of at least three lines holding base64 of one width, whatever precedes each and with no words after it, with no marker in sight.
	/// <see cref="PemBlock.IsUnmarkedKeyBody"/> rules out data: MIME's width, hex, data URIs, and bodies under a non-private BEGIN.
	/// The span ends before the last newline, so a message's end stays a line end.
	/// </summary>
	public static readonly RedactionRule PrivateKeyLines = new(
		"private-key-lines",
		Pattern(
			@"(?:^|[\r\n])[^\r\n]*?(?<secret>" + RunOnLine + "(?:" + AfterRun + @"\r?\n[^\r\n]*?" + RunOnLine + "){2,})" + AfterRun + @"(?:$|[\r\n])"),
		PemBlock.IsUnmarkedKeyBody,
		extend: PemBlock.ExtendLines,
		linesMatter: true,
		skipRejected: true);

	/// <summary>A key's base64 with no marker at all. The decoded head proves it: a certificate or public key starts differently.</summary>
	public static readonly RedactionRule PrivateKeyDer = new(
		"private-key-der",
		Pattern(@"(?:^|[^A-Za-z0-9+/])(?<secret>(?:(?:MII|MHc|MIG|b3BlbnNzaC1rZXktdjE)[A-Za-z0-9+/]{60,}|MC4CAQAwBQYDK2Vw[A-Za-z0-9+/]{20,})={0,2})"),
		c => PemBlock.IsDerPrivateKey(c.Value));

	/// <summary>A whole PEM key encoded as base64, as in a kubeconfig's <c>client-key-data</c>. A base64 certificate survives.</summary>
	public static readonly RedactionRule Base64PrivateKey = new(
		"base64-private-key",
		Pattern(@"LS0tLS1CRUdJTi[A-Za-z0-9+/]{50,}={0,2}"),
		c => PemBlock.IsBase64Pem(c.Value),
		literal: "LS0tLS1CRUdJTi");

	/// <summary>
	/// AWS secret access keys, recognizable only by context: a name such as
	/// <c>aws_secret_access_key</c>, or sitting right after an access key id as in
	/// <c>AKIA...:secret</c> or a CSV export. Runs before <see cref="AwsAccessKey"/>,
	/// which would otherwise erase that context.
	/// </summary>
	public static readonly RedactionRule AwsSecretKey = new(
		"aws-secret-key",
		Pattern(
			@"(?:(?i:(?:aws[_.\-]?secret[_.\-]?(?:access[_.\-]?)?key|secret[_.\-]?access[_.\-]?key))" + Close + Operator + @"(?:\\*[""'`]|%2[27])?"
			+ @"(?<secret>(?:[A-Za-z0-9/+]|%2[BbFf]){40,}(?:=|%3[Dd]){0,2})"
			+ @"|(?:AKIA|ASIA)[A-Z0-9]{16}[""']?(?:[:,;/|\s]|\\+[nt]){1,6}[""']?(?<secret>[A-Za-z0-9/+]{36,44})" + Right + ")"),
		c => !ValueShapes.IsPlaceholder(c.Value));

	/// <summary>A value shaped like an AWS secret soon after an access key id, in prose or on the next line, with no name to go by. A 40-digit hex value there is more likely a commit.</summary>
	public static readonly RedactionRule AwsSecretNearId = new(
		"aws-secret-near-id",
		Pattern(@"(?:AKIA|ASIA)[A-Z0-9]{16}(?:[^\r\n]|\r?\n){1,160}?[^A-Za-z0-9/+](?<secret>[A-Za-z0-9/+]{40})(?:$|[^A-Za-z0-9/+=])"),
		SecretHeuristics.IsAwsSecretNearId);

	/// <summary>Anthropic API, admin, and OAuth keys: <c>sk-ant-api03-</c>, <c>sk-ant-admin01-</c>, <c>sk-ant-oat01-</c>.</summary>
	public static readonly RedactionRule AnthropicKey = new(
		"anthropic-key",
		Pattern(@"sk-ant-[A-Za-z0-9_\-]{20,}"),
		SecretHeuristics.IsPrefixedToken,
		literal: "sk-ant-");

	/// <summary>OpenAI <c>sk-</c>, <c>sk-proj-</c>, <c>sk-svcacct-</c>, and <c>sk-admin-</c> keys. The left boundary keeps <c>task-</c> and <c>disk-</c> out, and the judge keeps <c>sk-learn-*</c> names out.</summary>
	public static readonly RedactionRule OpenAiKey = new(
		"openai-key",
		Pattern(Left + @"(?<secret>sk-[A-Za-z0-9_\-]{20,})"),
		c => SecretHeuristics.IsOpenAiKey(c.Value),
		literal: "sk-");

	/// <summary>GitHub classic tokens (<c>ghp_ gho_ ghu_ ghs_ ghr_</c>) and fine-grained tokens (<c>github_pat_</c>).</summary>
	public static readonly RedactionRule GitHubToken = new(
		"github-token",
		Pattern(@"(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})"),
		c => !ValueShapes.IsPlaceholder(c.Value));

	/// <summary>AWS access key ids: long-term <c>AKIA</c> and temporary <c>ASIA</c>, exactly 20 characters. Keeps the documentation sample ending in <c>EXAMPLE</c>.</summary>
	public static readonly RedactionRule AwsAccessKey = new(
		"aws-access-key",
		Pattern(Left + @"(?<secret>(?:AKIA|ASIA)[A-Z0-9]{16})" + Right),
		c => !ValueShapes.IsPlaceholder(c.Value) && !c.Value.EndsWith("EXAMPLE", StringComparison.Ordinal));

	/// <summary>
	/// JSON Web Tokens and JWEs. A JWT has a base64url JSON header and payload, with or without a signature.
	/// A JWE's second part is random or empty, so it is recognized by having five parts.
	/// </summary>
	public static readonly RedactionRule Jwt = new(
		"jwt",
		Pattern(@"eyJ[A-Za-z0-9_\-]{6,}\.(?:e[wy][A-Za-z0-9_\-]{6,}(?:\.[A-Za-z0-9_\-]*){0,3}|[A-Za-z0-9_\-]*(?:\.[A-Za-z0-9_\-]+){3})"),
		literal: "eyJ");

	/// <summary>Slack tokens: <c>xoxb- xoxp- xoxa- xoxs- xoxr- xoxe- xoxd-</c> and app-level <c>xapp-</c>. Browser <c>xoxd-</c> values are URL-encoded.</summary>
	public static readonly RedactionRule SlackToken = new(
		"slack-token",
		Pattern(@"(?:xoxd-(?:[A-Za-z0-9\-+/=]|%[0-9A-Fa-f]{2}){10,}|xox[a-ce-z]-[A-Za-z0-9\-]{10,}|xapp-[0-9]-[A-Za-z0-9\-]{10,})"),
		SecretHeuristics.IsPrefixedToken);

	/// <summary>Slack incoming webhook paths, which authorize posting with no other credential. Escaped and encoded slashes included. The host stays readable.</summary>
	public static readonly RedactionRule SlackWebhook = new(
		"slack-webhook",
		Pattern(@"hooks\.slack\.com" + Slash + @"services" + Slash + @"(?<secret>T[A-Za-z0-9]+" + Slash + @"B[A-Za-z0-9]+" + Slash + @"[A-Za-z0-9]{16,})"),
		c => !ValueShapes.IsPlaceholder(c.Value[(c.Value.LastIndexOf('/') + 1)..]),
		literal: "hooks.slack.com");

	/// <summary>Google API keys: <c>AIza</c> plus exactly 35 characters, bounded so a base64 image that contains the four letters is not cut.</summary>
	public static readonly RedactionRule GoogleApiKey = new(
		"google-api-key",
		Pattern(Left + @"(?<secret>AIza[0-9A-Za-z_\-]{35})(?:$|[^A-Za-z0-9_\-])"),
		c => !ValueShapes.IsPlaceholder(c.Value),
		literal: "AIza");

	/// <summary>Google OAuth credentials: access tokens (<c>ya29.</c>, which can hold dots), client secrets (<c>GOCSPX-</c>), and refresh tokens (<c>1//0</c>).</summary>
	public static readonly RedactionRule GoogleOAuthToken = new(
		"google-oauth-token",
		Pattern(Left + @"(?<secret>(?:ya29\.[0-9A-Za-z_\-]+(?:\.[0-9A-Za-z_\-]+)*|GOCSPX-[0-9A-Za-z_\-]{20,}|1//0[0-9A-Za-z_\-]{30,}))"),
		c => c.Value.Length >= 24 && !ValueShapes.IsPlaceholder(c.Value));

	/// <summary>Stripe secret and restricted keys in live and test mode, and webhook signing secrets. Publishable <c>pk_</c> keys are public by design and survive.</summary>
	public static readonly RedactionRule StripeKey = new(
		"stripe-key",
		Pattern(@"(?:(?:sk|rk)_(?:live|test)_[0-9A-Za-z]{16,}|whsec_[0-9A-Za-z+/=]{24,})"),
		c => !ValueShapes.IsPlaceholder(c.Value));

	/// <summary>npm automation and publish tokens. The 36-character unbroken body keeps <c>npm_config_*</c> and <c>npm_package_*</c> variables out.</summary>
	public static readonly RedactionRule NpmToken = new(
		"npm-token",
		Pattern(Left + @"(?<secret>npm_[A-Za-z0-9]{36,})"),
		c => !ValueShapes.IsPlaceholder(c.Value),
		literal: "npm_");

	/// <summary>Azure storage and Service Bus keys in connection strings: <c>AccountKey=</c> and <c>SharedAccessKey=</c>, URL-encoded or not.</summary>
	public static readonly RedactionRule AzureStorageKey = new(
		"azure-storage-key",
		Pattern(@"(?i:AccountKey|SharedAccessKey)[ \t]*(?:=|%3[Dd])[ \t]*(?<secret>(?:[A-Za-z0-9+/=]|%2[BbFf]|%3[Dd]){20,})"),
		c => !ValueShapes.IsPlaceholder(c.Value));

	/// <summary>The <c>sig=</c> of an Azure SAS URL. Requires a query-string boundary, including JSON-escaped <c>\u0026</c> and HTML-escaped <c>&amp;amp;</c>, and a value long enough to be a signature.</summary>
	public static readonly RedactionRule AzureSasSignature = new(
		"azure-sas-signature",
		Pattern(@"(?:[?&;\s""'`]|%26|%3[Ff]|\\u0026)sig(?:=|%3[Dd])(?<secret>[A-Za-z0-9%+/=_\-]{16,})"),
		SecretHeuristics.IsRealToken);

	/// <summary>S3 and GCS presigned URL signatures, which grant access to whoever holds the URL.</summary>
	public static readonly RedactionRule PresignedUrlSignature = new(
		"presigned-url-signature",
		Pattern(@"(?i:X-(?:Amz|Goog)-Signature)(?:=|%3[Dd])(?<secret>[A-Za-z0-9%]{32,})"),
		c => !ValueShapes.IsPlaceholder(c.Value));

	/// <summary>Hugging Face user access tokens.</summary>
	public static readonly RedactionRule HuggingFaceToken = new(
		"huggingface-token",
		Pattern(Left + @"(?<secret>hf_[A-Za-z0-9]{30,})"),
		c => !ValueShapes.IsPlaceholder(c.Value),
		literal: "hf_");

	/// <summary>GitLab personal, deploy, runner, pipeline, OAuth application, agent, incoming mail, and service account tokens, including the dotted routable form and runner registration tokens.</summary>
	public static readonly RedactionRule GitLabToken = new(
		"gitlab-token",
		Pattern(@"(?:gl(?:pat|dt|rt|ptt|soat|cbt|oas|agent|imt)-|GR1348941)[A-Za-z0-9_\-]{20,}(?:\.[A-Za-z0-9_\-]+)*"),
		SecretHeuristics.IsPrefixedToken);

	/// <summary>This project's own API keys, <c>mem_&lt;id&gt;_&lt;secret&gt;</c>. Metric names such as <c>mem_total_bytes</c> are rejected by shape.</summary>
	public static readonly RedactionRule MemoriaApiKey = new(
		"memoria-api-key",
		Pattern(Left + @"(?<secret>mem_[A-Za-z0-9]{2,}_[A-Za-z0-9_\-]{16,})"),
		c => SecretHeuristics.IsMemoriaKey(c.Value),
		literal: "mem_");

	/// <summary>Shared secrets this project's own services read from the environment. The name alone is decisive, so entropy is not consulted; only references such as <c>$X</c> and documentation placeholders survive.</summary>
	public static readonly RedactionRule MemoriaSharedSecret = new(
		"memoria-shared-secret",
		Pattern(@"(?:MEMORIA_QMD_SECRET|MEMORIA_QMD_WORKER_TOKEN|MEMORIA_WORKER_TOKEN|MEMORIA_SIDECAR_SECRET|Memoria__Sidecar__Secret)" + Close + Operator + Value),
		c => !ValueShapes.IsPlaceholder(c.JudgedValue) && !ValueShapes.IsCodeReference(c.JudgedValue) && !c.Value.Equals("unset", StringComparison.OrdinalIgnoreCase)
			&& !CodeValues.HasInterpolation(c.JudgedValue) && !CodeValues.IsCommandWord(c.Input, c.Value, c.End)
			&& !(c.Match.Index >= 2 && c.Input.AsSpan(c.Match.Index - 2, 2) is "${"));

	/// <summary>
	/// Passwords in URLs and connection strings for any scheme: postgres, mysql, mongodb, redis, amqp, https, jdbc.
	/// A password with a raw <c>/</c> is caught unless a numeric port stands before the slash, because
	/// <c>host:8080/p@x</c> is a path and <c>u:ab/cd@h</c> is not.
	/// </summary>
	public static readonly RedactionRule UrlPassword = new(
		"url-password",
		Pattern(
			@"[A-Za-z][A-Za-z0-9+.\-]*(?::|%3[Aa])(?:\\*/\\*/|%2[Ff]%2[Ff])[^\s/:@""'`<>\[]*(?::|%3[Aa])"
			+ @"(?:(?<secret>[^\s/""`]+)(?:@|%40)|(?<secret>[^\s/""`@]+/[^\s""`@]*)@)"),
		SecretHeuristics.IsUrlPasswordCandidate);

	/// <summary>A bare token as URL userinfo, as in <c>https://TOKEN@host/repo.git</c>. Requires a token-like value, so <c>ssh://git@host</c> survives.</summary>
	public static readonly RedactionRule UrlToken = new(
		"url-token",
		Pattern(@"[A-Za-z][A-Za-z0-9+.\-]*(?::|%3[Aa])(?:\\*/\\*/|%2[Ff]%2[Ff])(?<secret>[A-Za-z0-9_\-]{20,})(?:@|%40)"),
		c => !ValueShapes.IsPlaceholder(c.Value) && SecretHeuristics.IsTokenValue(c.Value));

	/// <summary><c>user:password@host</c> with no scheme, and Go's <c>user:password@tcp(host)</c>. Versions, image digests, and <c>mailto:</c> are ruled out by shape.</summary>
	public static readonly RedactionRule UserInfoPassword = new(
		"userinfo-password",
		Pattern(@"(?:^|[\s""'`=(])[A-Za-z0-9_.\-]+:(?<secret>[^\s:@/""'`]+)@[A-Za-z0-9]"),
		SecretHeuristics.IsBareUserInfoPassword);

	/// <summary>The password in an ADO.NET style connection string, where spaces are legal: <c>Server=x;Password=a b;</c>. The leading semicolon separates it from code.</summary>
	public static readonly RedactionRule ConnectionStringPassword = new(
		"connection-string-password",
		Pattern(@";[ \t]*(?i:Password|Pwd|User[ ]Password)[ \t]*=[ \t]*(?<secret>[^;""'\r\n\\]+)"),
		c => !ValueShapes.IsPlaceholder(c.JudgedValue) && !ValueShapes.IsCallOrMember(c.JudgedValue.Trim()));

	/// <summary><c>Authorization</c> and <c>Proxy-Authorization</c> headers with a Bearer, Basic, Token, or ApiKey scheme, in shell, JSON at any escaping depth, and URL-encoded form.</summary>
	public static readonly RedactionRule AuthorizationHeader = new(
		"authorization-header",
		Pattern(@"(?i:authorization)" + Close + Operator + @"(?:\\*[""'`]|%2[27])?(?:[ \t]|%20|\+)*(?i:bearer|basic|token|apikey)(?:[ \t]|%20|\+)+(?<secret>\$\{[ \t]*(?:""[^""\r\n]*""|'[^'\r\n]*')[ \t]*\}|[^\s""'`\\,;<>&]+(?:[,;]+[^\s""'`\\,;<>&]+)*)"),
		c => SecretHeuristics.IsExplicitCredential(CodeValues.UnwrapLiteral(c.JudgedValue)) && !CodeValues.HasInterpolation(c.JudgedValue));

	/// <summary>A bearer token with no header name around it, such as a value in a config or a log line. Stricter than <see cref="AuthorizationHeader"/>, because prose says "bearer" too.</summary>
	public static readonly RedactionRule BearerToken = new(
		"bearer-token",
		Pattern(@"(?i:\bbearer)(?:[ \t]|%20|\+)+(?<secret>[A-Za-z0-9._~+/=\-]{20,}(?:[,;]+[A-Za-z0-9._~+/=\-]+)*)"),
		c => !ValueShapes.IsPlaceholder(c.Value) && SecretHeuristics.IsTokenValue(c.Value));

	/// <summary>The password in <c>curl -u user:password</c>. Anchored on the flag, so every occurrence on a line is found and <c>--user-agent</c> is not one.</summary>
	public static readonly RedactionRule CurlUserPassword = new(
		"curl-user-password",
		Pattern(@"(?:^|[\s""'`])(?:-[A-Za-z]*u|--user)[ =]?[""']?[^\s:""'=]*:(?<secret>[^\s""'`]+)"),
		SecretHeuristics.IsCurlUserPassword);

	/// <summary><c>Cookie</c> and <c>Set-Cookie</c> headers holding a session-length value. The whole header goes, because any cookie in it may be the live one.</summary>
	public static readonly RedactionRule CookieHeader = new(
		"cookie-header",
		Pattern(@"(?i:\b(?:set-)?cookie)(?:\\*[""'])?[ \t]*(?::|%3[Aa]|\|)[ \t]*(?:\\*[""'])?(?<secret>[^\r\n""'\\`|]+)"),
		c => SecretHeuristics.IsSessionCookie(c.Value));

	/// <summary>curl's <c>-b</c> and <c>--cookie</c> with inline cookies. A cookie file name has no equals sign.</summary>
	public static readonly RedactionRule CurlCookie = new(
		"curl-cookie",
		Pattern(@"(?:^|\s)(?:-b|--cookie)[ =](?:""(?<secret>[^""\r\n]+)|'(?<secret>[^'\r\n]+)|(?<secret>[^\s""']+))"),
		SecretHeuristics.IsCurlCookie);

	/// <summary>Session cookies known by name: <c>PHPSESSID</c>, <c>JSESSIONID</c>, <c>ASP.NET_SessionId</c>, <c>connect.sid</c>.</summary>
	public static readonly RedactionRule SessionCookieValue = new(
		"session-cookie",
		Pattern(@"(?i:PHPSESSID|JSESSIONID|ASP\.NET_SessionId|connect\.sid|sessionid)=(?<secret>[^\s;,""'&]+)"),
		c => SecretHeuristics.IsSessionValue(c.Value));

	/// <summary>Passwords in the conventional places of common tools: <c>sshpass -p</c>, <c>docker login -p</c>, <c>redis-cli -a</c>, <c>htpasswd -b</c>, <c>az login -p</c>, <c>ssh-keygen -N</c> and <c>-P</c>, <c>cmdkey /pass:</c>, <c>net use</c>, <c>setx</c>.</summary>
	public static readonly RedactionRule CommandPassword = new(
		"command-password",
		Pattern(
			@"(?:\bsshpass" + CommandGap + "-p(?:" + FlagGap + @")?|\bdocker" + CommandGap + @"login\b" + CommandArguments + FlagStart + "-p" + FlagGap + @"|\bredis-cli\b" + CommandArguments + FlagStart + "-a" + FlagGap + @"|\baz\s+login\b" + CommandArguments + FlagStart + "(?:-p|--password)" + FlagGap
			+ @"|\bhtpasswd\s+-[a-mo-zA-Z]*b[a-zA-Z]*\s+\S+\s+\S+\s+|\bhtpasswd\s+-[a-zA-Z]*n[a-zA-Z]*b[a-zA-Z]*\s+\S+\s+"
			+ @"|\bssh-keygen\b[^\r\n|]*?\s-[NP][ ]+|\bcmdkey\b[^\r\n|]*?\s/pass:|\bnet\s+use\s+(?:[A-Za-z]:\s+)?\\\\\S+\s+|\bsetx\s+\S*(?i:pass|secret|token|key)\S*\s+)"
			+ @"(?:""(?<secret>[^""\r\n]+)|'(?<secret>[^'\r\n]+)|(?<secret>[^\s""'`|;&]+))"),
		SecretHeuristics.IsCommandCredential);

	/// <summary><c>mysql -pSECRET</c>, <c>mysqldump</c>, <c>mysqladmin</c>, and <c>mariadb</c>, where the password is attached to the flag.</summary>
	public static readonly RedactionRule MysqlPassword = new(
		"mysql-password",
		Pattern(@"\s-p(?:'(?<secret>[^'\r\n]+)|""(?<secret>[^""\r\n]+)|(?<secret>[^\s""'`|;&]+))"),
		SecretHeuristics.IsMysqlPassword);

	/// <summary>The plain text passed to PowerShell's <c>ConvertTo-SecureString</c>.</summary>
	public static readonly RedactionRule SecureStringPassword = new(
		"securestring-password",
		Pattern(@"(?i:ConvertTo-SecureString)\s+(?:-String\s+)?(?:""(?<secret>[^""\r\n]+)|'(?<secret>[^'\r\n]+))"),
		SecretHeuristics.IsCommandCredential);

	/// <summary>The second argument of calls that take a user and a password: <c>login("u","x")</c>, <c>auth=("u","x")</c>, <c>NetworkCredential("u","x")</c>, <c>BasicAuth("u","x")</c>.</summary>
	public static readonly RedactionRule CredentialArgument = new(
		"credential-argument",
		Pattern(
			@"(?:\b(?:login|signin|sign_in|authenticate)|\bauth[ ]?=[ ]?|\b(?:NetworkCredential|BasicAuth|HTTPBasicAuth))[ ]?\([ ]?[""'][^""'\r\n]*[""'][ ]?,[ ]?"
			+ @"(?:""(?<secret>[^""\r\n]+)|'(?<secret>[^'\r\n]+))"),
		SecretHeuristics.IsCommandCredential);

	/// <summary>A <c>.netrc</c> entry: <c>machine host login user password x</c>, or <c>password x</c> alone on its line with a value that is not a plain word.</summary>
	public static readonly RedactionRule NetrcPassword = new(
		"netrc-password",
		Pattern(@"(?:\bmachine\s+\S+\s+login\s+\S+\s+password[ \t]+(?<secret>\S+)|(?:^|[\r\n])[ \t]*password[ \t]+(?<secret>[^\s]*[^A-Za-z\s][^\s]*)[ \t]*(?:$|[\r\n]))"),
		SecretHeuristics.IsCommandCredential);

	/// <summary>
	/// Secrets in XML: <c>&lt;password&gt;x&lt;/password&gt;</c>, and <c>&lt;add key="ApiKey" value="x"/&gt;</c> from .NET configuration.
	/// A regex's named group <c>(?&lt;secret&gt;…)</c> and markdown's <c>&lt;token&gt;</c> before prose are not elements.
	/// </summary>
	public static readonly RedactionRule XmlSecret = new(
		"xml-secret",
		Pattern(
			@"(?i:<(?:password|passwd|secret|token|api[_\-]?key|access[_\-]?key|secret[_\-]?key|client[_\-]?secret)>)(?<secret>[^<\r\n]+)<"
			+ @"|(?i:<add\s+(?:key|name)=[""'][A-Za-z0-9_.:\-]*(?:password|pwd|secret|token|key)[""']\s+value=)[""'](?<secret>[^""'\r\n]+)"
			+ @"|(?i:<add\s+value=)[""'](?<secret>[^""'\r\n]+)[""'](?i:\s+(?:key|name)=[""'][A-Za-z0-9_.:\-]*(?:password|pwd|secret|token|key)[""'])"),
		c => !IsRegexGroupOrProse(c) && SecretHeuristics.IsCommandCredential(c));

	/// <summary>
	/// A regex's named group, as in <c>(?&lt;secret&gt;[^\s]+)</c>, and markdown that names a placeholder before prose, as in
	/// <c>Bearer &lt;token&gt;` when a token is set</c>, are not XML elements. Prose there is three words or more that run out of a
	/// markdown code span, past its closing backtick. A passphrase, as in <c>&lt;password&gt;correct horse battery staple&lt;br&gt;</c>, has none.
	/// </summary>
	private static bool IsRegexGroupOrProse(SecretCandidate candidate)
	{
		return (candidate.Match.Index >= 2 && candidate.Input.AsSpan(candidate.Match.Index - 2, 2) is "(?")
			|| (candidate.Value.Contains('`', StringComparison.Ordinal) && candidate.Value.Count(static c => c == ' ') >= 2);
	}

	/// <summary>SQL that sets a password: <c>IDENTIFIED BY 'x'</c>, <c>IDENTIFIED BY "x"</c>, and <c>WITH PASSWORD 'x'</c>.</summary>
	public static readonly RedactionRule SqlPassword = new(
		"sql-password",
		Pattern(@"(?i:\bIDENTIFIED\s+BY\s+""(?<secret>[^""\r\n]+)""|(?:\bIDENTIFIED\s+BY|\bPASSWORD)\s+'(?<secret>(?:[^'\r\n]|'')+)')"),
		SecretHeuristics.IsCommandCredential);

	/// <summary>A PuTTY private key file. Its private lines run from the count to the MAC, and the public lines above them survive.</summary>
	public static readonly RedactionRule PuttyPrivateKey = new(
		"putty-private-key",
		Pattern(@"Private-Lines:[ \t]*[0-9]+\r?\n(?<secret>(?:[A-Za-z0-9+/=]+\r?\n)+Private-(?:MAC|Hash):[ \t]*[0-9a-fA-F]+)"),
		literal: "Private-Lines:",
		linesMatter: true);

	/// <summary>The computed <c>response</c> of a Digest authorization header, which can be replayed.</summary>
	public static readonly RedactionRule DigestResponse = new(
		"digest-response",
		Pattern(@"(?i:\bdigest\s)[^\r\n]*?(?i:\bresponse=)[""']?(?<secret>[0-9a-fA-F]{32,})"));

	/// <summary>Query parameters that carry a credential under a plain name: <c>?key=</c> and OAuth's <c>?code=</c>. Only a value that reads as generated counts.</summary>
	public static readonly RedactionRule QueryCredential = new(
		"query-credential",
		Pattern(@"(?:[?&]|%3[Ff]|%26|\\u0026)(?:key|code)=(?<secret>[A-Za-z0-9_.~%\-]{16,})"),
		c => !ValueShapes.IsPlaceholder(c.Value) && SecretHeuristics.IsTokenValue(c.Value));

	/// <summary>A Microsoft Teams incoming webhook, which authorizes posting with no other credential. The host stays readable.</summary>
	public static readonly RedactionRule TeamsWebhook = new(
		"teams-webhook",
		Pattern(@"webhook\.office\.com" + Slash + @"webhookb2" + Slash + @"(?<secret>[A-Za-z0-9\-@_/\\%]{40,})"),
		c => !ValueShapes.IsPlaceholder(c.Value),
		literal: "webhook.office.com");

	/// <summary>Command line flags with the value after a space or as the next array item: <c>--password x</c>, <c>--api-key x</c>, <c>["--token", "x"]</c>. Judged like an assignment.</summary>
	public static readonly RedactionRule SecretFlag = new(
		"secret-flag",
		Pattern(@"(?:^|[\s""'`\[,])(?<key>-{1,2}[A-Za-z0-9\-]*(?i:password|passwd|pwd|passphrase|pass|token|secret|auth|key|api-?key)[A-Za-z0-9\-]*)(?:[""'][ \t]*,[ \t]*|[ \t]+)" + Value),
		SecretHeuristics.IsSecretFlag,
		extend: SecretHeuristics.ExtendAssignment);

	/// <summary>
	/// The catch-all: <c>password=</c>, <c>api_key:</c>, <c>API key:</c>, <c>token":"..."</c>, <c>.env</c> lines,
	/// <c>export FOO_SECRET=</c>, markdown table rows, and YAML values on the next line. Judged by
	/// <see cref="SecretHeuristics.IsSecretAssignment"/> on the key's words and the value's shape and
	/// entropy, because the name alone would redact <c>max_tokens: 4096</c> and <c>password = getPassword()</c>.
	/// </summary>
	public static readonly RedactionRule SecretAssignment = new(
		"secret-assignment",
		Pattern(@"(?i:" + SecretKey + @")" + Close + @"(?:" + Operator + @"|[ \t]{0,4}\|[ \t]{0,4})" + Value),
		SecretHeuristics.IsSecretAssignment,
		extend: SecretHeuristics.ExtendAssignment);

	/// <summary>
	/// Tokens identified by a distinctive prefix. One table, one shape per line: the rule's name, a literal that
	/// every match contains (so most documents skip the regex entirely), and the pattern with its minimum length.
	/// </summary>
	public static readonly IReadOnlyList<RedactionRule> ProviderTokens =
	[
		Provider("sendgrid-key", "SG.", @"SG\.[A-Za-z0-9_\-]{16,}\.[A-Za-z0-9_\-]{16,}"),
		Provider("twilio-key", null, @"(?:AC|SK)[0-9a-f]{32}" , fixedLength: true),
		Provider("mailgun-key", "key-", @"key-[0-9a-f]{32}", fixedLength: true, strictLeft: true),
		Provider("digitalocean-token", "_v1_", @"do[por]_v1_[0-9a-f]{40,}"),
		Provider("vercel-token", "vcp_", @"vcp_[A-Za-z0-9]{24,}"),
		Provider("supabase-token", "sbp_", @"sbp_[A-Za-z0-9]{32,}"),
		Provider("azure-client-secret", "8Q~", @"[A-Za-z0-9_~.\-]{3}8Q~[A-Za-z0-9_~.\-]{25,}"),
		Provider("pypi-token", "pypi-", @"pypi-[A-Za-z0-9_\-]{32,}"),
		Provider("docker-token", "dckr_pat_", @"dckr_pat_[A-Za-z0-9_\-]{20,}"),
		Provider("telegram-token", null, @"[0-9]{8,10}:[A-Za-z0-9_\-]{35}", fixedLength: true),
		// No real prefix: this format also fits a dotted name like ModelContextProtocolServer.Server.AspNetCoreHttpTransportExtensions.
		new("discord-token", Pattern(Left + @"(?<secret>[MNO][A-Za-z0-9_\-]{23,27}\.[A-Za-z0-9_\-]{6}\.[A-Za-z0-9_\-]{27,})"), SecretHeuristics.IsRealToken),
		Provider("notion-token", null, @"(?:secret_|ntn_)[A-Za-z0-9]{40,}"),
		Provider("linear-token", "lin_api_", @"lin_api_[A-Za-z0-9]{32,}"),
		Provider("plane-token", "plane_api_", @"plane_api_[A-Za-z0-9]{24,}"),
		Provider("age-secret-key", "AGE-SECRET-KEY-1", @"AGE-SECRET-KEY-1[A-Z0-9]{40,}"),
		Provider("vault-token", null, @"hv[sbr]\.[A-Za-z0-9_\-]{20,}"),
		Provider("databricks-token", "dapi", @"dapi[0-9a-f]{32}(?:-[0-9])?", fixedLength: true),
		Provider("shopify-token", "shp", @"shp(?:at|ss|ca|pa)_[0-9a-fA-F]{32}"),
		Provider("tailscale-key", "tskey-", @"tskey-[A-Za-z0-9\-]{20,}"),
		Provider("atlassian-token", "ATATT3", @"ATATT3[A-Za-z0-9_=\-]{32,}"),
		Provider("groq-key", "gsk_", @"gsk_[A-Za-z0-9]{32,}"),
		Provider("xai-key", "xai-", @"xai-[A-Za-z0-9]{32,}"),
		Provider("langsmith-key", "lsv2_", @"lsv2_[A-Za-z0-9_]{32,}"),
		Provider("onepassword-token", "ops_", @"ops_[A-Za-z0-9_\-]{32,}"),
		Provider("cloudflare-token", "cfut_", @"cfut_[A-Za-z0-9_\-]{32,}"),
		// An organization token's body is base64 with a second base64 part after an underscore, so + / = are inside it.
		Provider("sentry-token", "sntrys_", @"sntrys_[A-Za-z0-9_+/=\-]{32,}"),
		Provider("grafana-token", null, @"(?:glsa_|glc_)[A-Za-z0-9_+/=\-]{32,}"),
		Provider("figma-token", "figd_", @"figd_[A-Za-z0-9_\-]{32,}"),
		Provider("tavily-key", "tvly-", @"tvly-[A-Za-z0-9_\-]{24,}"),
		Provider("perplexity-key", "pplx-", @"pplx-[A-Za-z0-9]{32,}"),
		Provider("replicate-token", "r8_", @"r8_[A-Za-z0-9]{32,}"),
		Provider("render-key", "rnd_", @"rnd_[A-Za-z0-9]{24,}"),
		Provider("netlify-token", "nfp_", @"nfp_[A-Za-z0-9]{32,}"),
		Provider("planetscale-token", "pscale_tkn_", @"pscale_tkn_[A-Za-z0-9_\-]{32,}"),
		Provider("doppler-token", "dp.", @"dp\.(?:st|pt|ct|sa|scim|audit)\.[A-Za-z0-9_.\-]{32,}"),
		Provider("alibaba-key", "LTAI", @"LTAI[A-Za-z0-9]{12,20}", fixedLength: true),
		Provider("square-token", "sq0", @"sq0(?:atp|csp)-[A-Za-z0-9_\-]{22,}"),
		Provider("square-production-token", "EAAA", @"EAAA[A-Za-z0-9_\-]{60}", fixedLength: true),
		Provider("hubspot-token", "pat-", @"pat-(?:na|eu|ap)[0-9]-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"),
		Provider("atlassian-cookie-token", "ATCTT3", @"ATCTT3[A-Za-z0-9_=\-]{32,}"),
		Provider("supabase-secret", "sb_secret_", @"sb_secret_[A-Za-z0-9_\-]{20,}"),
		Provider("facebook-token", "EAAC", @"EAAC[A-Za-z0-9]{40,}"),
		Provider("huggingface-org-token", "api_org_", @"api_org_[A-Za-z0-9]{30,}"),
		Provider("gitlab-feature-flag-token", "glffct-", @"glffct-[A-Za-z0-9_\-]{20,}"),
	];

	/// <summary>A 32-digit hex value on a line that names Twilio, however it is written. Alone it is only a hash, so the name is what makes it a credential.</summary>
	public static readonly RedactionRule TwilioAuthToken = new(
		"twilio-auth-token",
		Pattern(@"(?i:twilio)[^\r\n]{0,60}?[^A-Za-z0-9](?<secret>[0-9a-fA-F]{32})(?:$|[^A-Za-z0-9])"),
		c => !ValueShapes.IsPlaceholder(c.Value));

	/// <summary>Percent-encoded text whose decoded form holds a credential, such as a token with <c>%5F</c> for its underscore. See <see cref="EncodedTokens"/>.</summary>
	public static readonly RedactionRule PercentEncodedToken = new(
		"percent-encoded-token",
		Pattern(@"(?:[A-Za-z0-9._~+\-]|%[0-9A-Fa-f]{2})*%[0-9A-Fa-f]{2}(?:[A-Za-z0-9._~+\-]|%[0-9A-Fa-f]{2})*"),
		EncodedTokens.IsPercentEncodedCredential,
		literal: "%",
		skipRejected: true);

	/// <summary>
	/// Hex whose decoded bytes hold a credential, as in <c>676870…</c> for a GitHub token. A run of hex alone says nothing: a commit
	/// hash decodes to random bytes, which hold no token. Only an even run of at least 32 digits is decoded, with no hex digit
	/// either side, so a run after a JSON <c>\n</c>, PostgreSQL's <c>\x</c> or a <c>0x</c> is read too.
	/// </summary>
	public static readonly RedactionRule HexEncodedToken = new(
		"hex-token",
		Pattern(@"(?:^|[^0-9A-Fa-f])(?<secret>(?:[0-9A-Fa-f]{2}){16,})(?:$|[^0-9A-Fa-f])"),
		EncodedTokens.IsHexEncodedCredential,
		skipRejected: true);

	/// <summary>
	/// Text with <c>\uXXXX</c> escapes written out, as a JSON encoder writes <c>_</c> or <c>-</c>, whose unescaped form holds a credential.
	/// The run never starts on the letter of an escape such as <c>\n</c>, so the escape before it stays whole.
	/// </summary>
	public static readonly RedactionRule UnicodeEscapedToken = new(
		"unicode-escaped-token",
		Pattern(@"(?:^|\\[nrtfbv0]|[^A-Za-z0-9._~+\-])(?<secret>(?:[A-Za-z0-9._~+\-]|\\u[0-9A-Fa-f]{4})*\\u[0-9A-Fa-f]{4}(?:[A-Za-z0-9._~+\-]|\\u[0-9A-Fa-f]{4})*)"),
		EncodedTokens.IsUnicodeEscapedCredential,
		literal: @"\u",
		skipRejected: true);

	/// <summary>Base64 whose decoded form holds a credential. A run of base64 alone says nothing, so only what it decodes to counts. See <see cref="EncodedTokens"/>.</summary>
	public static readonly RedactionRule Base64EncodedToken = new(
		"base64-token",
		Pattern(@"[A-Za-z0-9+/_\-]{24,}={0,2}"),
		EncodedTokens.IsBase64EncodedCredential,
		skipRejected: true);

	/// <summary>A Discord webhook path, which authorizes posting with no other credential. The host and channel id stay readable.</summary>
	public static readonly RedactionRule DiscordWebhook = new(
		"discord-webhook",
		Pattern(@"discord(?:app)?\.com" + Slash + @"api" + Slash + @"webhooks" + Slash + @"[0-9]+" + Slash + @"(?<secret>[A-Za-z0-9_\-]{30,})"),
		SecretHeuristics.IsPrefixedToken,
		literal: "discord");

	/// <summary>The rules, in execution order.</summary>
	public static readonly IReadOnlyList<RedactionRule> All =
	[
		PrivateKey,
		PrivateKeyEnd,
		PrivateKeyLines,
		PrivateKeyDer,
		PuttyPrivateKey,
		Base64PrivateKey,
		AwsSecretKey,
		AwsSecretNearId,
		AnthropicKey,
		OpenAiKey,
		GitHubToken,
		AwsAccessKey,
		Jwt,
		SlackToken,
		SlackWebhook,
		GoogleApiKey,
		GoogleOAuthToken,
		StripeKey,
		NpmToken,
		AzureStorageKey,
		AzureSasSignature,
		PresignedUrlSignature,
		HuggingFaceToken,
		GitLabToken,
		MemoriaApiKey,
		.. ProviderTokens,
		TwilioAuthToken,
		DiscordWebhook,
		TeamsWebhook,
		MemoriaSharedSecret,
		UrlPassword,
		UrlToken,
		UserInfoPassword,
		ConnectionStringPassword,
		AuthorizationHeader,
		DigestResponse,
		QueryCredential,
		BearerToken,
		CurlUserPassword,
		CookieHeader,
		CurlCookie,
		SessionCookieValue,
		CommandPassword,
		MysqlPassword,
		SecureStringPassword,
		CredentialArgument,
		NetrcPassword,
		XmlSecret,
		SqlPassword,
		SecretFlag,
		SecretAssignment,
		.. GitleaksRules.All,
		HexEncodedToken,
		UnicodeEscapedToken,
		PercentEncodedToken,
		Base64EncodedToken,
	];

	/// <summary>Rules that know a credential by its own format, so they can look inside decoded text, where what surrounded it means nothing. They include the encoded ones, for encoding inside encoding.</summary>
	public static readonly IReadOnlyList<RedactionRule> SelfEvident =
	[
		AnthropicKey,
		OpenAiKey,
		GitHubToken,
		AwsAccessKey,
		Jwt,
		SlackToken,
		GoogleApiKey,
		GoogleOAuthToken,
		StripeKey,
		NpmToken,
		HuggingFaceToken,
		GitLabToken,
		MemoriaApiKey,
		.. ProviderTokens,
		.. GitleaksRules.SelfEvident,
		HexEncodedToken,
		UnicodeEscapedToken,
		PercentEncodedToken,
		Base64EncodedToken,
	];

	private static RedactionRule Provider(string name, string? literal, string pattern, bool fixedLength = false, bool strictLeft = false)
	{
		string regex = (strictLeft ? StrictLeft : Left) + "(?<secret>" + pattern + ")" + (fixedLength ? Right : "");
		return new RedactionRule(name, Pattern(regex), SecretHeuristics.IsPrefixedToken, literal: literal);
	}

	/// <summary>How every rule's pattern is built, imported ones included.</summary>
	internal static Regex Pattern(string pattern)
	{
		return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout);
	}
}
