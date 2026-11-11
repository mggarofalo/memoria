using System.Text.RegularExpressions;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;
using Memoria.Core.Tests.Redaction;

namespace Memoria.Core.Tests.Sessions;

/// <summary>
/// What the part gate replaces when it finds a hit: the field's whole value in the shape it had, the message digest once a
/// message goes, and the title and every message when the check cannot finish. Every secret is synthetic.
/// </summary>
public sealed class PartGateRefusalTests
{
	private const string Frontmatter = "---\nproject: \"demo\"\n---\n";

	private static string Token => Synthetic.GitHubClassic('p');

	/// <summary>A check that cannot finish on text holding "poison", as when a crafted document uses up a rule's scan budget.</summary>
	private static readonly Redactor Unfinishable = new(
		[new RedactionRule("poison", new Regex("poison"), static _ => throw new RedactionException("poison", RedactionFailure.ScanBudgetExhausted))],
		TimeSpan.FromMinutes(1));

	[Fact]
	public void Refusing_a_message_also_replaces_the_message_digest()
	{
		string document = "---\nproject: \"demo\"\nmessage_sha256: \"abc\"\n---\n\n## User\n\n" + Token + "\n";

		string cleaned = Clean(document, [Token], out _);

		Assert.Equal("---\nproject: \"demo\"\nmessage_sha256: \"[REDACTED:refused-field]\"\n---\n\n## User\n\n[REDACTED:refused-message]\n", cleaned);
	}

	[Fact]
	public void The_message_digest_stays_when_no_message_is_refused()
	{
		string document = "---\nslug: \"" + Token + "\"\nmessage_sha256: \"abc\"\n---\n\n## User\n\nx\n";

		string cleaned = Clean(document, ["x"], out _);

		Assert.Equal("---\nslug: \"[REDACTED:refused-field]\"\nmessage_sha256: \"abc\"\n---\n\n## User\n\nx\n", cleaned);
	}

	[Fact]
	public void A_field_with_no_name_cannot_be_replaced_so_the_part_is_refused()
	{
		string document = "---\n: \"" + Token + "\"\n---\n\n## User\n\nx\n";

		RedactionException exception = Assert.Throws<RedactionException>(() => Clean(document, ["x"], out _));

		Assert.Equal(RedactionFailure.FoundAfterRedaction, exception.Failure);
	}

	[Fact]
	public void An_indented_first_line_is_replaced_as_its_own_field()
	{
		string document = " note: \"" + Token + "\"\n---\n\n## User\n\nx\n";

		Assert.Equal(" note: \"[REDACTED:refused-field]\"\n---\n\n## User\n\nx\n", Clean(document, ["x"], out _));
	}

	[Fact]
	public void A_field_that_runs_onto_indented_lines_is_replaced_as_a_list()
	{
		string document = "---\nnote: a\n  \"" + Token + "\"\n---\n\n## User\n\nx\n";

		Assert.Equal("---\nnote: [\"[REDACTED:refused-field]\"]\n---\n\n## User\n\nx\n", Clean(document, ["x"], out _));
	}

	[Fact]
	public void An_inline_list_is_replaced_as_a_list()
	{
		string document = "---\ntags: [\"a\", \"" + Token + "\"]\n---\n\n## User\n\nx\n";

		Assert.Equal("---\ntags: [\"[REDACTED:refused-field]\"]\n---\n\n## User\n\nx\n", Clean(document, ["x"], out _));
	}

	[Fact]
	public void A_heading_with_no_title_text_is_not_read_as_a_title()
	{
		string document = Frontmatter + "\n# \n## User\n\nx\n";

		string cleaned = Clean(document, ["x"], out List<SessionFieldRefusal> refusals);

		Assert.StartsWith(Frontmatter, cleaned, StringComparison.Ordinal);
		Assert.Empty(refusals);
	}

	[Fact]
	public void Too_many_hits_refuse_the_title_and_name_each_message_by_its_place_in_the_session()
	{
		string[] texts = [.. Enumerable.Range(0, 70).Select(static i => "m" + i + " " + Token)];
		SessionPart part = Part(texts, messageStart: 5);
		List<SessionFieldRefusal> refusals = [];

		string output = PartGate.Clean(Render("Title", texts), part, refusals).Text;

		Assert.Contains("\n# [REDACTED:refused-field]\n", output, StringComparison.Ordinal);
		Assert.Contains(refusals, static r => r.Field == "title");
		string[] expected = [.. Enumerable.Range(5, 70).Select(static i => $"message:{i}")];
		Assert.Equal(expected.Order(StringComparer.Ordinal), refusals.Where(static r => r.Field != "title").Select(static r => r.Field).Order(StringComparer.Ordinal));
	}

	[Fact]
	public void A_check_that_cannot_finish_costs_the_part_its_title_and_messages()
	{
		string[] texts = ["poison here", "fine"];
		List<SessionFieldRefusal> refusals = [];

		string output = PartGate.Clean(Render("Title", texts), Part(texts, messageStart: 0), refusals, Unfinishable).Text;

		Assert.Equal(Frontmatter + "\n# [REDACTED:refused-field]\n\n## User\n\n[REDACTED:refused-message]\n\n## Assistant\n\n[REDACTED:refused-message]\n", output);
		Assert.Equal(["title", "message:0", "message:1"], refusals.Select(static r => r.Field));
		Assert.All(refusals, static r => Assert.Equal("poison", r.Rule));
	}

	[Fact]
	public void A_check_that_still_cannot_finish_after_the_body_is_refused_refuses_the_part()
	{
		string[] texts = ["poison here"];
		string document = "---\nproject: \"poison\"\n---\n\n## User\n\npoison here\n";

		RedactionException exception = Assert.Throws<RedactionException>(() => PartGate.Clean(document, Part(texts, messageStart: 0), [], Unfinishable));

		Assert.Equal(RedactionFailure.ScanBudgetExhausted, exception.Failure);
	}

	private static SessionPart Part(string[] texts, int messageStart)
	{
		SessionMessage[] all = [.. texts.Select((text, i) => new SessionMessage(i % 2 == 0 ? SessionMessage.User : SessionMessage.Assistant, text, null, null, text.Length))];
		return new SessionPart(new ArraySegment<SessionMessage>(all), 1, messageStart, SessionTimestamp.FromFallback(null));
	}

	private static string Render(string? title, string[] texts)
	{
		string body = title is null ? "\n" : "\n# " + title + "\n\n";
		for (int i = 0; i < texts.Length; i++)
		{
			body += (i > 0 ? "\n" : "") + (i % 2 == 0 ? "## User\n\n" : "## Assistant\n\n") + texts[i] + "\n";
		}

		return Frontmatter + body;
	}

	private static string Clean(string document, string[] texts, out List<SessionFieldRefusal> refusals)
	{
		refusals = [];
		return PartGate.Clean(document, Part(texts, messageStart: 0), refusals).Text;
	}
}
