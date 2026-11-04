using Memoria.Core.Redaction;
using Memoria.Core.Sessions;
using Memoria.Core.Tests.Redaction;

namespace Memoria.Core.Tests.Sessions;

/// <summary>Exact offsets in the part gate: which field or message a hit belongs to, on each side of each boundary.</summary>
public sealed class PartGatePinTests
{
	private const string Frontmatter = "---\nproject: \"demo\"\n---\n";

	private static string Token => Synthetic.GitHubClassic('p');

	[Fact]
	public void First_field_after_the_fence_is_replaced()
	{
		string document = "---\nslug: \"" + Token + "\"\nproject: \"demo\"\n---\n\n## User\n\nx\n";

		Assert.Equal("---\nslug: \"[REDACTED:refused-field]\"\nproject: \"demo\"\n---\n\n## User\n\nx\n", Clean(document, ["x"], out _));
	}

	[Fact]
	public void Last_field_before_the_fence_is_replaced()
	{
		string document = "---\nproject: \"demo\"\nslug: \"" + Token + "\"\n---\n\n## User\n\nx\n";

		Assert.Equal("---\nproject: \"demo\"\nslug: \"[REDACTED:refused-field]\"\n---\n\n## User\n\nx\n", Clean(document, ["x"], out _));
	}

	[Fact]
	public void List_at_the_end_of_frontmatter_is_replaced_whole()
	{
		string document = "---\nproject: \"demo\"\ntags:\n  - \"" + Token + "\"\n  - \"b\"\n---\n\n## User\n\nx\n";

		Assert.Equal("---\nproject: \"demo\"\ntags: [\"[REDACTED:refused-field]\"]\n---\n\n## User\n\nx\n", Clean(document, ["x"], out _));
	}

	[Fact]
	public void Hit_on_a_later_list_item_still_names_the_key_above()
	{
		string document = "---\ntags:\n  - \"a\"\n  - \"" + Token + "\"\n---\n\n## User\n\nx\n";

		Clean(document, ["x"], out List<SessionFieldRefusal> refusals);

		Assert.Equal("tags", Assert.Single(refusals).Field);
	}

	[Fact]
	public void Document_with_no_frontmatter_has_only_a_body()
	{
		string document = "\n## User\n\n" + Token + "\n";

		Assert.Equal("\n## User\n\n[REDACTED:refused-message]\n", Clean(document, [Token], out List<SessionFieldRefusal> refusals));
		Assert.Equal("message:0", Assert.Single(refusals).Field);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public void Hit_is_assigned_to_the_message_holding_it(int index)
	{
		string[] texts = ["one", "two", "three"];
		texts[index] = Token;

		string cleaned = Clean(Render(null, texts), texts, out List<SessionFieldRefusal> refusals);

		Assert.Equal($"message:{index}", Assert.Single(refusals).Field);
		texts[index] = PartGate.MessageMarker;
		Assert.Equal(Render(null, texts), cleaned);
	}

	[Fact]
	public void Hit_at_a_message_start_is_that_message_and_not_the_one_before()
	{
		string[] texts = ["one", Token];

		Clean(Render("Title", texts), texts, out List<SessionFieldRefusal> refusals);

		Assert.Equal("message:1", Assert.Single(refusals).Field);
	}

	[Fact]
	public void Hit_at_the_end_of_a_message_is_that_message()
	{
		string[] texts = ["one " + Token, "two"];

		Clean(Render(null, texts), texts, out List<SessionFieldRefusal> refusals);

		Assert.Equal("message:0", Assert.Single(refusals).Field);
	}

	[Fact]
	public void Assistant_heading_survives_replacement()
	{
		string[] texts = ["one", Token];

		string cleaned = Clean(Render("T", texts), texts, out _);

		Assert.Equal(Frontmatter + "\n# T\n\n## User\n\none\n\n## Assistant\n\n[REDACTED:refused-message]\n", cleaned);
	}

	[Fact]
	public void Short_title_is_kept_when_the_hit_is_elsewhere()
	{
		string cleaned = Clean(Render("T", [Token]), [Token], out List<SessionFieldRefusal> refusals);

		Assert.Equal(Frontmatter + "\n# T\n\n## User\n\n[REDACTED:refused-message]\n", cleaned);
		Assert.Equal("message:0", Assert.Single(refusals).Field);
	}

	[Fact]
	public void Hit_in_the_title_is_the_title_and_the_message_stays()
	{
		string cleaned = Clean(Render(Token, ["kept"]), ["kept"], out List<SessionFieldRefusal> refusals);

		Assert.Equal(Frontmatter + "\n# [REDACTED:refused-field]\n\n## User\n\nkept\n", cleaned);
		Assert.Equal("title", Assert.Single(refusals).Field);
	}

	[Fact]
	public void Part_number_is_reported()
	{
		List<SessionFieldRefusal> refusals = [];
		SessionMessage[] all = [new(SessionMessage.User, "x", null, null, 1)];
		SessionPart part = new(new ArraySegment<SessionMessage>(all), 4, 0, SessionTimestamp.FromFallback(null));

		PartGate.Clean("---\nslug: \"" + Token + "\"\n---\n\n## User\n\nx\n", part, refusals);

		Assert.Equal(4, Assert.Single(refusals).Part);
	}

	[Fact]
	public void Hit_that_cannot_be_replaced_refuses_with_the_part()
	{
		SessionMessage[] all = [new(SessionMessage.User, PartGate.MessageMarker, null, null, 1)];
		SessionPart part = new(new ArraySegment<SessionMessage>(all), 2, 0, SessionTimestamp.FromFallback(null));
		string document = "---\n" + Token + "\n---\n\n## User\n\n" + PartGate.MessageMarker + "\n";

		RedactionException exception = Assert.Throws<RedactionException>(() => PartGate.Clean(document, part, []));

		Assert.Equal(2, exception.Part);
		Assert.Equal(RedactionFailure.FoundAfterRedaction, exception.Failure);
		Assert.DoesNotContain(Token, exception.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void Too_many_hits_refuse_rather_than_loop()
	{
		string fields = string.Concat(Enumerable.Range(0, 70).Select(i => $"f{i}: \"{Token}\"\n"));
		SessionMessage[] all = [new(SessionMessage.User, "x", null, null, 1)];
		SessionPart part = new(new ArraySegment<SessionMessage>(all), 1, 0, SessionTimestamp.FromFallback(null));

		RedactionException exception = Assert.Throws<RedactionException>(() => PartGate.Clean("---\n" + fields + "---\n\n## User\n\nx\n", part, []));

		Assert.Equal(RedactionFailure.DidNotConverge, exception.Failure);
		Assert.Equal(1, exception.Part);
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
		SessionMessage[] all = [.. texts.Select((text, i) => new SessionMessage(i % 2 == 0 ? SessionMessage.User : SessionMessage.Assistant, text, null, null, text.Length))];
		SessionPart part = new(new ArraySegment<SessionMessage>(all), 1, 0, SessionTimestamp.FromFallback(null));
		refusals = [];
		return PartGate.Clean(document, part, refusals).Text;
	}
}
