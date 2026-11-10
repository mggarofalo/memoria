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

	private static readonly (GitleaksRuleSpec Spec, RedactionRule Rule)[] s_builtIn = BuildPairs(GitleaksRuleData.BuiltIn);

	/// <summary>The built-in rules, in upstream's order.</summary>
	public static IReadOnlyList<RedactionRule> All { get; } = [.. s_builtIn.Select(static pair => pair.Rule)];

	/// <summary>
	/// The rules that know a secret by its own format, which may look inside decoded text. A rule made from upstream's assignment
	/// template, a keyword then <c>=</c> or <c>:</c> then a value, knows it by what surrounds it, so it stays out: percent-encoded
	/// prose saying <c>asana task: 1203456789012345</c> is not a token.
	/// </summary>
	public static IReadOnlyList<RedactionRule> SelfEvident { get; } = [.. s_builtIn.Where(static pair => !IsAssignmentTemplate(pair.Spec)).Select(static pair => pair.Rule)];

	public static IReadOnlyList<RedactionRule> Build(GitleaksRuleData data)
	{
		return [.. BuildPairs(data).Select(static pair => pair.Rule)];
	}

	/// <summary>Upstream writes every assignment rule from one template, which starts with up to 50 name characters before the keyword.</summary>
	public static bool IsAssignmentTemplate(GitleaksRuleSpec spec)
	{
		return spec.UpstreamPattern.Contains(@"[\w.-]{0,50}?(?:", StringComparison.Ordinal);
	}

	private static (GitleaksRuleSpec Spec, RedactionRule Rule)[] BuildPairs(GitleaksRuleData data)
	{
		CompiledAllowlist global = new(data.GlobalAllowlist);
		return [.. data.Rules.Select(spec => (spec, Rule(spec, global)))];
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
	/// Letters and digits glued to the end of a match go with it. Some upstream patterns have no boundary after a fixed length, so
	/// <c>flutterwave-encryption-key</c> matches the head of a longer secret key, and replacing only the head would leave the rest.
	/// One <c>-</c> or <c>_</c> and a short suffix after the run go too, as the <c>-X</c> that ends that key, but no more, so a
	/// token followed by <c>-backup-copy</c> keeps its words.
	/// </summary>
	private static int ThroughGluedText(SecretCandidate candidate)
	{
		string input = candidate.Input;
		int end = PaddingEnd(input, AlphanumericEnd(input, candidate.End));
		if (end == candidate.End && EndsWithSentencePeriod(input, candidate.Secret.Index, end))
		{
			return end - 1;
		}

		return GluedSuffixEnd(input, end);
	}

	/// <summary>Base64 padding goes with its token: a pattern's own ending can match an <c>=</c> too, and would leave the last one behind.</summary>
	private static int PaddingEnd(string input, int end)
	{
		for (int pad = 0; pad < MaxPadding && end < input.Length && input[end] == '='; pad++)
		{
			end++;
		}

		return end;
	}

	/// <summary>A period that ends a sentence is not the token's, though some alphabets allow one.</summary>
	private static bool EndsWithSentencePeriod(string input, int start, int end)
	{
		return end - 1 > start && input[end - 1] == '.' && (end == input.Length || !char.IsAsciiLetterOrDigit(input[end]));
	}

	private static int GluedSuffixEnd(string input, int end)
	{
		if (end + 1 >= input.Length || input[end] is not ('-' or '_'))
		{
			return end;
		}

		int suffixEnd = AlphanumericEnd(input, end + 1);
		bool shortSuffix = suffixEnd - end - 1 is >= 1 and <= MaxGluedSuffix && (suffixEnd == input.Length || input[suffixEnd] is not ('-' or '_'));
		return shortSuffix ? suffixEnd : end;
	}

	private const int MaxGluedSuffix = 4;

	private const int MaxPadding = 3;

	private static int AlphanumericEnd(string input, int start)
	{
		int end = start;
		while (end < input.Length && char.IsAsciiLetterOrDigit(input[end]))
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
