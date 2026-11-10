using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction.Gitleaks;

/// <summary>
/// One synthetic case per imported rule, written from the rule's own pattern by <see cref="RegexSampler"/> from a fixed seed. A case is
/// kept only when the rule itself finds exactly its secret in it, so it is a real positive for that rule. The negative puts a mask of
/// the same length in the secret's place, which no rule should take.
/// </summary>
public static class ImportedCases
{
	private const int Seed = 20261109;
	private const int Attempts = 400;

	/// <summary>A sampled match: the text before the secret, the secret, and the text after it, all from the rule's pattern.</summary>
	public sealed record Form(string Rule, string Before, string Secret, string After)
	{
		public string Text => Before + Secret + After;

		public string Negative => Before + new string('x', Secret.Length) + After;

		public override string ToString() => Rule;
	}

	private static readonly Lazy<(Form[] Forms, string[] Unsampled)> s_built = new(Build);

	public static IReadOnlyList<Form> Forms => s_built.Value.Forms;

	/// <summary>Imported rules the sampler could not write a case for. Each needs a hand-written case, so this must be empty.</summary>
	public static IReadOnlyList<string> Unsampled => s_built.Value.Unsampled;

	public static TheoryData<Form> All()
	{
		TheoryData<Form> data = [];
		foreach (Form form in Forms)
		{
			data.Add(form);
		}

		return data;
	}

	private static (Form[] Forms, string[] Unsampled) Build()
	{
		List<Form> forms = [];
		List<string> unsampled = [];
		foreach (RedactionRule rule in GitleaksRules.All)
		{
			if (Sample(rule) is { } form)
			{
				forms.Add(form);
			}
			else
			{
				unsampled.Add(rule.Name);
			}
		}

		return ([.. forms], [.. unsampled]);
	}

	private static Form? Sample(RedactionRule rule)
	{
		for (int attempt = 0; attempt < Attempts; attempt++)
		{
			Random random = new(TokenShapes.CellSeed(Seed + attempt, new TokenSpec(rule.Name, "", ""), TokenShape.Random));
			string text = RegexSampler.Sample(rule.Pattern.ToString(), random);

			// A rule fires only beside one of its keywords, which some patterns do not hold, so the keyword goes first.
			text = rule.Keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ? text : rule.Keywords[0] + " " + text;
			if (text.Contains("[REDACTED", StringComparison.Ordinal) || !rule.TryFindNext(text, 0, out int start, out int end))
			{
				continue;
			}

			return new Form(rule.Name, text[..start], text[start..end], text[end..]);
		}

		return null;
	}
}
