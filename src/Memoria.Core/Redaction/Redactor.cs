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

	/// <summary>How long one redaction may take, and so how long a session may spend looking for the part of it that cannot be redacted.</summary>
	internal TimeSpan Budget => _budget;

	/// <summary>True when the text itself made redaction give up. Replacing that text ends the problem. Running out of time or an internal fault does not depend on any one piece of text.</summary>
	internal static bool IsCausedByText(RedactionFailure failure)
	{
		return failure is RedactionFailure.DidNotConverge or RedactionFailure.ScanBudgetExhausted;
	}

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
	/// Finds the first thing a rule would still replace in finished text, or null when it is clean. A clean result is exactly
	/// what makes a pass change nothing, so this judges the same text the same way the redactor does.
	/// </summary>
	internal GateHit? FindFirstHit(string text)
	{
		Stopwatch clock = Stopwatch.StartNew();
		ViewBuffers buffers = new();
		foreach (bool join in new[] { false, true })
		{
			Dictionary<string, int> unused = [];
			ViewCache views = new(join, buffers);
			foreach (RedactionRule rule in join ? _tokenRules : _rules)
			{
				GateHit? hit = FindGuarded(rule, text, views, clock);
				if (hit is not null)
				{
					return hit;
				}
			}
		}

		return null;
	}

	private GateHit? FindGuarded(RedactionRule rule, string text, ViewCache views, Stopwatch clock)
	{
		return Guarded(rule, () =>
		{
			DetectionView? view = views.Get(text);
			ScanGuard guard = new(rule, text.Length, clock, _budget);
			if (!rule.TryFindNext(view?.Text ?? text, 0, guard, out int start, out int end))
			{
				return null;
			}

			(int originalStart, int originalEnd) = view?.ToOriginal(start, end) ?? (start, end);
			return new GateHit(rule.Name, originalStart, originalEnd);
		});
	}

	private RedactionResult Run(string text, bool quoteFrontmatter)
	{
		Stopwatch clock = Stopwatch.StartNew();
		Dictionary<string, int> counts = [];
		ViewBuffers buffers = new();
		string current = text;
		for (int pass = 0; pass < MaxPasses; pass++)
		{
			string next = RunPass(current, quoteFrontmatter, clock, counts, buffers);
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
	private string RunPass(string text, bool quoteFrontmatter, Stopwatch clock, Dictionary<string, int> counts, ViewBuffers buffers)
	{
		string current = Sweep(_lineRules, text, joinWrappedLines: false, clock, counts, buffers);
		current = Sweep(_tokenRules, current, joinWrappedLines: true, clock, counts, buffers);
		current = Sweep(_rules, current, joinWrappedLines: false, clock, counts, buffers);
		string quoted = quoteFrontmatter ? YamlFrontmatter.QuotePlaceholders(current) : current;
		return quoted == text ? text : quoted;
	}

	private string Sweep(IReadOnlyList<RedactionRule> rules, string text, bool joinWrappedLines, Stopwatch clock, Dictionary<string, int> counts, ViewBuffers buffers)
	{
		string current = text;
		ViewCache views = new(joinWrappedLines, buffers);
		foreach (RedactionRule rule in rules)
		{
			current = Guarded(rule, () => Apply(rule, current, views, clock, counts));
		}

		return current;
	}

	/// <summary>Turns any failure into a typed exception that says nothing about the input.</summary>
	private static T Guarded<T>(RedactionRule rule, Func<T> body)
	{
		try
		{
			return body();
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

	private string Apply(RedactionRule rule, string input, ViewCache views, Stopwatch clock, Dictionary<string, int> counts)
	{
		DetectionView? view = views.Get(input);
		string searched = view?.Text ?? input;
		ScanGuard guard = new(rule, searched.Length, clock, _budget);
		StringBuilder? output = null;
		int copied = 0;
		int position = 0;
		int hits = 0;

		guard.ThrowIfOverBudget();
		while (rule.TryFindNext(searched, position, guard, out int start, out int end))
		{
			guard.ThrowIfOverBudget();
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

	/// <summary>A span crossing message boundaries leaves a placeholder in each message it covers text of, so the boundaries survive.</summary>
	private static void AppendReplacement(StringBuilder output, string input, int start, int end, string placeholder)
	{
		int cursor = start;
		int boundary = NextBoundary(input, cursor, end);
		if (boundary < 0)
		{
			output.Append(placeholder);
			return;
		}

		while (boundary >= 0)
		{
			AppendPiece(output, input, cursor, boundary, placeholder);
			output.Append(MessageSeparator);
			cursor = boundary + 1;
			boundary = NextBoundary(input, cursor, end);
		}

		AppendPiece(output, input, cursor, end, placeholder);
	}

	/// <summary>A boundary character outside a separator is only noise, and the replacement swallows it.</summary>
	private static int NextBoundary(string input, int start, int end)
	{
		int boundary = input.IndexOf(MessageBoundary, start, end - start);
		while (boundary >= 0 && !DetectionView.IsSeparatorBoundary(input, boundary))
		{
			boundary = input.IndexOf(MessageBoundary, boundary + 1, end - boundary - 1);
		}

		return boundary;
	}

	private static void AppendPiece(StringBuilder output, string input, int start, int end, string placeholder)
	{
		if (!input.AsSpan(start, end - start).IsWhiteSpace())
		{
			output.Append(placeholder);
		}
	}

	/// <summary>The view of the text a sweep is on. Built when a rule first needs it, inside the guard, so running out of memory is a typed refusal.</summary>
	private sealed class ViewCache(bool join, ViewBuffers buffers)
	{
		private string? _text;
		private DetectionView? _view;

		public DetectionView? Get(string text)
		{
			if (!ReferenceEquals(_text, text))
			{
				_view = DetectionView.Create(text, join, buffers);
				_text = text;
			}

			return _view;
		}
	}
}

/// <summary>What a rule would still replace in finished text. Holds positions and a rule name, never the value.</summary>
internal sealed record GateHit(string Rule, int Start, int End);
