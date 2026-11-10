using System.Text;
using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>Regressions from the third adversarial review. Synthetic values only.</summary>
public sealed class ReviewRoundThreeTests
{
	private const string Key = "[REDACTED:private-key]";
	private const string Begin = "-----BEGIN " + Synthetic.PrivateWord + " KEY-----";
	private const string End = "-----END " + Synthetic.PrivateWord + " KEY-----";
	private const string Assigned = "[REDACTED:secret-assignment]";

	private static readonly Redactor Redactor = new();

	private static string Line(int seed = 1100) => Synthetic.Body(64, seed);

	[Theory]
	[InlineData("f:{0}:")]
	[InlineData("f-{0}-")]
	[InlineData("key.pem:{0}: ")]
	[InlineData("2026-11-04T10:00:0{0}Z ")]
	[InlineData("[INFO] ")]
	[InlineData("% ")]
	[InlineData("│ ")]
	[InlineData(" ")]
	[InlineData("REM ")]
	public void Marked_key_with_any_line_prefix_is_removed(string format)
	{
		string[] lines = [Begin, Line(1101), Line(1102), End];
		string input = string.Join('\n', lines.Select((line, i) => string.Format(format, i + 1) + line));

		string output = Redactor.Redact(input).Text;

		Assert.DoesNotContain(Line(1101), output, StringComparison.Ordinal);
		Assert.DoesNotContain(Line(1102), output, StringComparison.Ordinal);
		Assert.DoesNotContain(End, output, StringComparison.Ordinal);
		Assert.EndsWith(Key, output, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("f:{0}:")]
	[InlineData("2026-11-04T10:00:0{0}Z ")]
	[InlineData("[INFO] ")]
	public void Unmarked_key_lines_with_any_prefix_are_removed(string format)
	{
		string input = string.Join('\n', Enumerable.Range(1, 4).Select(i => string.Format(format, i) + Line(1102 + i))) + "\nafter words";

		string output = Redactor.Redact(input).Text;

		Assert.Contains("[REDACTED:private-key-lines]\nafter words", output, StringComparison.Ordinal);
		Assert.DoesNotContain(Line(1104), output, StringComparison.Ordinal);
	}

	[Fact]
	public void Padding_cannot_hide_shorter_real_lines()
	{
		string padding = new string('x', 200);
		string input = $"{Begin}\n{padding}\n{padding}\n{Synthetic.Body(40, 1107)}\n{End}";

		Assert.Equal(Key, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Backward_walk_ignores_padding_when_judging_a_mask()
	{
		string padding = new string('x', 64);
		string input = $"{padding}\n{padding}\n{Synthetic.Body(40, 1108)}\n{End}";

		Assert.Equal("[REDACTED:private-key-end]", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Time_budget_holds_while_every_candidate_is_rejected()
	{
		RedactionRule rejectAll = new("reject", new System.Text.RegularExpressions.Regex("a"), _ => false);
		Redactor redactor = new([rejectAll], TimeSpan.Zero);

		RedactionException exception = Assert.Throws<RedactionException>(() => redactor.Redact(new string('a', 1000)));

		Assert.Equal(RedactionFailure.TimedOut, exception.Failure);
	}

	[Theory]
	[InlineData(" -pa ", 400_000)]
	[InlineData("cu" + "rl -u a:password ", 150_000)]
	[InlineData("cu" + "rl -u a:Hunter2x9 ", 150_000)]
	[InlineData("x -u a:Hunter2x9 ", 200_000)]
	[Trait("Category", RedactorSafetyTests.TimingCategory)]
	public void Repeated_flags_on_one_line_stay_linear(string unit, int repeats)
	{
		// About 4 times the slowest case, curl -u a:Hunter2x9, which took 2.1 s of this thread's CPU on a plain Debug run.
		TimeSpan budget = TimeSpan.FromSeconds(8);
		string input = new StringBuilder(unit.Length * repeats).Insert(0, unit, repeats).ToString();
		TimeSpan started = ThreadCpuTime.Now();

		try
		{
			Redactor.Redact(input);
		}
		catch (RedactionException exception)
		{
			Assert.Equal(RedactionFailure.ScanBudgetExhausted, exception.Failure);
		}

		TimeSpan spent = ThreadCpuTime.Now() - started;
		Assert.True(spent < budget, $"Repeated flags took {spent.TotalSeconds:F1} s of CPU, over the budget of {budget.TotalSeconds:F0} s.");
	}

	[Theory]
	[InlineData("cu" + "rl -s \\\n  -u me:{S} https://h", "cu" + "rl-user-password")]
	[InlineData("cu" + "rl -s \\\r\n  -u me:{S} https://h", "cu" + "rl-user-password")]
	[InlineData("cu" + "rl -s `\n  -u me:{S} https://h", "cu" + "rl-user-password")]
	[InlineData("mysql -h db \\\n  -p{S} x", "mysql-password")]
	[InlineData("mysql -p'{S}' x", "mysql-password")]
	[InlineData("mysql -p\"{S}\" x", "mysql-password")]
	[InlineData("sudo FOO=1 /usr/bin/mysql -p{S}", "mysql-password")]
	[InlineData("docker exec db mysql -p{S}", "mysql-password")]
	[InlineData("kubectl exec db -- mysql -p{S}", "mysql-password")]
	[InlineData("$ mysql -p{S}", "mysql-password")]
	[InlineData("true && mysql -p{S}", "mysql-password")]
	public void Command_is_found_across_continuations_and_wrappers(string template, string rule)
	{
		string input = template.Replace("{S}", "Hunter2x", StringComparison.Ordinal);

		Assert.Equal(template.Replace("{S}", $"[REDACTED:{rule}]", StringComparison.Ordinal), Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("email:alice2@example.com")]
	[InlineData("Author:bob1@example.com")]
	[InlineData("npm:lodash4@4.17.21")]
	[InlineData("12:30pm@home")]
	[InlineData("docker run --name mysql -p3306:3306 image")]
	[InlineData("mkdir mysql && mkdir -pv dir")]
	[InlineData("cache-key-0123456789abcdef0123456789abcdef")]
	[InlineData("file_key: 0123456789abcdef0123456789abcdef")]
	[InlineData("data_key: 0123456789abcdef0123456789abcdef")]
	[InlineData("record_key: 0123456789abcdef0123456789abcdef")]
	[InlineData("storage_key: 0123456789abcdef0123456789abcdef")]
	[InlineData("pass: TestFoo_Works2")]
	[InlineData("pass: TestFooBarBaz_WorksCorrectly2")]
	[InlineData("--- PASS: TestFooBarBaz_WorksCorrectly2")]
	public void Over_match_is_closed(string input)
	{
		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("cfut_", 40, "cloudflare-token")]
	[InlineData("sntrys_", 60, "sentry-token")]
	[InlineData("glsa_", 40, "grafana-token")]
	[InlineData("glc_", 60, "grafana-token")]
	[InlineData("figd_", 40, "figma-token")]
	[InlineData("tvly-", 32, "tavily-key")]
	[InlineData("pplx-", 48, "perplexity-key")]
	[InlineData("r8_", 37, "replicate-token")]
	[InlineData("rnd_", 32, "render-key")]
	[InlineData("nfp_", 40, "netlify-token")]
	[InlineData("pscale_tkn_", 43, "planetscale-token")]
	[InlineData("dp.st.", 44, "doppler-token")]
	[InlineData("LTAI", 20, "alibaba-key")]
	[InlineData("sq0atp-", 22, "square-token")]
	[InlineData("ATCTT3", 60, "atlassian-cookie-token")]
	[InlineData("sb_secret_", 32, "supabase-secret")]
	[InlineData("EAAC", 60, "facebook-token")]
	[InlineData("api_org_", 34, "huggingface-org-token")]
	[InlineData("glffct-", 24, "gitlab-feature-flag-token")]
	public void New_provider_prefix_is_redacted(string prefix, int body, string rule)
	{
		string token = prefix[..1] + prefix[1..] + Synthetic.Body(body, 1109);

		Assert.Equal($"use [REDACTED:{rule}] now", Redactor.Redact($"use {token} now").Text);
	}

	[Theory]
	[InlineData(11, false)]
	[InlineData(12, true)]
	[InlineData(20, true)]
	[InlineData(21, false)]
	public void Alibaba_key_length_is_bounded_on_both_sides(int body, bool redacted)
	{
		string key = "LT" + "AI" + Synthetic.Body(body, 1400);

		Assert.Equal(redacted ? "[REDACTED:alibaba-key]" : key, Redactor.Redact(key).Text);
	}

	[Theory]
	[InlineData(23, false)]
	[InlineData(24, true)]
	public void Google_access_token_needs_its_minimum_length(int total, bool redacted)
	{
		string token = "ya" + "29." + Synthetic.Body(total - 5, 1401);

		Assert.Equal(redacted ? "[REDACTED:google-oauth-token]" : token, Redactor.Redact(token).Text);
	}

	[Fact]
	public void Hubspot_and_teams_shapes_are_redacted()
	{
		string hubspot = "pa" + "t-na1-" + "01234567-89ab-cdef-0123-456789abcdef";
		string teams = "https://x.webhook.office.com/webhookb2/" + "01234567-89ab-cdef-0123-456789abcdef@01234567-89ab-cdef-0123-456789abcdef/IncomingWebhook/" + Synthetic.Hex(32, 1110);

		Assert.Equal("[REDACTED:hubspot-token]", Redactor.Redact(hubspot).Text);
		Assert.Equal("https://x.webhook.office.com/webhookb2/[REDACTED:teams-webhook]", Redactor.Redact(teams).Text);
	}

	[Theory]
	[InlineData("-----BEGIN TRUSTED CERTIFICATE-----\n{B}\n-----END TRUSTED CERTIFICATE-----")]
	[InlineData("-----BEGIN CERTIFICATE-----\n{B}")]
	[InlineData("-----BEGIN CERTIFICATE-----\n\n{B}\n-----END CERTIFICATE-----")]
	[InlineData("-----BEGIN PGP PUBLIC KEY BLOCK-----\n{B}\n-----END PGP PUBLIC KEY BLOCK-----")]
	[InlineData("-----BEGIN X509 CRL-----\n{B}\n-----END X509 CRL-----")]
	[InlineData("-----BEGIN PGP SIGNATURE-----\n{B}\n-----END PGP SIGNATURE-----")]
	[InlineData("-----BEGIN PGP MESSAGE-----\n{B}\n-----END PGP MESSAGE-----")]
	[InlineData("-----BEGIN PKCS7-----\n{B}\n-----END PKCS7-----")]
	[InlineData("-----BEGIN DH PARAMETERS-----\n{B}\n-----END DH PARAMETERS-----")]
	[InlineData("-----BEGIN SSH SIGNATURE-----\n{B}\n-----END SSH SIGNATURE-----")]
	[InlineData("data:image/png;base64,{B}")]
	[InlineData("{B}\n-----END CERTIFICATE-----")]
	public void Public_material_survives(string template)
	{
		string body = string.Join('\n', Enumerable.Range(0, 30).Select(static i => Synthetic.Body(64, 1120 + i)));
		string input = template.Replace("{B}", body, StringComparison.Ordinal);

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Go_checksum_lists_survive_with_any_prefix()
	{
		string lines = string.Join('\n', Enumerable.Range(0, 6).Select(static i => $"github.com/a/b{i} v1.2.{i} h1:{Synthetic.Body(43, 1190 + i)}="));

		Assert.Equal(lines, Redactor.Redact(lines).Text);
	}

	[Fact]
	public void Lockfile_hash_lists_survive()
	{
		string lines = string.Join('\n', Enumerable.Range(0, 5).Select(static i => $"  integrity sha512-{Synthetic.Body(86, 1196 + i)}=="));

		Assert.Equal(lines, Redactor.Redact(lines).Text);
	}

	[Theory]
	[InlineData("f(\"password=admin\");", "f(\"password=[REDACTED:secret-assignment]\");")]
	[InlineData("f('password=admin-x');", "f('password=[REDACTED:secret-assignment]');")]
	[InlineData("x = \"apikey=a1b2c3d4e5f6a7b8\"", "x = \"apikey=[REDACTED:secret-assignment]\"")]
	public void Closing_quote_of_a_surrounding_string_is_kept(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Net_use_user_flag_is_not_a_password()
	{
		const string Input = "net use X: \\\\h\\s /user:me";

		Assert.Equal(Input, Redactor.Redact(Input).Text);
	}

	[Fact]
	public void Mixed_case_hex_lists_survive()
	{
		string input = string.Join('\n', Enumerable.Range(0, 5).Select(static i => Synthetic.Hex(32, 1150 + i) + Synthetic.Hex(32, 1160 + i).ToUpperInvariant()));

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("the CERTIFICATE text, the PUBLIC KEY, and base64, are mentioned above\n")]
	[InlineData("-----BEGIN CERTIFICATE-----\nabc\n-----END CERTIFICATE-----\n")]
	public void Words_above_a_run_do_not_hide_it(string above)
	{
		string body = string.Join('\n', Enumerable.Range(0, 3).Select(static i => Synthetic.Body(64, 1170 + i)));

		Assert.Equal(above + "[REDACTED:private-key-lines]", Redactor.Redact(above + body).Text);
	}

	[Fact]
	public void Certificate_end_marker_far_below_a_run_does_not_hide_it()
	{
		string body = string.Join('\n', Enumerable.Range(0, 3).Select(static i => Synthetic.Body(64, 1173 + i)));

		Assert.Equal("[REDACTED:private-key-lines]\nsome prose\n-----END CERTIFICATE-----", Redactor.Redact(body + "\nsome prose\n-----END CERTIFICATE-----").Text);
	}

	[Fact]
	public void Credit_scales_with_length_so_line_separated_decoys_do_not_exhaust_it()
	{
		string decoy = "token_count=" + string.Concat(Enumerable.Repeat("word-", 1000)) + "\n";
		string decoys = string.Concat(Enumerable.Repeat(decoy, 40));
		string input = decoys + "password=" + "Hunter2x";

		Assert.Equal(decoys + "password=" + Assigned, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Secret_after_a_long_rejected_value_on_the_same_line_is_found()
	{
		string input = "secret=" + string.Concat(Enumerable.Repeat("word-", 1000)) + "password=Hunter2x";

		Assert.DoesNotContain("Hunter2x", Redactor.Redact(input).Text, StringComparison.Ordinal);
	}

	[Fact]
	public void Exhausted_credit_refuses_rather_than_skips()
	{
		RedactionRule rejectAll = new("reject", new System.Text.RegularExpressions.Regex("a+"), _ => false);
		Redactor redactor = new([rejectAll], TimeSpan.FromMinutes(1));

		RedactionException exception = Assert.Throws<RedactionException>(() => redactor.Redact(new string('a', 2_000_000)));

		Assert.Equal("reject", exception.RuleName);
		Assert.Equal(RedactionFailure.ScanBudgetExhausted, exception.Failure);
	}

	[Fact]
	public void Long_value_is_judged_whole()
	{
		string words = string.Concat(Enumerable.Repeat("/some/path/words", 280));
		string secret = Synthetic.Body(40, 1176);

		Assert.Equal("client_secret=" + Assigned, Redactor.Redact("client_secret=" + words + secret).Text);
	}

	[Fact]
	public void Cookie_with_a_session_id_after_many_small_pairs_is_removed()
	{
		string pairs = string.Concat(Enumerable.Range(0, 500).Select(static i => $"p{i}=x; "));

		Assert.Equal("Cookie: [REDACTED:cookie-header]", Redactor.Redact("Cookie: " + pairs + "sid=" + Synthetic.Body(32, 1177)).Text);
	}

	[Theory]
	[InlineData('‎')]
	[InlineData('‮')]
	[InlineData('⁢')]
	[InlineData('͏')]
	[InlineData('️')]
	[InlineData('᠎')]
	[InlineData('؜')]
	[InlineData('�')]
	public void Invisible_character_splitting_a_token_is_read_through(char invisible)
	{
		string token = Synthetic.GitHubClassic('p');

		Assert.Equal("a [REDACTED:github-token] b", Redactor.Redact($"a {token[..8]}{invisible}{token[8..]} b").Text);
	}

	[Fact]
	public void Tag_character_splitting_a_token_is_read_through()
	{
		string token = Synthetic.GitHubClassic('p');

		Assert.Equal("[REDACTED:github-token]", Redactor.Redact(token[..8] + "\U000E0041" + token[8..]).Text);
	}

	[Fact]
	public void Boundary_character_outside_a_separator_is_noise()
	{
		string token = Synthetic.GitHubClassic('p');

		Assert.Equal("[REDACTED:github-token]", Redactor.Redact(token[..8] + Redactor.MessageBoundary + token[8..]).Text);
	}

	[Theory]
	[InlineData("\u001b[?25l")]
	[InlineData("\u001b[38:5:1m")]
	[InlineData("\u009b31m")]
	[InlineData("\u001b]0;title\u0007")]
	[InlineData("\u001b]8;;x\u001b\\")]
	[InlineData("\u001b(B")]
	[InlineData("\u001b[1 q")]
	[InlineData("\\x1b]0;t\\x07")]
	[InlineData("^[]0;t\\a")]
	public void Terminal_sequence_splitting_a_key_name_is_read_through(string sequence)
	{
		Assert.Equal($"pass{sequence}word={Assigned}", Redactor.Redact($"pass{sequence}word=hunter2").Text);
	}

	[Theory]
	[InlineData("&lt;password&gt;x", "<password>")]
	[InlineData("&#119;", "w")]
	[InlineData("&#x77;", "w")]
	public void Entities_decode_for_detection(string input, string meaning)
	{
		DetectionView view = Assert.IsType<DetectionView>(DetectionView.Create(input, joinWrappedLines: false));

		Assert.StartsWith(meaning, view.Text, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("pass&#119;ord=hunter2", "pass&#119;ord=" + Assigned)]
	[InlineData("pass&#x77;ord=hunter2", "pass&#x77;ord=" + Assigned)]
	[InlineData("p%61ssword=hunter2", "p%61ssword=" + Assigned)]
	[InlineData("&#x201C;password&#x201D;: &#x201C;hunter2&#x201D;", "&#x201C;password&#x201D;: &#x201C;" + Assigned + "&#x201D;")]
	public void Encoded_key_names_are_read_through(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("&#0;")]
	[InlineData("&#xD800;")]
	[InlineData("&#99999999;")]
	[InlineData("&#x;")]
	[InlineData("&#;")]
	[InlineData("&#notclosed")]
	[InlineData("%3D")]
	[InlineData("%ZZ")]
	[InlineData("100%")]
	public void Text_that_only_looks_encoded_is_left_alone(string input)
	{
		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("password=it’s9abc", "password=" + Assigned)]
	[InlineData("password=it's9abc", "password=" + Assigned)]
	[InlineData("password=\"ab&quot;9xyz\"", "password=\"" + Assigned + "\"")]
	[InlineData("password=\"ab\"9xyz\"", "password=\"" + Assigned + "\"")]
	public void Stray_quote_does_not_end_a_value(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("password=abcd's quoted' more", "password=" + Assigned + "'s quoted' more")]
	[InlineData("password=\"abcd\" next", "password=\"" + Assigned + "\" next")]
	[InlineData("password=\"abcd\"x", "password=\"" + Assigned + "\"x")]
	public void Paired_quote_still_ends_a_value(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Curly_apostrophes_in_a_large_document_stay_within_memory_and_time()
	{
		string block = "it’s a block of ordinary prose with one curly apostrophe in it\n";
		string input = new StringBuilder(block.Length * 100_000).Insert(0, block, 100_000).ToString();
		long before = GC.GetAllocatedBytesForCurrentThread();

		Assert.Equal(input, Redactor.Redact(input).Text);

		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.True(allocated < input.Length * 80L, "The view allocated more than a few passes' worth of memory.");
	}

	[Theory]
	[InlineData(50)]
	[InlineData(78)]
	[InlineData(90)]
	[InlineData(132)]
	public void Recognized_prefix_running_to_the_line_end_joins_at_any_column(int column)
	{
		string token = "gh" + "p_" + Synthetic.Body(150, 1178);
		string wrapped = token[..column] + "\n" + token[column..];

		Assert.Equal("[REDACTED:github-token]", Redactor.Redact(wrapped).Text);
	}

	[Theory]
	[InlineData(39)]
	[InlineData(40)]
	[InlineData(41)]
	public void Aws_pair_is_removed_whole_near_forty_characters(int length)
	{
		string secret = Synthetic.Body(length, 1179);

		Assert.Equal("[REDACTED:aws-access-key]:[REDACTED:aws-secret-key]", Redactor.Redact(Synthetic.AwsAccessKey + ":" + secret).Text);
	}

	[Theory]
	[InlineData("| Cookie | {S} |", "| Cookie | [REDACTED:cookie-header]|")]
	[InlineData("\"Cookie\": \"{S}\"", "\"Cookie\": \"[REDACTED:cookie-header]\"")]
	public void Cookie_after_a_quote_or_pipe_is_removed(string template, string expected)
	{
		string input = template.Replace("{S}", "sid=" + Synthetic.Body(24, 1180), StringComparison.Ordinal);

		Assert.Equal(expected, Redactor.Redact(input).Text.Replace("] |", "]|", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("Authorization: Bearer abcd1234,efgh5678", "Authorization: Bearer [REDACTED:authorization-header]")]
	[InlineData("Authorization: Bearer abcd1234;efgh5678", "Authorization: Bearer [REDACTED:authorization-header]")]
	[InlineData("Authorization: Bearer abcd1234, next", "Authorization: Bearer [REDACTED:authorization-header], next")]
	public void Tail_after_a_comma_or_semicolon_stays_with_a_bearer_token(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("ssh-keygen -p -P {S} -N x -f k", "command-password")]
	[InlineData("ssh-keygen -t ed25519 -N {S}", "command-password")]
	[InlineData("cmdkey /add:h /user:u /pass:{S}", "command-password")]
	[InlineData("net use X: \\\\h\\s {S} /user:u", "command-password")]
	[InlineData("setx DB_PASSWORD {S}", "command-password")]
	[InlineData("https://h/p?key={S}&x=1", "query-credential")]
	[InlineData("https://h/cb?state=1&code={S}", "query-credential")]
	[InlineData("sessionid={S}", "session-cookie")]
	public void More_credential_forms_are_removed(string template, string rule)
	{
		string secret = Synthetic.Body(24, 1181);
		RedactionResult result = Redactor.Redact(template.Replace("{S}", secret, StringComparison.Ordinal));

		Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
		Assert.True(result.CountsByRule.ContainsKey(rule), rule);
	}

	[Fact]
	public void Digest_response_is_removed()
	{
		string response = Synthetic.Hex(32, 1182);

		Assert.Equal($"Authorization: Digest username=\"u\", response=\"[REDACTED:digest-response]\"", Redactor.Redact($"Authorization: Digest username=\"u\", response=\"{response}\"").Text);
	}

	[Theory]
	[InlineData("https://h/p?key=short")]
	[InlineData("https://h/p?code=200")]
	[InlineData("https://h/p?key=some-word-only-name-here")]
	public void Plain_query_values_survive(string input)
	{
		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Pkcs12_and_ed25519_bare_der_are_removed()
	{
		string pkcs12 = Convert.ToBase64String([0x30, 0x82, 0x09, 0xBD, 0x02, 0x01, 0x03, 0x30, 0x82, 0x09, 0x83, 0x06]) + Synthetic.Body(80, 1183);
		string ed25519 = "MC4CAQAwBQYDK2Vw" + Synthetic.Body(32, 1184);

		Assert.Equal("[REDACTED:private-key-der]", Redactor.Redact(pkcs12).Text);
		Assert.Equal("[REDACTED:private-key-der]", Redactor.Redact(ed25519).Text);
	}

	[Theory]
	[InlineData("IDENTIFIED BY \"{S}\"", "sql-password")]
	[InlineData("<add value=\"{S}\" key=\"ApiKey\"/>", "xml-secret")]
	[InlineData("postgres://u:{S}@h/db", "url-password")]
	public void Listed_forms_are_removed(string template, string rule)
	{
		const string Secret = "it's-Hunter2x";
		string value = rule == "url-password" ? Secret : "Hunter2x";

		Assert.Equal(template.Replace("{S}", $"[REDACTED:{rule}]", StringComparison.Ordinal), Redactor.Redact(template.Replace("{S}", value, StringComparison.Ordinal)).Text);
	}

	[Fact]
	public void Putty_private_lines_are_removed_and_public_lines_survive()
	{
		string input = $"PuTTY-User-Key-File-3: ssh-ed25519\nPublic-Lines: 1\n{Synthetic.Body(40, 1185)}\nPrivate-Lines: 1\n{Synthetic.Body(40, 1186)}\nPrivate-MAC: {Synthetic.Hex(64, 1187)}\n";

		string output = Redactor.Redact(input).Text;

		Assert.Contains(Synthetic.Body(40, 1185), output, StringComparison.Ordinal);
		Assert.Contains("Private-Lines: 1\n[REDACTED:putty-private-key]\n", output, StringComparison.Ordinal);
	}

	[Fact]
	public void One_word_reply_after_a_truncated_key_survives_the_boundary()
	{
		string joined = $"{Begin}\n{Line(1188)}{Redactor.MessageSeparator}Thanks";

		Assert.Equal($"{Key}{Redactor.MessageSeparator}Thanks", Redactor.RedactFragment(joined).Text);
	}

	[Fact]
	public void Short_tail_with_prose_after_it_goes_with_the_key()
	{
		string tail = Synthetic.Body(12, 1189);

		Assert.Equal($"{Key} is the rest", Redactor.Redact($"{Begin}\n{Line(1190)}\n{tail} is the rest").Text);
	}

	[Fact]
	public void Ansi_view_does_not_change_text_outside_the_span()
	{
		const string Input = "\u001b[31mred\u001b[0m password=hunter2 ​after”";

		Assert.Equal("\u001b[31mred\u001b[0m password=" + Assigned + " ​after”", Redactor.Redact(Input).Text);
	}

	[Fact]
	public void Unredacted_text_cannot_be_read_back_from_outside()
	{
		Assert.Null(typeof(Memoria.Core.Sessions.UnredactedMarkdown).GetMethod(nameof(Memoria.Core.Sessions.UnredactedMarkdown.ExposeForRedaction)));
		Assert.Equal(
			"password=" + Assigned,
			Redactor.Redact(Memoria.Core.Sessions.UnredactedMarkdown.FromText("password=hunter2")).Text);
		Assert.Throws<ArgumentNullException>(() => Memoria.Core.Sessions.UnredactedMarkdown.FromText(null!));
	}
}
