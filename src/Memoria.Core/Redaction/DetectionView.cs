using System.Buffers;
using System.Text;

namespace Memoria.Core.Redaction;

/// <summary>
/// What the rules read when the text carries noise that would split a credential or its name:
/// ANSI colors, zero-width characters, soft hyphens, HTML-escaped quotes, and curly quotes. Only
/// detection reads this. Hits are mapped back, so everything outside a replaced span stays
/// byte for byte what it was.
/// </summary>
internal sealed class DetectionView
{
	private const char Escape = '\u001b';

	private static readonly (string Spelling, char Meaning)[] s_entities =
	[
		("&quot;", '"'), ("&#34;", '"'), ("&#x22;", '"'), ("&apos;", '\''), ("&#39;", '\''), ("&#x27;", '\''), ("&amp;", '&'),
		("“", '"'), ("”", '"'), ("‘", '\''), ("’", '\''),
	];

	private static readonly string[] s_invisible = ["​", "‌", "‍", "⁠", "﻿", "­"];

	private static readonly string[] s_ansiOpeners = ["\u001b", "\\u001b", "\\x1b", "\\033", "\\e[", "^[["];

	private static readonly SearchValues<string> s_noise = SearchValues.Create(
		[.. s_entities.Select(static entity => entity.Spelling), .. s_invisible, .. s_ansiOpeners],
		StringComparison.Ordinal);

	private readonly int[] _starts;
	private readonly int[] _ends;

	private DetectionView(string text, int[] starts, int[] ends)
	{
		Text = text;
		_starts = starts;
		_ends = ends;
	}

	/// <summary>The text the rules read.</summary>
	public string Text { get; }

	public static bool HasNoise(string input)
	{
		return input.AsSpan().ContainsAny(s_noise);
	}

	/// <summary>A view with noise removed and, when asked, lines that hold one wrapped token joined. Null when nothing changes.</summary>
	public static DetectionView? Create(string input, bool joinWrappedLines)
	{
		bool join = joinWrappedLines && LineWraps.Any(input);
		if (!join && !HasNoise(input))
		{
			return null;
		}

		Builder builder = new(input);
		bool changed = builder.Run(join);
		return changed ? builder.Build() : null;
	}

	/// <summary>Maps a span of <see cref="Text"/> back onto the original input.</summary>
	public (int Start, int End) ToOriginal(int start, int end)
	{
		return (_starts[start], _ends[end - 1]);
	}

	/// <summary>The length of an ANSI escape at the position, in raw or printed form, or 0.</summary>
	private static int AnsiLength(string input, int position)
	{
		int body = AnsiBodyStart(input, position);
		if (body < 0)
		{
			return 0;
		}

		int cursor = body;
		while (cursor < input.Length && (char.IsAsciiDigit(input[cursor]) || input[cursor] == ';'))
		{
			cursor++;
		}

		return cursor < input.Length && char.IsAsciiLetter(input[cursor]) ? cursor + 1 - position : 0;
	}

	/// <summary>Where the parameters begin, past the escape's opener and its <c>[</c>, or -1.</summary>
	private static int AnsiBodyStart(string input, int position)
	{
		foreach (string opener in s_ansiOpeners)
		{
			if (string.CompareOrdinal(input, position, opener, 0, opener.Length) == 0)
			{
				int next = position + opener.Length;
				bool bracketed = opener[^1] == '[';
				return bracketed ? next : next < input.Length && input[next] == '[' ? next + 1 : -1;
			}
		}

		return -1;
	}

	/// <summary>Walks the input once, writing the view and where each of its characters came from.</summary>
	private sealed class Builder(string input)
	{
		private readonly StringBuilder _text = new(input.Length);
		private readonly List<int> _starts = new(input.Length);
		private readonly List<int> _ends = new(input.Length);
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
			return new DetectionView(_text.ToString(), [.. _starts], [.. _ends]);
		}

		private int Step(int position, bool joinWrappedLines)
		{
			int skipped = SkippedLength(position, joinWrappedLines);
			if (skipped > 0)
			{
				_changed = true;
				return position + skipped;
			}

			foreach ((string spelling, char meaning) in s_entities)
			{
				if (string.CompareOrdinal(input, position, spelling, 0, spelling.Length) == 0)
				{
					Write(meaning, position, position + spelling.Length);
					_changed = true;
					return position + spelling.Length;
				}
			}

			Write(input[position], position, position + 1);
			return position + 1;
		}

		private int SkippedLength(int position, bool joinWrappedLines)
		{
			char c = input[position];
			if (c is '​' or '‌' or '‍' or '⁠' or '﻿' or '­')
			{
				return 1;
			}

			if (c is Escape or '\\' or '^')
			{
				return AnsiLength(input, position);
			}

			return joinWrappedLines && c is '\n' or '\r' ? LineWraps.JoinLength(input, position) : 0;
		}

		private void Write(char c, int start, int end)
		{
			_text.Append(c);
			_starts.Add(start);
			_ends.Add(end);
		}
	}
}
