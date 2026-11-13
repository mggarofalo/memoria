using Memoria.Core.Sessions;
using static Memoria.Core.Tests.Sessions.SessionTestKit;

namespace Memoria.Core.Tests.Sessions;

/// <summary>A session resumed from another directory exists in two project folders under one id, and neither copy may overwrite the other.</summary>
public sealed class SessionRelocatedCopyTests
{
	private const string OriginalFolder = "-Users-me-Source-demo";
	private const string OtherFolder = "-Users-me-Source-other";
	private const string Cwd = "/Users/me/Source/demo";

	[Fact]
	public async Task Two_copies_of_one_session_get_different_paths()
	{
		RedactedSession original = await RenderIn(OriginalFolder, ClaudeLine("user", "first"));
		RedactedSession copy = await RenderIn(OtherFolder, ClaudeLine("user", "first") + ClaudeLine("assistant", "resumed elsewhere"));

		Assert.NotEqual(original.Documents[0].Path, copy.Documents[0].Path);
		Assert.Null(SidecarRefusal(copy.Documents[0].Path));
	}

	[Fact]
	public async Task The_original_keeps_the_exporters_path()
	{
		RedactedSession original = await RenderIn(OriginalFolder, ClaudeLine("user", "first"));

		Assert.Equal("2026-10-01_abcdef12.md", original.Documents[0].Path);
	}

	[Fact]
	public async Task A_copys_path_is_exact_and_depends_only_on_its_folder()
	{
		RedactedSession copy = await RenderIn(OtherFolder, ClaudeLine("user", "first"));
		RedactedSession again = await RenderIn(OtherFolder, ClaudeLine("user", "first") + ClaudeLine("assistant", "more"));

		string digest = SessionPaths.Digest(OtherFolder);
		Assert.Equal($"2026-10-01_abcdef12-{digest}.md", copy.Documents[0].Path);
		Assert.Equal(copy.Documents[0].Path, again.Documents[0].Path);
		Assert.DoesNotContain("other", copy.Documents[0].Path, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Copies_in_two_other_folders_do_not_collide_with_each_other()
	{
		RedactedSession first = await RenderIn(OtherFolder, ClaudeLine("user", "x"));
		RedactedSession second = await RenderIn("-Users-me-Source-third", ClaudeLine("user", "x"));

		Assert.NotEqual(first.Documents[0].Path, second.Documents[0].Path);
	}

	[Fact]
	public async Task Re_ingesting_one_copy_never_deletes_the_other()
	{
		RedactedSession original = await RenderIn(OriginalFolder, ClaudeLine("user", "first"));
		RedactedSession copy = await RenderIn(OtherFolder, ClaudeLine("user", "first"));

		Assert.False(original.IsSupersededPath(copy.Documents[0].Path));
		Assert.False(copy.IsSupersededPath(original.Documents[0].Path));
	}

	[Fact]
	public async Task A_copy_still_finds_its_own_stale_parts()
	{
		RedactedSession copy = await RenderIn(OtherFolder, ClaudeLine("user", "first"));
		string stale = copy.Documents[0].Path.Replace(".md", "_part-002.md", StringComparison.Ordinal);

		Assert.True(copy.IsSupersededPath(stale));
		Assert.False(copy.IsSupersededPath(copy.Documents[0].Path));
	}

	[Theory]
	[InlineData("/Users/me/Source/demo", "-Users-me-Source-demo", "")]
	[InlineData("C:\\Users\\me\\Source\\demo", "C--Users-me-Source-demo", "")]
	[InlineData("c:\\users\\me", "C--Users-me", "")]
	[InlineData("/srv/a_b.c", "-srv-a-b-c", "")]
	[InlineData("", "-srv", "")]
	[InlineData("/srv", null, "")]
	[InlineData("/srv", "", "")]
	public void Matching_or_unknown_folder_adds_nothing(string cwd, string? folder, string expected)
	{
		Assert.Equal(expected, SessionPaths.RelocatedCopySuffix(cwd, folder));
	}

	[Fact]
	public void Mismatched_folder_adds_a_digest_of_the_folder()
	{
		Assert.Equal("-" + SessionPaths.Digest("-other"), SessionPaths.RelocatedCopySuffix("/srv", "-other"));
		Assert.Equal(9, SessionPaths.RelocatedCopySuffix("/srv", "-other").Length);
	}

	[Fact]
	public async Task The_first_cwd_decides_when_a_session_moves_around()
	{
		string moved = ClaudeLine("user", "first", cwd: Cwd) + ClaudeLine("assistant", "second", cwd: "/Users/me/Source/other");

		RedactedSession session = await RenderIn(OriginalFolder, moved);

		Assert.Equal("2026-10-01_abcdef12.md", session.Documents[0].Path);
	}

	[Fact]
	public async Task A_session_with_no_cwd_keeps_its_path_because_nothing_can_tell_it_apart()
	{
		RedactedSession session = await RenderIn(OtherFolder, ClaudeLine("user", "first", cwd: null));

		Assert.Equal("2026-10-01_abcdef12.md", session.Documents[0].Path);
	}

	[Fact]
	public async Task A_credential_in_the_cwd_does_not_reach_the_path_or_change_the_comparison_output()
	{
		string token = Memoria.Core.Tests.Redaction.Synthetic.GitHubClassic('p');

		RedactedSession session = await RenderIn(OtherFolder, ClaudeLine("user", "first", cwd: "/srv/" + token));

		Assert.DoesNotContain(token, session.Documents[0].Path, StringComparison.Ordinal);
		Assert.DoesNotContain(token, session.Documents[0].Text(), StringComparison.Ordinal);
	}

	private static Task<RedactedSession> RenderIn(string folder, string jsonl)
	{
		return Render(jsonl, new SessionSource(SessionFormat.ClaudeCode, ClaudeId) { ProjectDirectory = folder });
	}
}
