using System.Buffers;
using System.Text.RegularExpressions;

namespace Memoria.Core.Redaction;

/// <summary>A regex hit awaiting judgement. The value must never be logged or stored.</summary>
internal readonly record struct SecretCandidate(string Input, Match Match, Group Secret)
{
	/// <summary>The value, cut to <see cref="RedactionRule.MaxJudgedLength"/>. The start of a value says what it is.</summary>
	public string Value => Secret.Length <= RedactionRule.MaxJudgedLength
		? Secret.Value
		: Input.Substring(Secret.Index, RedactionRule.MaxJudgedLength);

	/// <summary>The value plus any closing delimiter just past it, so a template or a call is judged whole.</summary>
	public string JudgedValue => ValueShapes.ExtendToCloser(Input, Secret.Index, Secret.Index + Secret.Length, Value);

	public int End => Secret.Index + Secret.Length;
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
	/// <summary>
	/// How much of a candidate is judged. Longer ones are judged on this prefix, so a long value is
	/// neither accepted blindly nor read end to end. A rejected long candidate is rescanned from here
	/// on, not from just inside its start, because one crafted run would otherwise cost quadratic time.
	/// </summary>
	public const int MaxJudgedLength = 4096;

	/// <summary>How many long rejected candidates one search rescans before skipping past the rest, which caps the cost of one crafted document.</summary>
	private const int MaxLongRescans = 16;

	private const string SecretGroup = "secret";

	private readonly SearchValues<string>? _prefilter;

	public RedactionRule(
		string name,
		Regex pattern,
		Func<SecretCandidate, bool>? accept = null,
		Func<string, Match, (int Start, int End)?>? findSpan = null,
		Func<SecretCandidate, int>? extend = null,
		string? literal = null,
		bool linesMatter = false)
	{
		Name = name;
		Pattern = pattern;
		Accept = accept;
		FindSpan = findSpan;
		Extend = extend;
		LinesMatter = linesMatter;
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

	/// <summary>Finds the next span to replace at or after <paramref name="start"/>. False when the rule has no more hits.</summary>
	public bool TryFindNext(string input, int start, out int spanStart, out int spanEnd)
	{
		spanStart = 0;
		spanEnd = 0;
		if (_prefilter is not null && !input.AsSpan(start).ContainsAny(_prefilter))
		{
			return false;
		}

		int position = start;
		int longRescans = 0;
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

			longRescans += secret.Length > MaxJudgedLength ? 1 : 0;
			position = NextPosition(match, secret, longRescans);
		}

		return false;
	}

	/// <summary>
	/// From the rejected span's start, not past it: a real secret can sit inside a rejected
	/// candidate, as in token_count=api_key=&lt;value&gt;. The match start still moves forward.
	/// </summary>
	private static int NextPosition(Match match, Group secret, int longRescans)
	{
		if (longRescans > MaxLongRescans)
		{
			return secret.Index + secret.Length;
		}

		int resume = secret.Length > MaxJudgedLength ? secret.Index + MaxJudgedLength : secret.Index;
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
