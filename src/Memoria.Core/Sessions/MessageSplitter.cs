using System.Text;

namespace Memoria.Core.Sessions;

/// <summary>
/// Cuts a giant message into pieces that each fit a limit in UTF-8 bytes. A cut prefers the last line
/// break in the final stretch of a piece and never falls inside a surrogate pair. Each cut depends only
/// on the text before it, so text appended later changes nothing but the last piece.
/// </summary>
internal static class MessageSplitter
{
	/// <summary>How far back from the limit to look for a line break, so a cut lands between lines when one is near.</summary>
	private const int LineSearchChars = 64 * 1024;

	public static IEnumerable<SessionMessage> Split(SessionMessage message, int maxBytes)
	{
		if (Encoding.UTF8.GetByteCount(message.Text) <= maxBytes)
		{
			yield return message;
			yield break;
		}

		// Always moving forward means the loop ends after at most one piece per character, however a cut is worked out.
		int end;
		for (int start = 0; start < message.Text.Length; start = end)
		{
			end = Math.Max(NextCut(message.Text, start, maxBytes), start + 1);
			string piece = message.Text[start..end];
			yield return message with { Text = piece, CodePoints = PythonText.CodePointLength(piece) };
		}
	}

	private static int NextCut(string text, int start, int maxBytes)
	{
		int end = LastFitting(text, start, maxBytes);
		if (end == text.Length)
		{
			return end;
		}

		int searchFrom = Math.Max(start + 1, end - LineSearchChars);
		int lineBreak = text.LastIndexOf('\n', end - 1, end - searchFrom);
		return lineBreak >= 0 ? lineBreak + 1 : end;
	}

	/// <summary>The furthest index whose text from <paramref name="start"/> fits. Always advances by at least one code point.</summary>
	private static int LastFitting(string text, int start, int maxBytes)
	{
		long bytes = 0;
		int i = start;
		while (i < text.Length)
		{
			int width = char.IsSurrogatePair(text, i) ? 2 : 1;
			bytes += Utf8Bytes(text[i], width);
			if (bytes > maxBytes)
			{
				return i;
			}

			i += width;
		}

		return i;
	}

	private static int Utf8Bytes(char c, int width)
	{
		if (width == 2)
		{
			return 4;
		}

		return c switch
		{
			< (char)0x80 => 1,
			< (char)0x800 => 2,
			_ => 3,
		};
	}
}
