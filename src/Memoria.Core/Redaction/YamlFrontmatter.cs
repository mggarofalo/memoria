using System.Text;

namespace Memoria.Core.Redaction;

/// <summary>
/// Keeps frontmatter parseable after redaction. A YAML plain scalar cannot start
/// with <c>[</c>: <c>title: [REDACTED:x] rotated</c> is a syntax error, and the
/// sidecar drops a document's whole metadata when its frontmatter will not parse.
/// A value that now starts with a placeholder is single-quoted.
/// </summary>
internal static class YamlFrontmatter
{
	private const string Fence = "---";
	private const string PlaceholderStart = "[REDACTED:";

	public static string QuotePlaceholders(string text)
	{
		if (!text.StartsWith(Fence, StringComparison.Ordinal) || !text.Contains(PlaceholderStart, StringComparison.Ordinal))
		{
			return text;
		}

		StringBuilder? output = null;
		int copied = 0;
		int lineStart = NextLine(text, 0);
		while (lineStart < text.Length)
		{
			int lineEnd = LineEnd(text, lineStart);
			ReadOnlySpan<char> line = text.AsSpan(lineStart, lineEnd - lineStart);
			if (line.TrimEnd().SequenceEqual(Fence))
			{
				break;
			}

			int valueOffset = FindValueStart(line);
			if (valueOffset >= 0)
			{
				output ??= new StringBuilder(text.Length + 16);
				AppendQuoted(output, text, copied, lineStart + valueOffset, lineEnd);
				copied = lineEnd;
			}

			lineStart = NextLine(text, lineEnd);
		}

		return output is null ? text : output.Append(text, copied, text.Length - copied).ToString();
	}

	private static void AppendQuoted(StringBuilder output, string text, int copied, int valueStart, int lineEnd)
	{
		string value = text[valueStart..lineEnd];
		string trimmed = value.TrimEnd();
		output.Append(text, copied, valueStart - copied)
			.Append('\'')
			.Append(trimmed.Replace("'", "''", StringComparison.Ordinal))
			.Append('\'')
			.Append(value, trimmed.Length, value.Length - trimmed.Length);
	}

	/// <summary>The offset of a value starting with a placeholder on a <c>key: value</c> or <c>- value</c> line, or -1.</summary>
	private static int FindValueStart(ReadOnlySpan<char> line)
	{
		int offset = line.IndexOf(PlaceholderStart, StringComparison.Ordinal);
		if (offset < 2 || line[offset - 1] != ' ')
		{
			return -1;
		}

		return line[offset - 2] is ':' or '-' ? offset : -1;
	}

	private static int LineEnd(string text, int start)
	{
		int newline = text.IndexOf('\n', start);
		return newline < 0 ? text.Length : newline;
	}

	/// <summary>The start of the line after <paramref name="from"/>, or the text length when there is none.</summary>
	private static int NextLine(string text, int from)
	{
		int newline = text.IndexOf('\n', from);
		return newline < 0 ? text.Length : newline + 1;
	}
}
