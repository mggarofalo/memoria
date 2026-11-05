using System.Text.Json;
using System.Text.Json.Serialization;
using Memoria.Core.Sessions;

namespace Memoria.Core.Redaction;

/// <summary>
/// Markdown that has been through the redaction decision, and the only thing the ingest sink accepts. The
/// constructor is internal, so text cannot reach the index without passing that decision: skipping it fails
/// to compile. There are two ways through. <see cref="Redactor"/> redacts, which is the default and the
/// only way for any collection an operator has not named. <see cref="PassThroughForDisabledCollection"/> returns
/// the text untouched, for one collection an operator turned redaction off for, and needs the
/// <see cref="RedactionDisabledCollection"/> that proves it. <see cref="PassThroughCollection"/> tells them apart.
/// </summary>
[JsonConverter(typeof(RedactedMarkdownJsonConverter))]
public sealed class RedactedMarkdown
{
	internal RedactedMarkdown(string text, IReadOnlyDictionary<string, int> countsByRule)
	{
		Text = text;
		CountsByRule = countsByRule;
	}

	/// <summary>
	/// The one way to this type without redacting. Returns the text as it is, for storing only in the collection
	/// <paramref name="disabled"/> names. The sidecar client refuses to send it anywhere else.
	/// </summary>
	public static RedactedMarkdown PassThroughForDisabledCollection(UnredactedMarkdown markdown, RedactionDisabledCollection disabled)
	{
		ArgumentNullException.ThrowIfNull(markdown);
		ArgumentNullException.ThrowIfNull(disabled);
		return new RedactedMarkdown(markdown.ExposeForRedaction(), new Dictionary<string, int>()) { PassThroughCollection = disabled.Collection };
	}

	/// <summary>The text: redacted, unless <see cref="PassThroughCollection"/> is set.</summary>
	public string Text { get; }

	/// <summary>The collection this text is for when redaction was turned off for it, and null when it was redacted.</summary>
	public string? PassThroughCollection { get; private init; }

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
		throw new NotSupportedException("Redacted markdown cannot be deserialized; only the redaction decision creates it.");
	}

	public override void Write(Utf8JsonWriter writer, RedactedMarkdown value, JsonSerializerOptions options)
	{
		writer.WriteStringValue(value.Text);
	}
}
