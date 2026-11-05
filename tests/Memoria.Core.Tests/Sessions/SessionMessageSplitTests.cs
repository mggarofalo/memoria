using System.Net;
using System.Text;
using Memoria.Core.Sessions;
using Memoria.Core.Sidecar;
using Memoria.Core.Tests.Redaction;
using static Memoria.Core.Tests.Sessions.SessionTestKit;

namespace Memoria.Core.Tests.Sessions;

/// <summary>A message larger than a document can hold, and the identity of a stored document.</summary>
public sealed class SessionMessageSplitTests
{
	private const int Limit = 4096;

	private static readonly SessionRenderOptions Small = new() { MaxPartChars = 512, MaxMessageBytes = Limit };

	private static int Bytes(RedactedSessionDocument document)
	{
		return Encoding.UTF8.GetByteCount(document.Text());
	}

	private static int BodyBytes(RedactedSessionDocument document)
	{
		return Encoding.UTF8.GetByteCount(Body(document));
	}

	/// <summary>The real limits with the widest characters and the longest title, against the sidecar's real cap.</summary>
	[Fact]
	public async Task With_the_default_limits_no_part_passes_the_sidecars_cap()
	{
		const string Emoji = "\U0001F600";
		const int Cap = 2 * 1024 * 1024;
		string giant = string.Concat(Enumerable.Repeat(Emoji, 1_200_000));

		RedactedSession session = await Claude(ClaudeLine("user", giant));

		Assert.True(session.Documents.Count >= 3);
		Assert.All(session.Documents, static document => Assert.InRange(Bytes(document), 1, Cap));
	}

	private static string Body(RedactedSessionDocument document)
	{
		string text = document.Text();
		int heading = text.IndexOf("\n## ", StringComparison.Ordinal);
		return text[heading..];
	}

	[Fact]
	public async Task A_message_over_the_limit_becomes_parts_that_each_fit_and_lose_nothing()
	{
		string line = new string('x', 63) + "\n";
		string giant = string.Concat(Enumerable.Repeat(line, 500));

		RedactedSession session = await Claude(ClaudeLine("user", giant), Small);

		Assert.True(session.Documents.Count >= 8);
		Assert.All(session.Documents, static document => Assert.InRange(BodyBytes(document), 1, Limit + 64));
		Assert.Equal(500, session.Documents.Sum(static document => document.Text().Split('\n').Count(static l => l == new string('x', 63))));
		Assert.Empty(session.Refusals);
	}

	[Fact]
	public async Task A_cut_falls_at_a_line_break_when_one_is_near()
	{
		string line = new string('x', 99) + "\n";

		RedactedSession session = await Claude(ClaudeLine("user", string.Concat(Enumerable.Repeat(line, 100))), Small);

		Assert.All(session.Documents, static document => Assert.DoesNotContain(document.Text().Split('\n'), static l => l.StartsWith('x') && l.Length != 99));
	}

	[Fact]
	public async Task A_message_with_no_line_breaks_is_cut_anyway()
	{
		RedactedSession session = await Claude(ClaudeLine("user", new string('y', 3 * Limit)), Small);

		Assert.Equal(3 * Limit, session.Documents.Sum(static document => Body(document).Count(static c => c == 'y')));
		Assert.All(session.Documents, static document => Assert.InRange(BodyBytes(document), 1, Limit + 64));
	}

	[Fact]
	public async Task A_cut_never_splits_a_surrogate_pair_and_counts_bytes_not_chars()
	{
		const string Emoji = "\U0001F600";
		string giant = string.Concat(Enumerable.Repeat(Emoji, 3000));

		RedactedSession session = await Claude(ClaudeLine("user", giant), Small);

		Assert.All(session.Documents, static document =>
		{
			Assert.InRange(BodyBytes(document), 1, Limit + 64);
			Assert.DoesNotContain('�', document.Text());
		});
		Assert.Equal(3000, session.Documents.Sum(static document => (Body(document).Length - Body(document).Replace(Emoji, "", StringComparison.Ordinal).Length) / Emoji.Length));
	}

	[Theory]
	[InlineData("é", 2)]
	[InlineData("€", 3)]
	public async Task Wider_characters_are_counted_at_their_real_width(string character, int width)
	{
		string giant = string.Concat(Enumerable.Repeat(character, 3 * Limit / width));

		RedactedSession session = await Claude(ClaudeLine("user", giant), Small);

		Assert.InRange(session.Documents.Count, 3, 4);
		Assert.All(session.Documents, static document => Assert.InRange(BodyBytes(document), 1, Limit + 64));
	}

	[Fact]
	public async Task Appending_to_a_session_leaves_earlier_cuts_alone()
	{
		string text = string.Concat(Enumerable.Repeat(new string('z', 39) + "\n", 400));

		RedactedSession shorter = await Claude(ClaudeLine("user", text), Small);
		RedactedSession longer = await Claude(ClaudeLine("user", text + string.Concat(Enumerable.Repeat(new string('q', 39) + "\n", 300))), Small);

		for (int i = 0; i < shorter.Documents.Count - 1; i++)
		{
			Assert.Equal(shorter.Documents[i].Path, longer.Documents[i].Path);
			Assert.Equal(Body(shorter.Documents[i]), Body(longer.Documents[i]));
		}
	}

	[Fact]
	public async Task A_short_session_renders_exactly_as_before()
	{
		RedactedSession session = await Claude(ClaudeLine("user", "hello") + ClaudeLine("assistant", "world"), Small);

		RedactedSessionDocument document = Assert.Single(session.Documents);
		Assert.Equal(2, document.MessageCount);
	}

	[Fact]
	public async Task Messages_after_a_giant_one_keep_their_order_and_the_files_they_touched()
	{
		string giant = string.Concat(Enumerable.Repeat(new string('a', 63) + "\n", 300));
		string jsonl = ClaudeLine("user", "before") + ClaudeLine("assistant", giant) + ClaudeLine("user", "after");

		RedactedSession session = await Claude(jsonl, Small);

		Assert.Contains("before", session.Documents[0].Text(), StringComparison.Ordinal);
		Assert.Contains("after", session.Documents[^1].Text(), StringComparison.Ordinal);
		for (int i = 1; i < session.Documents.Count; i++)
		{
			Assert.Equal(session.Documents[i - 1].MessageStart + session.Documents[i - 1].MessageCount, session.Documents[i].MessageStart);
		}
	}

	[Fact]
	public async Task A_codex_giant_message_is_split_too()
	{
		string giant = string.Concat(Enumerable.Repeat(new string('c', 63) + "\n", 300));

		RedactedSession session = await Codex(CodexMeta() + CodexMessage("user", giant), Small);

		Assert.True(session.Documents.Count >= 4);
		Assert.All(session.Documents, static document => Assert.InRange(BodyBytes(document), 1, Limit + 64));
	}

	[Fact]
	public async Task A_credential_at_a_cut_is_redacted_because_redaction_runs_first()
	{
		string token = Synthetic.GitHubClassic('p');
		string padded = new string('w', Limit - 10) + token + new string('w', Limit);

		RedactedSession session = await Claude(ClaudeLine("user", padded), Small);

		string joined = string.Concat(session.Documents.Select(static document => document.Text()));
		Assert.DoesNotContain(token, joined, StringComparison.Ordinal);
		Assert.DoesNotContain(token[..20], joined, StringComparison.Ordinal);
		Assert.Equal(1, session.CountsByRule["github-token"]);
	}

	[Fact]
	public async Task A_refusal_in_a_giant_session_replaces_the_right_message()
	{
		string random = Synthetic.Body(32, 1500);
		string giant = string.Concat(Enumerable.Repeat(new string('a', 63) + "\n", 200));
		string jsonl = CodexMeta() + CodexMessage("user", giant) + CodexMessage("user", "hello", "--token") + CodexMessage("assistant", "reply", random);

		RedactedSession session = await Codex(jsonl, Small);

		string joined = string.Concat(session.Documents.Select(static document => document.Text()));
		Assert.DoesNotContain(random, joined, StringComparison.Ordinal);
		Assert.Contains("hello", joined, StringComparison.Ordinal);
		Assert.Contains("reply", joined, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Listing_patterns_match_every_path_of_a_session_and_no_others()
	{
		RedactedSession claude = await Claude(ClaudeLine("user", new string('a', 3000)) + ClaudeLine("assistant", new string('b', 3000)), Small);
		RedactedSession codex = await Codex(CodexMeta() + CodexMessage("user", new string('a', 3000)) + CodexMessage("assistant", new string('b', 3000)), Small);

		foreach (RedactedSession session in new[] { claude, codex })
		{
			string pattern = "^" + System.Text.RegularExpressions.Regex.Escape(session.ExistingPathsGlob).Replace("\\?", ".").Replace("\\*", ".*") + "$";
			Assert.All(session.Documents, document => Assert.Matches(pattern, document.Path));
			Assert.DoesNotContain('/', session.ExistingPathsGlob);
		}
	}

	[Fact]
	public async Task Render_limits_must_leave_room_for_a_character()
	{
		SessionRenderOptions bad = new() { MaxMessageBytes = 3 };

		await Assert.ThrowsAsync<ArgumentException>(() => SessionRenderer.RenderAsync(new MemoryStream(), new SessionSource(SessionFormat.ClaudeCode, "x"), bad));
	}

	// --- document identity ---------------------------------------------------------

	[Theory]
	[InlineData("notes", "a", "^notes-[0-9a-f]{32}\\.md$")]
	[InlineData("My Notes!", "a", "^my-notes-[0-9a-f]{32}\\.md$")]
	[InlineData("../..", "a", "^doc-[0-9a-f]{32}\\.md$")]
	[InlineData("!!!", "a", "^doc-[0-9a-f]{32}\\.md$")]
	[InlineData("ThisIsASourceNameThatIsMuchLongerThanTheLabelAllows", "a", "^thisisasourcenamethatismuchlonge-[0-9a-f]{32}\\.md$")]
	public void A_storage_path_is_flat_lower_case_and_safe(string source, string id, string pattern)
	{
		Assert.Matches(pattern, new DocumentEnvelope(source, id).StoragePath());
	}

	[Fact]
	public void A_storage_path_is_stable_and_separates_identities()
	{
		string one = new DocumentEnvelope("s", "a").StoragePath();

		Assert.Equal(one, new DocumentEnvelope(" s ", " a ").StoragePath());
		Assert.NotEqual(one, new DocumentEnvelope("s", "b").StoragePath());
		Assert.NotEqual(new DocumentEnvelope("s", "a\0b").StoragePath(), new DocumentEnvelope("s\0a", "b").StoragePath());
	}

	[Fact]
	public void A_storage_path_requires_an_identity()
	{
		Assert.Throws<ArgumentException>(() => new DocumentEnvelope(" ", "a").StoragePath());
	}

	[Fact]
	public void A_credential_as_the_source_stays_out_of_the_path()
	{
		string token = Synthetic.GitHubClassic('p');

		Assert.Matches("^doc-[0-9a-f]{32}\\.md$", new DocumentEnvelope(token, "a").StoragePath());
	}

	[Fact]
	public void Field_redactions_are_reported_by_field_and_rule()
	{
		string token = Synthetic.GitHubClassic('p');
		List<EnvelopeFieldRedaction> found = [];

		new DocumentEnvelope("s", "i", Title: token, Tags: [token, "plain"]).Render("body", found);

		Assert.Contains(new EnvelopeFieldRedaction("title", "github-token", 1), found);
		Assert.Contains(new EnvelopeFieldRedaction("tags", "github-token", 1), found);
		Assert.Equal(2, found.Count);
	}

	// --- client -----------------------------------------------------------------------

	[Fact]
	public void A_bounded_stream_raises_what_the_caller_chose()
	{
		using BoundedReadStream stream = new(new MemoryStream(new byte[10]), 4, static () => new TimeoutException("over"));

		Assert.Throws<TimeoutException>(() => stream.ReadExactly(new byte[10]));
	}
}
