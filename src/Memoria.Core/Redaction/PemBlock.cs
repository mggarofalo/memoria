using System.Text.RegularExpressions;

namespace Memoria.Core.Redaction;

/// <summary>
/// Finds the extent of a private key. A hand-written linear walk, because the decision needs context
/// a regular language cannot express cheaply: accept a whole base64 line, stop at the prose after a
/// truncated key, read past an elision, and stop at the next BEGIN so repeated markers stay linear.
/// A BEGIN marker is decisive unless nothing key-like follows it, which is what a mention looks like.
/// </summary>
internal static class PemBlock
{
	/// <summary>A marker mentioned in code or prose has no key material behind it. Less than this is not a key.</summary>
	private const int MinimumBody = 16;

	/// <summary>Real base64 lines are 64 or 70 characters, so a token this long is a key line wherever it sits.</summary>
	private const int FullLineLength = 32;

	/// <summary>How many lines of prose may sit between the marker and the key before the marker is taken for a mention.</summary>
	private const int ProseLineBudget = 3;

	private const int MaxSkippedLineLength = 512;

	/// <summary>A base64 run this long is a body line wherever it sits on the line: after a log timestamp, inside a JSON value, or inside a box.</summary>
	private const int LongRunLength = 40;

	/// <summary>The longest note in brackets allowed after a body line, such as <c>(line 2)</c>.</summary>
	private const int MaxNoteLength = 22;

	/// <summary>The most closing punctuation allowed after a body line, such as <c>"},</c>.</summary>
	private const int MaxClosers = 4;

	private const string DataUriStart = "data:";
	private const string DataUriEnd = ";base64,";

	private const int MimeWidth = 76;

	/// <summary>A random token this long at a line's start is the end of a key, even with prose after it on the line.</summary>
	private const int TailBeforeProseLength = 8;

	private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");

	private const string EndMarker = "END";
	private const string BeginMarker = "BEGIN";

	/// <summary>The span from a BEGIN marker to the end of its key, or null when no key material follows.</summary>
	public static (int Start, int End)? FindForward(string input, Match marker)
	{
		Walk walk = new(input, marker.Index + marker.Length);
		while (walk.Advance())
		{
		}

		int end = walk.Result();
		return end < 0 ? null : (marker.Index, end);
	}

	/// <summary>The span from the start of the body before an END marker with no BEGIN, through the marker, or null for a bare mention.</summary>
	public static (int Start, int End)? FindBackward(string input, Match marker)
	{
		int start = marker.Index;
		int cursor = marker.Index;
		int body = 0;
		int real = 0;
		bool nearest = true;
		while (true)
		{
			cursor = PemSeparators.SkipBack(input, cursor);
			int tokenStart = PemSeparators.TokenStartBefore(input, cursor);
			int length = cursor - tokenStart;
			if (length == 0 || (length < FullLineLength && !nearest))
			{
				break;
			}

			body += length;
			real += IsMaskLine(input.AsSpan(tokenStart, length)) ? 0 : length;
			start = tokenStart;
			cursor = tokenStart;
			nearest = false;
		}

		return body >= FullLineLength && real >= FullLineLength ? (start, marker.Index + marker.Length) : null;
	}

	/// <summary>Mostly the letter x, line by line, so padding cannot hide shorter real lines in the same block.</summary>
	private static bool IsMaskLine(ReadOnlySpan<char> line)
	{
		int xs = 0;
		foreach (char c in line)
		{
			xs += c is 'x' or 'X' ? 1 : 0;
		}

		return xs * 10 >= line.Length * 6;
	}

	/// <summary>Extends a run of base64 lines over a final, shorter line with the same prefix that is base64 to its end.</summary>
	public static int ExtendLines(SecretCandidate candidate)
	{
		string input = candidate.Input;
		int next = SkipAfterRun(input, candidate.End);
		next += next < input.Length && input[next] == '\r' ? 1 : 0;
		if (next >= input.Length || input[next] != '\n')
		{
			return candidate.End;
		}

		int lineStart = next + 1 + LastLinePrefixWidth(candidate);
		int cursor = Math.Min(lineStart, input.Length);
		cursor += Base64RunLength(input, cursor);
		return IsFinalShortLine(input, lineStart, cursor) ? cursor : candidate.End;
	}

	private static int Base64RunLength(string input, int start)
	{
		int cursor = start;
		while (cursor < input.Length && (char.IsAsciiLetterOrDigit(input[cursor]) || input[cursor] is '+' or '/' or '='))
		{
			cursor++;
		}

		return cursor - start;
	}

	private static bool IsFinalShortLine(string input, int start, int end)
	{
		int after = SkipAfterRun(input, end);
		if (end <= start || (after < input.Length && input[after] is not ('\r' or '\n')))
		{
			return false;
		}

		bool padded = input[end - 1] == '=';
		return padded || (end - start >= 8 && LooksRandom(input.AsSpan(start, end - start)));
	}

	/// <summary>Skips closing punctuation and a short note in brackets. A word after them means the line is not over, which the caller sees by what is next.</summary>
	private static int SkipAfterRun(string input, int start)
	{
		int cursor = SkipBlanks(input, start);
		int limit = Math.Min(input.Length, cursor + MaxClosers);
		while (cursor < limit && IsCloser(input[cursor]))
		{
			cursor++;
		}

		cursor = SkipBlanks(input, cursor);
		int noteEnd = cursor < input.Length && input[cursor] == '(' ? input.IndexOf(')', cursor, Math.Min(MaxNoteLength, input.Length - cursor)) : -1;
		return noteEnd < 0 ? cursor : SkipBlanks(input, noteEnd + 1);
	}

	private static int SkipBlanks(string input, int position)
	{
		int cursor = position;
		while (cursor < input.Length && input[cursor] is ' ' or '\t')
		{
			cursor++;
		}

		return cursor;
	}

	private static bool IsCloser(char c)
	{
		return !char.IsAsciiLetterOrDigit(c) && !char.IsWhiteSpace(c) && c != '(';
	}

	/// <summary>How much text comes before the base64 on the run's last line.</summary>
	private static int LastLinePrefixWidth(SecretCandidate candidate)
	{
		string input = candidate.Input;
		int lineStart = input.LastIndexOf('\n', candidate.End - 1) + 1;
		int runStart = PemSeparators.TokenStartBefore(input, candidate.End);
		return runStart - lineStart;
	}

	private static bool LooksRandom(ReadOnlySpan<char> token)
	{
		bool hasDigit = token.IndexOfAnyInRange('0', '9') >= 0;
		bool hasUpper = token.IndexOfAnyInRange('A', 'Z') >= 0;
		bool hasLower = token.IndexOfAnyInRange('a', 'z') >= 0;
		return hasDigit || (hasUpper && hasLower) || token.EndsWith("=");
	}

	/// <summary>
	/// True for lines of base64 that could be a key with its markers gone. Lines must share one width, which a list of paths does
	/// not, and MIME's 76 columns is data. Hex is a list of hashes. The nearest marker above decides the rest: under a
	/// non-private BEGIN with no END in between, the run is that object's body.
	/// </summary>
	public static bool IsUnmarkedKeyBody(SecretCandidate candidate)
	{
		return HasKeyLineShape(candidate) && !IsUnderPublicMarker(candidate) && !FollowsDataUri(candidate) && !PrecedesPublicEnd(candidate);
	}

	private static bool HasKeyLineShape(SecretCandidate candidate)
	{
		ReadOnlySpan<char> run = candidate.Input.AsSpan(candidate.Secret.Index, candidate.Secret.Length);
		int width = -1;
		int paddedLines = 0;
		bool hasNonHex = false;
		foreach (ReadOnlySpan<char> line in run.EnumerateLines())
		{
			int length = TrailingBase64Length(WithoutNote(line));
			width = width < 0 ? length : width;
			if (length != width)
			{
				return false;
			}

			// One encoded value has padding on its last line only. Padding on more is a list of values: go.sum, lockfiles.
			ReadOnlySpan<char> text = WithoutNote(line);
			paddedLines += text.EndsWith("=") ? 1 : 0;
			hasNonHex |= text[^length..].IndexOfAnyExcept(HexDigits) >= 0;
		}

		return hasNonHex && paddedLines <= 1 && width != MimeWidth && run.IndexOfAnyInRange('A', 'Z') >= 0 && run.IndexOfAnyInRange('a', 'z') >= 0;
	}

	/// <summary>The line up to the end of its base64, without what <see cref="SkipAfterRun"/> allows after it.</summary>
	private static ReadOnlySpan<char> WithoutNote(ReadOnlySpan<char> line)
	{
		ReadOnlySpan<char> text = line.TrimEnd();
		int open = text.EndsWith(")") ? text.LastIndexOf('(') : -1;
		text = open >= 0 && text.Length - open <= MaxNoteLength ? text[..open].TrimEnd() : text;
		int closers = 0;
		while (closers < MaxClosers && text.Length > 0 && IsCloser(text[^1]) && text[^1] is not ('+' or '/' or '='))
		{
			text = text[..^1];
			closers++;
		}

		return text.TrimEnd();
	}

	private static int TrailingBase64Length(ReadOnlySpan<char> line)
	{
		ReadOnlySpan<char> text = line.TrimEnd();
		int i = text.Length;
		while (i > 0 && (char.IsAsciiLetterOrDigit(text[i - 1]) || text[i - 1] is '+' or '/' or '='))
		{
			i--;
		}

		return text.Length - i;
	}

	private static bool IsUnderPublicMarker(SecretCandidate candidate)
	{
		string input = candidate.Input;
		int cursor = candidate.Secret.Index;
		while (cursor > 0)
		{
			int dashes = input.LastIndexOf("-----", cursor - 1, StringComparison.Ordinal);
			if (dashes < 0)
			{
				return false;
			}

			int after = SkipDashes(input, dashes);
			if (string.CompareOrdinal(input, after, EndMarker, 0, EndMarker.Length) == 0)
			{
				return false;
			}

			if (string.CompareOrdinal(input, after, BeginMarker, 0, BeginMarker.Length) == 0)
			{
				return !NamesPrivateKey(input, after);
			}

			cursor = dashes;
		}

		return false;
	}

	/// <summary>Only a real header directly joined to the run: <c>data:image/png;base64,AAAA</c>. The word on its own proves nothing.</summary>
	private static bool FollowsDataUri(SecretCandidate candidate)
	{
		string input = candidate.Input;
		int lineStart = candidate.Secret.Index == 0 ? 0 : input.LastIndexOfAny(['\r', '\n'], candidate.Secret.Index - 1) + 1;
		ReadOnlySpan<char> before = input.AsSpan(lineStart, candidate.Secret.Index - lineStart);
		if (!before.EndsWith(DataUriEnd, StringComparison.Ordinal))
		{
			return false;
		}

		int start = before.LastIndexOf(DataUriStart, StringComparison.Ordinal);
		if (start < 0)
		{
			return false;
		}

		ReadOnlySpan<char> mediaType = before[(start + DataUriStart.Length)..^DataUriEnd.Length];
		return mediaType.Contains('/') && !mediaType.ContainsAny(" \t,\"'");
	}

	/// <summary>A certificate cut off at its top ends right after the run. Anything further away proves nothing.</summary>
	private static bool PrecedesPublicEnd(SecretCandidate candidate)
	{
		string input = candidate.Input;
		int next = candidate.End;
		while (next < input.Length && char.IsWhiteSpace(input[next]))
		{
			next++;
		}

		int after = SkipDashes(input, next);
		return after - next >= 2 && string.CompareOrdinal(input, after, EndMarker, 0, EndMarker.Length) == 0 && !NamesPrivateKey(input, after);
	}

	/// <summary>True when base64 opens a DER private key: a SEQUENCE whose first member is the version 0 or 1, which a certificate or public key never has.</summary>
	public static bool IsDerPrivateKey(string value)
	{
		const int HeadChars = 16;
		if (value.StartsWith("b3BlbnNzaC1rZXktdjE", StringComparison.Ordinal) || value.StartsWith("MC4CAQAwBQYDK2Vw", StringComparison.Ordinal))
		{
			return true;
		}

		Span<byte> head = stackalloc byte[12];
		if (value.Length < HeadChars || !Convert.TryFromBase64String(value[..HeadChars], head, out _) || head[0] != 0x30)
		{
			return false;
		}

		// A long-form length takes 1 + n bytes, where the low bits of its first byte give n.
		int lengthBytes = head[1] < 0x80 ? 1 : 1 + (head[1] & 0x7F);
		int member = 1 + lengthBytes;
		// Version 0 or 1 is a private key. Version 3 is a PKCS12 bundle, which holds one.
		return member + 2 < head.Length && head[member] == 0x02 && head[member + 1] == 0x01 && head[member + 2] is 0x00 or 0x01 or 0x03;
	}

	/// <summary>True when base64 decodes to a PEM private key, as in a kubeconfig. A base64 certificate does not.</summary>
	public static bool IsBase64Pem(string value)
	{
		const int HeadChars = 64;
		Span<byte> head = stackalloc byte[48];
		return value.Length >= HeadChars
			&& Convert.TryFromBase64String(value[..HeadChars], head, out int written)
			&& System.Text.Encoding.ASCII.GetString(head[..written]).Contains("PRIVATE KEY", StringComparison.Ordinal);
	}

	private static int SkipDashes(string input, int position)
	{
		while (position < input.Length && input[position] == '-')
		{
			position++;
		}

		return position;
	}

	/// <summary>Two dashes are enough to be sure. With fewer, the rest of the line must name a private key.</summary>
	private static bool IsMarker(string input, int position, string marker)
	{
		int cursor = SkipDashes(input, position);
		if (string.Compare(input, cursor, marker, 0, marker.Length, StringComparison.OrdinalIgnoreCase) != 0)
		{
			return false;
		}

		return cursor - position >= 2 || NamesPrivateKey(input, cursor + marker.Length);
	}

	private static bool NamesPrivateKey(string input, int position)
	{
		int length = Math.Min(40, input.Length - position);
		return input.AsSpan(position, length).Contains("private", StringComparison.OrdinalIgnoreCase);
	}

	private static int SkipEndMarker(string input, int position)
	{
		int afterEnd = SkipDashes(input, position) + EndMarker.Length;
		int cursor = afterEnd;
		while (cursor < input.Length && (char.IsAsciiLetterOrDigit(input[cursor]) || input[cursor] is ' ' or '+' or '%'))
		{
			cursor++;
		}

		// Only a closing dash run or the words of the name prove the text belongs to the marker.
		if (cursor < input.Length && input[cursor] == '-')
		{
			return SkipDashes(input, cursor);
		}

		return NameEnd(input, afterEnd, cursor);
	}

	/// <summary>The end of <c>KEY</c> or <c>KEY BLOCK</c> in a marker with no closing dashes, so ordinary text after it survives.</summary>
	private static int NameEnd(string input, int start, int limit)
	{
		ReadOnlySpan<char> name = input.AsSpan(start, limit - start);
		int key = name.IndexOf("KEY", StringComparison.OrdinalIgnoreCase);
		return key < 0 ? start : start + key + "KEY".Length;
	}

	private static int SkipToLineEnd(string input, int position)
	{
		int limit = Math.Min(input.Length, position + MaxSkippedLineLength);
		int cursor = position;
		while (cursor < limit && input[cursor] is not ('\n' or '\r' or Redactor.MessageBoundary))
		{
			if (input[cursor] == '\\' && cursor + 1 < limit && input[cursor + 1] is 'n' or 'r')
			{
				break;
			}

			cursor++;
		}

		return cursor;
	}

	/// <summary>Skips an RFC 1421 header such as <c>Proc-Type: 4,ENCRYPTED</c>, which encrypted and PGP keys carry before the body.</summary>
	private static bool TrySkipHeaderLine(string input, ref int position)
	{
		int cursor = position;
		while (cursor < input.Length && (char.IsAsciiLetterOrDigit(input[cursor]) || input[cursor] == '-'))
		{
			cursor++;
		}

		if (cursor == position || cursor >= input.Length || input[cursor] != ':')
		{
			return false;
		}

		// A line holding key material is not a header, whatever it starts with: a grep location or a log timestamp looks like one.
		int end = SkipToLineEnd(input, cursor);
		if (FindLongRuns(input, cursor, end) is not null)
		{
			return false;
		}

		position = end;
		return true;
	}

	/// <summary>The start of the first long base64 run in the span and the end of the last, or null when it holds none.</summary>
	private static (int Start, int End)? FindLongRuns(string input, int start, int end)
	{
		(int Start, int End)? found = null;
		int cursor = start;
		while (cursor < end)
		{
			int run = Math.Min(Base64RunLength(input, cursor), end - cursor);
			if (run >= LongRunLength)
			{
				found = (found?.Start ?? cursor, cursor + run);
			}

			cursor += Math.Max(run, 1);
		}

		return found;
	}

	/// <summary>Skips a line that stands in for removed lines, such as <c>[...]</c> or <c>... 12 lines omitted ...</c>.</summary>
	private static bool TrySkipElision(string input, ref int position)
	{
		int end = SkipToLineEnd(input, position);
		ReadOnlySpan<char> line = input.AsSpan(position, end - position).Trim();
		if (line.IsEmpty || line.Length > 80 || !IsElisionText(line))
		{
			return false;
		}

		position = end;
		return true;
	}

	private static bool IsElisionText(ReadOnlySpan<char> line)
	{
		return line.Contains("...", StringComparison.Ordinal)
			|| line.Contains('…')
			|| line.Contains("snip", StringComparison.OrdinalIgnoreCase)
			|| line.Contains("truncat", StringComparison.OrdinalIgnoreCase)
			|| line.Contains("omitted", StringComparison.OrdinalIgnoreCase)
			|| line.Contains("elided", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>The walker's state across body lines.</summary>
	private ref struct Walk(string input, int start)
	{
		private readonly string _input = input;
		private int _position = start;
		private int _lastAccepted = start;
		private int _bodyLength;
		private int _longest;
		private int _prefixWidth;
		private int _endMarker = -1;
		private int _proseLeft = ProseLineBudget;
		private bool _atStart = true;
		private bool _needsFullLine;
		private bool _afterLongRun;

		/// <summary>Consumes one line. False when the block is over.</summary>
		public bool Advance()
		{
			int lineStart = _position;
			int lineFeeds = PemSeparators.Skip(_input, ref _position);
			if (lineFeeds > 0)
			{
				_position = PemSeparators.SkipLinePrefix(_input, _position);
			}

			// A short token can only be a body line straight after the previous line.
			// One past a blank line is prose: PEM has blank lines only after its headers.
			bool crossedMessage = _input.AsSpan(lineStart, _position - lineStart).Contains(Redactor.MessageBoundary);
			bool lineBreakBefore = (lineFeeds == 1 && !crossedMessage) || _atStart;
			_atStart = false;
			_afterLongRun &= lineFeeds == 0;
			return !AtBlockEnd() && AdvanceInBlock(lineBreakBefore);
		}

		public readonly int Result()
		{
			bool enoughBody = _bodyLength >= MinimumBody && (_endMarker >= 0 || _longest >= MinimumBody);
			if (!enoughBody)
			{
				return -1;
			}

			return _endMarker >= 0 ? SkipEndMarker(_input, _endMarker) : _lastAccepted;
		}

		private bool AtBlockEnd()
		{
			if (_position >= _input.Length || IsMarker(_input, _position, BeginMarker))
			{
				return true;
			}

			if (IsMarker(_input, _position, EndMarker))
			{
				_endMarker = _position;
				return true;
			}

			return false;
		}

		private bool AdvanceInBlock(bool lineBreakBefore)
		{
			if (_bodyLength == 0 && TrySkipHeaderLine(_input, ref _position))
			{
				_atStart = true;
				return true;
			}

			return TryAcceptToken(lineBreakBefore) || TryAcceptAfterPrefix() || TrySkipNonBody();
		}

		/// <summary>
		/// Whatever surrounds it, a long base64 run is a body line: a log timestamp, a grep location, a box-drawing border, a JSON
		/// value. A shorter line with the same prefix is the key's tail. A marker anywhere in the line ends the block.
		/// </summary>
		private bool TryAcceptAfterPrefix()
		{
			int lineEnd = SkipToLineEnd(_input, _position);
			if (FindEndMarkerInLine(lineEnd) is { } marker)
			{
				_position = marker;
				_endMarker = marker;
				return false;
			}

			if (FindLongRuns(_input, _position, lineEnd) is { } runs)
			{
				_prefixWidth = runs.Start - _position;
				Accept(runs.End - runs.Start, _input.AsSpan(runs.Start, runs.End - runs.Start), runs.End);
				return true;
			}

			return TryAcceptTail(lineEnd);
		}

		/// <summary>The last line of a key is the only short one, so it comes once. Words after it mean the line is prose, not a tail.</summary>
		private bool TryAcceptTail(int lineEnd)
		{
			int start = _position + _prefixWidth;
			if (_prefixWidth == 0 || start >= lineEnd)
			{
				return false;
			}

			int run = Math.Min(Base64RunLength(_input, start), lineEnd - start);
			int after = SkipAfterRun(_input, start + run);
			if (run < 4 || after < lineEnd || !LooksRandom(_input.AsSpan(start, run)))
			{
				return false;
			}

			_prefixWidth = 0;
			Accept(run, _input.AsSpan(start, run), start + run);
			return true;
		}

		private readonly int? FindEndMarkerInLine(int lineEnd)
		{
			for (int i = _position; i < lineEnd; i++)
			{
				bool startsRun = _input[i] == '-' && (i == _position || _input[i - 1] != '-');
				if (startsRun && IsMarker(_input, i, EndMarker))
				{
					return i;
				}
			}

			return null;
		}

		/// <summary>Before the key, a line of prose or a label on the marker's line. After it, an elision with more key behind it.</summary>
		private bool TrySkipNonBody()
		{
			if (_afterLongRun)
			{
				// The rest of a body line, such as "(line 2)" or a closing brace. It stays in the output, and the next line decides.
				int rest = SkipToLineEnd(_input, _position);
				bool skipped = rest > _position;
				_position = rest;
				return skipped;
			}

			if (_bodyLength > 0)
			{
				_needsFullLine = TrySkipElision(_input, ref _position);
				return _needsFullLine;
			}

			if (_proseLeft == 0)
			{
				return false;
			}

			_proseLeft--;
			_needsFullLine = true;
			int end = SkipToLineEnd(_input, _position);
			bool moved = end > _position;
			_position = end;
			return moved;
		}

		private bool TryAcceptToken(bool lineBreakBefore)
		{
			int cursor = _position;
			int units = 0;
			while (cursor < _input.Length)
			{
				int length = PemSeparators.TokenUnitLength(_input, cursor);
				if (length == 0)
				{
					break;
				}

				units++;
				cursor += length;
			}

			if (units == 0 || !IsBodyLine(units, cursor, lineBreakBefore))
			{
				return false;
			}

			Accept(units, _input.AsSpan(_position, cursor - _position), cursor);
			return true;
		}

		/// <summary>A line of padding extends the block but does not count as key material.</summary>
		private void Accept(int units, ReadOnlySpan<char> line, int end)
		{
			int real = IsMaskLine(line) ? 0 : units;
			_bodyLength += real;
			_longest = Math.Max(_longest, real);
			_position = end;
			_lastAccepted = end;
			_needsFullLine = false;
			_afterLongRun = units >= LongRunLength;
		}

		/// <summary>Short tokens count only when they fill a line or close the block, so the prose after a truncated key survives.</summary>
		private readonly bool IsBodyLine(int units, int tokenEnd, bool lineBreakBefore)
		{
			if (units >= FullLineLength)
			{
				return true;
			}

			int lookahead = tokenEnd;
			bool lineBreakAfter = PemSeparators.Skip(_input, ref lookahead) > 0 || lookahead >= _input.Length || EndsString(_input, tokenEnd);
			bool closesBlock = lookahead < _input.Length && IsMarker(_input, lookahead, EndMarker);
			return closesBlock || (lineBreakBefore && IsShortBodyLine(units, tokenEnd, lineBreakAfter));
		}

		/// <summary>A short line of a key has digits or mixed case. A plain word is the prose after it.</summary>
		private readonly bool IsShortBodyLine(int units, int tokenEnd, bool lineBreakAfter)
		{
			return !_needsFullLine
				&& LooksRandom(_input.AsSpan(_position, tokenEnd - _position))
				&& (lineBreakAfter || units >= TailBeforeProseLength);
		}

		/// <summary>A closing quote ends the line too: the last line of a key cut off inside a JSON string has no newline after it.</summary>
		private static bool EndsString(string input, int position)
		{
			int next = PemSeparators.SkipBackslashes(input, position);
			return next < input.Length && input[next] is '"' or '\'';
		}
	}
}
