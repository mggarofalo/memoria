using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// Passwords that were stored as they came: a command quoted mid-sentence, an assignment after other words on its line, and a value
/// cut at the first quote or space inside it. Each still leaves the text around it, and the near misses beside them stay as written.
/// </summary>
public sealed class CommandAndAssignmentGapTests
{
	/// <summary>Split, as in the other fixtures, so a scanner does not read these lines as a real command.</summary>
	private const string Curl = "cu" + "rl ";

	private const string CurlRule = "cu" + "rl-user-password";

	private const string CurlName = "cu" + "rl";

	private static readonly string s_password = Synthetic.Body(14, 5001);

	private static string Redact(string text) => Redactor.Shared.RedactFragment(text).Text;

	private static string Fill(string template, string value) => template.Replace("{S}", value, StringComparison.Ordinal);

	[Theory]
	[InlineData("To check it I ran " + Curl + "-u admin:{S} https://h.example.test and it worked", CurlRule)]
	[InlineData("then " + Curl + "-uadmin:{S} https://h.example.test", CurlRule)]
	[InlineData("then I ran " + Curl + "-sS -X POST -u deploy:{S} -H \"Accept: json\" https://h.example.test/hook to trigger it", CurlRule)]
	[InlineData("value here " + Curl + "https://h.example.test -u admin:{S} end", CurlRule)]
	[InlineData("You can connect with mysql -h db -u app -p{S} and then run it.", "mysql-password")]
	[InlineData("ssh box mysql -u app -p{S}", "mysql-password")]
	[InlineData("run mysql -h db --port=3306 -u app -p{S}", "mysql-password")]
	[InlineData("then run mongosh -u admin -p {S} --authenticationDatabase admin", "mongo-password")]
	public void A_command_quoted_mid_sentence_gives_up_its_password(string template, string rule)
	{
		Assert.Equal(Fill(template, $"[REDACTED:{rule}]"), Redact(Fill(template, s_password)));
	}

	[Theory]
	[InlineData("after mysql finished I ran tar -pxf archive.tar")]
	[InlineData("the mysql docs say -pfoo is unsafe")]
	[InlineData("then mysql --host=db app -pxfoo")]
	[InlineData("then mysql -h db app -pxfoo")]
	[InlineData("docker run -p 8080:80 nginx")]
	[InlineData("git log -p -- file")]
	[InlineData("journalctl -u mysql -pwarning")]
	[InlineData("systemctl status mysql -pdump")]
	[InlineData("grep -r mysql -pxfoo")]
	[InlineData("pgrep -f mysql -pid")]
	[InlineData("apt install mysql -pkg")]
	[InlineData("docker run --name mongo -p 27017:27017 -d mongo:7")]
	[InlineData("kubectl port-forward svc/mongo -p 27017")]
	[InlineData("docker compose up -d mongo -p projectname")]
	[InlineData("journalctl -u mongo -p warning")]
	[InlineData("mongosh --port 27017 -p 27017")]
	[InlineData("the mongo -p option prompts")]
	[InlineData("pip install mongo -p sqlalchemy")]
	[InlineData("cd mongo -p packages")]
	[InlineData("then I ran mysql -u app -pletmein")]
	[InlineData("then I ran mysql -u app -pAb3x9")]
	[InlineData("docker run mongo -p 127.0.0.1:27017:27017")]
	[InlineData("podman run mongo -p 0.0.0.0:27017:27017")]
	[InlineData("docker run mongo -p 27017:27017/tcp")]
	[InlineData("docker run mongo -p 27017-27019:27017-27019")]
	[InlineData("docker run mongo -p [::1]:27017:27017")]
	[InlineData("docker run mysql -p127.0.0.1:3306:3306")]
	[InlineData("docker compose up mongo -p proj2")]
	[InlineData("docker compose -f a.yml up mongo -p proj_2024")]
	public void A_flag_of_another_command_stays(string text)
	{
		Assert.Equal(text, Redact(text));
	}

	[Theory]
	[InlineData("ran " + Curl + "-u admin:x", true)]
	[InlineData("ran " + Curl + "https://h -u admin:x", true)]
	[InlineData("ran " + Curl + "-X POST -u admin:x", true)]
	[InlineData("ran " + Curl + "--data=a b -u admin:x", false)]
	[InlineData("ran " + Curl + "then -u admin:x", false)]
	[InlineData("ran /usr/bin/" + Curl + "-u admin:x", true)]
	[InlineData("a" + Curl + "-u admin:x", false)]
	[InlineData("ran - " + Curl + "-u admin:x", true)]
	[InlineData("ran " + Curl + "- -u admin:x", false)]
	[InlineData("ran -o " + Curl + "-u admin:x", false)]
	[InlineData("ran " + Curl + "-v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -u admin:x", true)]
	[InlineData("ran " + Curl + "-v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -v -u admin:x", false)]
	public void A_command_mid_sentence_runs_to_the_flag_through_arguments_only(string text, bool expected)
	{
		Assert.Equal(expected, CommandLines.IsCommand(text, text.IndexOf(" -u", StringComparison.Ordinal), CurlName));
	}

	[Theory]
	[InlineData(Curl + "-u admin:x", "SegmentStart")]
	[InlineData("docker exec box " + Curl + "-u admin:x", "SegmentStart")]
	[InlineData("ran " + Curl + "-u admin:x", "MidSentence")]
	[InlineData("ran wget -u admin:x", "None")]
	public void A_command_s_place_is_known(string text, string expected)
	{
		Assert.Equal(expected, CommandLines.Place(text, text.IndexOf(" -u", StringComparison.Ordinal), CurlName).ToString());
	}

	[Theory]
	[InlineData("mongosh -u admin -p {S}")]
	[InlineData("mongo -u admin -p{S}")]
	[InlineData("mongosh -u admin -p '{S}' --quiet")]
	[InlineData("mongosh -u admin -p \"{S}\"")]
	[InlineData("docker exec db mongosh -u admin -p {S}")]
	[InlineData("mongodump -u admin -p {S} --out dump")]
	[InlineData("mongorestore -u admin -p {S} dump")]
	[InlineData("mongoexport -u admin -p{S} -c items")]
	[InlineData("mongosh -u admin -p\t{S}")]
	public void The_mongo_shell_password_goes(string template)
	{
		string output = Redact(Fill(template, s_password));

		Assert.Equal(Fill(template, "[REDACTED:mongo-password]"), output);
	}

	[Theory]
	[InlineData("mongosh -u admin -p --authenticationDatabase admin")]
	[InlineData("mongosh -u admin -p")]
	[InlineData("mongosh -u admin -p $MONGO_PASSWORD")]
	[InlineData("mongosh -u admin -p abc")]
	[InlineData("psql -h db -p 5432 -U app")]
	[InlineData("ssh -p 2222 me@h")]
	public void A_mongo_shell_prompt_or_another_tool_s_port_stays(string text)
	{
		Assert.Equal(text, Redact(text));
	}

	[Theory]
	[InlineData("user: admin password: {S}")]
	[InlineData("user:admin password: {S}")]
	[InlineData("DB_USER=app DB_PASSWORD: {S}")]
	[InlineData("host=db user=app  password: {S}")]
	public void A_password_after_another_pair_on_its_line_goes(string template)
	{
		Assert.Equal(Fill(template, "[REDACTED:secret-assignment]"), Redact(Fill(template, "letmeinplease")));
	}

	[Theory]
	[InlineData("the password: done")]
	[InlineData("Set the password: required")]
	[InlineData("admin password: abcde")]
	[InlineData("admin password: letmeinplease and more")]
	[InlineData("admin password letmeinplease")]
	[InlineData("admin password: letmeinplease")]
	[InlineData("Then I changed the password: successfully")]
	[InlineData("Reset your password: instructions")]
	[InlineData("database password: production")]
	[InlineData("Note: on the password: rotation")]
	[InlineData("  @Column password: Varchar")]
	[InlineData("x= password: letmeinplease")]
	[InlineData("=app password: letmeinplease")]
	[InlineData(": app password: letmeinplease")]
	[InlineData("user:  app password: letmeinplease")]
	[InlineData("docker run -v \"$PWD\":/out debian")]
	[InlineData("Error: invalid password: expired")]
	[InlineData("Tip: change password: weekly")]
	[InlineData("Step 2: set password: something")]
	[InlineData("- name: db password: managed")]
	[InlineData("## Step 3: Admin password: rotation")]
	[InlineData("time=12:00 msg=login password: failed")]
	[InlineData("user=: password: letmeinplease")]
	[InlineData("2024-01-01 WARN auth user=bob password: rejected")]
	[InlineData("level=info user=bob password: incorrect")]
	[InlineData("user: admin password: changed")]
	[InlineData("email: a@b.test password: weak")]
	[InlineData("DB_HOST=localhost DB_PASSWORD: something")]
	public void Prose_that_names_a_password_without_assigning_one_stays(string text)
	{
		Assert.Equal(text, Redact(text));
	}

	[Fact]
	public void A_bare_word_at_the_start_of_its_line_is_a_password()
	{
		Assert.Equal("password: [REDACTED:secret-assignment]", Redact("password: abcd"));
		Assert.Equal("$password = \"[REDACTED:secret-assignment]\";", Redact("$password = \"letmeinplease\";"));
	}

	[Theory]
	[InlineData("PGPASSWORD=$(printf 'Tr0ub4dor-x9Q') psql", "PGPASSWORD=[REDACTED:secret-assignment] psql")]
	[InlineData("PGPASSWORD=$(printf %s Tr0ub4dor-x9Q) psql", "PGPASSWORD=[REDACTED:secret-assignment] psql")]
	[InlineData("PASSWORD=$(echo Tr0ub4dor-x9Q) psql", "PASSWORD=[REDACTED:secret-assignment] psql")]
	[InlineData("PASSWORD=$(echo -n 'Tr0ub4dor-x9Q')", "PASSWORD=[REDACTED:secret-assignment]")]
	[InlineData("token=[\"a8F3kQ9zLm2Wx7Vb5Nc1Rt6Y\"]", "token=[REDACTED:secret-assignment]")]
	[InlineData("\"api_keys\": [\"a8F3kQ9zLm2Wx7Vb5Nc1Rt6Y\", \"b7G2kQ9zLm2Wx7Vb5Nc1Rt6Z\"]", "\"api_keys\": [REDACTED:secret-assignment]")]
	[InlineData("api_keys: [read, a8F3kQ9zLm2Wx7Vb5Nc1Rt6Y]", "api_keys: [REDACTED:secret-assignment]")]
	[InlineData("const token = `${prefix + \"a8F3kQ9zLm2Wx7Vb5Nc1Rt6Y\"}`;", "const token = `[REDACTED:secret-assignment]`;")]
	[InlineData("const token = `${\"a8F3kQ9zLm2Wx7Vb5Nc1Rt6Y\" + suffix}`;", "const token = `[REDACTED:secret-assignment]`;")]
	[InlineData("password: { correcthorse }", "password: [REDACTED:secret-assignment]")]
	[InlineData("password={Tr0ub4dor-x9Q}", "password=[REDACTED:secret-assignment]")]
	[InlineData("password=${API_KEY/x/Tr0ub4dorx9Q}", "password=[REDACTED:secret-assignment]")]
	[InlineData("password=${API_KEY//x/Tr0ub4dorx9Q} next", "password=[REDACTED:secret-assignment] next")]
	[InlineData("PASSWORD=$( echo Tr0ub4dor-x9Q )", "PASSWORD=[REDACTED:secret-assignment]")]
	[InlineData("PASSWORD=$(/bin/echo Tr0ub4dor-x9Q)", "PASSWORD=[REDACTED:secret-assignment]")]
	[InlineData("api_keys = [[\"a8F3kQ9zLm2Wx7Vb5Nc1Rt6Y\", 1]]", "api_keys = [REDACTED:secret-assignment]")]
	[InlineData("token=${prefix + `a8F3kQ9zLm2Wx7Vb5Nc1Rt6Y`} next", "token=[REDACTED:secret-assignment] next")]
	[InlineData("password: Tr0ub`4dor-x9Q", "password: [REDACTED:secret-assignment]")]
	public void A_value_with_quotes_or_spaces_inside_goes_whole(string text, string expected)
	{
		Assert.Equal(expected, Redact(text));
	}

	[Theory]
	[InlineData("PASSWORD=$(printf ')Tr0ub4dor-x9Q') psql", "PASSWORD=[REDACTED:secret-assignment] psql")]
	[InlineData("PASSWORD=$(echo $(printf Tr0ub4dor-x9Q)) next", "PASSWORD=[REDACTED:secret-assignment] next")]
	[InlineData("token=[\"a8F3kQ9zLm2Wx7Vb5Nc1Rt6Y\", \"]\"] next", "token=[REDACTED:secret-assignment] next")]
	public void A_balanced_value_skips_quoted_closers_and_counts_nesting(string text, string expected)
	{
		Assert.Equal(expected, Redact(text));
	}

	/// <summary>A closer on the next line, or past the window, does not belong to the value, so only the opener is judged.</summary>
	[Fact]
	public void A_balanced_value_ends_on_its_line_and_within_its_window()
	{
		string near = "token=[\"" + Synthetic.Body(200, 5002) + "\"]";
		string far = "token=[\"" + Synthetic.Body(300, 5003) + "\"]";
		string wrapped = "token=[\"" + Synthetic.Body(30, 5004) + "\"\n]";

		Assert.Equal("token=[REDACTED:secret-assignment]", Redact(near));
		Assert.Equal(far, Redact(far));
		Assert.Equal(wrapped, Redact(wrapped));
	}

	[Theory]
	[InlineData("PASSWORD=$(cat /run/secrets/db) psql")]
	[InlineData("TOKEN=$(gh auth token) gh api")]
	[InlineData("PASSWORD=$(echo $DB_PASSWORD) psql")]
	[InlineData("PASSWORD=$(echo \"$PASS\")")]
	[InlineData("PASSWORD=$(echo -n \"$PASS\" | base64)")]
	[InlineData("PASSWORD=$(echo $RANDOM | md5sum | head -c 20)")]
	[InlineData("PASSWORD=$(printf '%s:%s' \"$U\" \"$P\" | base64)")]
	[InlineData("PASSWORD=$(echo x|md5sum)")]
	[InlineData("password: { }")]
	[InlineData("passphrase = { correct horse battery staple }")]
	[InlineData("password: {the db password}")]
	[InlineData("password: {set via env}")]
	[InlineData("password: [Validators.required, Validators.minLength(8)]")]
	[InlineData("password = [c for c in pw if c.isalnum()]")]
	[InlineData("api_keys = [get_key(\"openai\"), get_key(\"anthropic\")]")]
	[InlineData("password=$( echo \"$PW\" )")]
	[InlineData("password=$( printf '%s' \"$X\" )")]
	[InlineData("password=$( printf %s )")]
	[InlineData("password = { get; set; }")]
	[InlineData("password = { inherit (cfg) password; }")]
	[InlineData("PASSWORD=$(printf '%s' \"$DB_PASSWORD\") psql")]
	[InlineData("\"api_keys\": [\"read\", \"write\"]")]
	[InlineData("passwords: [admin, root]")]
	[InlineData("auth: { token }")]
	[InlineData("secret: {{ secrets.TOKEN }}")]
	[InlineData("const key = `${includeLexical}:${query}`;")]
	[InlineData("const token = `${a ? \"one\" : \"two\"}`;")]
	[InlineData("const token = `${randomBytes(32).toString(\"base64url\")}`;")]
	[InlineData("password: { user: admin, pass: x }")]
	public void Code_with_no_literal_secret_stays(string text)
	{
		Assert.Equal(text, Redact(text));
	}

	/// <summary>A backtick that closes a code span ends the value, and one inside a passphrase does not.</summary>
	[Fact]
	public void A_closing_backtick_still_ends_a_value()
	{
		Assert.Equal(
			"Remember to set `password: [REDACTED:secret-assignment]` in the file and `token: abc` too",
			Redact("Remember to set `password: hunter2x` in the file and `token: abc` too"));
	}

	[Theory]
	[InlineData("const h = `Basic ${btoa(\"admin:Tr0ub4dor-x9Q\")}`;", "const h = `Basic ${btoa(\"admin:[REDACTED:basic-auth-literal]\")}`;")]
	[InlineData("headers.Authorization = `Basic ${btoa('admin:Tr0ub4dor-x9Q')}`;", "headers.Authorization = `Basic ${btoa('admin:[REDACTED:basic-auth-literal]')}`;")]
	[InlineData("headers.Authorization = \"Basic \" + btoa(\"admin:Tr0ub4dor-x9Q\");", "headers.Authorization = \"Basic \" + btoa(\"admin:[REDACTED:basic-auth-literal]\");")]
	[InlineData("`Basic ${Buffer.from(`admin:Tr0ub4dor-x9Q`).toString(\"base64\")}`", "`Basic ${Buffer.from(`admin:[REDACTED:basic-auth-literal]`).toString(\"base64\")}`")]
	[InlineData("headers: { Authorization: 'Basic ' + window.btoa('admin:Tr0ub4dor-x9Q') }", "headers: { Authorization: 'Basic ' + window.btoa('admin:[REDACTED:basic-auth-literal]') }")]
	[InlineData("\"Basic \" + base64.b64encode(b\"admin:Tr0ub4dor-x9Q\")", "\"Basic \" + base64.b64encode(b\"admin:[REDACTED:basic-auth-literal]\")")]
	public void A_basic_credential_encoded_from_a_literal_goes(string text, string expected)
	{
		Assert.Equal(expected, Redact(text));
	}

	[Theory]
	[InlineData("headers.Authorization = `Basic ${btoa(`${user}:${pass}`)}`;")]
	[InlineData("headers.Authorization = `Basic ${btoa(\"user:\")}`;")]
	[InlineData("headers.Authorization = `Basic ${btoa(\"admin:abc\")}`;")]
	[InlineData("headers.Authorization = `Basic ${btoa(\"admin:password\")}`;")]
	[InlineData("headers.Authorization = `Basic ${btoa(\"name\")}`;")]
	[InlineData("headers.Authorization = `Basic ${btoa(\"admin:<password>\")}`;")]
	[InlineData("`Basic ${btoa(\"Content-Type: text/plain\")}`")]
	public void A_basic_credential_from_code_or_a_sample_stays(string text)
	{
		Assert.Equal(text, Redact(text));
	}

	/// <summary>Each would reach code if fixed: upper-case names, a short remainder and a version range read as code beside a span.</summary>
	[Theory]
	[InlineData("password=${X}PROD_ADMIN_PASSWORD_X")]
	[InlineData("password=${PREFIX}abc!")]
	[InlineData("password=^2024.11")]
	public void Shapes_left_to_keep_code_readable_stay(string text)
	{
		Assert.Equal(text, Redact(text));
	}

	/// <summary>Each password flag reads back over a bounded number of words, so a flood of them stays linear with a small constant.</summary>
	[Fact]
	[Trait("Category", RedactorSafetyTests.TimingCategory)]
	public void A_flood_of_password_flags_is_read_quickly()
	{
		// About 4 times its cost, 0.80 s of this thread's CPU on a plain Debug run.
		TimeSpan budget = TimeSpan.FromSeconds(3.2);
		string text = "ran " + string.Concat(Enumerable.Repeat("mongo -p ", 40_000));
		TimeSpan started = ThreadCpuTime.Now();

		string redacted = Redact(text);

		TimeSpan spent = ThreadCpuTime.Now() - started;
		Assert.True(spent < budget, $"40,000 password flags took {spent.TotalSeconds:F2} s of CPU, over the budget of {budget.TotalSeconds:F1} s.");
		Assert.Equal(text, redacted);
	}

	[Theory]
	[InlineData("$(printf 'x')", true)]
	[InlineData("$(echo x)", true)]
	[InlineData("$(echo $X)", false)]
	[InlineData("$(cat 'secret-file')", false)]
	[InlineData("$(printf)", false)]
	[InlineData("printf 'x'", false)]
	public void A_substitution_that_prints_a_literal_is_known(string value, bool expected)
	{
		Assert.Equal(expected, CodeValues.PrintsLiteral(value.Replace("x", "Tr0ub4dor-x9Q", StringComparison.Ordinal)));
	}

	[Theory]
	[InlineData("${prefix + \"a8F3kQ9z\"}", "a8F3kQ9z")]
	[InlineData("${\"a\" + b + \"c\"}", "a c")]
	[InlineData("${f(\"a8F3kQ9z\")}", null)]
	[InlineData("${prefix + \"a8F3kQ9z\"} tail", null)]
	[InlineData("${x}", null)]
	[InlineData("abc", null)]
	public void A_template_s_joined_literals_are_found(string value, string? expected)
	{
		Assert.Equal(expected, CodeValues.JoinedLiteralsOf(value) is { } found ? string.Join(' ', found) : null);
	}
}
