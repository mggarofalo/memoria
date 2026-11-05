using System.Globalization;
using System.Text.RegularExpressions;
using Memoria.Core.Contracts;
using Memoria.Core.Redaction;

namespace Memoria.Core.Sessions;

/// <summary>A document someone else rendered, kept as it came, on its way through the redaction decision. Its frontmatter is text like the rest of it, so it is redacted the same way.</summary>
public static partial class RenderedDocument
{
	/// <summary>The whole text, frontmatter included, for the redactor.</summary>
	public static UnredactedMarkdown ForRedaction(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		return new UnredactedMarkdown(text);
	}

	/// <summary>The same text, already through the redaction decision: for the one collection <paramref name="disabled"/> names.</summary>
	public static RedactedMarkdown WithoutRedactionForDisabledCollection(string text, RedactionDisabledCollection disabled)
	{
		ArgumentNullException.ThrowIfNull(disabled);
		return RedactedMarkdown.PassThroughForDisabledCollection(ForRedaction(text), disabled);
	}

	/// <summary>The session a rendered document says it belongs to, wherever in its frontmatter that is, as the session can take the document over even when the sidecar cannot read the block.</summary>
	public static string? SessionIdOf(string text)
	{
		Match found = SessionIdField().Match(FrontmatterBlock(text));
		return found.Success ? found.Groups[1].Value : null;
	}

	/// <summary>
	/// How many of its session's messages come up to the end of this document, so a later copy with fewer has lost some. <c>message_count</c> counts one part alone, so a part's
	/// <c>message_start</c> goes with it. Codex documents also have <c>message_end</c>. Null when it does not say.
	/// </summary>
	public static long? MessagesThroughOf(string text)
	{
		string block = FrontmatterBlock(text);
		if (MessageCountField().Match(block) is { Success: true } count)
		{
			return Number(MessageStartField().Match(block)) + Number(count);
		}

		return MessageEndField().Match(block) is { Success: true } end ? Number(end) + 1 : null;
	}

	private static long Number(Match field)
	{
		return field.Success ? long.Parse(field.Groups[1].ValueSpan, CultureInfo.InvariantCulture) : 0;
	}

	private static string FrontmatterBlock(string text)
	{
		string body = text.StartsWith('﻿') ? text[1..] : text;
		if (!body.StartsWith("---\n", StringComparison.Ordinal) && !body.StartsWith("---\r\n", StringComparison.Ordinal))
		{
			return "";
		}

		Match close = ClosingFence().Match(body);
		return close.Success ? body[..close.Index] : "";
	}

	[GeneratedRegex(@"^session_id:[ \t]*""?([^""\r\n]+?)""?[ \t]*\r?$", RegexOptions.Multiline, 1000)]
	private static partial Regex SessionIdField();

	[GeneratedRegex(@"^message_start:[ \t]*(\d{1,18})[ \t]*\r?$", RegexOptions.Multiline, 1000)]
	private static partial Regex MessageStartField();

	[GeneratedRegex(@"^message_end:[ \t]*(\d{1,18})[ \t]*\r?$", RegexOptions.Multiline, 1000)]
	private static partial Regex MessageEndField();

	[GeneratedRegex(@"^message_count:[ \t]*(\d{1,18})[ \t]*\r?$", RegexOptions.Multiline, 1000)]
	private static partial Regex MessageCountField();

	/// <summary>The sidecar is what decides, as it reads the same window here as in a rescan. This only says why, when it found nothing.</summary>
	public static string FrontmatterOf(string text, bool read)
	{
		ArgumentNullException.ThrowIfNull(text);
		if (read)
		{
			return RenderedFrontmatter.Read;
		}

		string body = text.StartsWith('﻿') ? text[1..] : text;
		if (!body.StartsWith("---\n", StringComparison.Ordinal) && !body.StartsWith("---\r\n", StringComparison.Ordinal))
		{
			return RenderedFrontmatter.Missing;
		}

		int end = 0;
		for (int seen = 0; seen < SessionFrontmatter.SidecarHeadWindow && end < text.Length; seen++)
		{
			end += char.IsSurrogatePair(text, end) ? 2 : 1;
		}

		return ClosingFence().IsMatch(text[..end]) ? RenderedFrontmatter.Unparsable : RenderedFrontmatter.PastWindow;
	}

	[GeneratedRegex(@"\r?\n---(?:\r?\n|$)", RegexOptions.None, 1000)]
	private static partial Regex ClosingFence();
}
