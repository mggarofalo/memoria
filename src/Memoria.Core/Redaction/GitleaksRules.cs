using System.Text.RegularExpressions;

namespace Memoria.Core.Redaction;

/// <summary>
/// Gitleaks's default rules, from <see cref="GitleaksRuleData"/>, as ordinary rules of this engine. Each runs on the same cleaned
/// view of the text as every other rule, with the same budgets, and replaces only its <c>secret</c> group. It fires only when the
/// text holds one of its keywords. A candidate is rejected as upstream rejects it: its Shannon entropy is at or below the rule's
/// threshold, or an allowlist or a stopword covers it. It is also rejected when it reads as a placeholder or a sample, as every
/// other provider rule's is. Names are <c>gitleaks-</c> and the upstream id, so a count says where a rule came from.
/// </summary>
internal static class GitleaksRules
{
	public const string Prefix = "gitleaks-";

	/// <summary>The built-in rules, in upstream's order.</summary>
	public static IReadOnlyList<RedactionRule> All { get; } = Build(GitleaksRuleData.BuiltIn);

	public static IReadOnlyList<RedactionRule> Build(GitleaksRuleData data)
	{
		CompiledAllowlist global = new(data.GlobalAllowlist);
		return [.. data.Rules.Select(spec => Rule(spec, global))];
	}

	private static RedactionRule Rule(GitleaksRuleSpec spec, CompiledAllowlist global)
	{
		CompiledAllowlist[] own = [.. spec.Allowlists.Select(static allowlist => new CompiledAllowlist(allowlist))];
		return new RedactionRule(
			Prefix + spec.Id,
			null,
			candidate => Accept(candidate, spec.Entropy, global, own),
			extend: ThroughGluedText,
			keywords: spec.Keywords,
			backstop: true,
			buildPattern: () => RedactionRules.Pattern(spec.Pattern));
	}

	/// <summary>
	/// Letters and digits glued to the end of a match go with it, and so do <c>-</c> and <c>_</c> between them. Some upstream
	/// patterns have no boundary after a fixed length, so <c>flutterwave-encryption-key</c> matches the head of a longer secret key,
	/// ending <c>-X</c>, and replacing only the head would leave the rest. A dash or underscore with nothing glued after it stays.
	/// </summary>
	private static int ThroughGluedText(SecretCandidate candidate)
	{
		string input = candidate.Input;
		int end = candidate.End;
		while (end < input.Length && (char.IsAsciiLetterOrDigit(input[end]) || (input[end] is '-' or '_' && end + 1 < input.Length && char.IsAsciiLetterOrDigit(input[end + 1]))))
		{
			end++;
		}

		return end;
	}

	private static bool Accept(SecretCandidate candidate, double entropy, CompiledAllowlist global, CompiledAllowlist[] own)
	{
		string secret = candidate.Value;
		if (ValueShapes.IsPlaceholder(secret) || (entropy > 0 && ShannonEntropy(secret) <= entropy))
		{
			return false;
		}

		return !global.Allows(candidate) && !own.Any(allowlist => allowlist.Allows(candidate));
	}

	/// <summary>Bits per character, as upstream computes it over the secret.</summary>
	public static double ShannonEntropy(string value)
	{
		double entropy = 0;
		foreach (IGrouping<char, char> group in value.GroupBy(static c => c))
		{
			double share = (double)group.Count() / value.Length;
			entropy -= share * Math.Log2(share);
		}

		return entropy;
	}

	/// <summary>An allowlist with its patterns built once.</summary>
	private sealed class CompiledAllowlist(GitleaksAllowlist allowlist)
	{
		private readonly Lazy<Regex[]> _regexes = new(() => [.. allowlist.Regexes.Select(RedactionRules.Pattern)]);
		private readonly string[] _stopwords = [.. allowlist.Stopwords.Select(static word => word.ToLowerInvariant())];

		/// <summary>Upstream's rule: with AND, every kind given must hit; otherwise any hit allows the finding.</summary>
		public bool Allows(SecretCandidate candidate)
		{
			bool regex = allowlist.Regexes.Count > 0 && _regexes.Value.Any(pattern => pattern.IsMatch(Target(candidate)));
			string secret = candidate.Value.ToLowerInvariant();
			bool stopword = _stopwords.Length > 0 && _stopwords.Any(word => secret.Contains(word, StringComparison.Ordinal));
			if (allowlist.Condition == GitleaksCondition.And)
			{
				return (allowlist.Regexes.Count == 0 || regex) && (_stopwords.Length == 0 || stopword);
			}

			return regex || stopword;
		}

		private string Target(SecretCandidate candidate)
		{
			return allowlist.Target switch
			{
				GitleaksTarget.Match => candidate.Match.Value,
				GitleaksTarget.Line => LineOf(candidate.Input, candidate.Match),
				_ => candidate.Value,
			};
		}

		/// <summary>The lines the match starts and ends on, as upstream reports a finding's line.</summary>
		private static string LineOf(string input, Match match)
		{
			int start = match.Index == 0 ? 0 : input.LastIndexOf('\n', match.Index - 1) + 1;
			int end = input.IndexOfAny(['\n', '\r'], match.Index + match.Length);
			return input[start..(end < 0 ? input.Length : end)];
		}
	}
}
