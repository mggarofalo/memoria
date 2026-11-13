using System.Text.RegularExpressions;
using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// A replacement that runs over a message boundary in joined text keeps the boundary, so the renderer gets every message back. No
/// built-in rule's match crosses one, so a rule made here does.
/// </summary>
public sealed class MessageBoundaryTests
{
	private const string Placeholder = "[REDACTED:span]";

	private static Redactor Spanning(string pattern)
	{
		return new Redactor([new RedactionRule("span", new Regex(pattern, RegexOptions.NonBacktracking))], TimeSpan.FromMinutes(1));
	}

	[Fact]
	public void A_span_over_a_boundary_leaves_a_placeholder_in_each_message()
	{
		string text = "a S1" + Redactor.MessageSeparator + "2E b";

		Assert.Equal("a " + Placeholder + Redactor.MessageSeparator + Placeholder + " b", Spanning(@"S[^E]*E").RedactFragment(text).Text);
	}

	[Fact]
	public void A_message_whose_part_of_the_span_is_only_space_gets_no_placeholder()
	{
		string text = "a" + Redactor.MessageSeparator + "2E b";

		Assert.Equal("a" + Redactor.MessageSeparator + Placeholder + " b", Spanning(@"\n" + Redactor.MessageBoundary + @"\n2E").RedactFragment(text).Text);
	}

	[Fact]
	public void A_boundary_character_outside_a_separator_is_noise_the_replacement_takes()
	{
		string text = "a S1" + Redactor.MessageBoundary + "2E b";

		Assert.Equal("a " + Placeholder + " b", Spanning(@"S[^E]*E").RedactFragment(text).Text);
	}

	[Fact]
	public void A_span_over_two_boundaries_keeps_both()
	{
		string text = "S1" + Redactor.MessageSeparator + "2" + Redactor.MessageSeparator + "3E";

		Assert.Equal(Placeholder + Redactor.MessageSeparator + Placeholder + Redactor.MessageSeparator + Placeholder, Spanning(@"S[^E]*E").RedactFragment(text).Text);
	}
}
