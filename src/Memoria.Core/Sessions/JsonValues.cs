using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Memoria.Core.Sessions;

/// <summary>Typed access to record fields. A field of the wrong type is treated as absent, never as an error.</summary>
internal static class JsonValues
{
	private const char ReplacementCharacter = (char)0xFFFD;

	public static string? String(JsonElement holder, string name, SessionReadReport report)
	{
		return holder.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? Decode(value, report)
			: null;
	}

	/// <summary>
	/// Decodes a string, keeping one that holds an unpaired surrogate escape. The transcript's
	/// other text is worth more than failing the record, and the result must be encodable.
	/// </summary>
	public static string Decode(JsonElement value, SessionReadReport report)
	{
		try
		{
			return value.GetString()!;
		}
		catch (InvalidOperationException)
		{
			report.RepairedStrings++;
			return RepairEscapes(value.GetRawText());
		}
	}

	/// <summary>A property name, or null when it holds an unpaired surrogate and so has no usable form.</summary>
	public static string? Name(JsonProperty property, SessionReadReport report)
	{
		try
		{
			return property.Name;
		}
		catch (InvalidOperationException)
		{
			report.InvalidKeys++;
			return null;
		}
	}

	/// <summary>
	/// A message's text: a plain string, or the non-blank text blocks joined by a blank line.
	/// Both forms are stripped, as the exporters do.
	/// </summary>
	public static string BlockText(JsonElement holder, SessionReadReport report, Func<string?, bool> isTextBlock)
	{
		if (!holder.TryGetProperty("content", out JsonElement content))
		{
			return "";
		}
		return content.ValueKind switch
		{
			JsonValueKind.String => PythonText.Strip(Decode(content, report)),
			JsonValueKind.Array => JoinTextBlocks(content, report, isTextBlock),
			_ => "",
		};
	}

	private static string JoinTextBlocks(JsonElement content, SessionReadReport report, Func<string?, bool> isTextBlock)
	{
		List<string> parts = [];
		foreach (JsonElement block in content.EnumerateArray())
		{
			string stripped = BlockTextOf(block, report, isTextBlock);
			if (stripped.Length > 0)
			{
				parts.Add(stripped);
			}
		}
		return string.Join("\n\n", parts);
	}

	private static string BlockTextOf(JsonElement block, SessionReadReport report, Func<string?, bool> isTextBlock)
	{
		if (block.ValueKind != JsonValueKind.Object || !isTextBlock(String(block, "type", report)))
		{
			return "";
		}
		return PythonText.Strip(String(block, "text", report) ?? "");
	}

	private static string RepairEscapes(string raw)
	{
		ReadOnlySpan<char> body = raw.AsSpan(1, raw.Length - 2);
		StringBuilder output = new(body.Length);
		for (int i = 0; i < body.Length; i++)
		{
			if (body[i] == '\\')
			{
				i = AppendEscape(body, i + 1, output);
			}
			else
			{
				output.Append(body[i]);
			}
		}
		return output.ToString();
	}

	/// <summary>Appends the escape whose letter is at <paramref name="index"/> and returns the index of its last character.</summary>
	private static int AppendEscape(ReadOnlySpan<char> body, int index, StringBuilder output)
	{
		if (body[index] == 'u')
		{
			return AppendUnicodeEscape(body, index, output);
		}
		output.Append(body[index] switch
		{
			'b' => '\b',
			'f' => '\f',
			'n' => '\n',
			'r' => '\r',
			't' => '\t',
			char other => other, // the quote, backslash, and solidus escapes
		});
		return index;
	}

	private static int AppendUnicodeEscape(ReadOnlySpan<char> body, int index, StringBuilder output)
	{
		char unit = ReadHex(body, index + 1);
		int last = index + 4;
		if (char.IsHighSurrogate(unit) && StartsUnicodeEscape(body, last + 1) && char.IsLowSurrogate(ReadHex(body, last + 3)))
		{
			output.Append(unit).Append(ReadHex(body, last + 3));
			return last + 6;
		}
		output.Append(char.IsSurrogate(unit) ? ReplacementCharacter : unit);
		return last;
	}

	private static bool StartsUnicodeEscape(ReadOnlySpan<char> body, int index)
	{
		return body[index..].StartsWith("\\u", StringComparison.Ordinal);
	}

	private static char ReadHex(ReadOnlySpan<char> body, int start)
	{
		return (char)ushort.Parse(body.Slice(start, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
	}
}
