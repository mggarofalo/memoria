using System.Buffers;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Memoria.Core.Redaction;

/// <summary>A regex hit awaiting judgement. The value must never be logged or stored.</summary>
internal readonly record struct SecretCandidate(string Input, Match Match, Group Secret)
{
	public string Value => Secret.Value;

	/// <summary>The value plus any closing delimiter just past it, so a template or a call is judged whole.</summary>
	public string JudgedValue => ValueShapes.ExtendToCloser(Input, Secret.Index, Secret.Index + Secret.Length, Value);

	public int End => Secret.Index + Secret.Length;
}

/// <summary>
/// Limits one rule's search over one text. Rejecting a candidate means reading its text again, so a crafted
/// document can make rejection quadratic. The search may reread a multiple of the text's length; line-separated
/// candidates each cost only their own length and never exhaust it. Past that the document is refused, because
/// skipping the rest could skip a secret.
/// </summary>
internal sealed class ScanGuard
{
	private const int RereadMultiple = 8;
	private const long BaseCredit = 1 << 20;

	private readonly RedactionRule _rule;
	private readonly Stopwatch? _clock;
	private readonly TimeSpan _budget;
	private long _credit;

	public ScanGuard(RedactionRule rule, int textLength, Stopwatch? clock = null, TimeSpan budget = default)
	{
		_rule = rule;
		_clock = clock;
		_budget = budget;
		_credit = BaseCredit + (RereadMultiple * (long)textLength);
	}

	/// <summary>Called for each rejection, so the time budget also holds while nothing is being accepted.</summary>
	/// <exception cref="RedactionException">The rescan credit or the time budget ran out.</exception>
	public void OnRejected(int rereadLength)
	{
		_credit -= rereadLength;
		if (_credit < 0)
		{
			throw new RedactionException(_rule.Name, RedactionFailure.ScanBudgetExhausted);
		}

		ThrowIfOverBudget();
	}

	public void ThrowIfOverBudget()
	{
		if (_clock is not null && _clock.Elapsed > _budget)
		{
			throw new RedactionException(_rule.Name, RedactionFailure.TimedOut);
		}
	}
}

/// <summary>
/// One detector. The pattern finds candidates: the <c>secret</c> group, or the
/// whole match when the group is absent, is the span replaced. Text around that
/// group is left alone. <c>accept</c> can veto a candidate, <c>findSpan</c> can
/// replace the span with one a regular language cannot describe, and <c>extend</c>
/// can lengthen an accepted span.
/// </summary>
internal sealed class RedactionRule
{
	/// <summary>A rejected candidate longer than this is rescanned from about this far in, since rescanning one character at a time would read it again for every character.</summary>
	public const int LongCandidate = 4096;

	/// <summary>Kept behind the resume point so a key name straddling it is still found. Names longer than this are not judged at all.</summary>
	private const int ResumeOverlap = 128;

	private const string SecretGroup = "secret";

	private readonly SearchValues<string>? _prefilter;

	public RedactionRule(
		string name,
		Regex pattern,
		Func<SecretCandidate, bool>? accept = null,
		Func<string, Match, (int Start, int End)?>? findSpan = null,
		Func<SecretCandidate, int>? extend = null,
		string? literal = null,
		bool linesMatter = false,
		bool skipRejected = false)
	{
		Name = name;
		Pattern = pattern;
		Accept = accept;
		FindSpan = findSpan;
		Extend = extend;
		LinesMatter = linesMatter;
		SkipRejected = skipRejected;
		Placeholder = $"[REDACTED:{name}]";
		Literal = literal;
		_prefilter = literal is null ? null : SearchValues.Create([literal], StringComparison.Ordinal);
	}

	/// <summary>Stable name, used in the placeholder and in the per-rule counts.</summary>
	public string Name { get; }

	public Regex Pattern { get; }

	public string Placeholder { get; }

	/// <summary>Text every match contains, so a document without it skips the regex. Null when no such text exists.</summary>
	public string? Literal { get; }

	public Func<SecretCandidate, bool>? Accept { get; }

	public Func<string, Match, (int Start, int End)?>? FindSpan { get; }

	public Func<SecretCandidate, int>? Extend { get; }

	/// <summary>True for a rule that reads line structure, so it runs before lines holding one wrapped token are joined.</summary>
	public bool LinesMatter { get; }

	/// <summary>True when a rejected candidate is rejected as a whole, so its inside need not be searched again.</summary>
	public bool SkipRejected { get; }

	/// <summary>Finds the next span to replace at or after <paramref name="start"/>. False when the rule has no more hits.</summary>
	public bool TryFindNext(string input, int start, out int spanStart, out int spanEnd)
	{
		return TryFindNext(input, start, new ScanGuard(this, input.Length), out spanStart, out spanEnd);
	}

	/// <inheritdoc cref="TryFindNext(string, int, out int, out int)"/>
	public bool TryFindNext(string input, int start, ScanGuard guard, out int spanStart, out int spanEnd)
	{
		spanStart = 0;
		spanEnd = 0;
		if (_prefilter is not null && !input.AsSpan(start).ContainsAny(_prefilter))
		{
			return false;
		}

		int position = start;
		while (position <= input.Length)
		{
			Match match = Pattern.Match(input, position);
			if (!match.Success)
			{
				return false;
			}

			Group secret = SecretOf(match);
			if (Resolve(input, match, secret) is { } span)
			{
				(spanStart, spanEnd) = span;
				return true;
			}

			guard.OnRejected(secret.Length);
			position = NextPosition(match, secret);
		}

		return false;
	}

	/// <summary>
	/// From the rejected span's start, not past it: a real secret can sit inside a rejected
	/// candidate, as in token_count=api_key=&lt;value&gt;. The match start still moves forward.
	/// </summary>
	private int NextPosition(Match match, Group secret)
	{
		if (SkipRejected)
		{
			return secret.Index + secret.Length;
		}

		int resume = secret.Length > LongCandidate ? secret.Index + LongCandidate - ResumeOverlap : secret.Index;
		return Math.Max(resume, match.Index + 1);
	}

	private static Group SecretOf(Match match)
	{
		Group secret = match.Groups[SecretGroup];
		secret = secret.Success ? secret : match;
		if (secret.Length == 0)
		{
			throw new InvalidOperationException("A redaction rule produced an empty span.");
		}

		return secret;
	}

	/// <summary>The span to replace, or null when the candidate is rejected.</summary>
	private (int Start, int End)? Resolve(string input, Match match, Group secret)
	{
		if (FindSpan is not null)
		{
			return FindSpan(input, match);
		}

		SecretCandidate candidate = new(input, match, secret);
		if (Accept is not null && !Accept(candidate))
		{
			return null;
		}

		return (secret.Index, Extend?.Invoke(candidate) ?? candidate.End);
	}
}
