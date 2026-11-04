using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>Boundary values for the judges, so a changed threshold or a flipped condition fails a test.</summary>
public sealed class SecretHeuristicsTests
{
	[Theory]
	[InlineData("", 0.0)]
	[InlineData("aaaa", 0.0)]
	[InlineData("ab", 1.0)]
	[InlineData("abcd", 2.0)]
	[InlineData("aabb", 1.0)]
	public void Entropy_is_exact(string value, double expected)
	{
		Assert.Equal(expected, SecretHeuristics.ShannonEntropy(value), 6);
	}

	[Theory]
	[InlineData("", true)]
	[InlineData("   ", true)]
	[InlineData("<your-key>", true)]
	[InlineData("{{ x }}", true)]
	[InlineData("#{x}", true)]
	[InlineData("${x}", true)]
	[InlineData("$(x)", true)]
	[InlineData("{name}", true)]
	[InlineData("{0}", true)]
	[InlineData("{not!a!name}", false)]
	[InlineData("$NAME", true)]
	[InlineData("$not a name", false)]
	[InlineData("%s", true)]
	[InlineData("%", true)]
	[InlineData("%(name)s", true)]
	[InlineData("%NAME%", true)]
	[InlineData("%a b%", false)]
	[InlineData("process.env.X", true)]
	[InlineData("PROCESS.ENV.X", true)]
	[InlineData("vault:secret/x", true)]
	[InlineData("op://v/i/f", true)]
	[InlineData("[REDACTED:x]", true)]
	[InlineData("[redacted]", true)]
	[InlineData("***", true)]
	[InlineData("...", true)]
	[InlineData("xxx", true)]
	[InlineData("xx", false)]
	[InlineData("XXXab", true)]
	[InlineData("xxxabc", false)]
	[InlineData("abc...xyz", true)]
	[InlineData("abc…xyz", true)]
	[InlineData("abcdefgh...ijklmnop", false)]
	[InlineData("abcdefg...ijklmnop", true)]
	[InlineData("YourApiKeyHere", true)]
	[InlineData("your_api_key_here", true)]
	[InlineData("example", true)]
	[InlineData("EXAMPLE", true)]
	[InlineData("your-" + "abcdefghijklm", false)]
	[InlineData("your-" + "abcdefghijkl", true)]
	[InlineData("hunter2", false)]
	public void Placeholder_is_recognized(string value, bool expected)
	{
		Assert.Equal(expected, ValueShapes.IsPlaceholder(value));
	}

	[Theory]
	[InlineData("my-token-name", true)]
	[InlineData("session.key", true)]
	[InlineData("snake_case_name", true)]
	[InlineData("two-words9", false)]
	[InlineData("abcdefghijklmnopqrstu-b", false)]
	[InlineData("abcdefghijklmnopqrst-b", true)]
	[InlineData("abcdefghijklmnopqrs", true)]
	[InlineData("abcdefghijklmnopqrst", false)]
	[InlineData("ABCDEFGHIJKLMNOPQRS", true)]
	[InlineData("ABCDEFGHIJKLMNOPQRST", false)]
	[InlineData("tokenValue", true)]
	[InlineData("AbCdEfGhIj", false)]
	[InlineData("Abc", true)]
	[InlineData("AbC", false)]
	public void Wordy_is_recognized(string value, bool expected)
	{
		Assert.Equal(expected, ValueShapes.IsWordy(value));
	}

	[Theory]
	[InlineData("", false)]
	[InlineData("getPassword()", true)]
	[InlineData("a.b.getPassword(x)", true)]
	[InlineData("args[0]", true)]
	[InlineData("(x", false)]
	[InlineData("(x:", true)]
	[InlineData(")x", false)]
	[InlineData("table.Column<string>(type: 1)", true)]
	[InlineData("table.Column<string>(type:", false)]
	[InlineData("a b<c>(d", false)]
	[InlineData("p@ss(word", false)]
	[InlineData("!isValid", true)]
	[InlineData("*ptr", true)]
	[InlineData("!a b", false)]
	[InlineData("config.password", true)]
	[InlineData("a.1b", false)]
	[InlineData("a..b", false)]
	[InlineData("a.b-c", false)]
	[InlineData("user_password", true)]
	[InlineData("USER_PASSWORD", true)]
	[InlineData("PASSWORD", true)]
	[InlineData("userPassword", true)]
	[InlineData("UserPassword", false)]
	[InlineData("admin", false)]
	[InlineData("Admin", false)]
	[InlineData("hunter2", false)]
	public void Code_reference_is_recognized(string value, bool expected)
	{
		Assert.Equal(expected, ValueShapes.IsCodeReference(value));
	}

	[Theory]
	[InlineData("1234567890123456", false, false)]
	[InlineData("two words 123456789", false, false)]
	[InlineData("abc-def", false, false)]
	[InlineData("abc123", true, true)]
	[InlineData("abc12", true, false)]
	[InlineData("abc123", false, false)]
	[InlineData("a1b2c3d4", false, true)]
	[InlineData("a1b2c3d", false, false)]
	[InlineData("a1a1a1a1", false, false)]
	[InlineData("!!!!!!!!", false, false)]
	[InlineData("aaaaaaaa1", false, false)]
	[InlineData("a1b2c3d4a1b2c3d4", false, true)]
	[InlineData("a1b2c3a1b2c3a1b2", false, false)]
	[InlineData("aaaaaaaabbbbbbbb1", false, false)]
	public void Token_value_thresholds_hold(string value, bool environmentAssignment, bool expected)
	{
		Assert.Equal(expected, SecretHeuristics.IsTokenValue(value, environmentAssignment));
	}

	[Theory]
	[InlineData("abcd1234", true)]
	[InlineData("abcd123", false)]
	[InlineData("<token-here>", false)]
	[InlineData("password", false)]
	[InlineData("accessToken", false)]
	public void Explicit_credential_thresholds_hold(string value, bool expected)
	{
		Assert.Equal(expected, SecretHeuristics.IsExplicitCredential(value));
	}

	[Theory]
	[InlineData("8080", true)]
	[InlineData("123456", true)]
	[InlineData("1a", true)]
	[InlineData("password", false)]
	[InlineData("$X", false)]
	public void Url_password_rules_hold(string value, bool expected)
	{
		Assert.Equal(expected, SecretHeuristics.IsUrlPassword(value));
	}

	[Theory]
	[InlineData("sk-aaaaaaaaaaaaaaaaaaaa", true)]
	[InlineData("sk-xxxxxxxxxxxxxxxxxxxx", false)]
	[InlineData("sk-proj-aaaaaaaaaaaaaaaaaaaa1", true)]
	[InlineData("sk-svcacct-aaaaaaaaaaaaaaaaaaaa1", true)]
	[InlineData("sk-admin-aaaaaaaaaaaaaaaaaaaa1", true)]
	[InlineData("sk-proj-only-plain-words-in-here", false)]
	[InlineData("sk-learn-contrib-imbalanced", false)]
	[InlineData("sk-aaaaaaaaaa1-aaaaaaaaaaaaa", false)]
	[InlineData("sk-learn-intelex-2024-01-release", false)]
	[InlineData("sk-abcdefgh12-ijklmnop34", true)]
	public void OpenAi_shapes_are_judged(string value, bool expected)
	{
		Assert.Equal(expected, SecretHeuristics.IsOpenAiKey(value));
	}

	[Theory]
	[InlineData("mem_ab_abcdefghijkl", true)]
	[InlineData("mem_ab_abcdefghijk", false)]
	[InlineData("mem_ab_" + "xxxxxxxxxxxxxxxx", false)]
	public void Memoria_shapes_are_judged(string value, bool expected)
	{
		Assert.Equal(expected, SecretHeuristics.IsMemoriaKey(value));
	}

	[Theory]
	[InlineData("a=b", false)]
	[InlineData("no equals sign anywhere in this", false)]
	[InlineData("sid=abcdefgh1234", true)]
	[InlineData("sid=abcdefgh123", false)]
	[InlineData("sid=aaaaaaaaaaaaaaaa", false)]
	[InlineData("sid=<session-cookie>", false)]
	[InlineData("sid=<session-cookie-value>", false)]
	[InlineData("theme=dark; sid=abcdefgh12345678", true)]
	public void Session_cookie_is_recognized(string value, bool expected)
	{
		Assert.Equal(expected, SecretHeuristics.IsSessionCookie(value));
	}

	[Theory]
	[InlineData("password", KeyKind.Password)]
	[InlineData("DB_PASSWD", KeyKind.Password)]
	[InlineData("mysql-pwd", KeyKind.Password)]
	[InlineData("userPassphrase", KeyKind.Password)]
	[InlineData("PGPASSWORD", KeyKind.Password)]
	[InlineData("token", KeyKind.Token)]
	[InlineData("accessToken", KeyKind.Token)]
	[InlineData("client_secret", KeyKind.Token)]
	[InlineData("API_KEY", KeyKind.Token)]
	[InlineData("apikey", KeyKind.Token)]
	[InlineData("x-auth-key", KeyKind.Token)]
	[InlineData("signingKey", KeyKind.Token)]
	[InlineData("auth", KeyKind.Token)]
	[InlineData("_auth", KeyKind.Token)]
	[InlineData("DB_PASS", KeyKind.Token)]
	[InlineData("credentials", KeyKind.Token)]
	[InlineData("secret_id", KeyKind.Token)]
	[InlineData("TOKEN_PROD", KeyKind.Token)]
	[InlineData("token_id", KeyKind.Descriptive)]
	[InlineData("id", KeyKind.NotSecret)]
	[InlineData("token_count", KeyKind.Descriptive)]
	[InlineData("max_tokens", KeyKind.NotSecret)]
	[InlineData("tokenizer", KeyKind.NotSecret)]
	[InlineData("pageToken", KeyKind.NotSecret)]
	[InlineData("api_url", KeyKind.NotSecret)]
	[InlineData("key", KeyKind.NotSecret)]
	[InlineData("author", KeyKind.NotSecret)]
	[InlineData("---", KeyKind.NotSecret)]
	[InlineData("", KeyKind.NotSecret)]
	public void Key_names_are_classified(string key, object expected)
	{
		Assert.Equal(expected, SecretKeyNames.Classify(key));
	}

	[Theory]
	[InlineData("x-api_keyValue", "x,api,key,Value")]
	[InlineData("API_KEY", "API,KEY")]
	[InlineData("--flag", "flag")]
	[InlineData("HTTPToken", "HTTPToken")]
	[InlineData("a", "a")]
	public void Key_names_split_into_words(string key, string expected)
	{
		Assert.Equal(expected, string.Join(',', SecretKeyNames.SplitWords(key)));
	}

	[Theory]
	[InlineData("token=" + "a1b2c3d4e5f6a7b8", "token=[REDACTED:secret-assignment]")]
	[InlineData("key_name_that_is_way_too_long_for_a_real_configuration_variable_name_really_it_is_token=a1b2c3d4e5f6a7b8", "key_name_that_is_way_too_long_for_a_real_configuration_variable_name_really_it_is_token=a1b2c3d4e5f6a7b8")]
	[InlineData("password=hunter2 ", "password=[REDACTED:secret-assignment] ")]
	[InlineData("password = hunter", "password = hunter")]
	[InlineData("password = hunter2", "password = [REDACTED:secret-assignment]")]
	[InlineData("password: admin  ", "password: [REDACTED:secret-assignment]  ")]
	[InlineData("x password: admin", "x password: admin")]
	[InlineData("password: admin now", "password: admin now")]
	[InlineData("- password: admin\nnext", "- password: [REDACTED:secret-assignment]\nnext")]
	[InlineData("first\npassword: admin", "first\npassword: [REDACTED:secret-assignment]")]
	[InlineData("password: adm", "password: adm")]
	[InlineData("%22password%22%3A%22hunter2%22", "%22password%22%3A%22[REDACTED:secret-assignment]%22")]
	[InlineData("%27password%27%3A%27hunter%27", "%27password%27%3A%27[REDACTED:secret-assignment]%27")]
	[InlineData("| password | hunter2x |", "| password | [REDACTED:secret-assignment] |")]
	[InlineData("| `password` | Minimum length |", "| `password` | Minimum length |")]
	[InlineData("| token | a1b2c3d4 |", "| token | [REDACTED:secret-assignment] |")]
	[InlineData("echo password | a1b2c3d4", "echo password | a1b2c3d4")]
	[InlineData("token | a1b2c3d4e5f6a7b", "token | a1b2c3d4e5f6a7b")]
	[InlineData("token | a1b2c3d4e5f6a7b8", "token | [REDACTED:secret-assignment]")]
	[InlineData("TOKEN=abc123", "TOKEN=[REDACTED:secret-assignment]")]
	[InlineData("token=abc123", "token=abc123")]
	[InlineData("TOKEN = abc123", "TOKEN = abc123")]
	[InlineData("--TOKEN=abc123", "--TOKEN=[REDACTED:secret-assignment]")]
	[InlineData("password=password", "password=password")]
	[InlineData("mypwd=MYPWD", "mypwd=MYPWD")]
	[InlineData("token: \"getToken()\"", "token: \"getToken()\"")]
	[InlineData("password: \"getPassword1()\"", "password: \"[REDACTED:secret-assignment]\"")]
	[InlineData("password = getPassword1()", "password = getPassword1()")]
	[InlineData("password=[REDACTED:x]hunter2", "password=[REDACTED:secret-assignment]")]
	[InlineData("password=[REDACTED:unclosed", "password=[REDACTED:unclosed")]
	[InlineData("password=[REDACTED:NotAName1", "password=[REDACTED:secret-assignment]")]
	[InlineData("token: /Abc+def/ghi+jkl+mno+pqr=", "token: [REDACTED:secret-assignment]")]
	[InlineData("token: /only-one-segment", "token: /only-one-segment")]
	[InlineData("token: /Abc1Def2Ghi3Jkl4Mno5", "token: [REDACTED:secret-assignment]")]
	[InlineData("api_key: C:/keys/my.key", "api_key: C:/keys/my.key")]
	[InlineData("api_key: ../keys/my.key", "api_key: ../keys/my.key")]
	[InlineData("api_key: Z:", "api_key: Z:")]
	[InlineData("api_key: 1:/a/b/cdef12", "api_key: [REDACTED:secret-assignment]")]
	[InlineData("**Auth**: `AddAuthentication()`", "**Auth**: `AddAuthentication()`")]
	[InlineData("password_hash = table.Column<string>(type: \"text\")", "password_hash = table.Column<string>(type: \"text\")")]
	[InlineData("token: Foo<int", "token: Foo<int")]
	[InlineData("AuthController.cs: AddAuthentication()", "AuthController.cs: AddAuthentication()")]
	[InlineData("auth.tsx: src/AuthProvider.tsx", "auth.tsx: src/AuthProvider.tsx")]
	[InlineData("auth: src/AuthProvider.tsx", "auth: src/AuthProvider.tsx")]
	[InlineData("auth: Program.cs:12", "auth: Program.cs:12")]
	[InlineData("refreshToken: TokenService.cs:12,34", "refreshToken: TokenService.cs:12,34")]
	[InlineData("token: a1b2c3d4.unknownext:12", "token: [REDACTED:secret-assignment]")]
	[InlineData("token: a1b2c3d4e5f6:", "token: [REDACTED:secret-assignment]")]
	[InlineData("token: :a1b2c3d4e5f6", "token: [REDACTED:secret-assignment]")]
	[InlineData("token: a1b2c3d4.cs:x12", "token: [REDACTED:secret-assignment]")]
	[InlineData("token: a1b2+c3d4.cs", "token: [REDACTED:secret-assignment]")]
	[InlineData("pass: 2-rename-the-doc.md", "pass: 2-rename-the-doc.md")]
	[InlineData("changePassword: (data: Input) => run(data)", "changePassword: (data: Input) => run(data)")]
	[InlineData("ChangePasswordRequest: { a: b }", "ChangePasswordRequest: { a: b }")]
	[InlineData("must_reset_password = false", "must_reset_password = false")]
	[InlineData("reset_token=a1b2c3d4e5f6a7b8", "reset_token=[REDACTED:secret-assignment]")]
	[InlineData("refresh_token_expires_at = table.Column<DateTime>(type: \"x\")", "refresh_token_expires_at = table.Column<DateTime>(type: \"x\")")]
	[InlineData("old_password=hunter2", "old_password=[REDACTED:secret-assignment]")]
	[InlineData("bypass-https://example.test/a.b.c=", "bypass-https://example.test/a.b.c=")]
	[InlineData("token: \"a1b2c3d4.cs\"", "token: \"a1b2c3d4.cs\"")]
	[InlineData("password: \")\"", "password: \")\"")]
	[InlineData("password = (hunter2", "password = [REDACTED:secret-assignment]")]
	public void Assignments_are_judged_with_exact_output(string input, string expected)
	{
		Assert.Equal(expected, new Redactor().Redact(input).Text);
	}
}
