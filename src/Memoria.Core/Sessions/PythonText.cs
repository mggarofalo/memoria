using System.Buffers;

namespace Memoria.Core.Sessions;

/// <summary>
/// The Python string semantics the exporters depend on. They differ from .NET's in ways that
/// change output: Python counts and sorts by code point where .NET uses UTF-16 units, and
/// treats more characters as whitespace and as line breaks.
/// </summary>
internal static class PythonText
{
	/// <summary>Python's <c>str.isspace</c>. <see cref="char.IsWhiteSpace(char)"/> misses U+001C to U+001F.</summary>
	private static readonly SearchValues<char> s_space = SearchValues.Create(Chars(
		0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x85, 0xA0, 0x1680,
		0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A,
		0x2028, 0x2029, 0x202F, 0x205F, 0x3000));

	/// <summary>The characters <c>str.splitlines</c> breaks on.</summary>
	private static readonly SearchValues<char> s_lineBreak = SearchValues.Create(Chars(0x0A, 0x0D, 0x0B, 0x0C, 0x1C, 0x1D, 0x1E, 0x85, 0x2028, 0x2029));

	private static char[] Chars(params int[] codes)
	{
		return [.. codes.Select(static code => (char)code)];
	}

	public static string Strip(string text)
	{
		ReadOnlySpan<char> span = text;
		int start = span.IndexOfAnyExcept(s_space);
		return start < 0 ? "" : text[start..(span.LastIndexOfAnyExcept(s_space) + 1)];
	}

	public static int CodePointLength(string text)
	{
		int count = text.Length;
		for (int i = 0; i < text.Length - 1; i++)
		{
			if (char.IsSurrogatePair(text[i], text[i + 1]))
			{
				count--;
				i++;
			}
		}
		return count;
	}

	/// <summary><c>text[:count]</c>, which never cuts a surrogate pair in half.</summary>
	public static string Prefix(string text, int count)
	{
		int index = 0;
		for (int taken = 0; taken < count && index < text.Length; taken++)
		{
			index += index + 1 < text.Length && char.IsSurrogatePair(text[index], text[index + 1]) ? 2 : 1;
		}
		return text[..index];
	}

	/// <summary><c>text.splitlines()[0]</c>.</summary>
	public static string FirstSplitLine(string text)
	{
		int index = text.AsSpan().IndexOfAny(s_lineBreak);
		return index < 0 ? text : text[..index];
	}

	/// <summary>Orders by code point, which ordinal comparison gets wrong for astral characters against U+E000 and above.</summary>
	public static int CompareCodePoints(string left, string right)
	{
		int shared = Math.Min(left.Length, right.Length);
		for (int i = 0; i < shared; i++)
		{
			if (left[i] != right[i])
			{
				return CodeUnitRank(left[i]) - CodeUnitRank(right[i]);
			}
		}
		return left.Length - right.Length;
	}

	/// <summary>Moves surrogates above U+E000 to U+FFFF so UTF-16 units sort in code point order.</summary>
	private static int CodeUnitRank(char c)
	{
		if (c >= 0xE000)
		{
			return c - 0x800;
		}
		return c >= 0xD800 ? c + 0x2000 : c;
	}
}
