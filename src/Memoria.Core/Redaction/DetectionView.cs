using System.Buffers;
using System.Globalization;

namespace Memoria.Core.Redaction;

/// <summary>
/// What the rules read when the text carries noise that would split a credential or its name:
/// terminal escapes, control characters, no-break and thin spaces, invisible format characters, variation selectors, HTML entities, percent-encoded
/// letters, and curly quotes. Only detection reads this. Hits are mapped back, so everything outside a
/// replaced span stays byte for byte what it was.
/// </summary>
internal sealed class DetectionView
{
	private const char Escape = '\u001b';
	private const char ControlSequenceIntroducer = '\u009b';
	private const char ReplacementCharacter = '�';
	private const char TagHighSurrogate = '\uDB40';
	private const int MaxOscLength = 4096;

	private static readonly (string Spelling, char Meaning)[] s_namedEntities =
	[
		("&quot;", '"'), ("&apos;", '\''), ("&amp;", '&'), ("&lt;", '<'), ("&gt;", '>'),
	];

	private static readonly string[] s_ansiOpeners = ["\u001b", "\\u001b", "\\x1b", "\\033", "\\e", "^["];

	private static readonly SearchValues<string> s_noiseStrings = SearchValues.Create(
		[
			.. s_namedEntities.Select(static entity => entity.Spelling),
			"&#", "\\u001b", "\\x1b", "\\033", "\\e[", "\\e]", "^[[", "^[]", "%3", "%4", "%5", "%6", "%7",
		],
		StringComparison.Ordinal);

	private static readonly SearchValues<char> s_noiseChars = SearchValues.Create(BuildNoiseChars());

	private readonly int[] _starts;
	private readonly Dictionary<int, int> _longUnits;

	private DetectionView(string text, int[] starts, Dictionary<int, int> longUnits)
	{
		Text = text;
		_starts = starts;
		_longUnits = longUnits;
	}

	/// <summary>The text the rules read.</summary>
	public string Text { get; }

	public static bool HasNoise(string input)
	{
		ReadOnlySpan<char> span = input;
		return span.ContainsAny(s_noiseChars) || span.ContainsAny(s_noiseStrings) || HasLoneBoundary(input);
	}

	/// <summary>A view with noise removed and, when asked, lines that hold one wrapped token joined. Null when nothing changes.</summary>
	public static DetectionView? Create(string input, bool joinWrappedLines, ViewBuffers? buffers = null)
	{
		bool join = joinWrappedLines && LineWraps.Any(input);
		if (!join && !HasNoise(input))
		{
			return null;
		}

		Builder builder = new(input, buffers ?? new ViewBuffers());
		return builder.Run(join) ? builder.Build() : null;
	}

	/// <summary>Maps a span of <see cref="Text"/> back onto the original input.</summary>
	public (int Start, int End) ToOriginal(int start, int end)
	{
		int last = end - 1;
		return (_starts[start], _starts[last] + _longUnits.GetValueOrDefault(last, 1));
	}

	/// <summary>True for a marker U+FDD0 that is not a message boundary's own, which is then only noise.</summary>
	private static bool HasLoneBoundary(string input)
	{
		int index = input.IndexOf(Redactor.MessageBoundary);
		while (index >= 0)
		{
			if (!IsSeparatorBoundary(input, index))
			{
				return true;
			}

			index = input.IndexOf(Redactor.MessageBoundary, index + 1);
		}

		return false;
	}

	internal static bool IsSeparatorBoundary(string input, int index)
	{
		return index > 0 && input[index - 1] == '\n' && index + 1 < input.Length && input[index + 1] == '\n';
	}

	private static char[] BuildNoiseChars()
	{
		List<char> chars = [Escape, ControlSequenceIntroducer, ReplacementCharacter, TagHighSurrogate, '“', '”', '‘', '’', '͏'];
		for (int code = 0; code <= char.MaxValue; code++)
		{
			char c = (char)code;
			if (IsInvisible(c))
			{
				chars.Add(c);
			}
		}

		return [.. chars.Distinct()];
	}

	/// <summary>Every format character, variation selectors, and the combining grapheme joiner. None of them can be part of a credential.</summary>
	private static bool IsInvisible(char c)
	{
		return c is >= '︀' and <= '️' or '͏' or '᠋' or '᠌' or '᠍' or '᠎'
			|| CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format
			|| IsStrayControl(c)
			|| IsOtherSpace(c);
	}

	/// <summary>C0, DEL and C1 controls. Not line structure, which the rules read, and not what opens a terminal escape, which has its own handling.</summary>
	private static bool IsStrayControl(char c)
	{
		return char.IsControl(c) && c is not ('\t' or '\n' or '\r' or Escape or ControlSequenceIntroducer);
	}

	/// <summary>Every space but the plain one, which really does end a token: NBSP, thin and hair spaces and the rest of the category.</summary>
	private static bool IsOtherSpace(char c)
	{
		return c != ' ' && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
	}

	/// <summary>The length of a terminal escape at the position, raw or printed, or 0.</summary>
	private static int AnsiLength(string input, int position)
	{
		if (input[position] == ControlSequenceIntroducer)
		{
			return CsiLength(input, position, position + 1);
		}

		foreach (string opener in s_ansiOpeners)
		{
			if (string.CompareOrdinal(input, position, opener, 0, opener.Length) == 0)
			{
				return AfterOpenerLength(input, position, position + opener.Length);
			}
		}

		return 0;
	}

	private static int AfterOpenerLength(string input, int position, int next)
	{
		if (next >= input.Length)
		{
			return 0;
		}

		return input[next] switch
		{
			'[' => CsiLength(input, position, next + 1),
			']' => OscLength(input, position, next + 1),
			'(' or ')' or '*' or '+' when next + 1 < input.Length => next + 2 - position,
			_ => 0,
		};
	}

	/// <summary>Parameters including private and colon forms, then intermediates, then the final byte.</summary>
	private static int CsiLength(string input, int position, int cursor)
	{
		while (cursor < input.Length && input[cursor] is >= '0' and <= '?')
		{
			cursor++;
		}

		while (cursor < input.Length && input[cursor] is >= ' ' and <= '/')
		{
			cursor++;
		}

		return cursor < input.Length && input[cursor] is >= '@' and <= '~' ? cursor + 1 - position : 0;
	}

	/// <summary>An operating system command runs to BEL or to a string terminator, raw or printed.</summary>
	private static int OscLength(string input, int position, int cursor)
	{
		int limit = Math.Min(input.Length, cursor + MaxOscLength);
		for (int i = cursor; i < limit; i++)
		{
			int terminator = OscTerminatorLength(input, i);
			if (terminator > 0)
			{
				return i + terminator - position;
			}
		}

		return 0;
	}

	private static int OscTerminatorLength(string input, int i)
	{
		if (input[i] == '\u0007')
		{
			return 1;
		}

		foreach (string terminator in new[] { "\u001b\\", "\\u0007", "\\x07", "\\a" })
		{
			if (string.CompareOrdinal(input, i, terminator, 0, terminator.Length) == 0)
			{
				return terminator.Length;
			}
		}

		return 0;
	}

	/// <summary>Decodes an entity at the position. A numeric one must name a character in the basic plane.</summary>
	private static bool TryDecodeEntity(string input, int position, out char meaning, out int length)
	{
		foreach ((string spelling, char named) in s_namedEntities)
		{
			if (string.CompareOrdinal(input, position, spelling, 0, spelling.Length) == 0)
			{
				meaning = named;
				length = spelling.Length;
				return true;
			}
		}

		return TryDecodeNumericEntity(input, position, out meaning, out length);
	}

	private static bool TryDecodeNumericEntity(string input, int position, out char meaning, out int length)
	{
		meaning = '\0';
		length = 0;
		if (string.CompareOrdinal(input, position, "&#", 0, 2) != 0)
		{
			return false;
		}

		int semicolon = input.IndexOf(';', position, Math.Min(12, input.Length - position));
		if (semicolon < 0)
		{
			return false;
		}

		ReadOnlySpan<char> digits = input.AsSpan(position + 2, semicolon - position - 2);
		bool hex = digits.Length > 0 && digits[0] is 'x' or 'X';
		bool parsed = hex
			? int.TryParse(digits[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code)
			: int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out code);
		if (!parsed || code is <= 0 or > char.MaxValue || char.IsSurrogate((char)code))
		{
			return false;
		}

		meaning = MapCurlyQuote((char)code);
		length = semicolon + 1 - position;
		return true;
	}

	/// <summary><c>%61</c> is the letter a. Only letters and digits decode, so <c>%3D</c> and <c>%20</c> keep the meanings the rules give them.</summary>
	private static bool TryDecodePercentLetter(string input, int position, out char meaning)
	{
		meaning = '\0';
		if (input[position] != '%' || position + 2 >= input.Length || !char.IsAsciiHexDigit(input[position + 1]) || !char.IsAsciiHexDigit(input[position + 2]))
		{
			return false;
		}

		meaning = (char)Convert.ToInt32(input.Substring(position + 1, 2), 16);
		return char.IsAsciiLetterOrDigit(meaning);
	}

	private static char MapCurlyQuote(char c)
	{
		return c switch
		{
			'“' or '”' => '"',
			'‘' or '’' => '\'',
			_ => c,
		};
	}

	/// <summary>A tag character or supplementary variation selector is a pair whose high half is U+DB40.</summary>
	private static int InvisibleLength(string input, int position)
	{
		char c = input[position];
		if (c == TagHighSurrogate)
		{
			return position + 1 < input.Length && char.IsLowSurrogate(input[position + 1]) ? 2 : 0;
		}

		return c == ReplacementCharacter || IsInvisible(c) ? 1 : 0;
	}

	/// <summary>Walks the input once, writing the view and where each of its characters came from.</summary>
	private sealed class Builder(string input, ViewBuffers buffers)
	{
		private readonly char[] _text = buffers.Text(input.Length);
		private readonly int[] _starts = buffers.Starts(input.Length);
		private readonly Dictionary<int, int> _longUnits = [];
		private int _length;
		private bool _changed;

		public bool Run(bool joinWrappedLines)
		{
			int position = 0;
			while (position < input.Length)
			{
				position = Step(position, joinWrappedLines);
			}

			return _changed;
		}

		public DetectionView Build()
		{
			return new DetectionView(new string(_text, 0, _length), _starts, _longUnits);
		}

		private int Step(int position, bool joinWrappedLines)
		{
			int skipped = SkippedLength(position, joinWrappedLines);
			if (skipped > 0)
			{
				_changed = true;
				return position + skipped;
			}

			return WriteDecoded(position);
		}

		private int WriteDecoded(int position)
		{
			char c = input[position];
			if (c == '&' && TryDecodeEntity(input, position, out char meaning, out int length))
			{
				return WriteChanged(meaning, position, length);
			}

			if (c == '%' && TryDecodePercentLetter(input, position, out char letter))
			{
				return WriteChanged(letter, position, 3);
			}

			char mapped = MapCurlyQuote(c);
			_changed |= mapped != c;
			Write(mapped, position, 1);
			return position + 1;
		}

		private int WriteChanged(char c, int position, int length)
		{
			_changed = true;
			Write(c, position, length);
			return position + length;
		}

		private int SkippedLength(int position, bool joinWrappedLines)
		{
			char c = input[position];
			if (c == Redactor.MessageBoundary)
			{
				return IsSeparatorBoundary(input, position) ? 0 : 1;
			}

			int invisible = c is < ' ' or >= '\u007f' ? InvisibleLength(input, position) : 0;
			if (invisible > 0)
			{
				return invisible;
			}

			if (c is Escape or ControlSequenceIntroducer or '\\' or '^')
			{
				return AnsiLength(input, position);
			}

			return joinWrappedLines && c is '\n' or '\r' ? LineWraps.JoinLength(input, position) : 0;
		}

		private void Write(char c, int start, int length)
		{
			_text[_length] = c;
			_starts[_length] = start;
			if (length != 1)
			{
				_longUnits[_length] = length;
			}

			_length++;
		}
	}
}

/// <summary>One set of buffers per redaction call, so rebuilding a view after each change does not allocate new ones.</summary>
internal sealed class ViewBuffers
{
	private char[] _text = [];
	private int[] _starts = [];

	public char[] Text(int length)
	{
		if (_text.Length < length)
		{
			_text = new char[length];
		}

		return _text;
	}

	public int[] Starts(int length)
	{
		if (_starts.Length < length)
		{
			_starts = new int[length];
		}

		return _starts;
	}
}
