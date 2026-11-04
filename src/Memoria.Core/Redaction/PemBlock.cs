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
			start = tokenStart;
			cursor = tokenStart;
			nearest = false;
		}

		bool mask = input.AsSpan(start, marker.Index - start).Count('x') * 10 >= body * 6;
		return body >= FullLineLength && !mask ? (start, marker.Index + marker.Length) : null;
	}

	/// <summary>Extends a run of base64 lines over a final, shorter line that is base64 from end to end.</summary>
	public static int ExtendLines(SecretCandidate candidate)
	{
		string input = candidate.Input;
		int lineStart = candidate.End;
		int cursor = lineStart;
		while (cursor < input.Length && (char.IsAsciiLetterOrDigit(input[cursor]) || input[cursor] is '+' or '/' or '='))
		{
			cursor++;
		}

		bool wholeLine = cursor == input.Length || input[cursor] is '\r' or '\n';
		bool plausible = cursor - lineStart >= 8 || (cursor > lineStart && input[cursor - 1] == '=');
		return wholeLine && plausible ? cursor : candidate.End;
	}

	/// <summary>True unless the run sits under a certificate or public-key marker or in a data URI, or is single-case hex such as a list of SHA-256 hashes: those are not secret.</summary>
	public static bool IsUnmarkedKeyBody(SecretCandidate candidate)
	{
		const int Lookaround = 300;
		string input = candidate.Input;
		int before = Math.Max(0, candidate.Secret.Index - Lookaround);
		ReadOnlySpan<char> preceding = input.AsSpan(before, candidate.Secret.Index - before);
		int afterLength = Math.Min(Lookaround, input.Length - candidate.End);
		ReadOnlySpan<char> following = input.AsSpan(candidate.End, afterLength);
		ReadOnlySpan<char> run = input.AsSpan(candidate.Secret.Index, candidate.Secret.Length);
		bool mixedCase = run.IndexOfAnyInRange('A', 'Z') >= 0 && run.IndexOfAnyInRange('a', 'z') >= 0;
		return mixedCase
			&& !preceding.Contains("CERTIFICATE", StringComparison.Ordinal)
			&& !preceding.Contains("PUBLIC KEY", StringComparison.Ordinal)
			&& !preceding.Contains("base64,", StringComparison.Ordinal)
			&& !following.Contains("END CERTIFICATE", StringComparison.Ordinal)
			&& !following.Contains("END PUBLIC", StringComparison.Ordinal);
	}

	/// <summary>True when base64 opens a DER private key: a SEQUENCE whose first member is the version 0 or 1, which a certificate or public key never has.</summary>
	public static bool IsDerPrivateKey(string value)
	{
		const int HeadChars = 16;
		if (value.StartsWith("b3BlbnNzaC1rZXktdjE", StringComparison.Ordinal))
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
		return member + 2 < head.Length && head[member] == 0x02 && head[member + 1] == 0x01 && head[member + 2] <= 0x01;
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

		position = SkipToLineEnd(input, cursor);
		return true;
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
		private int _xCount;
		private int _endMarker = -1;
		private int _proseLeft = ProseLineBudget;
		private bool _atStart = true;
		private bool _needsFullLine;

		/// <summary>Consumes one line. False when the block is over.</summary>
		public bool Advance()
		{
			int lineFeeds = PemSeparators.Skip(_input, ref _position);
			if (lineFeeds > 0)
			{
				_position = PemSeparators.SkipLinePrefix(_input, _position);
			}

			// A short token can only be a body line straight after the previous line.
			// One past a blank line is prose: PEM has blank lines only after its headers.
			bool lineBreakBefore = lineFeeds == 1 || _atStart;
			_atStart = false;
			return !AtBlockEnd() && AdvanceInBlock(lineBreakBefore);
		}

		public readonly int Result()
		{
			bool enoughBody = _bodyLength >= MinimumBody && (_endMarker >= 0 || _longest >= MinimumBody);
			if (!enoughBody || _xCount * 10 >= _bodyLength * 6)
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

			return TryAcceptToken(lineBreakBefore) || TrySkipNonBody();
		}

		/// <summary>Before the key, a line of prose or a label on the marker's line. After it, an elision with more key behind it.</summary>
		private bool TrySkipNonBody()
		{
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
			int xs = 0;
			while (cursor < _input.Length)
			{
				int length = PemSeparators.TokenUnitLength(_input, cursor);
				if (length == 0)
				{
					break;
				}

				xs += _input[cursor] is 'x' or 'X' ? 1 : 0;
				units++;
				cursor += length;
			}

			if (units == 0 || !IsBodyLine(units, cursor, lineBreakBefore))
			{
				return false;
			}

			Accept(units, xs, cursor);
			return true;
		}

		private void Accept(int units, int xs, int end)
		{
			_bodyLength += units;
			_longest = Math.Max(_longest, units);
			_xCount += xs;
			_position = end;
			_lastAccepted = end;
			_needsFullLine = false;
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
			return closesBlock || (!_needsFullLine && lineBreakBefore && lineBreakAfter);
		}

		/// <summary>A closing quote ends the line too: the last line of a key cut off inside a JSON string has no newline after it.</summary>
		private static bool EndsString(string input, int position)
		{
			int next = PemSeparators.SkipBackslashes(input, position);
			return next < input.Length && input[next] is '"' or '\'';
		}
	}
}
