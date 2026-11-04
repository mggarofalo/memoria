namespace Memoria.Core.Redaction;

/// <summary>Why a document could not be redacted.</summary>
public enum RedactionFailure
{
	/// <summary>A rule threw while evaluating the document.</summary>
	RuleFailed,

	/// <summary>A rule's regex or the whole document exceeded its time budget.</summary>
	TimedOut,

	/// <summary>Repeated passes kept changing the text, so the result cannot be trusted as complete.</summary>
	DidNotConverge,

	/// <summary>The finished document still held a detectable credential after redaction, so the gate refused it.</summary>
	FoundAfterRedaction,
}

/// <summary>
/// Redaction could not complete, so the document must be refused. Carries only
/// the rule name, the failure kind, and the part, which is what an operator needs to act and all
/// that is safe to log. It deliberately has no inner exception:
/// <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException.Input"/>
/// holds the entire document, and a logger that walks inner exceptions would
/// publish the very credentials this library exists to remove.
/// </summary>
public sealed class RedactionException(string ruleName, RedactionFailure failure, int? part = null)
	: Exception($"Redaction failed in rule '{ruleName}' ({failure}){(part is null ? "" : $" in part {part}")}. The document must not be indexed.")
{
	/// <summary>The rule that failed, or <see cref="WholeDocument"/> when no single rule is to blame.</summary>
	public string RuleName { get; } = ruleName;

	/// <summary>The kind of failure.</summary>
	public RedactionFailure Failure { get; } = failure;

	/// <summary>The 1-based session part the gate refused, or null when the failure is not tied to one part.</summary>
	public int? Part { get; } = part;

	/// <summary>Value of <see cref="RuleName"/> for failures that span every rule.</summary>
	public const string WholeDocument = "*";
}
