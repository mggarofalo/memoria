using System.Text;
using System.Text.RegularExpressions;

namespace Memoria.Core.Redaction;

/// <summary>
/// Gitleaks's default rules, from <see cref="GitleaksRuleData"/>, as ordinary rules of this engine. Each runs on the same text as
/// every other rule, with the same budgets, and replaces only its <c>secret</c> group. It fires only when the text holds one of its
/// keywords. A candidate is rejected as upstream rejects it: its Shannon entropy is at or below the rule's threshold, or an allowlist
/// or a stopword covers it. It is also rejected when it is one character over and over, a mask. Names are <c>gitleaks-</c> and the
/// upstream id, so a count says where a rule came from.
/// </summary>
internal static class GitleaksRules
{
	public const string Prefix = "gitleaks-";

	private static readonly (GitleaksRuleSpec Spec, RedactionRule Rule)[] s_builtIn = BuildPairs(GitleaksRuleData.BuiltIn);

	/// <summary>
	/// The built-in rules: those that know a token by its own format first, then those that know a value by the name before it, each in
	/// upstream's order, except that a routable GitLab token's rule goes before the plain one's. A rule of the second kind can take the
	/// head of a longer token that one of the first knows whole, as <c>twitter-api-key</c> takes 25 characters of a bearer token after
	/// <c>twitter:</c>.
	/// </summary>
	public static IReadOnlyList<RedactionRule> All { get; } = [.. s_builtIn.Select(static pair => pair.Rule)];

	/// <summary>
	/// The rules that know a secret by its own format. A rule made from upstream's assignment template, a keyword then <c>=</c> or
	/// <c>:</c> then a value, knows it by what surrounds it, so it is not one of these.
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
		// A routable GitLab token is the plain one with a dotted part after it, so its rule goes first, or the plain one would take only its head.
		IEnumerable<GitleaksRuleSpec> formats = data.Rules.Where(static spec => !IsAssignmentTemplate(spec)).OrderBy(static spec => spec.Id.EndsWith("-routable", StringComparison.Ordinal) ? 0 : 1);
		IEnumerable<GitleaksRuleSpec> ordered = formats.Concat(data.Rules.Where(IsAssignmentTemplate));
		return [.. ordered.Select(spec => (spec, Rule(spec, global)))];
	}

	private static RedactionRule Rule(GitleaksRuleSpec spec, CompiledAllowlist global)
	{
		CompiledAllowlist[] own = [.. spec.Allowlists.Select(static allowlist => new CompiledAllowlist(allowlist))];
		string secret = SecretParts(spec.Pattern);
		string separators = SeparatorsIn(secret);
		bool fixedToken = EndsAtFixedLength(secret) && !IsAssignmentTemplate(spec);
		bool padded = secret.Contains("={0,", StringComparison.Ordinal);
		string pattern = IsAssignmentTemplate(spec) || separators.Contains('_', StringComparison.Ordinal) ? spec.Pattern : EndingAtUnderscore(spec.Pattern);
		bool underscoreEnds = !string.Equals(pattern, spec.Pattern, StringComparison.Ordinal);
		return new RedactionRule(
			Prefix + spec.Id,
			null,
			candidate => Accept(candidate, spec.Entropy, global, own),
			extend: candidate => ThroughGluedText(candidate, separators, new Ending(fixedToken, padded, underscoreEnds)),
			keywords: spec.Keywords,
			backstop: true,
			buildPattern: () => RedactionRules.Pattern(pattern));
	}

	/// <summary>The ending the translator draws for upstream's <c>\b</c> or common ending: any character but a letter, a digit or <c>_</c>.</summary>
	private const string WordEnd = @"(?:[^0-9A-Za-z_]|\z)";

	/// <summary>
	/// A token whose alphabet has no <c>_</c> ends at one, as in <c>TOKEN=rdme_…_backup</c> or a file named <c>rdme_…_old.txt</c>. Upstream's
	/// <c>\b</c> counts <c>_</c> as a letter, so it found no token there. Where the alphabet holds <c>_</c>, the run may go on, so it stays.
	/// A rule known by the name before its value keeps upstream's ending, since its value is judged only by that name.
	/// </summary>
	public static string EndingAtUnderscore(string pattern)
	{
		return pattern.EndsWith(WordEnd, StringComparison.Ordinal) ? pattern[..^WordEnd.Length] + @"(?:[^0-9A-Za-z]|\z)" : pattern;
	}

	/// <summary>
	/// What a rule's format says about where its token ends. <paramref name="FixedToken"/>: a format known by itself, at an exact count.
	/// <paramref name="Padded"/>: a format that ends in optional <c>=</c> padding. <paramref name="UnderscoreEnds"/>: the rule now ends at an underscore.
	/// </summary>
	private readonly record struct Ending(bool FixedToken, bool Padded, bool UnderscoreEnds);

	/// <summary>
	/// True when the secret ends with an exact count, as <c>[a-z0-9]{64}</c> does: the format has no more to it, so what follows a separator
	/// is not the rest of it. An ending such as <c>{32,64}</c> or <c>+</c> can be the head of a longer token.
	/// </summary>
	public static bool EndsAtFixedLength(string secret)
	{
		// With alternatives, the last one's count says nothing of the others, as in vault's hvs.{90,120}|s.{24}.
		if (secret.Contains('|', StringComparison.Ordinal))
		{
			return false;
		}

		string body = secret.TrimEnd(')');
		int open = body.LastIndexOf('{');
		return body.EndsWith('}') && open > 0 && body[open - 1] != '\\' && body.AsSpan(open + 1, body.Length - open - 2).IndexOfAnyExceptInRange('0', '9') < 0 && body.Length - open > 2;
	}

	/// <summary>
	/// Letters and digits glued to the end of a match go with it. Some upstream patterns have no boundary after a fixed length, so
	/// <c>flutterwave-encryption-key</c> matches the head of a longer secret key, and replacing only the head would leave the rest.
	/// One <c>-</c> or <c>_</c> and a short suffix after the run go too, as the <c>-X</c> that ends that key, but no more, so a
	/// token followed by <c>-backup-copy</c> keeps its words.
	/// </summary>
	private static int ThroughGluedText(SecretCandidate candidate, string separators, Ending ending)
	{
		string input = candidate.Input;
		int glued = AlphanumericEnd(input, candidate.End);
		int padded = PaddingEnd(input, glued);

		// A token already ended by its format's padding, or by its exact count, takes nothing encoded after it either.
		bool ended = ending.FixedToken || (ending.Padded && input[padded - 1] == '=');
		int end = ended ? padded : PaddingEnd(input, EncodedRunEnd(input, glued));
		if (end == candidate.End && EndsWithSentencePeriod(input, candidate.Secret.Index, end))
		{
			return end - 1;
		}

		return AfterToken(input, end, separators, ending);
	}

	/// <summary>
	/// Padding ends a token, where its format has padding, and so does a format's exact length, so text after a separator is not more of
	/// it. Where an underscore ends the token, what follows it is a name's next word, as <c>_old</c> in <c>rdme_…_old.txt</c>.
	/// </summary>
	private static int AfterToken(string input, int end, string separators, Ending ending)
	{
		bool complete = ending.FixedToken || (ending.Padded && input[end - 1] == '=');
		int? rest = complete ? null : RandomContinuationEnd(input, end, separators);
		bool atUnderscore = ending.UnderscoreEnds && end < input.Length && input[end] == '_';
		return rest ?? (atUnderscore ? end : GluedSuffixEnd(input, end));
	}

	/// <summary>
	/// A token longer than its rule allows, with separators in its alphabet, can stop the match at an inner <c>-</c>, <c>.</c> or
	/// <c>%</c>. When what follows the separator still reads as random, it is the rest of the token, and goes too. Words after a
	/// separator stay, so <c>-backup-copy</c> is kept. The separator must be one the secret's own alphabet holds: the patterns end
	/// greedily, so a match that stops at such a character was cut short. A complete <c>rdme_</c> token, whose alphabet has no
	/// <c>/</c>, keeps the path after it.
	/// </summary>
	private static int? RandomContinuationEnd(string input, int end, string separators)
	{
		if (end >= input.Length || !separators.Contains(input[end], StringComparison.Ordinal))
		{
			return null;
		}

		int runEnd = end;
		while (runEnd < input.Length && (char.IsAsciiLetterOrDigit(input[runEnd]) || input[runEnd] is '-' or '.' or '%' or '+' or '/' or '_' or '='))
		{
			runEnd++;
		}

		ReadOnlySpan<char> rest = input.AsSpan(end + 1, runEnd - end - 1);
		foreach (Range piece in rest.SplitAny("-.%+/_="))
		{
			if (rest[piece].Length >= MinRandomPiece && LooksRandom(rest[piece]))
			{
				return runEnd;
			}
		}

		return null;
	}

	private const int MinRandomPiece = 8;

	/// <summary>A piece of a token, not a word: it mixes letters and digits. Words after a separator, such as <c>-backup-copy</c>, stay.</summary>
	private static bool LooksRandom(ReadOnlySpan<char> piece)
	{
		return piece.IndexOfAnyInRange('0', '9') >= 0 && (piece.IndexOfAnyInRange('a', 'z') >= 0 || piece.IndexOfAnyInRange('A', 'Z') >= 0);
	}

	private const string Separators = "-.%+/_";

	/// <summary>Every <c>secret</c> group of the pattern, one per alternative, or the whole pattern when the whole match is the secret.</summary>
	private static string SecretParts(string pattern)
	{
		StringBuilder parts = new();
		for (int start = pattern.IndexOf(SecretGroup, StringComparison.Ordinal); start >= 0; start = pattern.IndexOf(SecretGroup, start + 1, StringComparison.Ordinal))
		{
			parts.Append(SecretPart(pattern, start));
		}

		return parts.Length == 0 ? pattern : parts.ToString();
	}

	private const string SecretGroup = "(?<secret>";

	/// <summary>The group that opens at <paramref name="start"/>, through its closing parenthesis.</summary>
	private static string SecretPart(string pattern, int start)
	{
		int depth = 0;
		bool inClass = false;
		for (int i = start; i < pattern.Length; i++)
		{
			char c = pattern[i];
			if (c == '\\')
			{
				i++;
			}
			else if (inClass || c == '[')
			{
				inClass = c != ']' || !inClass;
			}
			else
			{
				depth += c == '(' ? 1 : c == ')' ? -1 : 0;
				if (depth == 0)
				{
					return pattern[start..(i + 1)];
				}
			}
		}

		return pattern[start..];
	}

	/// <summary>
	/// The separators a pattern's character classes hold as literals: an escaped one, a <c>-</c> first or last, or any other
	/// unescaped. The translator writes every class in ASCII, so <c>\w</c> has already become <c>[0-9A-Za-z_]</c>.
	/// </summary>
	private static string SeparatorsIn(string pattern)
	{
		HashSet<char> found = [];
		int classStart = -1;
		for (int i = 0; i < pattern.Length; i++)
		{
			char c = pattern[i];
			if (c == '\\' && i + 1 < pattern.Length)
			{
				AddIf(found, classStart >= 0, pattern[++i]);
			}
			else if (classStart < 0)
			{
				classStart = c == '[' ? i : -1;
			}
			else
			{
				classStart = ClassCharacter(found, pattern, i, classStart);
			}
		}

		return string.Concat(found.Order());
	}

	/// <summary>Reads one unescaped character inside a class, and returns where the class started, or -1 once it closes.</summary>
	private static int ClassCharacter(HashSet<char> found, string pattern, int i, int classStart)
	{
		char c = pattern[i];
		bool first = i == classStart + 1 || (i == classStart + 2 && pattern[classStart + 1] == '^');
		if (c == ']' && !first)
		{
			return -1;
		}

		bool literalDash = c == '-' && (first || (i + 1 < pattern.Length && pattern[i + 1] == ']'));
		AddIf(found, c != '-' || literalDash, c);
		return classStart;
	}

	private static void AddIf(HashSet<char> found, bool condition, char c)
	{
		if (condition && Separators.Contains(c, StringComparison.Ordinal))
		{
			found.Add(c);
		}
	}

	/// <summary>
	/// A URL-encoded <c>+</c>, <c>/</c> or <c>=</c> glued to more of the run is more of the same token, as in a Twitter bearer token's
	/// <c>…0%2BuSeid%2BULv…</c>. Cut at the escape, the rest of the token would be left for a rule that can no longer see its start.
	/// </summary>
	private static int EncodedRunEnd(string input, int end)
	{
		int runEnd = end;
		bool random = false;
		while (runEnd + 3 < input.Length && input[runEnd] == '%' && IsEncodedBase64Symbol(input[runEnd + 1], input[runEnd + 2]) && char.IsAsciiLetterOrDigit(input[runEnd + 3]))
		{
			int pieceEnd = AlphanumericEnd(input, runEnd + 3);
			random |= pieceEnd - runEnd - 3 >= MinRandomPiece && LooksRandom(input.AsSpan(runEnd + 3, pieceEnd - runEnd - 3));
			runEnd = pieceEnd;
		}

		// A path or words after an encoded slash, as %2Fhome%2Fuser, are not more of a token: some piece of the rest must read as random.
		return random ? runEnd : end;
	}

	private static bool IsEncodedBase64Symbol(char high, char low)
	{
		return (high == '2' && low is 'B' or 'b' or 'F' or 'f') || (high == '3' && low is 'D' or 'd');
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
		if (IsMask(secret) || (entropy > 0 && ShannonEntropy(secret) <= entropy))
		{
			return false;
		}

		return !global.Allows(candidate) && !own.Any(allowlist => allowlist.Allows(candidate));
	}

	/// <summary>One character over and over, as documentation writes <c>xxxx</c> where a secret goes. A rule with no entropy threshold would take it.</summary>
	private static bool IsMask(string secret)
	{
		return secret.AsSpan().IndexOfAnyExcept(secret[0]) < 0;
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
