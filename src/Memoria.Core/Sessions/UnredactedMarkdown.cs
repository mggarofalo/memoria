using System.Text.Json;
using System.Text.Json.Serialization;

namespace Memoria.Core.Sessions;

/// <summary>
/// Rendered markdown that has NOT been through redaction. Transcripts carry live
/// credentials, and indexing is the point of no return, so this type exists to make the
/// wrong call fail to compile: it is not a string, converts to nothing, and refuses
/// to serialize. <see cref="ToString"/> withholds the text, so it is also safe in logs.
/// </summary>
/// <remarks>
/// The only way to the text is <see cref="ExposeForRedaction"/>. Its one legitimate caller
/// is the ingest step that hands the result to the redactor, which makes misuse easy to find
/// in review with a single search.
/// </remarks>
[JsonConverter(typeof(RefuseSerialization))]
public sealed class UnredactedMarkdown
{
	private readonly string _markdown;

	internal UnredactedMarkdown(string markdown)
	{
		_markdown = markdown;
	}

	/// <summary>Returns the unredacted text. Pass it to the redactor and nowhere else.</summary>
	public string ExposeForRedaction()
	{
		return _markdown;
	}

	public override string ToString()
	{
		return "[unredacted markdown withheld]";
	}

	private sealed class RefuseSerialization : JsonConverter<UnredactedMarkdown>
	{
		public override UnredactedMarkdown Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			throw new NotSupportedException("Unredacted markdown cannot be deserialized.");
		}

		public override void Write(Utf8JsonWriter writer, UnredactedMarkdown value, JsonSerializerOptions options)
		{
			throw new NotSupportedException("Unredacted markdown cannot be serialized; redact it first.");
		}
	}
}
