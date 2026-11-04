using Memoria.Core.Sessions;

namespace Memoria.Core.Redaction;

/// <summary>
/// Removes credentials from text before it can reach the index. Redaction is a
/// precondition of indexing: when <see cref="Redact"/> throws, the caller must
/// refuse the document, never fall back to the original text.
/// </summary>
public interface IRedactor
{
	/// <summary>Returns <paramref name="markdown"/> with every detected credential replaced by a typed placeholder.</summary>
	/// <exception cref="RedactionException">A rule failed or ran out of time. No partial text is returned.</exception>
	RedactedMarkdown Redact(UnredactedMarkdown markdown);
}

/// <summary>The outcome of redacting raw text, before it is sealed into a <see cref="RedactedMarkdown"/>.</summary>
/// <param name="Text">The text with credentials replaced by placeholders such as <c>[REDACTED:github-token]</c>.</param>
/// <param name="CountsByRule">Replacements made, by rule name. Rules that matched nothing are absent. Never holds a matched value.</param>
internal sealed record RedactionResult(string Text, IReadOnlyDictionary<string, int> CountsByRule)
{
	/// <summary>True when at least one credential was replaced.</summary>
	public bool WasRedacted => CountsByRule.Count > 0;
}
