using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// Pins list entries and exact boundaries in the engine's newer paths, one case on each side of every
/// line, so a dropped word or a shifted comparison fails a test. Words are written out here on purpose.
/// </summary>
public sealed class RedactionPinTests
{
	private const string Placeholder = "[REDACTED:private-key]";
	private const string Begin = "-----BEGIN " + Synthetic.PrivateWord + " KEY-----";
	private const string End = "-----END " + Synthetic.PrivateWord + " KEY-----";

	private static readonly Redactor Redactor = new();

	private static string Line(int seed = 1000) => Synthetic.Body(64, seed);

	[Theory]
	[InlineData("base64-private-key", "LS0tLS1CRUdJTi")]
	[InlineData("anthropic-key", "sk-ant-")]
	[InlineData("openai-key", "sk-")]
	[InlineData("jwt", "eyJ")]
	[InlineData("slack-webhook", "hooks.slack.com")]
	[InlineData("google-api-key", "AIza")]
	[InlineData("npm-token", "npm_")]
	[InlineData("huggingface-token", "hf_")]
	[InlineData("memoria-api-key", "mem_")]
	[InlineData("sendgrid-key", "SG.")]
	[InlineData("mailgun-key", "key-")]
	[InlineData("digitalocean-token", "_v1_")]
	[InlineData("vercel-token", "vcp_")]
	[InlineData("supabase-token", "sbp_")]
	[InlineData("azure-client-secret", "8Q~")]
	[InlineData("pypi-token", "pypi-")]
	[InlineData("docker-token", "dckr_pat_")]
	[InlineData("linear-token", "lin_api_")]
	[InlineData("plane-token", "plane_api_")]
	[InlineData("age-secret-key", "AGE-SECRET-KEY-1")]
	[InlineData("databricks-token", "dapi")]
	[InlineData("shopify-token", "shp")]
	[InlineData("tailscale-key", "tskey-")]
	[InlineData("atlassian-token", "ATATT3")]
	[InlineData("groq-key", "gsk_")]
	[InlineData("xai-key", "xai-")]
	[InlineData("langsmith-key", "lsv2_")]
	[InlineData("onepassword-token", "ops_")]
	[InlineData("discord-webhook", "discord")]
	[InlineData("cloudflare-token", "cfut_")]
	[InlineData("sentry-token", "sntrys_")]
	[InlineData("figma-token", "figd_")]
	[InlineData("tavily-key", "tvly-")]
	[InlineData("perplexity-key", "pplx-")]
	[InlineData("replicate-token", "r8_")]
	[InlineData("render-key", "rnd_")]
	[InlineData("netlify-token", "nfp_")]
	[InlineData("planetscale-token", "pscale_tkn_")]
	[InlineData("doppler-token", "dp.")]
	[InlineData("alibaba-key", "LTAI")]
	[InlineData("square-token", "sq0")]
	[InlineData("hubspot-token", "pat-")]
	[InlineData("atlassian-cookie-token", "ATCTT3")]
	[InlineData("supabase-secret", "sb_secret_")]
	[InlineData("facebook-token", "EAAC")]
	[InlineData("huggingface-org-token", "api_org_")]
	[InlineData("gitlab-feature-flag-token", "glffct-")]
	[InlineData("putty-private-key", "Private-Lines:")]
	[InlineData("teams-webhook", "webhook.office.com")]
	public void Prefilter_literal_is_pinned(string rule, string literal)
	{
		Assert.Equal(literal, RedactionRules.All.Single(r => r.Name == rule).Literal);
	}

	[Fact]
	public void Rule_with_a_literal_skips_text_without_it_even_when_its_pattern_would_match()
	{
		RedactionRule rule = new("x", new System.Text.RegularExpressions.Regex("a"), literal: "zz");

		Assert.False(rule.TryFindNext("aaa", 0, out _, out _));
		Assert.True(rule.TryFindNext("aaazz", 0, out int start, out int end));
		Assert.Equal((0, 1), (start, end));
		Assert.Null(new RedactionRule("y", new System.Text.RegularExpressions.Regex("a")).Literal);
	}

	[Theory]
	[InlineData("A", "C")]
	[InlineData("S", "K")]
	public void Twilio_key_is_exactly_its_length(string first, string second)
	{
		string key = first + second + Synthetic.Hex(32, 1001);

		Assert.Equal("[REDACTED:twilio-key]", Redactor.Redact(key).Text);
		Assert.Equal(key + "z", Redactor.Redact(key + "z").Text);
	}

	[Fact]
	public void Fixed_length_providers_do_not_match_inside_a_longer_run()
	{
		string mailgun = "ke" + "y-" + Synthetic.Hex(32, 1002) + "z";
		string databricks = "da" + "pi" + Synthetic.Hex(32, 1003) + "z";
		string telegram = Synthetic.Digits(10, 1004) + ":" + Synthetic.Body(35, 1005) + "z";

		Assert.Equal(mailgun, Redactor.Redact(mailgun).Text);
		Assert.Equal(databricks, Redactor.Redact(databricks).Text);
		Assert.Equal(telegram, Redactor.Redact(telegram).Text);
	}

	[Fact]
	public void Provider_needs_a_boundary_on_its_left()
	{
		string token = "xvc" + "p_" + Synthetic.Body(24, 1006);

		Assert.Equal(token, Redactor.Redact(token).Text);
		Assert.Equal("%3D[REDACTED:vercel-token]", Redactor.Redact("%3Dvc" + "p_" + Synthetic.Body(24, 1006)).Text);
	}

	[Theory]
	[InlineData("discord.com")]
	[InlineData("discordapp.com")]
	public void Discord_webhook_keeps_its_host_and_channel(string host)
	{
		string secret = Synthetic.Body(68, 1007);

		Assert.Equal($"https://{host}/api/webhooks/123/[REDACTED:discord-webhook]", Redactor.Redact($"https://{host}/api/webhooks/123/{secret}").Text);
		Assert.Equal($"https://{host}\\/api\\/webhooks\\/123\\/[REDACTED:discord-webhook]", Redactor.Redact($"https://{host}\\/api\\/webhooks\\/123\\/{secret}").Text);
		Assert.Equal($"https://{host}/api/other/123/{secret}", Redactor.Redact($"https://{host}/api/other/123/{secret}").Text);
	}

	[Theory]
	[InlineData("public", "primary", "foreign", "sort", "partition", "cache", "idempotency", "dedup", "dedupe", "object", "map", "hash", "lookup", "composite", "unique")]
	[InlineData("index", "group", "routing", "row", "join", "shard", "message", "event", "query", "ssh", "gpg", "pgp", "the", "a", "an", "this", "that", "your", "my", "our", "each", "any", "by", "of", "s3")]
	public void Non_secret_key_qualifier_is_pinned(params string[] qualifiers)
	{
		Assert.All(qualifiers, qualifier => Assert.Equal(KeyKind.NotSecret, SecretKeyNames.Classify(qualifier + "_key")));
		Assert.All(qualifiers, qualifier => Assert.Equal(KeyKind.NotSecret, SecretKeyNames.Classify(qualifier.ToUpperInvariant() + "_KEYS")));
		Assert.Equal(KeyKind.KeyFamily, SecretKeyNames.Classify("other_key"));
		Assert.Equal(KeyKind.KeyFamily, SecretKeyNames.Classify("other_keys"));
	}

	[Theory]
	[InlineData("BYPASS")]
	[InlineData("COMPASS")]
	[InlineData("ENCOMPASS")]
	[InlineData("SURPASS")]
	public void Words_ending_in_pass_are_not_passwords(string word)
	{
		Assert.Equal(KeyKind.NotSecret, SecretKeyNames.Classify(word));
	}

	[Theory]
	[InlineData("PASS", KeyKind.KeyFamily)]
	[InlineData("XPASS", KeyKind.Password)]
	[InlineData("xpass", KeyKind.NotSecret)]
	[InlineData("psk", KeyKind.Password)]
	[InlineData("PSK", KeyKind.Password)]
	[InlineData("key", KeyKind.NotSecret)]
	[InlineData("key_data", KeyKind.NotSecret)]
	[InlineData("data", KeyKind.NotSecret)]
	[InlineData("x_data", KeyKind.NotSecret)]
	[InlineData("client_key_data", KeyKind.KeyFamily)]
	[InlineData("api_keys", KeyKind.Token)]
	[InlineData("get_token", KeyKind.NotSecret)]
	[InlineData("old_token", KeyKind.Token)]
	public void Key_name_boundaries_are_exact(string key, object expected)
	{
		Assert.Equal(expected, SecretKeyNames.Classify(key));
	}

	[Theory]
	[InlineData("--pass", KeyKind.Password)]
	[InlineData("--PASS", KeyKind.Password)]
	[InlineData("--key", KeyKind.KeyFamily)]
	[InlineData("--KEY", KeyKind.KeyFamily)]
	[InlineData("--some-pass", KeyKind.Token)]
	[InlineData("--some", KeyKind.NotSecret)]
	[InlineData("--", KeyKind.NotSecret)]
	public void Flag_names_are_classified(string key, object expected)
	{
		Assert.Equal(expected, SecretKeyNames.ClassifyFlag(key));
	}

	[Theory]
	[InlineData("`t")]
	[InlineData("\\x09")]
	[InlineData("\\011")]
	[InlineData("%22")]
	public void Spelled_blank_is_a_separator(string blank)
	{
		Assert.Equal(Placeholder, Redactor.Redact(Begin + blank + Line() + blank + Line(1008)).Text);
	}

	[Theory]
	[InlineData("<BR>")]
	[InlineData("&#xa;")]
	[InlineData("\\x0A")]
	[InlineData("\\u000A")]
	[InlineData("&#xD;&#10;")]
	[InlineData("&#xd;&#10;")]
	[InlineData("\\x0d\\x0a")]
	[InlineData("\\x0D\\x0A")]
	[InlineData("\\015\\012")]
	[InlineData("\\u000d\\u000a")]
	[InlineData("\\u000D\\u000A")]
	public void Spelled_line_feed_lets_a_short_line_count(string join)
	{
		string input = string.Join(join, Begin, Line(), Synthetic.Body(20, 1009));

		Assert.Equal(Placeholder, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("12 lines omitted")]
	[InlineData("(elided)")]
	[InlineData("snip")]
	[InlineData("TRUNCATED")]
	[InlineData("...")]
	[InlineData("\u2026")]
	public void Each_elision_word_is_read_past(string elision)
	{
		Assert.Equal(Placeholder, Redactor.Redact($"{Begin}\n{Line()}\n{elision}\n{Line(1010)}").Text);
	}

	[Fact]
	public void Elision_line_over_the_length_limit_is_prose()
	{
		string elision = "... " + string.Concat(Enumerable.Repeat("word ", 20));

		Assert.Equal($"{Placeholder}\n{elision}\n{Line(1011)}", Redactor.Redact($"{Begin}\n{Line()}\n{elision}\n{Line(1011)}").Text);
	}

	[Fact]
	public void Prose_budget_is_three_lines()
	{
		string prose = "Some words here\nMore words here\nEven more words\n";

		Assert.Equal(Placeholder, Redactor.Redact($"{Begin}\n{prose}{Line()}").Text);
		string tooMuch = $"{Begin}\n{prose}One line too many\n{Line()}";
		Assert.DoesNotContain(Placeholder, Redactor.Redact(tooMuch).Text, StringComparison.Ordinal);
	}

	[Fact]
	public void Short_words_after_prose_are_not_a_body()
	{
		string input = $"{Begin}\nSome words here\nabcdefghij\nklmnopqrst\n";

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Short_lines_alone_with_no_end_marker_are_not_a_body()
	{
		string input = $"{Begin}\nabcdefgh\nijklmnop\nqrstuvwx";

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData(7, false, false)]
	[InlineData(8, false, true)]
	[InlineData(3, true, true)]
	public void Final_short_line_joins_a_run_by_length_or_padding(int length, bool padded, bool joins)
	{
		string tail = Synthetic.Body(length, 1012) + (padded ? "=" : "");
		string input = $"{Line(1013)}\n{Line(1014)}\n{Line(1015)}\n{tail}";

		Assert.Equal("[REDACTED:private-key-lines]" + (joins ? "" : "\n" + tail), Redactor.Redact(input).Text);
	}

	[Fact]
	public void Final_line_with_words_after_it_does_not_join_a_run()
	{
		string input = $"{Line(1016)}\n{Line(1017)}\n{Line(1018)}\nabcdefghij and more";

		Assert.Equal("[REDACTED:private-key-lines]\nabcdefghij and more", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Wide_lines_form_a_run_too()
	{
		string input = string.Join('\n', Enumerable.Range(0, 3).Select(static i => Synthetic.Body(70, 1019 + i))) + "\n";

		Assert.Equal("[REDACTED:private-key-lines]\n", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Two_lines_are_not_a_run()
	{
		string input = $"{Line(1022)}\n{Line(1023)}\n";

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("-----BEGIN PUBLIC KEY-----\n")]
	[InlineData("-----BEGIN CERTIFICATE-----\n")]
	[InlineData("data:application/octet-stream;base64,")]
	public void Lines_after_a_non_secret_marker_survive(string marker)
	{
		string input = $"{marker}{Line(1024)}\n{Line(1025)}\n{Line(1026)}\n";

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("-----END CERTIFICATE-----")]
	[InlineData("-----END PUBLIC KEY-----")]
	public void Lines_before_a_non_secret_end_marker_survive(string marker)
	{
		string input = $"{Line(1027)}\n{Line(1028)}\n{Line(1029)}\n{marker}";

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("\\n")]
	[InlineData("\\r\\n")]
	[InlineData("\\\\n")]
	public void Backward_walk_reads_escaped_line_breaks(string join)
	{
		string input = $"\"{Line(1030)}{join}{Line(1031)}{join}{End}\"";

		Assert.Equal("\"[REDACTED:private-key-end]\"", Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData(31, false)]
	[InlineData(32, true)]
	public void Backward_walk_needs_a_full_line_of_body(int length, bool isKey)
	{
		string body = Synthetic.Body(length, 1032);

		Assert.Equal(isKey ? "[REDACTED:private-key-end]" : $"{body}\n{End}", Redactor.Redact($"{body}\n{End}").Text);
	}

	[Fact]
	public void Backward_walk_stops_at_a_short_line_above_the_nearest()
	{
		string body = Line(1033);

		Assert.Equal("short\n[REDACTED:private-key-end]", Redactor.Redact($"short\n{body}\n{End}").Text);
	}

	[Fact]
	public void Backward_walk_treats_a_mask_as_a_mention()
	{
		string input = new string('x', 64) + "\n" + End;

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData(new byte[] { 0x30, 0x82, 0x04, 0xBD, 0x02, 0x01, 0x02, 0x30, 0x0D, 0x06, 0x09, 0x2A }, false)]
	[InlineData(new byte[] { 0x30, 0x82, 0x04, 0xBD, 0x02, 0x02, 0x00, 0x30, 0x0D, 0x06, 0x09, 0x2A }, false)]
	[InlineData(new byte[] { 0x30, 0x82, 0x04, 0xBD, 0x03, 0x01, 0x00, 0x30, 0x0D, 0x06, 0x09, 0x2A }, false)]
	[InlineData(new byte[] { 0x30, 0x81, 0x87, 0x02, 0x01, 0x00, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86 }, true)]
	[InlineData(new byte[] { 0x30, 0x88, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x02, 0x01 }, false)]
	[InlineData(new byte[] { 0x30, 0x82, 0x04, 0xBD, 0x02, 0x01, 0x03, 0x30, 0x0D, 0x06, 0x09, 0x2A }, true)]
	public void Der_head_is_read_exactly(byte[] head, bool isKey)
	{
		Assert.Equal(isKey, PemBlock.IsDerPrivateKey(Convert.ToBase64String(head)));
	}

	[Theory]
	[InlineData("MIIEvQIBADANBgk", false)]
	[InlineData("MIIEvQIBADANBgk!", false)]
	[InlineData("MIIEvQIBADANBgkq", true)]
	public void Der_head_needs_sixteen_valid_characters(string value, bool isKey)
	{
		Assert.Equal(isKey, PemBlock.IsDerPrivateKey(value));
	}

	[Fact]
	public void Base64_pem_needs_its_whole_head()
	{
		string whole = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("-----BEGIN " + Synthetic.PrivateWord + " KEY-----\n" + Synthetic.Body(40, 1034)));

		Assert.True(PemBlock.IsBase64Pem(whole));
		Assert.True(PemBlock.IsBase64Pem(whole[..64]));
		Assert.False(PemBlock.IsBase64Pem(whole[..63]));
	}

	[Theory]
	[InlineData("abcdefghijklmnopqrs1", true)]
	[InlineData("abcdefghijklmnopqr1", false)]
	[InlineData("AbCdEfGhIjKlMnOpQrSt", true)]
	[InlineData("abcdefghijklmnopqrst", false)]
	[InlineData("ABCDEFGHIJKLMNOPQRST", false)]
	[InlineData("someLongCamelCaseIdentifier", false)]
	[InlineData("a1b2c3d4e5f6", true)]
	[InlineData("a1b2c3d4e5f", false)]
	[InlineData("A1B2C3D4E5F6", true)]
	[InlineData("123456789012", false)]
	[InlineData("getPassword1", false)]
	[InlineData("a1a1a1a1a1a1", false)]
	public void Random_segment_boundaries_are_exact(string part, bool expected)
	{
		Assert.Equal(expected, ValueShapes.IsRandomSegment(part));
	}

	[Theory]
	[InlineData("abc", true)]
	[InlineData("a-b-1", true)]
	[InlineData("", true)]
	[InlineData("ABC", false)]
	[InlineData("a_b", false)]
	[InlineData("0123456789012345678901234567890123456789", true)]
	[InlineData("01234567890123456789012345678901234567890", false)]
	public void Placeholder_name_is_exact(string name, bool expected)
	{
		Assert.Equal(expected, ValueShapes.IsPlaceholderName(name));
	}

	[Theory]
	[InlineData("[REDACTED]", true)]
	[InlineData("[redacted]", true)]
	[InlineData("[REDACTED:a-b]", true)]
	[InlineData("[REDACTED:a-b", true)]
	[InlineData("[REDACTED:A]", false)]
	[InlineData("[redacted:a]", false)]
	[InlineData("REDACTED:a", false)]
	public void Redacted_form_is_exact(string text, bool expected)
	{
		Assert.Equal(expected, ValueShapes.IsRedactedForm(text));
	}

	[Theory]
	[InlineData("<a>", "<a>")]
	[InlineData("<a", "<a>")]
	[InlineData("$(a", "$(a b)")]
	[InlineData("f(a", "f(a b)")]
	[InlineData("x[a", "x[a b]")]
	[InlineData("{{a", "{{a b}}")]
	public void Value_extends_to_its_closer_on_the_same_line(string value, string expected)
	{
		string input = "=" + value + expected[value.Length..];

		Assert.Equal(expected, ValueShapes.ExtendToCloser(input, 1, 1 + value.Length, value));
	}

	[Theory]
	[InlineData("\"f(a", 1, 4)]
	[InlineData("'f(a", 1, 4)]
	[InlineData("`f(a", 1, 4)]
	public void Quoted_value_is_already_whole(string input, int start, int end)
	{
		Assert.Equal("f(a", ValueShapes.ExtendToCloser(input + ")", start, end, "f(a"));
	}

	[Fact]
	public void Closer_on_a_later_line_does_not_extend_a_value()
	{
		Assert.Equal("f(a", ValueShapes.ExtendToCloser("f(a\n)", 0, 3, "f(a"));
		Assert.Equal("f(a", ValueShapes.ExtendToCloser("f(a\r)", 0, 3, "f(a"));
		Assert.Equal("f(a", ValueShapes.ExtendToCloser("f(a", 0, 3, "f(a"));
		Assert.Equal("f(a" + new string(' ', 199) + ")", ValueShapes.ExtendToCloser("f(a" + new string(' ', 199) + ")", 0, 3, "f(a"));
		Assert.Equal("f(a", ValueShapes.ExtendToCloser("f(a" + new string(' ', 200) + ")", 0, 3, "f(a"));
	}

	[Theory]
	[InlineData("mysql -pAbc123", "mysql -p[REDACTED:mysql-password]")]
	[InlineData("mariadb -pAbc123", "mariadb -p[REDACTED:mysql-password]")]
	[InlineData("mysqladmin -pAbc123", "mysqladmin -p[REDACTED:mysql-password]")]
	[InlineData("sudo mysql -pAbc123", "sudo mysql -p[REDACTED:mysql-password]")]
	[InlineData("xmysql -pAbc123", "xmysql -pAbc123")]
	[InlineData("mysqlx -pAbc123", "mysqlx -pAbc123")]
	[InlineData("mysql\ntar -pAbc123", "mysql\ntar -pAbc123")]
	[InlineData("mysql -p$PW", "mysql -p$PW")]
	public void Mysql_flag_needs_the_mysql_command(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("PASSWORD=ab,cd", "PASSWORD=[REDACTED:secret-assignment]")]
	[InlineData("set PASSWORD=ab,cd", "set PASSWORD=[REDACTED:secret-assignment]")]
	[InlineData("export PASSWORD=ab,cd\r\nnext", "export PASSWORD=[REDACTED:secret-assignment]\r\nnext")]
	[InlineData("  PASSWORD=abcd,ef", "  PASSWORD=[REDACTED:secret-assignment]")]
	[InlineData("xset PASSWORD=abcd,ef", "xset PASSWORD=[REDACTED:secret-assignment],ef")]
	[InlineData("exportx PASSWORD=abcd,ef", "exportx PASSWORD=[REDACTED:secret-assignment],ef")]
	[InlineData("f(PASSWORD=abcd,ef)", "f(PASSWORD=[REDACTED:secret-assignment],ef)")]
	[InlineData("PASSWORD=abcd,ef more", "PASSWORD=[REDACTED:secret-assignment],ef more")]
	[InlineData("PASSWORD=abcd,ef=1", "PASSWORD=[REDACTED:secret-assignment],ef=1")]
	[InlineData("PASSWORD=abcd&ef=1", "PASSWORD=[REDACTED:secret-assignment]&ef=1")]
	[InlineData("PASSWORD=abcd,", "PASSWORD=[REDACTED:secret-assignment],")]
	[InlineData("'password=ab;cdef'", "'password=[REDACTED:secret-assignment]'")]
	[InlineData("\"password=abcd;ef' x", "\"password=[REDACTED:secret-assignment];ef' x")]
	[InlineData("password=\"abcd\",ef", "password=\"[REDACTED:secret-assignment]\",ef")]
	public void Value_runs_to_the_end_only_when_it_fills_its_line_or_string(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("http://h:123456@x/", "http://h:[REDACTED:url-password]@x/")]
	[InlineData("https://h:12345@x/", "https://h:12345@x/")]
	[InlineData("HTTP://h:12345@x/", "HTTP://h:12345@x/")]
	[InlineData("postgres://h:12345@x/", "postgres://h:[REDACTED:url-password]@x/")]
	[InlineData("postgres://h:12/x@y", "postgres://h:12/x@y")]
	[InlineData("postgres://h:ab/x@y", "postgres://h:[REDACTED:url-password]@y")]
	[InlineData("postgres://h:123456/x@y", "postgres://h:[REDACTED:url-password]@y")]
	public void Url_port_and_password_are_told_apart(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("connect u:pw1@host", "connect u:pw1@host")]
	[InlineData("connect u:pw12@host", "connect u:[REDACTED:userinfo-password]@host")]
	[InlineData("connect u:pw-x@host", "connect u:[REDACTED:userinfo-password]@host")]
	[InlineData("connect u:pw_x@host", "connect u:pw_x@host")]
	[InlineData("connect u:abcd@host", "connect u:abcd@host")]
	[InlineData("x:abc1@sha256", "x:abc1@sha256")]
	[InlineData("root:pw@tcp(h)", "root:[REDACTED:userinfo-password]@tcp(h)")]
	[InlineData("root:pw@unix(h)", "root:[REDACTED:userinfo-password]@unix(h)")]
	[InlineData("root:password@tcp(h)", "root:password@tcp(h)")]
	public void Bare_userinfo_boundaries_are_exact(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("token: v1.2.3", "token: v1.2.3")]
	[InlineData("token: 1.2.3-rc1abcdefgh", "token: 1.2.3-rc1abcdefgh")]
	[InlineData("token: 1a2b.3c4d", "token: [REDACTED:secret-assignment]")]
	[InlineData("token: v12345678", "token: [REDACTED:secret-assignment]")]
	[InlineData("token: v.12345678", "token: [REDACTED:secret-assignment]")]
	public void Version_shape_is_exact(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("api_key: //host/a", "api_key: //host/a")]
	[InlineData("api_key: x://host/a", "api_key: x://host/a")]
	[InlineData("api_key: C:\\a\\b1c", "api_key: C:\\a\\b1c")]
	[InlineData("api_key: Cx:\\a\\b1cdefgh", "api_key: [REDACTED:secret-assignment]")]
	[InlineData("api_key: 1:/a/b1c2d3e4", "api_key: [REDACTED:secret-assignment]")]
	[InlineData("api_key: /a1/b2/" + "abcdefghijklmnopqrstuvwxyz0123456", "api_key: [REDACTED:secret-assignment]")]
	[InlineData("api_key: /a1/b2/" + "abcdefghijklmnopqrstuvwxyz012345", "api_key: /a1/b2/abcdefghijklmnopqrstuvwxyz012345")]
	[InlineData("api_key: /a1/AuthProvider.tsx", "api_key: /a1/AuthProvider.tsx")]
	public void Locator_boundaries_are_exact(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("Cookie: sid=abcdefgh1234", "Cookie: [REDACTED:cookie-header]")]
	[InlineData("Cookie: sid=abcdefgh123", "Cookie: sid=abcdefgh123")]
	[InlineData("PHPSESSID=abcdefgh1234", "PHPSESSID=[REDACTED:session-cookie]")]
	[InlineData("JSESSIONID=abcdefgh1234", "JSESSIONID=[REDACTED:session-cookie]")]
	[InlineData("ASP.NET_SessionId=abcdefgh1234", "ASP.NET_SessionId=[REDACTED:session-cookie]")]
	[InlineData("connect.sid=abcdefgh1234", "connect.sid=[REDACTED:session-cookie]")]
	public void Session_cookie_boundaries_are_exact(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("abcdefghij1234567890", true)]
	[InlineData("abcdefghij123456789", false)]
	[InlineData("aabbccddeeff00112233", false)]
	[InlineData("two words abcdefghij1234567890", false)]
	[InlineData("12345678901234567890", false)]
	public void High_information_boundaries_are_exact(string value, bool expected)
	{
		Assert.Equal(expected, SecretHeuristics.IsHighInformation(value));
	}

	[Theory]
	[InlineData("app:recent-items", true)]
	[InlineData("a/b/c", true)]
	[InlineData("a\\b", true)]
	[InlineData("no-separator", false)]
	[InlineData("app:1a", false)]
	public void Word_list_shape_is_exact(string value, bool expected)
	{
		Assert.Equal(expected, ValueShapes.ReadsAsWordList(value));
	}

	[Theory]
	[InlineData("plain text", false)]
	[InlineData("a\u200Bb", true)]
	[InlineData("a\u200Cb", true)]
	[InlineData("a\u200Db", true)]
	[InlineData("a\u2060b", true)]
	[InlineData("a\uFEFFb", true)]
	[InlineData("a\u00ADb", true)]
	[InlineData("\u001b", true)]
	[InlineData("&quot;", true)]
	[InlineData("&q", false)]
	public void Noise_scan_is_exact(string text, bool expected)
	{
		Assert.Equal(expected, DetectionView.HasNoise(text));
	}

	[Fact]
	public void View_maps_entities_and_removed_characters_back_exactly()
	{
		DetectionView view = Assert.IsType<DetectionView>(DetectionView.Create("a&quot;b\u200Bc", joinWrappedLines: false));

		Assert.Equal("a\"bc", view.Text);
		Assert.Equal((0, 1), view.ToOriginal(0, 1));
		Assert.Equal((1, 7), view.ToOriginal(1, 2));
		Assert.Equal((7, 8), view.ToOriginal(2, 3));
		Assert.Equal((9, 10), view.ToOriginal(3, 4));
	}

	[Theory]
	[InlineData("plain", false)]
	[InlineData("\\u001bnot-an-escape", true)]
	[InlineData("^[[1", true)]
	public void View_is_null_only_when_nothing_changes(string text, bool hasNoise)
	{
		Assert.Equal(hasNoise, DetectionView.HasNoise(text));
		Assert.Null(DetectionView.Create(text, joinWrappedLines: false));
	}

	[Theory]
	[InlineData("\u001b[", "m")]
	[InlineData("\u001b[1;2", "H")]
	[InlineData("\\e[1", "m")]
	public void Ansi_escape_is_recognized_whole(string open, string close)
	{
		DetectionView view = Assert.IsType<DetectionView>(DetectionView.Create("a" + open + close + "b", joinWrappedLines: false));

		Assert.Equal("ab", view.Text);
	}

	[Theory]
	[InlineData("\u001b[1;", "")]
	[InlineData("\u001b[1;", "!")]
	[InlineData("\u001bx", "")]
	public void Incomplete_ansi_escape_is_kept(string open, string close)
	{
		DetectionView? view = DetectionView.Create("a" + open + close, joinWrappedLines: false);

		Assert.Null(view);
	}

	[Theory]
	[InlineData(16, true)]
	[InlineData(15, false)]
	public void Wrap_at_a_column_needs_a_long_enough_run(int run, bool joins)
	{
		string line = new string(' ', 76 - run) + Synthetic.Body(run, 1035);

		Assert.Equal(joins, LineWraps.JoinLength(line + "\na1B2", 76) > 0);
	}

	[Theory]
	[InlineData(64, true)]
	[InlineData(72, true)]
	[InlineData(76, true)]
	[InlineData(80, true)]
	[InlineData(100, true)]
	[InlineData(120, true)]
	[InlineData(79, false)]
	public void Wrap_columns_are_pinned(int column, bool joins)
	{
		string line = new string(' ', column - 20) + Synthetic.Body(20, 1036);

		Assert.Equal(joins, LineWraps.JoinLength(line + "\na1B2", column) > 0);
	}

	[Theory]
	[InlineData(20, 8, true)]
	[InlineData(19, 8, false)]
	[InlineData(20, 7, false)]
	public void Wrap_of_a_whole_line_needs_both_runs(int lineLength, int nextLength, bool joins)
	{
		string line = Synthetic.Body(lineLength - 1, 1037) + "1";
		string next = "1" + Synthetic.Body(nextLength - 1, 1038);

		Assert.Equal(joins, LineWraps.JoinLength(line + "\n" + next, lineLength) > 0);
	}

	[Theory]
	[InlineData("abcdefghij", false)]
	[InlineData("ABCDEFGHIJ", false)]
	[InlineData("abcdefghi1", false)]
	[InlineData("abcdefghiJ", true)]
	[InlineData("1234567890", true)]
	[InlineData("12345678", false)]
	[InlineData("The", false)]
	[InlineData("JavaScript", false)]
	[InlineData("OpenAI", false)]
	[InlineData("HTTP2", false)]
	[InlineData("x86", false)]
	[InlineData("1.", false)]
	[InlineData("2026-01-15", false)]
	[InlineData("README.md", false)]
	[InlineData("src/App2.tsx", false)]
	[InlineData("abc1234ZZ", true)]
	[InlineData("aB3dEf7H", true)]
	[InlineData("QwErTyUi", true)]
	[InlineData("App2.a1B2", true)]
	public void Wrap_continuation_must_look_random(string next, bool joins)
	{
		string line = Synthetic.Body(30, 1039);

		Assert.Equal(joins, LineWraps.JoinLength(line + "\n" + next, 30) > 0);
	}

	[Theory]
	[InlineData("\n", 1)]
	[InlineData("\r\n", 2)]
	[InlineData("\r", 1)]
	[InlineData("\n  ", 3)]
	[InlineData("\n\t", 2)]
	public void Join_length_covers_the_break_and_the_indent(string lineBreak, int expected)
	{
		string line = Synthetic.Body(30, 1040);

		Assert.Equal(expected, LineWraps.JoinLength(line + lineBreak + "a1B2c3D4e5", 30));
	}

	[Theory]
	[InlineData("_a1B2c3D4e5", true)]
	[InlineData("-a1B2c3D4e5", true)]
	[InlineData("+a1B2c3D4e5", true)]
	[InlineData("/a1B2c3D4e5", true)]
	[InlineData("=a1B2c3D4e5", true)]
	[InlineData(".a1B2c3D4e5", true)]
	[InlineData("!a1B2c3D4e5", false)]
	public void Token_characters_on_the_next_line_are_pinned(string next, bool joins)
	{
		string line = Synthetic.Body(30, 1041);

		Assert.Equal(joins, LineWraps.JoinLength(line + "\n" + next, 30) > 0);
	}

	[Fact]
	public void Join_refuses_edges_and_shared_prefixes()
	{
		string line = "abcd" + Synthetic.Body(30, 1042);

		Assert.Equal(0, LineWraps.JoinLength("\na1B2c3D4e5", 0));
		Assert.Equal(0, LineWraps.JoinLength(line + "\n", line.Length));
		Assert.Equal(0, LineWraps.JoinLength(line + "\nabcd1234ZZ", line.Length));
		Assert.Equal(11, LineWraps.JoinLength(line + "\n          abc1234ZZ", line.Length));
		Assert.True(LineWraps.Any("x\n" + line + "\na1B2c3D4e5"));
		Assert.True(LineWraps.Any(line + "\r\na1B2c3D4e5"));
		Assert.False(LineWraps.Any("short\nlines\nonly\n"));
		Assert.False(LineWraps.Any("no newline"));
	}

	[Fact]
	public void Rejected_long_candidate_still_yields_a_secret_found_inside_it()
	{
		string token = Synthetic.Body(32, 1043);
		string input = "pageToken=" + new string('a', 6000) + "&api_key=" + token;

		Assert.Equal("pageToken=" + new string('a', 6000) + "&api_key=[REDACTED:secret-assignment]", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Short_candidate_is_judged_whole()
	{
		List<string> values = [];
		RedactionRule rule = new("x", new System.Text.RegularExpressions.Regex("a+"), candidate =>
		{
			values.Add(candidate.Value);
			return false;
		});

		Assert.False(rule.TryFindNext("aaa", 0, out _, out _));

		// Rejected, so the search resumes one character in, because a secret can sit inside a rejected candidate.
		Assert.Equal(["aaa", "aa", "a"], values);
	}

	[Fact]
	public void Search_past_a_long_rejection_resumes_near_its_start_with_overlap_for_a_key_name()
	{
		List<int> starts = [];
		RedactionRule rule = new("x", new System.Text.RegularExpressions.Regex("a+"), candidate =>
		{
			starts.Add(candidate.Secret.Index);
			Assert.Equal(8200 - candidate.Secret.Index, candidate.Value.Length);
			return false;
		});

		Assert.False(rule.TryFindNext(new string('a', 8200), 0, out _, out _));

		Assert.Equal([0, 3968, 7936], starts.Take(3));
	}

	[Fact]
	public void Rule_marked_to_skip_a_rejection_does_not_search_inside_it()
	{
		List<int> starts = [];
		RedactionRule rule = new("x", new System.Text.RegularExpressions.Regex("a+"), candidate =>
		{
			starts.Add(candidate.Secret.Index);
			return false;
		}, skipRejected: true);

		Assert.False(rule.TryFindNext("aaa b aa", 0, out _, out _));

		Assert.Equal([0, 6], starts);
	}

	[Fact]
	public void Fragment_is_not_quoted_but_a_document_is()
	{
		string token = Synthetic.GitHubClassic('p');
		string input = "---\ntitle: " + token + "\n---\n";

		Assert.Equal("---\ntitle: [REDACTED:github-token]\n---\n", Redactor.RedactFragment(input).Text);
		Assert.Equal("---\ntitle: '[REDACTED:github-token]'\n---\n", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Span_across_boundaries_leaves_a_placeholder_in_each_message()
	{
		string joined = Begin + "\n" + Line() + Redactor.MessageSeparator + Line(1044) + Redactor.MessageSeparator + Line(1045) + "\n" + End;

		string[] parts = Redactor.RedactFragment(joined).Text.Split(Redactor.MessageSeparator);

		Assert.Equal([Placeholder, Placeholder, Placeholder], parts);
	}

	[Fact]
	public void Line_rules_run_before_wrapped_lines_are_joined()
	{
		Assert.All(RedactionRules.All.Where(static rule => rule.Name is "private-key" or "private-key-end" or "private-key-lines"), static rule => Assert.True(rule.LinesMatter));
		Assert.All(RedactionRules.All.Where(static rule => rule.Name is "github-token" or "jwt" or "secret-assignment"), static rule => Assert.False(rule.LinesMatter));
	}

	[Fact]
	public void Rules_use_the_non_backtracking_engine_and_ignore_culture()
	{
		Assert.All(RedactionRules.All, static rule => Assert.True(rule.Pattern.Options.HasFlag(System.Text.RegularExpressions.RegexOptions.CultureInvariant), rule.Name));
	}

	[Theory]
	[InlineData("x%22password%22%3A%22abcd%22", "x%22password%22%3A%22[REDACTED:secret-assignment]%22")]
	[InlineData("%2password%3Aabcd e", "%2password%3Aabcd e")]
	public void Encoded_quotes_count_only_when_whole(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("\"---\"")]
	public void Value_reading_as_punctuation_survives(string input)
	{
		Assert.Equal(input, Redactor.Redact(input).Text);
	}
}
