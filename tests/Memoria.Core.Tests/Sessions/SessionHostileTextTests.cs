using Memoria.Core.Redaction;
using Memoria.Core.Sessions;
using Memoria.Core.Tests.Redaction;
using static Memoria.Core.Tests.Sessions.SessionTestKit;

namespace Memoria.Core.Tests.Sessions;

/// <summary>Text that redaction cannot finish costs its own message or field, and never the whole session.</summary>
public sealed class SessionHostileTextTests
{
	private const string Marker = "[REDACTED:refused-message]";

	/// <summary>
	/// Each candidate is a call, so it is rejected, and holds the next one, so the rule rereads the rest of the line for each. A line stays
	/// under the length past which a rejected value is skipped rather than reread, and a few such lines take the rule past its allowance.
	/// </summary>
	private static string Hostile => string.Join('\n', Enumerable.Repeat("token=" + string.Concat(Enumerable.Repeat("f(token=", 450)), 4));

	private const string Piece = "f(token=";

	[Fact]
	public void The_flood_really_cannot_be_redacted()
	{
		RedactionException exception = Assert.Throws<RedactionException>(() => Redactor.Shared.RedactFragment(Hostile));

		Assert.Equal(RedactionFailure.ScanBudgetExhausted, exception.Failure);
	}

	[Fact]
	public async Task A_flood_costs_only_its_message_and_is_reported()
	{
		string jsonl = ClaudeLine("user", "first") + ClaudeLine("assistant", Hostile) + ClaudeLine("user", "third");

		RedactedSession session = await Claude(jsonl);

		string text = session.Documents[0].Text();
		Assert.Contains("first", text, StringComparison.Ordinal);
		Assert.Contains("third", text, StringComparison.Ordinal);
		Assert.Contains("## Assistant\n\n" + Marker + "\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain(Piece, text, StringComparison.Ordinal);
		SessionFieldRefusal refusal = Assert.Single(session.Refusals);
		Assert.Equal(new SessionFieldRefusal(1, "message:1", "secret-assignment"), refusal);
	}

	/// <summary>One line of the flood is within the allowance and two are not, so two messages of one line each fail only together.</summary>
	[Fact]
	public async Task Two_messages_that_fail_only_together_are_both_replaced()
	{
		string line = Hostile.Split('\n')[0];
		Assert.Equal(line, Redactor.Shared.RedactFragment(line).Text);
		string jsonl = ClaudeLine("user", "first") + ClaudeLine("assistant", line) + ClaudeLine("user", line) + ClaudeLine("assistant", "last");

		RedactedSession session = await Claude(jsonl);

		Assert.Equal(["message:1", "message:2"], session.Refusals.Select(static r => r.Field).Order());
		Assert.Contains("first", session.Documents[0].Text(), StringComparison.Ordinal);
		Assert.Contains("last", session.Documents[0].Text(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Every_flood_in_a_long_session_is_found()
	{
		string jsonl = string.Concat(Enumerable.Range(0, 9).Select(static i => ClaudeLine("user", i % 4 == 1 ? Hostile : "m" + i)));

		RedactedSession session = await Claude(jsonl);

		Assert.Equal(["message:1", "message:5"], session.Refusals.Select(static r => r.Field));
		Assert.Contains("m8", session.Documents[0].Text(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_refused_message_reports_the_part_it_landed_in()
	{
		string padding = new('a', 300);
		string jsonl = ClaudeLine("user", padding) + ClaudeLine("assistant", padding) + ClaudeLine("user", Hostile);

		RedactedSession session = await Claude(jsonl, new SessionRenderOptions { MaxPartChars = 400 });

		SessionFieldRefusal refusal = Assert.Single(session.Refusals);
		Assert.Equal("message:2", refusal.Field);
		Assert.Equal(session.Documents.Single(d => d.Text().Contains(Marker, StringComparison.Ordinal)).Part, refusal.Part);
	}

	[Fact]
	public async Task A_flood_as_the_first_message_cannot_reach_the_title()
	{
		RedactedSession session = await Claude(ClaudeLine("user", Hostile) + ClaudeLine("assistant", "ok"));

		Assert.DoesNotContain(Piece, session.Documents[0].Text(), StringComparison.Ordinal);
		Assert.Contains("ok", session.Documents[0].Text(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_flood_in_metadata_costs_only_that_field()
	{
		string jsonl = ClaudeLine("user", "first", cwd: "/srv/" + Hostile);

		RedactedSession session = await Claude(jsonl);

		string text = session.Documents[0].Text();
		Assert.Contains("first", text, StringComparison.Ordinal);
		Assert.DoesNotContain(Piece, text, StringComparison.Ordinal);
		Assert.Contains("[REDACTED:refused-field]", text, StringComparison.Ordinal);
		Assert.Equal(new SessionFieldRefusal(0, "metadata", "secret-assignment"), Assert.Single(session.Refusals));
	}

	[Fact]
	public async Task A_session_id_that_cannot_be_redacted_gets_a_digest_path()
	{
		string id = Hostile.Replace(" ", "", StringComparison.Ordinal);

		RedactedSession session = await Render(ClaudeLine("user", "first"), new SessionSource(SessionFormat.ClaudeCode, Hostile));

		Assert.DoesNotContain(id, session.Documents[0].Path, StringComparison.Ordinal);
		Assert.Null(SidecarRefusal(session.Documents[0].Path));
	}

	[Fact]
	public void Only_failures_caused_by_the_text_are_recoverable()
	{
		Assert.True(Redactor.IsCausedByText(RedactionFailure.DidNotConverge));
		Assert.True(Redactor.IsCausedByText(RedactionFailure.ScanBudgetExhausted));
		Assert.False(Redactor.IsCausedByText(RedactionFailure.TimedOut));
		Assert.False(Redactor.IsCausedByText(RedactionFailure.RuleFailed));
		Assert.False(Redactor.IsCausedByText(RedactionFailure.FoundAfterRedaction));
	}
}
