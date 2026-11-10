using System.Globalization;
using System.Text;

namespace Memoria.Core.Tests.Redaction.Gitleaks;

/// <summary>
/// Rewrites an RE2 pattern, as gitleaks writes them, into .NET syntax that means the same thing on ASCII text.
/// <list type="bullet">
/// <item><c>\d \w \s</c> and their negations become ASCII classes: .NET's would match any Unicode digit, letter or space.</item>
/// <item><c>$</c> becomes <c>\z</c>: RE2's <c>$</c> without <c>(?m)</c> is the end of the text, and .NET's also matches before a final newline.</item>
/// <item>POSIX classes such as <c>[[:alnum:]]</c> become ranges, and a literal <c>[</c> in a class is escaped so .NET never reads class subtraction.</item>
/// <item>The secret becomes the <c>secret</c> group. Upstream takes <c>secretGroup</c>, or else the first capture that matched: the one top-level
/// capture, or any of several in separate branches. Captures in sequence, as in <c>(x-){3}</c>, or under a quantifier at any depth, would
/// make the secret a fragment, so the whole match is the secret instead. Every other capture becomes non-capturing.</item>
/// <item>A <c>\b</c> that starts or ends the pattern beside the secret becomes a consuming ASCII boundary outside the secret,
/// <c>(?:^|[^0-9A-Za-z_])</c> or <c>(?:[^0-9A-Za-z_]|\z)</c>. .NET's <c>\b</c> is Unicode-aware, so a token touching a non-ASCII
/// letter, as in <c>éAKIA…</c>, would have no boundary and be missed. When the whole match is the secret, the secret group is drawn
/// inside such a boundary, and before a final terminator such as <c>(?:[^a-z]|\z)</c>, so neither neighbour is taken with it.</item>
/// <item>A <c>-</c> straight after an expanded shorthand or POSIX class in a class is escaped: <c>[\s-x]</c> is a space, a dash or an x.</item>
/// </list>
/// What it cannot express it refuses, so a rule is never imported with a different meaning: lookaround, <c>\Q</c>, <c>\p</c>, <c>\C</c>,
/// backreferences, the <c>U</c> and <c>m</c> flags, and a negated shorthand inside a class with anything else in it. Any other <c>\b</c>
/// stays as it is, and so is Unicode-aware.
/// </summary>
public sealed class Re2Translator
{
	private const string Digit = "0-9";
	private const string Word = "0-9A-Za-z_";
	private const string Space = "\\t\\n\\f\\r ";

	private static readonly Dictionary<char, string> s_outside = new()
	{
		['d'] = $"[{Digit}]",
		['D'] = $"[^{Digit}]",
		['w'] = $"[{Word}]",
		['W'] = $"[^{Word}]",
		['s'] = $"[{Space}]",
		['S'] = $"[^{Space}]",
	};

	private static readonly Dictionary<char, string> s_inside = new() { ['d'] = Digit, ['w'] = Word, ['s'] = Space };

	private static readonly Dictionary<char, string> s_negatedInside = new() { ['D'] = Digit, ['W'] = Word, ['S'] = Space };

	private static readonly Dictionary<string, string> s_posix = new(StringComparer.Ordinal)
	{
		["alnum"] = "0-9A-Za-z",
		["alpha"] = "A-Za-z",
		["digit"] = Digit,
		["lower"] = "a-z",
		["upper"] = "A-Z",
		["xdigit"] = "0-9A-Fa-f",
		["word"] = Word,
		["space"] = "\\t\\n\\v\\f\\r ",
		["punct"] = "!-/:-@\\[-`{-~",
	};

	private static readonly HashSet<char> s_unsupported = ['Q', 'E', 'p', 'P', 'C', '1', '2', '3', '4', '5', '6', '7', '8', '9'];

	private readonly string _pattern;
	private readonly List<string> _output = [];
	private readonly List<Capture> _captures = [];
	private readonly Stack<Frame> _frames = new();
	private readonly List<int> _flagTokens = [];
	private int _rootBranches;
	private int _position;
	private int _nodes;
	private (int Open, int Close, bool IsCapture, bool TopLevel) _lastGroup = (-1, -1, false, false);

	private Re2Translator(string pattern)
	{
		_pattern = pattern;
		_frames.Push(new Frame(0, IsCapture: false));
	}

	/// <summary>Translates a rule's pattern. <paramref name="secretGroup"/> is upstream's 1-based group number, or 0 for its default.</summary>
	public static string TranslateRule(string pattern, int secretGroup)
	{
		Re2Translator translator = Parse(pattern);
		HashSet<int> secret = translator.SecretCaptures(secretGroup);
		translator.DrawBoundaries(secret);
		return translator.Render(index => secret.Contains(index) ? "(?<secret>" : "(?:");
	}

	/// <summary>Translates an allowlist pattern, where no capture matters.</summary>
	public static string TranslatePlain(string pattern)
	{
		return Parse(pattern).Render(static _ => "(?:");
	}

	private static Re2Translator Parse(string pattern)
	{
		Re2Translator translator = new(pattern);
		translator.Run();
		return translator;
	}

	private void Run()
	{
		while (_position < _pattern.Length)
		{
			Step(_pattern[_position]);
		}

		if (_frames.Count != 1)
		{
			throw Unsupported("an unclosed group");
		}
	}

	private void Step(char c)
	{
		switch (c)
		{
			case '\\':
				_output.Add(EscapeOutside());
				break;
			case '[':
				_output.Add(CharacterClass());
				break;
			case '(':
				OpenGroup();
				break;
			case ')':
				CloseGroup();
				break;
			default:
				_output.Add(Plain(c));
				_position++;
				break;
		}
	}

	private string Plain(char c)
	{
		if (c == '|')
		{
			Frame top = _frames.Pop();
			_frames.Push(top with { Branch = top.Branch + 1 });
			_rootBranches += _frames.Count == 1 ? 1 : 0;
		}

		return c == '$' ? "\\z" : c.ToString();
	}

	private string EscapeOutside()
	{
		char next = Next(_position + 1);
		_position += 2;
		if (s_outside.TryGetValue(next, out string? replacement))
		{
			return replacement;
		}

		return next == 'x' ? Hex() : Escaped(next);
	}

	private string Escaped(char next)
	{
		if (s_unsupported.Contains(next))
		{
			throw Unsupported($"\\{next}");
		}

		return "\\" + next;
	}

	/// <summary><c>\x41</c> stays; RE2's <c>\x{41}</c> becomes .NET's <c>A</c>.</summary>
	private string Hex()
	{
		if (Next(_position) != '{')
		{
			string digits = _pattern.Substring(_position, 2);
			_position += 2;
			return "\\x" + digits;
		}

		int close = _pattern.IndexOf('}', _position);
		int value = int.Parse(_pattern.AsSpan(_position + 1, close - _position - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		_position = close + 1;
		if (value > 0xFFFF)
		{
			throw Unsupported("a code point above U+FFFF");
		}

		return value <= 0xFF ? $"\\x{value:X2}" : $"\\u{value:X4}";
	}

	private string CharacterClass()
	{
		ClassBody body = new();
		_position++;
		if (Next(_position) == '^')
		{
			body.Negated = true;
			_position++;
		}

		bool first = true;
		while (Next(_position) != ']' || first)
		{
			ClassItem(body);
			first = false;
		}

		_position++;
		return body.Render() ?? throw Unsupported("a negated shorthand mixed with other items in a class");
	}

	private void ClassItem(ClassBody body)
	{
		char c = Next(_position);
		if (c == '\\')
		{
			ClassEscape(body, Next(_position + 1));
			return;
		}

		if (c == '[' && Next(_position + 1) == ':')
		{
			body.Items.Append(Posix());
			EscapeDashAfterExpansion(body);
			return;
		}

		if (c == '[')
		{
			body.Items.Append("\\[");
			_position++;
			return;
		}

		body.Items.Append(c);
		_position++;
	}

	private void ClassEscape(ClassBody body, char next)
	{
		_position += 2;
		if (s_inside.TryGetValue(next, out string? inside))
		{
			body.Items.Append(inside);
			EscapeDashAfterExpansion(body);
		}
		else if (s_negatedInside.TryGetValue(next, out string? negated))
		{
			body.NegatedShorthands.Add(negated);
		}
		else
		{
			body.Items.Append(next == 'x' ? Hex() : Escaped(next));
		}
	}

	/// <summary>In RE2 a dash after a shorthand or a POSIX class is a dash. After the expansion it would start a range, so it is escaped.</summary>
	private void EscapeDashAfterExpansion(ClassBody body)
	{
		if (Next(_position) == '-')
		{
			body.Items.Append("\\-");
			_position++;
		}
	}

	private string Posix()
	{
		int close = _pattern.IndexOf(":]", _position, StringComparison.Ordinal);
		string name = close < 0 ? "" : _pattern[(_position + 2)..close];
		_position = close + 2;
		return s_posix.TryGetValue(name, out string? range) ? range : throw Unsupported($"the POSIX class {name}");
	}

	private void OpenGroup()
	{
		if (Next(_position + 1) != '?')
		{
			OpenCapture(1);
			return;
		}

		string rest = _pattern[_position..];
		if (rest.StartsWith("(?P<", StringComparison.Ordinal) || (rest.StartsWith("(?<", StringComparison.Ordinal) && !rest.StartsWith("(?<=", StringComparison.Ordinal) && !rest.StartsWith("(?<!", StringComparison.Ordinal)))
		{
			OpenNamed(rest.IndexOf('>', StringComparison.Ordinal) + 1);
			return;
		}

		OpenNonCapture(rest);
	}

	private void OpenCapture(int length)
	{
		_captures.Add(new Capture(_output.Count, ++_nodes, _frames.Count(static frame => frame.IsCapture), [.. _frames.Select(static frame => (frame.Node, frame.Branch))]));
		_output.Add("(");
		_frames.Push(new Frame(_nodes, IsCapture: true));
		_position += length;
	}

	/// <summary>
	/// A named group labels a part, as <c>jwt-base64</c> names each header field it recognizes, and is never the whole token. It still
	/// counts in upstream's numbering, so it stays a capture, but one that never stands for the secret.
	/// </summary>
	private void OpenNamed(int length)
	{
		OpenCapture(length);
		_captures[^1].Named = true;
	}

	/// <summary><c>(?:</c>, and flag groups such as <c>(?i)</c>, <c>(?i:</c> and <c>(?-i:</c>, which .NET reads the same way.</summary>
	private void OpenNonCapture(string rest)
	{
		int end = 2;
		while (end < rest.Length && "imsU-".Contains(rest[end], StringComparison.Ordinal))
		{
			end++;
		}

		string flags = rest[2..end];
		if (end >= rest.Length || rest[end] is not (':' or ')') || flags.Contains('U', StringComparison.Ordinal) || flags.Contains('m', StringComparison.Ordinal))
		{
			throw Unsupported("lookaround, a multi-line or ungreedy flag, or an unknown group");
		}

		_output.Add(rest[..(end + 1)]);
		_position += end + 1;
		if (rest[end] == ':')
		{
			_frames.Push(new Frame(++_nodes, IsCapture: false) { OpenToken = _output.Count - 1 });
		}
		else
		{
			_flagTokens.Add(_output.Count - 1);
		}
	}

	private void CloseGroup()
	{
		if (_frames.Count == 1)
		{
			throw Unsupported("an unmatched )");
		}

		Frame closed = _frames.Pop();
		_output.Add(")");
		_position++;
		if (closed.IsCapture)
		{
			_captures.Single(capture => capture.Node == closed.Node).CloseToken = _output.Count - 1;
		}

		_lastGroup = (closed.OpenToken, _output.Count - 1, closed.IsCapture, _frames.Count == 1);
		if (_position < _pattern.Length && "*+?{".Contains(_pattern[_position], StringComparison.Ordinal))
		{
			// A quantifier on any group repeats every capture inside it, as in (?:(abc)-)+.
			foreach (Capture capture in _captures.Where(capture => capture.Node == closed.Node || capture.Path.Any(step => step.Node == closed.Node)))
			{
				capture.Repeated = true;
			}
		}
	}

	private HashSet<int> SecretCaptures(int secretGroup)
	{
		if (secretGroup > 0)
		{
			return secretGroup <= _captures.Count ? [_captures[secretGroup - 1].Token] : throw Unsupported($"secretGroup {secretGroup}");
		}

		// A capture under a quantifier holds one repetition at most, a fragment, and upstream may report it. The whole match
		// always covers what upstream would report, so it stands for the secret instead.
		Capture[] top = [.. _captures.Where(static capture => capture.Depth == 0)];
		bool exclusive = top.All(a => top.All(b => ReferenceEquals(a, b) || Exclusive(a, b)));
		return exclusive && !top.Any(static capture => capture.Repeated || capture.Named) ? [.. top.Select(static capture => capture.Token)] : [];
	}

	/// <summary>True when the two sit in different branches of one alternation, so at most one of them can match.</summary>
	private static bool Exclusive(Capture a, Capture b)
	{
		Dictionary<int, int> branches = a.Path.ToDictionary(static step => step.Node, static step => step.Branch);
		return b.Path.Any(step => branches.TryGetValue(step.Node, out int branch) && branch != step.Branch);
	}

	/// <summary>
	/// Turns a <c>\b</c> that starts or ends the pattern beside the secret into a consuming ASCII boundary, and draws the secret
	/// group inside it, and before a final terminator, when the whole match was the secret. A pattern whose top level is an
	/// alternation is left as it is, as a boundary there belongs to one branch only.
	/// </summary>
	private void DrawBoundaries(HashSet<int> secret)
	{
		if (_rootBranches > 0)
		{
			return;
		}

		int first = FirstContentToken();
		bool leading = first >= 0 && _output[first] == "\\b";
		bool trailing = _output.Count - 1 > first && _output[^1] == "\\b";
		if (secret.Count == 0)
		{
			WrapWholeMatch(secret, first, leading, trailing);
		}

		MarkBoundaries(secret, first, leading, trailing);
	}

	private void WrapWholeMatch(HashSet<int> secret, int first, bool leading, bool trailing)
	{
		int terminator = Terminator();
		int end = trailing ? _output.Count - 1 : terminator >= 0 ? terminator : _output.Count;
		Wrap(secret, leading ? first + 1 : first, end);
	}

	private void MarkBoundaries(HashSet<int> secret, int first, bool leading, bool trailing)
	{
		Capture[] groups = [.. _captures.Where(capture => secret.Contains(capture.Token))];
		bool lead = leading && groups.Any(capture => capture.Token == first + 1);
		bool trail = trailing && groups.Any(capture => capture.CloseToken == _output.Count - 2);
		if (lead)
		{
			_output[first] = "(?:^|[^0-9A-Za-z_])";
		}

		if (trail)
		{
			_output[^1] = "(?:[^0-9A-Za-z_]|\\z)";
		}

		foreach (Capture group in groups)
		{
			DropInnerBoundaries(group, lead, trail);
		}
	}

	/// <summary>
	/// A <c>\b</c> just inside the secret, as in <c>\b(\bsgp_…\b)</c>, is Unicode-aware too, and would undo an ASCII boundary outside it.
	/// It goes when the outside already gives the boundary: a converted <c>\b</c>, or a final terminator, whose branches are all
	/// delimiters. A <c>\b</c> branch of that terminator becomes an ASCII one.
	/// </summary>
	private void DropInnerBoundaries(Capture group, bool lead, bool trail)
	{
		if (lead && _output[group.Token + 1] == "\\b")
		{
			_output[group.Token + 1] = "";
		}

		int terminator = Terminator();
		bool terminated = terminator >= 0 && terminator == group.CloseToken + 1;
		for (int i = terminator + 1; terminated && i < _output.Count - 1; i++)
		{
			_output[i] = _output[i] == "\\b" ? "(?:[^0-9A-Za-z_]|\\z)" : _output[i];
		}

		if ((trail || terminated) && _output[group.CloseToken - 1] == "\\b")
		{
			_output[group.CloseToken - 1] = "";
		}
	}

	/// <summary>The opening token of a final top-level group that only ends the token, such as <c>(?:[^a-z0-9]|\z)</c>, or -1.</summary>
	private int Terminator()
	{
		(int open, int close, bool isCapture, bool topLevel) = _lastGroup;
		if (isCapture || !topLevel || close != _output.Count - 1 || open < 0)
		{
			return -1;
		}

		string body = string.Concat(_output.Skip(open + 1).Take(close - open - 1));
		return body.Split('|').Any(static branch => branch is "\\z") ? open : -1;
	}

	/// <summary>Draws the secret group over tokens from <paramref name="start"/> up to <paramref name="end"/>, unless that is the whole pattern.</summary>
	private void Wrap(HashSet<int> secret, int start, int end)
	{
		if (start < 0 || end <= start || (start == FirstContentToken() && end == _output.Count))
		{
			return;
		}

		_output.Insert(end, ")");
		_output.Insert(start, "(");
		foreach (Capture capture in _captures)
		{
			capture.Shift(start, end);
		}

		for (int i = 0; i < _flagTokens.Count; i++)
		{
			_flagTokens[i] += _flagTokens[i] >= end ? 2 : _flagTokens[i] >= start ? 1 : 0;
		}

		Capture wrapper = new(start, -1, 0, []) { CloseToken = end + 1 };
		_captures.Add(wrapper);
		secret.Add(start);
	}

	private int FirstContentToken()
	{
		return Enumerable.Range(0, _output.Count).FirstOrDefault(index => !_flagTokens.Contains(index), -1);
	}

	private string Render(Func<int, string> capture)
	{
		HashSet<int> tokens = [.. _captures.Select(static c => c.Token)];
		StringBuilder output = new();
		for (int i = 0; i < _output.Count; i++)
		{
			output.Append(tokens.Contains(i) ? capture(i) : _output[i]);
		}

		return output.ToString();
	}

	private char Next(int index)
	{
		return index < _pattern.Length ? _pattern[index] : throw Unsupported("a pattern that ends early");
	}

	private NotSupportedException Unsupported(string what)
	{
		return new NotSupportedException($"Cannot translate {what} at {_position} in {_pattern}");
	}

	private sealed record Frame(int Node, bool IsCapture)
	{
		public int Branch { get; init; }

		public int OpenToken { get; init; } = -1;
	}

	/// <summary>A capture group. <see cref="Repeated"/> is set when a quantifier follows it, so it holds one repetition, or nothing.</summary>
	private sealed class Capture(int token, int node, int depth, (int Node, int Branch)[] path)
	{
		public int Token { get; private set; } = token;

		public int Node { get; } = node;

		public int Depth { get; } = depth;

		public (int Node, int Branch)[] Path { get; } = path;

		public bool Repeated { get; set; }

		public bool Named { get; set; }

		public int CloseToken { get; set; } = -1;

		/// <summary>Moves the token indexes past a wrapper opened at <paramref name="start"/> and closed before <paramref name="end"/>.</summary>
		public void Shift(int start, int end)
		{
			Token += Token >= end ? 2 : Token >= start ? 1 : 0;
			CloseToken += CloseToken >= end ? 2 : CloseToken >= start ? 1 : 0;
		}
	}

	private sealed class ClassBody
	{
		public bool Negated { get; set; }

		public StringBuilder Items { get; } = new();

		public List<string> NegatedShorthands { get; } = [];

		/// <summary>
		/// Null when a negated shorthand shares its class, which a single .NET class cannot say on ASCII terms. The exception is
		/// the idiom <c>[\s\S]</c>, any character, which a shorthand beside its own negation always means.
		/// </summary>
		public string? Render()
		{
			if (NegatedShorthands.Count == 0)
			{
				return $"[{(Negated ? "^" : "")}{Items}]";
			}

			string items = Items.ToString();
			if (!Negated && NegatedShorthands.All(items.Contains))
			{
				return "[\\s\\S]";
			}

			return NegatedShorthands.Count == 1 && Items.Length == 0 && !Negated ? $"[^{NegatedShorthands[0]}]" : null;
		}
	}
}
