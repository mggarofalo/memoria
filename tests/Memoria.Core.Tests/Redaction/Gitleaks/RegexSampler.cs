using System.Text;

namespace Memoria.Core.Tests.Redaction.Gitleaks;

/// <summary>
/// Writes a random string a translated gitleaks pattern matches, so every imported rule has a synthetic secret of its own real
/// shape. It reads the subset of .NET syntax <see cref="Re2Translator"/> emits. Inside the <c>secret</c> group it gives open
/// repeats a realistic length; elsewhere it writes the least the pattern needs, so the context stays short. Anchors and
/// boundaries are not planned for: the caller checks the result against the pattern and tries another seed.
/// </summary>
public sealed class RegexSampler
{
	private const string Printable = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.:/+=@ ";

	private static readonly Dictionary<char, char> s_escapes = new() { ['t'] = '\t', ['n'] = '\n', ['f'] = '\f', ['r'] = '\r', ['v'] = '\v' };

	private readonly string _pattern;
	private readonly bool[] _inert;
	private readonly Random _random;
	private int _position;
	private int _secretDepth;

	private RegexSampler(string pattern, Random random)
	{
		_pattern = pattern;
		_inert = Inert(pattern);
		_random = random;
	}

	/// <summary>A pattern with no <c>secret</c> group is a secret throughout, so all of it gets realistic lengths.</summary>
	public static string Sample(string pattern, Random random)
	{
		RegexSampler sampler = new(pattern, random) { _secretDepth = pattern.Contains("(?<secret>", StringComparison.Ordinal) ? 0 : 1 };
		return sampler.Alternation();
	}

	private string Alternation()
	{
		List<int> starts = [_position];
		int depth = 0;
		for (int i = _position; i < _pattern.Length && depth >= 0; i++)
		{
			depth += Depth(i);
			if (depth == 0 && _pattern[i] == '|' && !_inert[i])
			{
				starts.Add(i + 1);
			}
		}

		_position = starts[_random.Next(starts.Count)];
		return Sequence();
	}

	/// <summary>+1 for an opening paren and -1 for a closing one, outside classes and escapes.</summary>
	private int Depth(int i)
	{
		if (_inert[i])
		{
			return 0;
		}

		return _pattern[i] switch { '(' => 1, ')' => -1, _ => 0 };
	}

	/// <summary>Marks every character that is not syntax: escapes and everything in a class, brackets included.</summary>
	private static bool[] Inert(string pattern)
	{
		bool[] inert = new bool[pattern.Length];
		for (int i = 0; i < pattern.Length; i++)
		{
			if (pattern[i] == '\\')
			{
				inert[i] = true;
				inert[Math.Min(i + 1, pattern.Length - 1)] = true;
				i++;
			}
			else if (pattern[i] == '[')
			{
				int end = ClassEnd(pattern, i);
				Array.Fill(inert, true, i, end - i + 1);
				i = end;
			}
		}

		return inert;
	}

	/// <summary>The index of the <c>]</c> closing the class opened at <paramref name="open"/>. A <c>]</c> first in the class is a member.</summary>
	private static int ClassEnd(string pattern, int open)
	{
		int i = open + 1;
		i += i < pattern.Length && pattern[i] == '^' ? 1 : 0;
		i += i < pattern.Length && pattern[i] == ']' ? 1 : 0;
		while (i < pattern.Length && pattern[i] != ']')
		{
			i += pattern[i] == '\\' ? 2 : 1;
		}

		return i < pattern.Length ? i : throw new FormatException("Unclosed class in " + pattern);
	}

	private string Sequence()
	{
		StringBuilder output = new();
		while (_position < _pattern.Length && _pattern[_position] is not ('|' or ')'))
		{
			Func<string> atom = Atom();
			int times = Repeat();
			for (int i = 0; i < times; i++)
			{
				output.Append(atom());
			}
		}

		SkipToGroupEnd();
		return output.ToString();
	}

	/// <summary>After the chosen branch, the rest of the alternation is skipped, up to the group's close.</summary>
	private void SkipToGroupEnd()
	{
		int depth = 0;
		while (_position < _pattern.Length)
		{
			depth += Depth(_position);
			if (depth < 0)
			{
				return;
			}

			_position++;
		}
	}

	/// <summary>Reads one atom and returns a writer for it, called once per repetition.</summary>
	private Func<string> Atom()
	{
		char c = _pattern[_position];
		return c switch
		{
			'(' => Group(),
			'[' => Class(),
			'\\' => Escape(),
			// A dot is most often a literal one written loosely, as in a host name, so it is written as a dot.
			'.' => Single(static () => "."),
			'^' or '$' => Single(static () => ""),
			_ => Single(() => c.ToString()),
		};
	}

	private Func<string> Single(Func<string> write)
	{
		_position++;
		return write;
	}

	private Func<string> Escape()
	{
		char next = _pattern[_position + 1];
		_position += 2;
		if (next is 'b' or 'B' or 'z' or 'A' or 'Z' or 'G')
		{
			return static () => "";
		}

		string literal = Literal(next);
		return () => literal;
	}

	private string Literal(char next)
	{
		if (next == 'x')
		{
			string hex = _pattern.Substring(_position, 2);
			_position += 2;
			return ((char)Convert.ToInt32(hex, 16)).ToString();
		}

		if (next == 'u')
		{
			string hex = _pattern.Substring(_position, 4);
			_position += 4;
			return ((char)Convert.ToInt32(hex, 16)).ToString();
		}

		return s_escapes.TryGetValue(next, out char control) ? control.ToString() : next.ToString();
	}

	private Func<string> Group()
	{
		int close = MatchingClose(_position);
		string inner = _pattern[(_position + 1)..close];
		bool secret = inner.StartsWith("?<secret>", StringComparison.Ordinal);
		string body = Body(inner);
		_position = close + 1;
		return () => SampleGroup(body, secret);
	}

	/// <summary>The group's content with its opener removed. A flag group such as <c>(?i)</c> has none.</summary>
	private static string Body(string inner)
	{
		if (!inner.StartsWith('?'))
		{
			return inner;
		}

		int colon = inner.IndexOfAny([':', '>', ')']);
		bool flagsOnly = colon < 0 || inner.StartsWith("?i", StringComparison.Ordinal) && !inner.Contains(':', StringComparison.Ordinal);
		return flagsOnly ? "" : inner[(colon + 1)..];
	}

	private string SampleGroup(string body, bool secret)
	{
		RegexSampler inner = new(body, _random) { _secretDepth = _secretDepth + (secret ? 1 : 0) };
		return inner.Alternation();
	}

	private int MatchingClose(int open)
	{
		int depth = 0;
		for (int i = open; i < _pattern.Length; i++)
		{
			depth += Depth(i);
			if (depth == 0)
			{
				return i;
			}
		}

		throw new FormatException("Unclosed group in " + _pattern);
	}

	private Func<string> Class()
	{
		int end = ClassEnd(_pattern, _position);
		string body = _pattern[(_position + 1)..end];
		_position = end + 1;
		string chars = ClassChars(body);
		return () => Pick(chars);
	}

	private static string ClassChars(string body)
	{
		if (body is "\\s\\S")
		{
			return "abcdefghijklmnopqrstuvwxyz0123456789";
		}

		bool negated = body.StartsWith('^');
		HashSet<char> chars = Members(negated ? body[1..] : body);
		string result = negated ? string.Concat(Printable.Where(c => !chars.Contains(c))) : string.Concat(chars.Where(static c => c is >= ' ' and <= '~'));
		return result.Length > 0 ? result : string.Concat(chars);
	}

	private static HashSet<char> Members(string body)
	{
		List<char> items = [];
		for (int i = 0; i < body.Length; i++)
		{
			char c = body[i];
			if (c == '\\')
			{
				i++;
				c = s_escapes.TryGetValue(body[i], out char control) ? control : HexAt(body, ref i);
			}

			items.Add(c);
		}

		HashSet<char> members = [];
		for (int i = 0; i < items.Count; i++)
		{
			bool range = i + 2 < items.Count && items[i + 1] == '-' && !IsLiteralDash(body, i + 1);
			members.UnionWith(range ? Range(items[i], items[i + 2]) : [items[i]]);
			i += range ? 2 : 0;
		}

		return members;
	}

	/// <summary>The escape at <paramref name="i"/>: <c>\x60</c> or <c>`</c> as its character, or the character itself.</summary>
	private static char HexAt(string body, ref int i)
	{
		int length = body[i] switch { 'x' => 2, 'u' => 4, _ => 0 };
		if (length == 0)
		{
			return body[i];
		}

		char value = (char)Convert.ToInt32(body.Substring(i + 1, length), 16);
		i += length;
		return value;
	}

	/// <summary>A dash written as <c>\-</c> is a member, not a range. Escapes are resolved by now, so this rereads the source.</summary>
	private static bool IsLiteralDash(string body, int item)
	{
		int count = 0;
		for (int i = 0; i < body.Length; i++)
		{
			bool escaped = body[i] == '\\';
			if (count == item)
			{
				return escaped;
			}

			i += escaped ? (body[i + 1] is 'x' ? 3 : body[i + 1] is 'u' ? 5 : 1) : 0;
			count++;
		}

		return false;
	}

	private static IEnumerable<char> Range(char from, char to)
	{
		for (char c = from; c <= to; c++)
		{
			yield return c;
		}
	}

	/// <summary>How many times the atom just read is written, from the quantifier after it, which is consumed.</summary>
	private int Repeat()
	{
		if (_position >= _pattern.Length)
		{
			return 1;
		}

		(int min, int max) = Quantifier();
		if (_position < _pattern.Length && _pattern[_position] == '?' && (min, max) != (1, 1))
		{
			_position++;
		}

		return Count(min, max);
	}

	private (int Min, int Max) Quantifier()
	{
		char c = _pattern[_position];
		switch (c)
		{
			case '?':
				_position++;
				return (0, 1);
			case '*':
				_position++;
				return (0, int.MaxValue);
			case '+':
				_position++;
				return (1, int.MaxValue);
			case '{':
				return Braces();
			default:
				return (1, 1);
		}
	}

	private (int Min, int Max) Braces()
	{
		int close = _pattern.IndexOf('}', _position);
		string[] parts = _pattern[(_position + 1)..close].Split(',');
		_position = close + 1;
		int min = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
		if (parts.Length == 1)
		{
			return (min, min);
		}

		return (min, parts[1].Length == 0 ? int.MaxValue : int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
	}

	/// <summary>In the secret, an open repeat gets a realistic length. Elsewhere, the least the pattern allows, sometimes one more.</summary>
	private int Count(int min, int max)
	{
		if (min == max)
		{
			return min;
		}

		if (_secretDepth > 0)
		{
			int low = Math.Min(max, Math.Max(min, 16));
			int high = Math.Min(max, Math.Max(low, min + 24));
			return _random.Next(low, high == int.MaxValue ? high : high + 1);
		}

		return _random.Next(4) == 0 ? Math.Min(max, min + 1) : min;
	}

	private string Pick(string chars)
	{
		return chars[_random.Next(chars.Length)].ToString();
	}
}
