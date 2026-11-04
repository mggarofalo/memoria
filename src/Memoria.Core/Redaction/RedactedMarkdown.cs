using System.Text.Json;
using System.Text.Json.Serialization;

namespace Memoria.Core.Redaction;

/// <summary>
/// Markdown that has been through redaction, and the only thing the ingest sink accepts. The
/// constructor is internal and only <see cref="Redactor"/> calls it, so text cannot reach the
/// index without passing the redactor: skipping it fails to compile.
/// </summary>
[JsonConverter(typeof(RedactedMarkdownJsonConverter))]
public sealed class RedactedMarkdown
{
	internal RedactedMarkdown(string text, IReadOnlyDictionary<string, int> countsByRule)
	{
		Text = text;
		CountsByRule = countsByRule;
	}

	/// <summary>The redacted text.</summary>
	public string Text { get; }

	/// <summary>
	/// Replacements made in producing this text, by rule name. Never holds a matched value. A session part
	/// was redacted before it was assembled, so its counts are empty and the session carries the totals.
	/// </summary>
	public IReadOnlyDictionary<string, int> CountsByRule { get; }

	public override string ToString()
	{
		return Text;
	}
}

/// <summary>Serializes as a plain string for the wire. Reading one back would forge the type, so it is refused.</summary>
internal sealed class RedactedMarkdownJsonConverter : JsonConverter<RedactedMarkdown>
{
	public override RedactedMarkdown Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		throw new NotSupportedException("Redacted markdown cannot be deserialized; only the redactor creates it.");
	}

	public override void Write(Utf8JsonWriter writer, RedactedMarkdown value, JsonSerializerOptions options)
	{
		writer.WriteStringValue(value.Text);
	}
}
