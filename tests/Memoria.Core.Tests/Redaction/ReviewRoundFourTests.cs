using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>The spot-check's last findings. Every value is synthetic and built at runtime.</summary>
public sealed class ReviewRoundFourTests
{
	private const string UserInfo = "[REDACTED:userinfo-password]";

	private static readonly Redactor Redactor = new();

	private static readonly string Begin = "-----BEGIN " + Synthetic.PrivateWord + " KEY-----";

	private static readonly string End = "-----END " + Synthetic.PrivateWord + " KEY-----";

	[Theory]
	[InlineData("root:{P}@10.0.0.5:5432", "Pa55word!")]
	[InlineData("root:{P}@10.0.0.5", "Summer2024")]
	[InlineData("root:{P}@db.company.com:3306", "Summer2024")]
	[InlineData("DATABASE=root:{P}@db.company.com", "Summer2024")]
	[InlineData("root:{P}@db.company.com", "Password")]
	[InlineData("deploy:{P}@1.2.3:5432", "Summer2024")]
	[InlineData("deploy:{P}@10.0.0.5", "Summer2024")]
	[InlineData("deploy:{P}@1.2", "Summer2024")]
	[InlineData("me:{P}@example.com", "abc1")]
	[InlineData("me:{P}@example.org", "abc1")]
	[InlineData("me:{P}@example.net", "abc1")]
	[InlineData("me:{P}@example.io", "abc1")]
	[InlineData("admin:{P}@h", "summer")]
	[InlineData("POSTGRES:{P}@h", "Secret")]
	public void Ordinary_database_logins_are_passwords(string shape, string password)
	{
		string[] sides = shape.Split("{P}");

		Assert.Equal(sides[0] + UserInfo + sides[1], Redactor.Redact(sides[0] + password + sides[1]).Text);
	}

	[Theory]
	[InlineData("npm:name1@1.2.3")]
	[InlineData("deploy:abc1@1.2.3")]
	[InlineData("deploy:abc1@1.2.3.")]
	[InlineData("deploy:abc1@1.2.3-beta")]
	[InlineData("deploy:abc1@10.20.30")]
	[InlineData("image:tag1@sha256")]
	[InlineData("email:me2@example.com")]
	[InlineData("from:me2@example.com")]
	[InlineData("to:me2@example.com")]
	[InlineData("author:me2@example.com")]
	[InlineData("root:password@db.company.com")]
	[InlineData("root:abc@db.company.com")]
	[InlineData("root:$PW@db.company.com")]
	[InlineData("12:30pm2@home")]
	public void Versions_mail_labels_and_documentation_stay(string input)
	{
		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("[INFO] {L} (line 2)")]
	[InlineData("{\"msg\":\"{L}\"}")]
	[InlineData("{\"msg\":\"{L}\"},")]
	[InlineData("│ {L} │")]
	[InlineData("│ {L}")]
	[InlineData("INFO: {L} (line 2)")]
	[InlineData("2026-10-01T10:00:00Z {L} (x)")]
	[InlineData("{L} (line 2)")]
	public void A_key_line_in_a_log_leaves_nothing(string shape)
	{
		string[] lines = [.. Enumerable.Range(0, 6).Select(static i => Synthetic.Body(64, 1500 + i))];
		string key = string.Join('\n', [Begin, .. lines.Select(line => shape.Replace("{L}", line, StringComparison.Ordinal)), End]);

		string output = Redactor.Redact(key).Text;

		Assert.Equal("[REDACTED:private-key]", output);
	}

	[Theory]
	[InlineData("[INFO] {L} (line 2)", "[INFO] ", " (line 2)")]
	[InlineData("{\"msg\":\"{L}\"}", "{\"msg\":\"", "\"}")]
	[InlineData("│ {L} │", "│ ", " │")]
	public void A_truncated_key_in_a_log_keeps_only_the_text_around_its_last_line(string shape, string before, string after)
	{
		string[] lines = [.. Enumerable.Range(0, 3).Select(static i => Synthetic.Body(64, 1510 + i))];
		string key = string.Join('\n', [Begin, .. lines.Select(line => shape.Replace("{L}", line, StringComparison.Ordinal))]);

		Assert.Equal("[REDACTED:private-key]" + after, Redactor.Redact(key).Text);
		Assert.StartsWith(before, shape, StringComparison.Ordinal);
	}

	[Fact]
	public void The_short_last_line_of_a_key_in_a_log_goes_with_it_once()
	{
		string[] lines = [Synthetic.Body(64, 1520), Synthetic.Body(64, 1521)];
		string tail = Synthetic.Body(11, 1522) + "Z9=";
		string key = string.Join('\n', [Begin, .. lines.Select(static line => "[INFO] " + line + " (x)"), "[INFO] " + tail + " (x)", "[INFO] Server9 started"]);

		Assert.Equal("[REDACTED:private-key] (x)\n[INFO] Server9 started", Redactor.Redact(key).Text);
	}

	[Theory]
	[InlineData("[INFO] {L} (line 2)")]
	[InlineData("{\"msg\":\"{L}\"}")]
	[InlineData("│ {L} │")]
	[InlineData("{L}")]
	public void An_unmarked_key_in_a_log_is_redacted(string shape)
	{
		string[] lines = [.. Enumerable.Range(0, 5).Select(static i => Synthetic.Body(64, 1530 + i))];
		string text = string.Join('\n', lines.Select(line => shape.Replace("{L}", line, StringComparison.Ordinal)));

		string output = Redactor.Redact(text).Text;

		Assert.Contains("[REDACTED:private-key-lines]", output, StringComparison.Ordinal);
		Assert.All(lines, line => Assert.DoesNotContain(line, output, StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("{L}  some/file name.txt")]
	[InlineData("{L} and then more words")]
	[InlineData("\"integrity\": \"{L}==\",")]
	public void Ordinary_data_with_a_column_of_base64_stays(string shape)
	{
		string text = string.Join('\n', Enumerable.Range(0, 5).Select(i => shape.Replace("{L}", Synthetic.Body(64, 1540 + i), StringComparison.Ordinal)));

		Assert.Equal(text, Redactor.Redact(text).Text);
	}

	[Theory]
	[InlineData("-----BEGIN CERTIFICATE-----")]
	[InlineData("-----BEGIN PUBLIC KEY-----")]
	public void Public_material_in_a_log_stays(string marker)
	{
		string[] lines = [.. Enumerable.Range(0, 5).Select(static i => "[INFO] " + Synthetic.Body(64, 1550 + i) + " (x)")];
		string text = string.Join('\n', [marker, .. lines, marker.Replace("BEGIN", "END", StringComparison.Ordinal)]);

		Assert.Equal(text, Redactor.Redact(text).Text);
	}

	[Theory]
	[InlineData("the data is base64,\n")]
	[InlineData("base64,\n")]
	[InlineData("base64, ")]
	[InlineData("data:image/png;base64,\n")]
	[InlineData("data:;base64,")]
	[InlineData("data:a b/c;base64,")]
	[InlineData("x;base64,")]
	[InlineData("base64,")]
	public void The_word_base64_does_not_protect_an_unmarked_key(string before)
	{
		string[] lines = [.. Enumerable.Range(0, 5).Select(static i => Synthetic.Body(64, 1560 + i))];

		string output = Redactor.Redact(before + string.Join('\n', lines)).Text;

		Assert.All(lines, line => Assert.DoesNotContain(line, output, StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("data:image/png;base64,")]
	[InlineData("src=\"data:application/pdf;base64,")]
	public void A_real_data_uri_stays(string before)
	{
		string text = before + string.Join('\n', Enumerable.Range(0, 5).Select(static i => Synthetic.Body(64, 1570 + i)));

		Assert.Equal(text, Redactor.Redact(text).Text);
	}

	[Theory]
	[InlineData("docker login -u me \\\n  -p {S} registry.test")]
	[InlineData("docker login -u me `\n  -p {S} registry.test")]
	[InlineData("docker login -u me ^\n  -p {S} registry.test")]
	[InlineData("docker login -u me ^\r\n  -p {S} registry.test")]
	[InlineData("docker \\\n  login -p {S} registry.test")]
	[InlineData("docker login -p \\\n  {S} registry.test")]
	[InlineData("sshpass \\\n  -p {S} ssh me@h")]
	[InlineData("sshpass -p \\\n  {S} ssh me@h")]
	[InlineData("redis-cli -h db \\\n  -a {S} ping")]
	[InlineData("redis-cli \\\n  -h db \\\n  -a {S} ping")]
	public void Credentials_on_a_continued_line_are_redacted(string shape)
	{
		string input = shape.Replace("{S}", "Hunter2x", StringComparison.Ordinal);
		string expected = shape.Replace("{S}", "[REDACTED:command-password]", StringComparison.Ordinal);

		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("docker login -u me\n  -p Hunter2x registry.test")]
	[InlineData("redis-cli -h db\n-a Hunter2x ping")]
	[InlineData("docker login | grep -p Hunter2x")]
	public void A_new_line_or_a_pipe_still_ends_the_command(string input)
	{
		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("^\n")]
	[InlineData("^\r\n")]
	[InlineData("\\\n")]
	[InlineData("`\n")]
	public void Curl_on_a_continued_line_is_a_curl_command(string continuation)
	{
		string shape = string.Concat("cu", "rl ", continuation, " -u me:{S} ", continuation, " https://h.test");

		Assert.Equal(
			shape.Replace("{S}", "[REDACTED:curl-user-password]", StringComparison.Ordinal),
			Redactor.Redact(shape.Replace("{S}", "Hunter2x", StringComparison.Ordinal)).Text);
	}

	[Fact]
	public void A_caret_elsewhere_does_not_continue_a_command()
	{
		string input = string.Concat("cu", "rl ^ x\n -u me:Hunter2x");

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Fact]
	public void A_caret_is_a_wrapper_at_the_start_of_a_continued_line()
	{
		string text = "^ mysql -x";

		Assert.True(CommandLines.IsCommand(text, text.Length - 3, "mysql"));
	}
}
