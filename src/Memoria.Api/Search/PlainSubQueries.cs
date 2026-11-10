using System.Text;
using System.Text.RegularExpressions;
using Memoria.Core.Sidecar;

namespace Memoria.Api.Search;

/// <summary>
/// A plain query as typed sub-queries, so QMD searches it without expanding it: one
/// lexical and one vector sub-query on the text itself, which is what QMD's hybrid search
/// runs besides its expansions. Each is kept only if QMD's own query checks accept it,
/// because those apply to typed sub-queries and never to a plain query.
/// </summary>
/// <remarks>
/// These are the rules of the sidecar's <c>plainQueries</c> (<c>sidecar/src/llm/degraded-search.ts</c>),
/// which it runs on QMD's real <c>validateLexQuery</c> and <c>validateSemanticQuery</c>.
/// <c>sidecar/test/plain-query-cases.json</c> holds both to the same answers. QMD and the
/// sidecar are JavaScript, so whitespace here is JavaScript's <c>\s</c> and a word character
/// is ASCII, as JavaScript's <c>\w</c> is.
/// </remarks>
public static partial class PlainSubQueries
{
	/// <summary>JavaScript's <c>\s</c>. .NET's differs at U+0085 and U+FEFF.</summary>
	private const string SpaceClass = "\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff";

	[GeneratedRegex("[" + SpaceClass + "]+", RegexOptions.CultureInvariant)]
	private static partial Regex Spaces();

	/// <summary>QMD's <c>validateSemanticQuery</c>: negation at the start of a token. Hyphenated words pass.</summary>
	[GeneratedRegex("(^|[" + SpaceClass + "])-[A-Za-z0-9_\"]", RegexOptions.CultureInvariant)]
	private static partial Regex Negation();

	public static IReadOnlyList<SidecarSubQuery> For(string query)
	{
		ArgumentNullException.ThrowIfNull(query);
		string text = Collapse(query);
		if (text.Length == 0)
		{
			return [];
		}

		List<SidecarSubQuery> searches = [];
		if (IsLexical(text))
		{
			searches.Add(new SidecarSubQuery("lex", text));
		}

		if (!Negation().IsMatch(text))
		{
			searches.Add(new SidecarSubQuery("vec", text));
		}

		if (searches.Count > 0)
		{
			return searches;
		}

		string words = Collapse(WordsOnly(text));
		return words.Length == 0 ? [] : [new SidecarSubQuery("lex", words)];
	}

	/// <summary>QMD refuses a typed search with no letter or number, as the sidecar does a plain one.</summary>
	public static bool HasLetterOrDigit(string query)
	{
		ArgumentNullException.ThrowIfNull(query);
		foreach (Rune rune in query.EnumerateRunes())
		{
			if (Rune.IsLetter(rune) || Rune.IsNumber(rune))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>QMD's <c>validateLexQuery</c>: one line, and balanced double quotes. Collapsed text is one line.</summary>
	private static bool IsLexical(string text)
	{
		return text.Count(static c => c == '"') % 2 == 0;
	}

	private static string Collapse(string text)
	{
		return Spaces().Replace(text, " ").Trim(' ');
	}

	/// <summary>Letters, numbers and spaces kept, everything else a space, by code point as JavaScript's <c>u</c> flag reads it.</summary>
	private static string WordsOnly(string text)
	{
		StringBuilder kept = new(text.Length);
		foreach (Rune rune in text.EnumerateRunes())
		{
			if (Rune.IsLetter(rune) || Rune.IsNumber(rune) || rune.Value == ' ')
			{
				kept.Append(rune.ToString());
			}
			else
			{
				kept.Append(' ');
			}
		}

		return kept.ToString();
	}
}
