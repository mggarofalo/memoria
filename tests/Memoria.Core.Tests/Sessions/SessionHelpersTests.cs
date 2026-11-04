using Memoria.Core.Sessions;

namespace Memoria.Core.Tests.Sessions;

public sealed class SessionTimestampTests
{
	[Theory]
	[InlineData("2026-10-01T10:00:01Z", "2026-10-01T10:00:01+00:00", "2026-10-01")]
	[InlineData("2026-10-01T10:00:01.000Z", "2026-10-01T10:00:01+00:00", "2026-10-01")]
	[InlineData("2026-10-01T10:00:01.123Z", "2026-10-01T10:00:01.123000+00:00", "2026-10-01")]
	[InlineData("2026-10-01T10:00:01.1234567Z", "2026-10-01T10:00:01.123456+00:00", "2026-10-01")]
	[InlineData("2026-10-01T10:00:01,5Z", "2026-10-01T10:00:01.500000+00:00", "2026-10-01")]
	[InlineData("2026-10-01T23:30:00-05:00", "2026-10-01T23:30:00-05:00", "2026-10-01")]
	[InlineData("2026-10-01T23:30:00+0530", "2026-10-01T23:30:00+05:30", "2026-10-01")]
	[InlineData("2026-10-01T23:30:00+05", "2026-10-01T23:30:00+05:00", "2026-10-01")]
	[InlineData("2026-10-01T23:30:00+05:30:15", "2026-10-01T23:30:00+05:30:15", "2026-10-01")]
	[InlineData("2026-10-01T23:30:00-00:00", "2026-10-01T23:30:00+00:00", "2026-10-01")]
	[InlineData("2026-10-01T23:30:00+23:59", "2026-10-01T23:30:00+23:59", "2026-10-01")]
	[InlineData("2026-10-01T10:00:01", "2026-10-01T10:00:01", "2026-10-01")]
	[InlineData("2026-10-01 10:00", "2026-10-01T10:00:00", "2026-10-01")]
	[InlineData("2026-10-01T10", "2026-10-01T10:00:00", "2026-10-01")]
	[InlineData("2026-10-01", "2026-10-01T00:00:00", "2026-10-01")]
	[InlineData("2024-02-29T00:00:00Z", "2024-02-29T00:00:00+00:00", "2024-02-29")]
	public void Parses_and_prints_like_python(string input, string iso, string date)
	{
		SessionTimestamp timestamp = SessionTimestamp.TryParse(input)!.Value;

		Assert.Equal(iso, timestamp.ToIsoString());
		Assert.Equal(date, timestamp.DateText);
	}

	[Theory]
	[InlineData("")]
	[InlineData("yesterday")]
	[InlineData("2026-13-01T00:00:00Z")]
	[InlineData("2026-02-30T00:00:00Z")]
	[InlineData("2025-02-29T00:00:00Z")]
	[InlineData("0000-01-01T00:00:00Z")]
	[InlineData("2026-10-01T24:00:00Z")]
	[InlineData("2026-10-01T10:60:00Z")]
	[InlineData("2026-10-01T10:00:60Z")]
	[InlineData("2026-10-01T10:00:00+24:00")]
	[InlineData("2026-10-01T10:00:00+05:60")]
	[InlineData("2026-10-01T10:00:00z")]
	[InlineData("2026-10-01Z")]
	[InlineData("2026-10-01T10:00:00Z\n")]
	[InlineData("\u0662\u0660\u0662\u0666-10-01")]
	public void Refuses_what_is_not_a_timestamp(string input)
	{
		Assert.Null(SessionTimestamp.TryParse(input));
	}

	[Fact]
	public void A_very_long_value_is_refused_without_being_parsed()
	{
		Assert.Null(SessionTimestamp.TryParse("2026-10-01T10:00:00." + new string('1', 45) + "Z"));
		Assert.NotNull(SessionTimestamp.TryParse("2026-10-01T10:00:00." + new string('1', 43) + "Z"));
	}

	[Fact]
	public void Instants_compare_across_offsets_and_a_missing_offset_counts_as_utc()
	{
		long utc = SessionTimestamp.TryParse("2026-10-01T12:00:00Z")!.Value.Instant;

		Assert.Equal(utc, SessionTimestamp.TryParse("2026-10-01T07:00:00-05:00")!.Value.Instant);
		Assert.Equal(utc, SessionTimestamp.TryParse("2026-10-01T17:30:00+05:30")!.Value.Instant);
		Assert.Equal(utc, SessionTimestamp.TryParse("2026-10-01T12:00:00")!.Value.Instant);
	}

	[Fact]
	public void The_fallback_defaults_to_the_epoch_and_keeps_an_offset()
	{
		Assert.Equal("1970-01-01T00:00:00+00:00", SessionTimestamp.FromFallback(null).ToIsoString());
		Assert.Equal("2026-01-02T03:04:05.000006-05:00", SessionTimestamp.FromFallback(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5)).AddTicks(60)).ToIsoString());
	}
}

public sealed class PythonTextTests
{
	[Theory]
	[InlineData("plain", "plain")]
	[InlineData("  both  ", "both")]
	[InlineData("\u001C\u001F x \u3000\u00A0\u2003", "x")]
	[InlineData("inner  space", "inner  space")]
	[InlineData("   ", "")]
	[InlineData("", "")]
	[InlineData("\u200Bzero-width\u200B", "\u200Bzero-width\u200B")]
	public void Strip_matches_python(string input, string expected)
	{
		Assert.Equal(expected, PythonText.Strip(input));
	}

	[Theory]
	[InlineData("", 0)]
	[InlineData("abc", 3)]
	[InlineData("\U0001F680", 1)]
	[InlineData("a\U0001F680b\U0001F680", 4)]
	public void Code_points_count_a_pair_as_one(string input, int expected)
	{
		Assert.Equal(expected, PythonText.CodePointLength(input));
	}

	[Theory]
	[InlineData("abcdef", 3, "abc")]
	[InlineData("ab", 3, "ab")]
	[InlineData("a\U0001F680b", 2, "a\U0001F680")]
	[InlineData("\U0001F680\U0001F680", 1, "\U0001F680")]
	[InlineData("abc", 0, "")]
	public void Prefix_never_cuts_a_pair(string input, int count, string expected)
	{
		Assert.Equal(expected, PythonText.Prefix(input, count));
	}

	[Fact]
	public void An_unpaired_surrogate_counts_as_one_code_point()
	{
		Assert.Equal(1, PythonText.CodePointLength("\uD83D"));
		Assert.Equal(3, PythonText.CodePointLength("\uD83Dx\uDE00"));
		Assert.Equal("ab\uD83D", PythonText.Prefix("ab\uD83D", 3));
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r")]
	[InlineData("\u000B")]
	[InlineData("\f")]
	[InlineData("\u001C")]
	[InlineData("\u001D")]
	[InlineData("\u001E")]
	[InlineData("\u0085")]
	[InlineData("\u2028")]
	[InlineData("\u2029")]
	public void Splitlines_breaks_on_every_python_line_boundary(string separator)
	{
		Assert.Equal("first", PythonText.FirstSplitLine($"first{separator}second"));
	}

	[Theory]
	[InlineData("\u001F")]
	[InlineData("\t")]
	[InlineData(" ")]
	public void Splitlines_does_not_break_on_other_whitespace(string separator)
	{
		Assert.Equal($"first{separator}second", PythonText.FirstSplitLine($"first{separator}second"));
	}

	[Theory]
	[InlineData("a", "b", -1)]
	[InlineData("b", "a", 1)]
	[InlineData("a", "a", 0)]
	[InlineData("a", "ab", -1)]
	[InlineData("\uFFFD", "\U0001F600", -1)]
	[InlineData("\U0001F600", "\uFFFD", 1)]
	[InlineData("\uE000", "\U00010000", -1)]
	[InlineData("\uD7FF", "\uE000", -1)]
	[InlineData("z", "\uD7FF", -1)]
	public void Comparison_orders_by_code_point(string left, string right, int sign)
	{
		Assert.Equal(sign, Math.Sign(PythonText.CompareCodePoints(left, right)));
	}
}

public sealed class SessionProjectTests
{
	[Theory]
	[InlineData("/Users/me/Source/demo", "demo")]
	[InlineData("/Users/me/source/demo/sub/dir", "demo")]
	[InlineData("/home/me/Source/demo", "demo")]
	[InlineData("/root/Source/demo", "demo")]
	[InlineData("C:\\Users\\me\\Source\\memoria\\.claude\\worktrees\\x", "memoria")]
	[InlineData("c:/users/ME/SOURCE/demo", "demo")]
	[InlineData("/Users/me", "Home")]
	[InlineData("/Users/me/", "Home")]
	[InlineData("/root", "Home")]
	[InlineData("C:\\Users\\me", "Home")]
	[InlineData("/Users/me/Source", "Home")]
	[InlineData("/Users/me/source", "Home")]
	[InlineData("/Users/me/Documents/notes", "notes")]
	[InlineData("/Users/me/SOURCE/demo", "demo")]
	[InlineData("/srv/work/thing", "thing")]
	[InlineData("/Users", "Users")]
	[InlineData("/tmp/./demo/.", "demo")]
	[InlineData("/", "Home")]
	[InlineData("C:\\", "Home")]
	[InlineData("relative", "relative")]
	[InlineData("/Users/me/Source/../..", "..")]
	public void Claude_project_from_cwd(string cwd, string expected)
	{
		Assert.Equal(expected, SessionProject.FromClaudeCwd(cwd, null));
	}

	[Theory]
	[InlineData("/Users/me/Source/demo/sub", "demo")]
	[InlineData("/Users/me/Source", "Home")]
	[InlineData("/Users/me", "me")]
	[InlineData("/Users/me/source/demo", "demo")]
	[InlineData("/srv/thing", "thing")]
	[InlineData("", "Home")]
	public void Codex_project_from_cwd_treats_only_source_as_special(string cwd, string expected)
	{
		Assert.Equal(expected, SessionProject.FromCodexCwd(cwd, null));
	}

	[Theory]
	[InlineData("/volume1/me/Source/demo/sub", "/volume1/me", "demo")]
	[InlineData("/volume1/me", "/volume1/me", "Home")]
	[InlineData("/volume1/other/Source/demo", "/volume1/me", "demo")]
	[InlineData("/vol", "/volume1/me", "vol")]
	[InlineData("/Users/me/Source/demo/sub", "/volume1/me", "sub")]
	public void An_explicit_home_replaces_the_inference(string cwd, string home, string expected)
	{
		Assert.Equal(expected, SessionProject.FromClaudeCwd(cwd, home));
	}

	[Theory]
	[InlineData("-Users-me-Source-agent-skills", "agent-skills")]
	[InlineData("C--Users-me-Source-Receipts--claude-worktrees-foo", "Receipts")]
	[InlineData("-home-me-source-demo", "demo")]
	[InlineData("-Users-me", "Home")]
	[InlineData("C--Users-me", "Home")]
	[InlineData("-Users-me-Source", "Home")]
	[InlineData("-Users-me-Source-", "Home")]
	[InlineData("-Users-me-other", "-Users-me-other")]
	[InlineData("-tmp-demo", "-tmp-demo")]
	public void Claude_project_from_an_encoded_directory(string directory, string expected)
	{
		Assert.Equal(expected, SessionProject.FromClaudeDirectory(directory, null));
	}

	[Theory]
	[InlineData("-vol-me", "Home")]
	[InlineData("-vol-me-Source", "Home")]
	[InlineData("-vol-me-source", "Home")]
	[InlineData("-vol-me-Source-", "Home")]
	[InlineData("-vol-me-source-demo", "demo")]
	[InlineData("-vol-me-other", "-vol-me-other")]
	public void An_encoded_directory_with_an_explicit_home(string directory, string expected)
	{
		Assert.Equal(expected, SessionProject.FromClaudeDirectory(directory, "/vol/me"));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void No_directory_is_an_unknown_project(string? directory)
	{
		Assert.Equal("unknown", SessionProject.FromClaudeDirectory(directory, null));
	}
}
