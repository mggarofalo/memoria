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
/// The text cannot be read back outside this assembly. <see cref="ExposeForRedaction"/> is
/// internal and its one caller is the redactor, so the only thing anyone else can do with this
/// type is pass it there.
/// </remarks>
[JsonConverter(typeof(RefuseSerialization))]
public sealed class UnredactedMarkdown
{
	private readonly string _markdown;

	internal UnredactedMarkdown(string markdown)
	{
		_markdown = markdown;
	}

	/// <summary>Wraps text that has not been redacted. The only thing to do with the result is pass it to the redactor.</summary>
	public static UnredactedMarkdown FromText(string markdown)
	{
		ArgumentNullException.ThrowIfNull(markdown);
		return new UnredactedMarkdown(markdown);
	}

	/// <summary>Returns the unredacted text, for the redactor.</summary>
	internal string ExposeForRedaction()
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
