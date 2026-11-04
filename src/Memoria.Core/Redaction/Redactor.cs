using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Memoria.Core.Sessions;

namespace Memoria.Core.Redaction;

/// <summary>
/// Applies <see cref="RedactionRules.All"/> in order, with frontmatter quoting as a step of the same
/// loop, repeating until a pass changes nothing. The output is therefore a fixed point of the whole
/// pass, which makes redacting twice equal redacting once by construction. The only public
/// constructor uses the built-in rules: there is no runtime switch that could turn redaction off.
/// Thread-safe.
/// </summary>
public sealed class Redactor : IRedactor
{
	/// <summary>
	/// Marks a message boundary in joined text. A non-character, so no real text needs it, and input
	/// holding one is repaired first. <see cref="MessageSeparator"/> wraps it in newlines so every value
	/// stops at a boundary, while the key walker alone reads past it: a key spread over two messages
	/// is one key.
	/// </summary>
	internal const char MessageBoundary = '﷐';

	internal const string MessageSeparator = "\n﷐\n";

	/// <summary>A pass after the first only runs when the previous one changed something; more than this means rules are fighting.</summary>
	private const int MaxPasses = 6;

	/// <summary>A hard ceiling across all rules and passes, well above what a 50 MB document needs.</summary>
	private static readonly TimeSpan DefaultBudget = TimeSpan.FromMinutes(5);

	private readonly IReadOnlyList<RedactionRule> _rules;
	private readonly IReadOnlyList<RedactionRule> _lineRules;
	private readonly IReadOnlyList<RedactionRule> _tokenRules;
	private readonly TimeSpan _budget;

	/// <summary>Creates a redactor over the built-in rules.</summary>
	public Redactor()
		: this(RedactionRules.All, DefaultBudget)
	{
	}

	/// <summary>Test seam for failure injection. Not reachable outside this assembly's friends.</summary>
	internal Redactor(IReadOnlyList<RedactionRule> rules, TimeSpan budget)
	{
		_rules = rules;
		_lineRules = [.. rules.Where(static rule => rule.LinesMatter)];
		_tokenRules = [.. rules.Where(static rule => !rule.LinesMatter)];
		_budget = budget;
	}

	/// <summary>The instance the renderer uses. Redactors hold no per-call state.</summary>
	internal static Redactor Shared { get; } = new();

	/// <summary>The name of every built-in rule, for dashboards and audit logs.</summary>
	public static IReadOnlyList<string> RuleNames { get; } = [.. RedactionRules.All.Select(static r => r.Name)];

	/// <inheritdoc />
	public RedactedMarkdown Redact(UnredactedMarkdown markdown)
	{
		ArgumentNullException.ThrowIfNull(markdown);
		RedactionResult result = Redact(markdown.ExposeForRedaction());
		return new RedactedMarkdown(result.Text, result.CountsByRule);
	}

	/// <summary>Redacts a whole document, quoting any frontmatter value that now starts with a placeholder.</summary>
	internal RedactionResult Redact(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		return Run(text, quoteFrontmatter: true);
	}

	/// <summary>Redacts text that is not a document yet, such as joined messages or one metadata value.</summary>
	internal RedactionResult RedactFragment(string text)
	{
		return Run(text, quoteFrontmatter: false);
	}

	/// <summary>
	/// The last check on a finished document. Its parts were already redacted, so a hit here means
	/// assembly created or uncovered a credential, and the document is refused rather than repaired.
	/// </summary>
	/// <exception cref="RedactionException">The document still holds something a rule detects.</exception>
	internal RedactedMarkdown Seal(string finishedDocument)
	{
		RedactionResult gate = Run(finishedDocument, quoteFrontmatter: false);
		if (gate.WasRedacted)
		{
			throw new RedactionException(gate.CountsByRule.Keys.First(), RedactionFailure.FoundAfterRedaction);
		}

		return new RedactedMarkdown(finishedDocument, gate.CountsByRule);
	}

	private RedactionResult Run(string text, bool quoteFrontmatter)
	{
		Stopwatch clock = Stopwatch.StartNew();
		Dictionary<string, int> counts = [];
		string current = text;
		for (int pass = 0; pass < MaxPasses; pass++)
		{
			string next = RunPass(current, quoteFrontmatter, clock, counts);
			if (ReferenceEquals(next, current))
			{
				return new RedactionResult(current, counts);
			}

			current = next;
		}

		throw new RedactionException(RedactionException.WholeDocument, RedactionFailure.DidNotConverge);
	}

	/// <summary>
	/// Rules that read lines first. Then the view with wrapped lines joined, while a wrapped token is still
	/// whole: replacing its first line alone would move the line off its column and strand the tail. Then every
	/// rule on the noise-free view, and quoting.
	/// </summary>
	private string RunPass(string text, bool quoteFrontmatter, Stopwatch clock, Dictionary<string, int> counts)
	{
		string current = Sweep(_lineRules, text, joinWrappedLines: false, clock, counts);
		current = Sweep(_tokenRules, current, joinWrappedLines: true, clock, counts);
		current = Sweep(_rules, current, joinWrappedLines: false, clock, counts);
		string quoted = quoteFrontmatter ? YamlFrontmatter.QuotePlaceholders(current) : current;
		return quoted == text ? text : quoted;
	}

	private string Sweep(IReadOnlyList<RedactionRule> rules, string text, bool joinWrappedLines, Stopwatch clock, Dictionary<string, int> counts)
	{
		string current = text;
		DetectionView? view = DetectionView.Create(current, joinWrappedLines);
		foreach (RedactionRule rule in rules)
		{
			string next = ApplyGuarded(rule, current, view, clock, counts);
			if (!ReferenceEquals(next, current))
			{
				current = next;
				view = DetectionView.Create(current, joinWrappedLines);
			}
		}

		return current;
	}

	/// <summary>Turns any failure into a typed exception that says nothing about the input.</summary>
	private string ApplyGuarded(RedactionRule rule, string input, DetectionView? view, Stopwatch clock, Dictionary<string, int> counts)
	{
		try
		{
			return Apply(rule, input, view, clock, counts);
		}
		catch (RedactionException)
		{
			throw;
		}
		catch (RegexMatchTimeoutException)
		{
			// Its Input property holds the whole document, so it must not travel as an inner exception.
			throw new RedactionException(rule.Name, RedactionFailure.TimedOut);
		}
		catch (Exception)
		{
			throw new RedactionException(rule.Name, RedactionFailure.RuleFailed);
		}
	}

	private string Apply(RedactionRule rule, string input, DetectionView? view, Stopwatch clock, Dictionary<string, int> counts)
	{
		string searched = view?.Text ?? input;
		StringBuilder? output = null;
		int copied = 0;
		int position = 0;
		int hits = 0;

		ThrowIfOverBudget(rule, clock);
		while (rule.TryFindNext(searched, position, out int start, out int end))
		{
			ThrowIfOverBudget(rule, clock);
			position = end;
			(int originalStart, int originalEnd) = view?.ToOriginal(start, end) ?? (start, end);

			output ??= new StringBuilder(input.Length);
			output.Append(input, copied, originalStart - copied);
			AppendReplacement(output, input, originalStart, originalEnd, rule.Placeholder);
			copied = originalEnd;
			hits++;
		}

		if (output is null)
		{
			return input;
		}

		output.Append(input, copied, input.Length - copied);
		counts[rule.Name] = counts.GetValueOrDefault(rule.Name) + hits;
		return output.ToString();
	}

	/// <summary>A span crossing message boundaries leaves a placeholder in each message, so the boundaries survive.</summary>
	private static void AppendReplacement(StringBuilder output, string input, int start, int end, string placeholder)
	{
		int cursor = start;
		int boundary = input.IndexOf(MessageBoundary, cursor, end - cursor);
		while (boundary >= 0)
		{
			output.Append(placeholder).Append(MessageSeparator);
			cursor = boundary + 1;
			boundary = input.IndexOf(MessageBoundary, cursor, end - cursor);
		}

		output.Append(placeholder);
	}

	private void ThrowIfOverBudget(RedactionRule rule, Stopwatch clock)
	{
		if (clock.Elapsed > _budget)
		{
			throw new RedactionException(rule.Name, RedactionFailure.TimedOut);
		}
	}
}
