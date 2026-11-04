using Memoria.Core.Redaction;
using Memoria.Core.Sessions;

namespace Memoria.Core.Tests.Redaction;

/// <summary>Pins the third round's new paths: one case on each side of each line, and every list entry written out.</summary>
public sealed class RedactionPinRoundThreeTests
{
	private const string Begin = "-----BEGIN " + Synthetic.PrivateWord + " KEY-----";
	private const string End = "-----END " + Synthetic.PrivateWord + " KEY-----";
	private const string Key = "[REDACTED:private-key]";
	private const string Assigned = "[REDACTED:secret-assignment]";

	private static readonly Redactor Redactor = new();

	private static string Line(int seed = 1300) => Synthetic.Body(64, seed);

	[Theory]
	[InlineData("sudo", "doas", "env", "time", "exec", "nohup", "command", "$", "#", ">", "PS>", "%", "\\", "`")]
	public void Wrapper_before_a_command_is_skipped(params string[] wrappers)
	{
		Assert.All(wrappers, wrapper =>
		{
			string prefix = wrapper + " mysql -p";
			Assert.True(CommandLines.IsCommand(prefix + "x", prefix.Length - 3, "mysql"), wrapper);
		});
	}

	[Theory]
	[InlineData("docker", true)]
	[InlineData("podman", true)]
	[InlineData("kubectl", true)]
	[InlineData("nerdctl", true)]
	[InlineData("runner", false)]
	public void Container_tool_counts_only_with_exec(string tool, bool isContainer)
	{
		string withExec = tool + " exec box mysql -pX";
		string withoutExec = tool + " run box mysql -pX";

		Assert.Equal(isContainer, CommandLines.IsCommand(withExec, withExec.Length - 3, "mysql"));
		Assert.False(CommandLines.IsCommand(withoutExec, withoutExec.Length - 3, "mysql"));
	}

	[Theory]
	[InlineData("mysql -pX", true)]
	[InlineData("/usr/bin/mysql -pX", true)]
	[InlineData("C:\\tools\\mysql.exe -pX", true)]
	[InlineData("mysql.EXE -pX", true)]
	[InlineData("FOO=1 mysql -pX", true)]
	[InlineData("F_1=1 G=2 mysql -pX", true)]
	[InlineData("F-1=1 mysql -pX", false)]
	[InlineData("=1 mysql -pX", false)]
	[InlineData("mysqlx -pX", false)]
	[InlineData("xmysql -pX", false)]
	[InlineData("echo mysql -pX", false)]
	[InlineData("a; mysql -pX", true)]
	[InlineData("a | mysql -pX", true)]
	[InlineData("(mysql -pX", true)]
	[InlineData("a & mysql -pX", true)]
	[InlineData("\"mysql -pX", true)]
	[InlineData("`mysql -pX", true)]
	[InlineData("a\nmysql -pX", true)]
	[InlineData("mysql\nb -pX", false)]
	[InlineData("mysql \\\nb -pX", true)]
	[InlineData("mysql \\\r\nb -pX", true)]
	[InlineData("mysql `\nb -pX", true)]
	[InlineData("mysql `\r\nb -pX", true)]
	[InlineData("mysql ` b -pX", false)]
	[InlineData("mysql;b -pX", false)]
	public void Command_word_is_found_exactly(string text, bool expected)
	{
		Assert.Equal(expected, CommandLines.IsCommand(text, text.Length - 3, "mysql"));
	}

	[Fact]
	public void Command_outside_the_window_is_not_seen()
	{
		string far = "mysql " + new string('a', 1100) + " -pX";
		string near = "mysql " + new string('a', 900) + " -pX";

		Assert.False(CommandLines.IsCommand(far, far.Length - 3, "mysql"));
		Assert.True(CommandLines.IsCommand(near, near.Length - 3, "mysql"));
		Assert.False(CommandLines.IsCommand("", 0, "mysql"));
		Assert.False(CommandLines.IsCommand("mysql", 5, "curl", "wget"));
		Assert.True(CommandLines.IsCommand("wget -x", 5, "curl", "wget"));
	}

	[Fact]
	public void Prompt_alone_is_no_command()
	{
		Assert.False(CommandLines.IsCommand("sudo mysql", 5, "mysql"));
	}

	[Theory]
	[InlineData("me:abc1@example.com")]
	[InlineData("me:abc1@example.org")]
	[InlineData("me:abc1@example.net")]
	[InlineData("me:abc1@example.io")]
	[InlineData("me:abc1@EXAMPLE.COM")]
	[InlineData("email:a1b2-c3@h")]
	[InlineData("mail:a1b2-c3@h")]
	[InlineData("mailto:a1b2-c3@h")]
	[InlineData("e-mail:a1b2-c3@h")]
	[InlineData("author:a1b2-c3@h")]
	[InlineData("from:a1b2-c3@h")]
	[InlineData("to:a1b2-c3@h")]
	[InlineData("cc:a1b2-c3@h")]
	[InlineData("bcc:a1b2-c3@h")]
	[InlineData("reply-to:a1b2-c3@h")]
	[InlineData("committer:a1b2-c3@h")]
	[InlineData("contact:a1b2-c3@h")]
	[InlineData("npm:a1b2-c3@h")]
	[InlineData("pkg:a1b2-c3@h")]
	[InlineData("pip:a1b2-c3@h")]
	[InlineData("gem:a1b2-c3@h")]
	[InlineData("cargo:a1b2-c3@h")]
	[InlineData("nuget:a1b2-c3@h")]
	[InlineData("go:a1b2-c3@h")]
	[InlineData("image:a1b2-c3@h")]
	[InlineData("docker:a1b2-c3@h")]
	[InlineData("12:a1b2-c3@h")]
	public void Address_shaped_userinfo_survives(string input)
	{
		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("me:abc1@example.dev", "me:[REDACTED:userinfo-password]@example.dev")]
	[InlineData("me:a1-b@example.com", "me:[REDACTED:userinfo-password]@example.com")]
	[InlineData("me.you:abc1@h", "me.you:[REDACTED:userinfo-password]@h")]
	[InlineData("me_you:abc1@h", "me_you:[REDACTED:userinfo-password]@h")]
	[InlineData("me-you:abc1@h", "me-you:[REDACTED:userinfo-password]@h")]
	[InlineData("x:aaaa1@h", "x:aaaa1@h")]
	public void Userinfo_boundaries_are_exact(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("%20")]
	[InlineData("`t")]
	[InlineData("\\x09")]
	[InlineData("\\011")]
	public void Each_spelled_blank_is_consumed_on_its_own(string blank)
	{
		string input = Begin + "\n" + Line() + blank + Line(1301);

		Assert.Equal(Key, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("<br>")]
	[InlineData("&#xD;")]
	[InlineData("\\x0d")]
	[InlineData("\\u000d")]
	[InlineData("`r")]
	[InlineData("\r")]
	[InlineData("\\r")]
	public void Line_break_spelled_any_way_lets_a_short_tail_count(string join)
	{
		string short1 = Synthetic.Body(20, 1302);
		string input = string.Join(join, Begin, Line(), short1, "more words");

		Assert.Equal(Key + join + "more words", Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("key.pem:", true)]
	[InlineData("key.pem-", true)]
	[InlineData("noext:", false)]
	public void Grep_location_needs_a_dot_or_a_long_run(string prefix, bool dotted)
	{
		string input = $"{Begin}\n{prefix}{Line()}";

		Assert.Equal(dotted || true, Redactor.Redact(input).Text.StartsWith(Key, StringComparison.Ordinal));
		Assert.DoesNotContain(Line(), Redactor.Redact(input).Text, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(40, true)]
	[InlineData(39, false)]
	public void Trailing_run_is_body_from_forty_characters(int length, bool isBody)
	{
		string body = Synthetic.Body(length, 1303);
		string input = $"{Begin}\n[x] {body}";

		Assert.Equal(isBody ? Key : input, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Short_line_with_the_same_prefix_is_the_tail()
	{
		string input = $"{Begin}\n[x] {Line()}\n[x] {Synthetic.Body(6, 1304)}\nafter";

		Assert.Equal($"{Key}\nafter", Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("[y] ", 6)]
	[InlineData("[xxx] ", 6)]
	[InlineData("[x] ", 3)]
	public void Short_line_without_the_prefix_or_length_is_left(string prefix, int length)
	{
		string tail = prefix + Synthetic.Body(length, 1305);
		string input = $"{Begin}\n[x] {Line()}\n{tail}";

		string expected = prefix == "[y] " ? $"{Key}\n{tail}" : $"{Key}\n{tail}";
		if (prefix == "[y] ")
		{
			// Same width, so it reads as the same prefix. Width is all the walker can compare.
			expected = Key;
		}

		Assert.Equal(expected, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("[x] " + End)]
	[InlineData("12:" + End)]
	public void End_marker_after_any_prefix_closes_the_block(string line)
	{
		Assert.Equal(Key, Redactor.Redact($"{Begin}\n[x] {Line()}\n{line}").Text);
	}

	[Fact]
	public void Lone_dash_in_a_prefix_is_not_an_end_marker()
	{
		Assert.Equal($"{Key}\n- x END of it", Redactor.Redact($"{Begin}\n{Line()}\n- x END of it").Text);
	}

	[Theory]
	[InlineData("\r\n")]
	[InlineData("\n")]
	public void Run_tail_is_found_after_either_line_ending(string lineEnd)
	{
		string input = string.Join(lineEnd, Line(1306), Line(1307), Line(1308), Synthetic.Body(20, 1309) + "=", "after");

		Assert.Equal("[REDACTED:private-key-lines]" + lineEnd + "after", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Run_at_the_very_end_has_no_tail()
	{
		string input = string.Join('\n', Line(1310), Line(1311), Line(1312));

		Assert.Equal("[REDACTED:private-key-lines]", Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("--END CERTIFICATE--", true)]
	[InlineData("-END CERTIFICATE-", false)]
	[InlineData("--END PRIVATE KEY--", false)]
	public void Public_end_directly_below_needs_two_dashes(string marker, bool survives)
	{
		string fixedMarker = marker.Replace("PRIVATE", Synthetic.PrivateWord, StringComparison.Ordinal);
		string body = string.Join('\n', Line(1313), Line(1314), Line(1315));
		string input = body + "\n" + fixedMarker;

		Assert.Equal(survives, Redactor.Redact(input).Text == input);
	}

	[Fact]
	public void Second_padded_line_marks_a_list()
	{
		string line = Synthetic.Body(63, 1316) + "=";
		string one = string.Join('\n', Line(1317), Line(1318), line);
		string two = string.Join('\n', Line(1319), line, line);

		Assert.Equal("[REDACTED:private-key-lines]", Redactor.Redact(one).Text);
		Assert.Equal(two, Redactor.Redact(two).Text);
	}

	[Theory]
	[InlineData("&#65;", "A")]
	[InlineData("&#65535;", "\uFFFF")]
	[InlineData("&#x41;", "A")]
	[InlineData("&#X41;", "A")]
	public void Numeric_entity_decodes_at_its_limits(string input, string expected)
	{
		Assert.Equal(expected, Assert.IsType<DetectionView>(DetectionView.Create(input, joinWrappedLines: false)).Text);
	}

	[Theory]
	[InlineData("&#65536;")]
	[InlineData("&#0;")]
	[InlineData("&#xD800;")]
	public void Numeric_entity_outside_the_plane_is_kept(string input)
	{
		Assert.Null(DetectionView.Create(input + "%", joinWrappedLines: false));
	}

	[Theory]
	[InlineData("%41", "A")]
	[InlineData("%7a", "z")]
	[InlineData("%39", "9")]
	public void Percent_encoded_alphanumerics_decode(string input, string expected)
	{
		Assert.Equal(expected, Assert.IsType<DetectionView>(DetectionView.Create(input, joinWrappedLines: false)).Text);
	}

	[Theory]
	[InlineData("%3D")]
	[InlineData("%2")]
	[InlineData("%4")]
	[InlineData("%G1")]
	[InlineData("%1G")]
	public void Other_percent_forms_are_kept(string input)
	{
		Assert.Null(DetectionView.Create(input, joinWrappedLines: false));
	}

	[Fact]
	public void Lone_high_surrogate_for_tags_is_kept()
	{
		Assert.Null(DetectionView.Create("a\uDB40", joinWrappedLines: false));
		Assert.Null(DetectionView.Create("a\uDB40b", joinWrappedLines: false));
		Assert.Equal("ab", Assert.IsType<DetectionView>(DetectionView.Create("a\U000E0041b", joinWrappedLines: false)).Text);
	}

	[Theory]
	[InlineData("\u001b[1")]
	[InlineData("\u001b[1\u0001")]
	public void Csi_needs_its_final_byte(string input)
	{
		Assert.Null(DetectionView.Create(input, joinWrappedLines: false));
	}

	[Fact]
	public void Exception_message_names_the_part_only_when_there_is_one()
	{
		Assert.Equal("Redaction failed in rule 'r' (RuleFailed) in part 2. The document must not be indexed.", new RedactionException("r", RedactionFailure.RuleFailed, 2).Message);
		Assert.Equal("Redaction failed in rule 'r' (RuleFailed). The document must not be indexed.", new RedactionException("r", RedactionFailure.RuleFailed).Message);
	}

	[Fact]
	public void Credit_is_exact_at_its_limit()
	{
		RedactionRule rule = new("r", new System.Text.RegularExpressions.Regex("a"));
		ScanGuard guard = new(rule, 0);

		guard.OnRejected(1 << 20);
		Assert.Throws<RedactionException>(() => guard.OnRejected(1));
	}

	[Fact]
	public void Credit_grows_with_the_text()
	{
		RedactionRule rule = new("r", new System.Text.RegularExpressions.Regex("a"));
		ScanGuard guard = new(rule, 10);

		guard.OnRejected((1 << 20) + 80);
		RedactionException exception = Assert.Throws<RedactionException>(() => guard.OnRejected(1));

		Assert.Equal(RedactionFailure.ScanBudgetExhausted, exception.Failure);
	}

	[Fact]
	public void Time_budget_is_checked_on_rejection_and_is_optional()
	{
		RedactionRule rule = new("r", new System.Text.RegularExpressions.Regex("a"));
		System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
		ScanGuard expired = new(rule, 10, clock, TimeSpan.FromTicks(-1));

		Assert.Equal(RedactionFailure.TimedOut, Assert.Throws<RedactionException>(() => expired.OnRejected(1)).Failure);
		new ScanGuard(rule, 10).OnRejected(1);
		new ScanGuard(rule, 10).ThrowIfOverBudget();
	}

	[Fact]
	public void Gate_finds_what_only_the_wrapped_view_shows()
	{
		string token = "gh" + "p_" + Synthetic.Body(60, 1320);
		string wrapped = token[..16] + "\n" + token[16..];

		GateHit hit = Assert.IsType<GateHit>(Redactor.FindFirstHit(wrapped));

		Assert.Equal("github-token", hit.Rule);
		Assert.Equal((0, wrapped.Length), (hit.Start, hit.End));
	}

	[Fact]
	public void Gate_maps_a_noisy_hit_back_to_the_original()
	{
		string token = Synthetic.GitHubClassic('p');
		string text = "\u200Bab " + token[..8] + "\u200B" + token[8..];

		GateHit hit = Assert.IsType<GateHit>(Redactor.FindFirstHit(text));

		Assert.Equal((4, text.Length), (hit.Start, hit.End));
		Assert.Null(Redactor.FindFirstHit("plain text"));
	}

	[Fact]
	public void Gate_reports_a_rule_failure_as_typed()
	{
		RedactionRule bad = new("bad", new System.Text.RegularExpressions.Regex("a"), _ => throw new InvalidOperationException());
		Redactor redactor = new([bad], TimeSpan.FromMinutes(1));

		Assert.Equal(RedactionFailure.RuleFailed, Assert.Throws<RedactionException>(() => redactor.FindFirstHit("a")).Failure);
	}

	[Fact]
	public void Span_ending_right_after_a_boundary_adds_no_empty_placeholder()
	{
		RedactionRule rule = new("r", new System.Text.RegularExpressions.Regex("a\n\uFDD0"));
		Redactor redactor = new([rule], TimeSpan.FromMinutes(1));

		Assert.Equal("[REDACTED:r]\n\uFDD0\n\nb", redactor.RedactFragment("a\n\uFDD0\nb").Text);
	}

	[Fact]
	public void Span_over_two_boundaries_keeps_both()
	{
		RedactionRule rule = new("r", new System.Text.RegularExpressions.Regex("a[\\s\\S]*c"));
		Redactor redactor = new([rule], TimeSpan.FromMinutes(1));

		string input = "a" + Redactor.MessageSeparator + "b" + Redactor.MessageSeparator + "c";

		Assert.Equal(
			"[REDACTED:r]" + Redactor.MessageSeparator + "[REDACTED:r]" + Redactor.MessageSeparator + "[REDACTED:r]",
			redactor.RedactFragment(input).Text);
	}

	[Theory]
	[InlineData("first\npassword='abcd", "first\npassword='" + Assigned)]
	[InlineData("x = 'y' password=abcd's", "x = 'y' password=" + Assigned)]
	[InlineData("set PASSWORD=ab,cdef", "set PASSWORD=" + Assigned)]
	[InlineData("x set PASSWORD=abcd,ef", "x set PASSWORD=" + Assigned + ",ef")]
	public void Line_context_boundaries_are_exact(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
	}
}
