using System.Buffers;
using System.Globalization;
using System.Text;

namespace Memoria.Core.Sessions;

/// <summary>
/// Writes frontmatter and message bodies. Every string value goes through <see cref="Quote"/>, and
/// nothing else is ever interpolated into a YAML line, so a value can only ever be a value.
/// </summary>
internal static class SessionFrontmatter
{
	/// <summary>
	/// The longest metadata value kept, in UTF-16 units. The sidecar reads only a document's first
	/// 8 KiB for frontmatter, and past that the whole document loses its metadata.
	/// </summary>
	public const int MaxFieldChars = 1024;

	/// <summary>
	/// Written as <c>\uXXXX</c>: C0 and C1 controls, DEL, NEL (a line break to YAML 1.1 parsers), the
	/// Unicode line and paragraph separators, a BOM, and non-characters.
	/// </summary>
	private static readonly SearchValues<char> s_mustEscape = SearchValues.Create(
		[.. Enumerable.Range(0, 0x20).Concat(Enumerable.Range(0x7F, 0x21)).Concat([0x2028, 0x2029, 0xFEFF, 0xFFFE, 0xFFFF]).Select(static code => (char)code)]);

	public static string Clip(string value, SessionReadReport? report = null, int maxChars = MaxFieldChars)
	{
		if (value.Length <= maxChars)
		{
			return value;
		}
		report?.ClippedFields += 1;
		return value[..(char.IsHighSurrogate(value[maxChars - 1]) ? maxChars - 1 : maxChars)];
	}

	/// <summary>
	/// A YAML double-quoted scalar. Identical to the exporters' <c>json.dumps</c> for ordinary text,
	/// and also escapes what JSON permits raw but YAML does not. Inside double quotes <c>---</c>,
	/// <c>&amp;</c>, <c>*</c>, <c>:</c> and <c>#</c> are ordinary text, so anchors and aliases need
	/// no special handling.
	/// </summary>
	public static string Quote(string value)
	{
		StringBuilder output = new(value.Length + 2);
		output.Append('"');
		for (int i = 0; i < value.Length; i++)
		{
			if (char.IsSurrogate(value[i]))
			{
				i = AppendSurrogate(value, i, output);
			}
			else
			{
				AppendChar(value[i], output);
			}
		}
		return output.Append('"').ToString();
	}

	public static void Scalar(StringBuilder output, string key, string value)
	{
		output.Append(key).Append(": ").Append(Quote(value)).Append('\n');
	}

	public static void Number(StringBuilder output, string key, int value)
	{
		output.Append(key).Append(": ").Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
	}

	/// <summary>A block sequence, or an inline empty one, as the exporters write them.</summary>
	public static void Sequence(StringBuilder output, string key, IReadOnlyCollection<string> values)
	{
		if (values.Count == 0)
		{
			output.Append(key).Append(": []\n");
			return;
		}
		output.Append(key).Append(":\n");
		foreach (string value in values)
		{
			output.Append("  - ").Append(Quote(value)).Append('\n');
		}
	}

	public static void FlowSequence(StringBuilder output, string key, IEnumerable<string> values)
	{
		output.Append(key).Append(": [").AppendJoin(", ", values.Select(Quote)).Append("]\n");
	}

	/// <summary>The blank line after the frontmatter, the optional title, and each message.</summary>
	public static void Body(StringBuilder output, string? title, ArraySegment<SessionMessage> messages)
	{
		output.Append('\n');
		if (title is not null)
		{
			output.Append("# ").Append(title).Append("\n\n");
		}
		for (int i = 0; i < messages.Count; i++)
		{
			output.Append(i > 0 ? "\n" : "");
			output.Append(messages[i].IsUser ? "## User\n\n" : "## Assistant\n\n").Append(messages[i].Text).Append('\n');
		}
	}

	private static void AppendChar(char c, StringBuilder output)
	{
		string? shortEscape = ShortEscape(c);
		if (shortEscape is not null)
		{
			output.Append(shortEscape);
		}
		else if (s_mustEscape.Contains(c))
		{
			output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
		}
		else
		{
			output.Append(c);
		}
	}

	private static string? ShortEscape(char c)
	{
		return c switch
		{
			'"' => "\\\"",
			'\\' => "\\\\",
			'\n' => "\\n",
			'\r' => "\\r",
			'\t' => "\\t",
			'\f' => "\\f",
			'\b' => "\\b",
			_ => null,
		};
	}

	private static int AppendSurrogate(string value, int index, StringBuilder output)
	{
		if (index + 1 < value.Length && char.IsSurrogatePair(value[index], value[index + 1]))
		{
			output.Append(value[index]).Append(value[index + 1]);
			return index + 1;
		}
		output.Append("\\ufffd");
		return index;
	}
}
