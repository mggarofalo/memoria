using System.Text;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// Reproductions from an independent adversarial review, rebuilt with synthetic values. <c>{S}</c> marks
/// the exact span that must become the placeholder; everything else must survive byte for byte.
/// </summary>
internal static class ReviewCases
{
	private static readonly string s_random32 = Synthetic.Body(32, 800);
	private static readonly string s_random40 = Synthetic.Body(40, 801);

	/// <summary>A value of a real provider's shape, by rule. Every prefix is split so nothing here trips a scanner.</summary>
	public static readonly (string Rule, string Secret)[] ProviderTokens =
	[
		("sendgrid-key", "S" + "G." + Synthetic.Body(22, 802) + "." + Synthetic.Body(43, 803)),
		("twilio-key", "S" + "K" + Synthetic.Hex(32, 805)),
		("mailgun-key", "ke" + "y-" + Synthetic.Hex(32, 806)),
		("newrelic-key", "NR" + "AK-" + Synthetic.Upper(27, 813)),
		("newrelic-key", "NR" + "AA-" + Synthetic.Body(27, 814)),
		("newrelic-key", "NR" + "II-" + Synthetic.Body(16, 815) + "-" + Synthetic.Body(15, 816)),
		("newrelic-key", "NR" + "JS-" + Synthetic.Hex(19, 817)),
		("newrelic-key", Synthetic.Hex(36, 818) + "NR" + "AL"),
		("digitalocean-token", "do" + "p_v1_" + Synthetic.Hex(64, 807)),
		("vercel-token", "vc" + "p_" + Synthetic.Body(24, 808)),
		("supabase-token", "sb" + "p_" + Synthetic.Hex(40, 809)),
		("azure-client-secret", Synthetic.Body(3, 810) + "8" + "Q~" + Synthetic.Body(31, 811)),
		("pypi-token", "py" + "pi-" + Synthetic.Body(40, 812)),
		("docker-token", "dc" + "kr_pat_" + Synthetic.Body(27, 813)),
		("telegram-token", Synthetic.Digits(10, 814) + ":" + Synthetic.Body(35, 815)),
		("discord-token", "M" + Synthetic.Body(25, 816) + "." + Synthetic.Body(6, 817) + "." + Synthetic.Body(38, 818)),
		("notion-token", "sec" + "ret_" + Synthetic.Body(43, 819)),
		("notion-token", "nt" + "n_" + Synthetic.Body(46, 820)),
		("linear-token", "li" + "n_api_" + Synthetic.Body(40, 821)),
		("plane-token", "pl" + "ane_api_" + Synthetic.Body(32, 822)),
		("age-secret-key", "AGE-" + "SECRET-KEY-1" + Synthetic.Upper(58, 823)),
		("vault-token", "hv" + "s." + Synthetic.Body(90, 824)),
		("databricks-token", "da" + "pi" + Synthetic.Hex(32, 825)),
		("shopify-token", "sh" + "pat_" + Synthetic.Hex(32, 826)),
		("tailscale-key", "ts" + "key-auth-" + Synthetic.Body(30, 827)),
		("atlassian-token", "AT" + "ATT3" + Synthetic.Body(60, 828)),
		("groq-key", "gs" + "k_" + Synthetic.Body(52, 829)),
		("xai-key", "xa" + "i-" + Synthetic.Body(80, 830)),
		("langsmith-key", "ls" + "v2_pt_" + Synthetic.Body(32, 831)),
		("onepassword-token", "op" + "s_" + Synthetic.Body(60, 832)),
		("gitlab-token", "gl" + "oas-" + Synthetic.Body(40, 833)),
		("gitlab-token", "gl" + "agent-" + Synthetic.Body(40, 834)),
		("gitlab-token", "gl" + "imt-" + Synthetic.Body(40, 835)),
		("gitlab-token", "GR" + "1348941" + Synthetic.Body(20, 836)),
	];

	/// <summary>(rule, template, secret): the listed recall failures.</summary>
	public static readonly (string Rule, string Template, string Secret)[] Positives =
	[
		// H1: a key name with a space.
		("secret-assignment", "API key: {S}", s_random32),
		("secret-assignment", "api key = {S}", s_random32),
		("secret-assignment", "Your api key: {S} is live", s_random32),

		// H2: a template-like start is not a template without its closing delimiter and a name.
		("secret-assignment", "password=\"{S}\"", "<Secret123"),
		("secret-assignment", "password=\"{S}\"", "${Secret123"),
		("secret-assignment", "password=\"{S}\"", "{{Secret123"),
		("secret-assignment", "password=\"{S}\"", "$(Secret123"),
		("secret-assignment", "password=\"{S}\"", "#{Secret123"),
		("secret-assignment", "password={S}", "!Secret123"),
		("secret-assignment", "password={S}", "(Secret123"),
		("secret-assignment", "password={S}", "Abcde(123"),
		("secret-assignment", "password={S}", "Abcde[123"),
		("secret-assignment", "password=\"{S}\"", "env.Secret123"),
		("secret-assignment", "token=\"{S}\"", "secrets." + s_random32),
		("secret-assignment", "token=\"{S}\"", "vault:" + s_random32),
		("secret-assignment", "token=\"{S}\"", "[REDACTED" + s_random32),

		// H3: a dotted token is not member access.
		("secret-assignment", "API_TOKEN={S}", Synthetic.Body(24, 837) + "." + Synthetic.Body(24, 838)),
		("secret-assignment", "token: {S}", "abc." + Synthetic.Body(24, 839)),
		("authorization-header", "Authorization: token {S}", "abc." + Synthetic.Body(24, 840)),

		// H5: the flag itself, every time.
		("cu" + "rl-user-password", "cu" + "rl -u a:{S} --user-agent=x:y https://h", s_random32),
		("cu" + "rl-user-password", "cu" + "rl -su a:{S} https://h", s_random32),

		// H6: the *_key family, plurals, and *PASS.
		("secret-assignment", "API_KEYS={S}", s_random32),
		("secret-assignment", "CONSUMER_KEY={S}", s_random32),
		("secret-assignment", "APP_KEY={S}", s_random32),
		("secret-assignment", "CLIENT_KEY={S}", s_random32),
		("secret-assignment", "HMAC_KEY={S}", s_random32),
		("secret-assignment", "JWT_KEY={S}", s_random32),
		("secret-assignment", "LICENSE_KEY={S}", s_random32),
		("secret-assignment", "DD_APP_KEY={S}", s_random32),
		("secret-assignment", "Ocp-Apim-Subscription-Key: {S}", s_random32),
		("secret-assignment", "x-functions-key: {S}", s_random32),
		("secret-assignment", "client-key-data: {S}", s_random32),
		("secret-assignment", "consumerKey: {S}", s_random32),
		("secret-assignment", "SSHPASS={S}", "Hunter2x"),
		("secret-assignment", "psk={S}", "Hunter2x"),
		("secret-assignment", "MYSQLPASS={S}", "abc"+ "d"),

		// H7: a descriptive ending vetoes only low-information values.
		("secret-assignment", "AUTH_TOKEN_SERVICE={S}", s_random40),
		("secret-assignment", "GITHUB_TOKEN_MODEL={S}", s_random40),
		("secret-assignment", "API_KEY_INPUT={S}", s_random40),
		("secret-assignment", "API_KEY_REQUEST={S}", s_random40),
		("secret-assignment", "SECRET_FORM={S}", s_random40),
		("secret-assignment", "TOKEN_SOURCE={S}", s_random40),
		("secret-assignment", "token_count={S}", s_random40),
		("secret-assignment", "PASSWORD_SSH={S}", "Hunter2x"),
		("secret-assignment", "client_secret={S}", "/wJalrXUt/K7MDENG/bPxRfiCY1"),
		("secret-assignment", "SECRET_KEY={S}", "correct-horse-battery-staple"),

		// M1: a value on the next line.
		("secret-assignment", "password:\n  {S}", "hunter2"),
		("secret-assignment", "\"api_key\":\n  \"{S}\"", s_random32),
		("secret-assignment", "token: |\n  {S}", s_random32),
		("secret-assignment", "token: >-\n  {S}", s_random32),

		// M2: flags as array items, and short flag names.
		("secret-flag", "\"args\": [\"--token\", \"{S}\"]", s_random32),
		("secret-flag", "tool --pass {S}", "Hunter2x"),
		("secret-flag", "tool --pwd {S}", "Hunter2x"),
		("secret-flag", "tool --auth {S}", s_random32),
		("secret-flag", "tool --key {S}", s_random32),

		// M3: narrow anchored formats.
		("netrc-password", "machine example.test login me password {S}", "abcdef"),
		("netrc-password", "machine example.test\n  login me\n  password {S}\n", "abcdef"),
		("netrc-password", "login me\npassword {S}\n", "Hunter2x"),
		("xml-secret", "<password>{S}</password>", "hunter"),
		("xml-secret", "<add key=\"ApiKey\" value=\"{S}\"/>", s_random32),
		("xml-secret", "<add name=\"DbPassword\" value=\"{S}\"/>", "hunter"),
		("sql-password", "CREATE USER me IDENTIFIED BY '{S}';", "hunter"),
		("sql-password", "CREATE ROLE me WITH PASSWORD '{S}';", "hunter"),
		("sql-password", "ALTER USER me PASSWORD '{S}';", "it''s-a-pass"),

		// M4: more forms of URL credentials.
		("url-password", "postgres://me%40srv:{S}@h/db", "pw12"),
		("userinfo-password", "dsn := \"root:{S}@tcp(db:3306)/x\"", "pw12"),
		("userinfo-password", "connect u:{S}@host", "pw12"),
		("userinfo-password", "dsn := \"root:{S}@unix(/tmp/x)/db\"", "pw"),
		("userinfo-password", "dsn := \"root:{S}@tcp(db)/x\"", "pw"),
		("url-password", "postgres://u:{S}@h/db", "12345"),
		("url-password", "https://u:{S}@h/p", "ab/cd"),
		("url-password", "postgres://u:{S}@h/db?x=y@z", "p9"),

		// M5: escapes inside values.
		("secret-assignment", "\\\"password\\\":\\\"{S}\\\"", "ab\\\\cd12"),
		("secret-assignment", "password: '{S}'", "Ab1''Xyz9"),

		// M6: a marker word does not make a password a sample.
		("secret-assignment", "password: \"{S}\"", "Hidden-Here-42"),
		("secret-assignment", "password: \"{S}\"", "Insert-Coin-99"),
		("secret-assignment", "password: \"{S}\"", "Foo!9921"),

		// M7: more token shapes.
		("jwt", "{S}", "ey" + "J" + Synthetic.Body(20, 841) + "." + Synthetic.Body(40, 842) + "." + Synthetic.Body(16, 843) + "." + Synthetic.Body(40, 844) + "." + Synthetic.Body(22, 845)),
		("jwt", "{S}", "ey" + "J" + Synthetic.Body(20, 846) + ".." + Synthetic.Body(16, 847) + "." + Synthetic.Body(40, 848) + "." + Synthetic.Body(22, 856)),
		("jwt", "{S}", "ey" + "J" + Synthetic.Body(20, 849) + ".ey" + "J" + Synthetic.Body(40, 850)),
		("slack-token", "d={S}", "xo" + "xd-" + Synthetic.Body(30, 851) + "%2B" + Synthetic.Body(30, 852) + "%3D"),
		("google-oauth-token", "{S}", "ya" + "29.a." + Synthetic.Body(40, 853)),

		// Listed misses: common command lines.
		("mysql-password", "mysql -u root -p{S} db", "Hunter2x"),
		("mysql-password", "mysqldump -h db -p{S} x", "Hunter2x"),
		("okta-token", "Authorization: SSWS {S}", "00" + Synthetic.Body(40, 7101)),
		("mongo-password", "mongosh -u admin -p {S} --quiet", "Hunter2x"),
		("mongo-password", "mongo -u admin -p{S} db", "Hunter2x"),
		("basic-auth-literal", "const auth = `Basic ${btoa(\"admin:{S}\")}`;", "Hunter2x"),
		("command-password", "sshpass -p {S} ssh me@h", "Hunter2x"),
		("command-password", "docker login -u me -p {S} registry.test", "Hunter2x"),
		("command-password", "redis-cli -h db -a {S} ping", "Hunter2x"),
		("command-password", "htpasswd -b .htpasswd me {S}", "Hunter2x"),
		("command-password", "htpasswd -nb me {S}", "Hunter2x"),
		("command-password", "az login -u me -p {S}", "Hunter2x"),
		("securestring-password", "ConvertTo-SecureString \"{S}\" -AsPlainText -Force", "Hunter2x"),
		("securestring-password", "ConvertTo-SecureString -String '{S}' -AsPlainText", "Hunter2x"),

		// Listed misses: narrow function arguments.
		("credential-argument", "login(\"u\",\"{S}\")", "Hunter2x"),
		("credential-argument", "requests.get(url, auth=(\"u\", \"{S}\"))", "Hunter2x"),
		("credential-argument", "new NetworkCredential(\"u\", \"{S}\")", "Hunter2x"),

		// Listed misses: single quotes are literal, and a whole value runs to its end.
		("secret-assignment", "password='{S}'", "$abc123"),
		("secret-assignment", "PASSWORD={S}", "ab,cd;ef&gh"),
		("secret-assignment", "export PASSWORD={S}\nnext", "ab,cd"),
		("secret-assignment", "\"password={S}\"", "ab,cd;ef"),

		// Low: short session cookies and curl's cookie flags.
		("cookie-header", "Cookie: {S}", "sid=" + Synthetic.Body(12, 854)),
		("cu" + "rl-cookie", "cu" + "rl -b \"{S}\" https://h", "sid=" + s_random32),
		("cu" + "rl-cookie", "cu" + "rl --cookie \"{S}\" https://h", "sid=" + s_random32),
		("session-cookie", "PHPSESSID={S}", Synthetic.Body(26, 855)),
	];

	/// <summary>Text that must survive byte for byte.</summary>
	public static readonly string[] Negatives =
	[
		"pass: v1.2.3-rc1",
		"pass: TestFoo_Works2",
		"sk-learn-intelex-2024-01-release-build-artifacts",
		"cu" + "rl --user-agent=Mozilla:5.0 https://h",
		"cu" + "rl --user-agent Mozilla:5.0 https://h",
		"ps -u root:wheel",
		"password=$abc123",
		"password=\"$API_KEY\"",
		"password='$API_KEY'",
		"password=\"<Secret>\"",
		"password=\"${Secret}\"",
		"password=\"{{ secret }}\"",
		"password: ${{ secrets.Db_Password }}",
		"password=\"$(cat secret.txt)\"",
		"password=\"#{secret}\"",
		"token=\"secrets.GITHUB_TOKEN\"",
		"token=\"env.API_TOKEN\"",
		"token=\"vault:secret/data/app\"",
		"url=https://example.test:8080/p@x",
		"http://host:8080@proxy/",
		"user@example.test",
		"mailto:me@example.test",
		"image:tag@sha256:" + Synthetic.Hex(64, 860),
		"pkg:npm/left-pad@1.3.0",
		"foo:1.2.3@host",
		"a=1&password=$PW&b=2",
		"the --key flag selects a key",
		"primary_key: " + Synthetic.Body(32, 861),
		"public_key: " + Synthetic.Body(32, 862),
		"cache_key: " + Synthetic.Hex(40, 863),
		"sort_key=" + Synthetic.Body(32, 864),
		"monkey: " + Synthetic.Body(32, 865),
		"key: " + Synthetic.Body(32, 866),
		"The primary key: id is unique.",
		"token_count=1234",
		"TOKEN_TYPE=bearer",
		"SECRET_NAME=my-app-secret",
		"PASSWORD_FIELD=userPassword",
		"token_path=/var/run/secrets/kubernetes.io/token",
		"certificate-authority-data: " + Convert.ToBase64String(Encoding.ASCII.GetBytes("-----BEGIN CERTIFICATE-----\n" + Synthetic.PemBody(2, 867))),
		"Set the password in the machine login password prompt.",
		"password required",
		"<password></password>",
		"<password>$PW</password>",
		"<add key=\"Timeout\" value=\"30\"/>",
		"<add key=\"ApiKey\" value=\"$API_KEY\"/>",
		"WHERE password = 'x'",
		"IDENTIFIED BY 'password'",
		"mysql -p",
		"mysql -u root -p db",
		"pip install -pure",
		"tar -pfoo bar",
		"sshpass -p $PW ssh me@h",
		"docker login -p $TOKEN registry.test",
		"login(\"u\", password)",
		"login(\"u\", \"password\")",
		"cu" + "rl -b cookies.txt https://h",
		"Cookie: sid=short",
		"PHPSESSID=short",
		"session_id: 2e39c784-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
		"-----BEGIN " + Synthetic.PrivateWord + " KEY----- marks the start of the file",
		"Begin the private key rotation on Monday.",
		"Look for BEGIN " + Synthetic.PrivateWord + " KEY in the output",
		"The END " + Synthetic.PrivateWord + " KEY line closes it.",
		"it ends with -----END " + Synthetic.PrivateWord + " KEY----- as usual",
		"begin\nprivate key\nend",
		"data:image/png;base64," + Synthetic.PemBody(4, 868),
		Synthetic.Hex(40, 869) + "\n" + Synthetic.Hex(40, 870) + "\n" + Synthetic.Hex(40, 871),
		string.Join('\n', Enumerable.Range(0, 5).Select(static i => Synthetic.Base64(76, 872 + i))),
		"MII" + Synthetic.Base64(80, 877),
		"line one\n" + new string('w', 40) + " with words\nlast",
		string.Join('\n', Enumerable.Range(0, 5).Select(static i => Synthetic.Hex(64, 878 + i))),
		"recent_key: app:recent-items-list",
		"saved_filters_key: \"\\\"AppName:filters\\\"\"",
		"changedByApiKeyId: _currentUser?.GetApiKeyId",
		"auth.tsx: src-auth/components/dialogs",
		"C:\\Users\\me\\node_modules\\@scope\\pkg",
		"file:src_name@host",
	];
}
