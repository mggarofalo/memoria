using Memoria.Api.Configuration;
using Memoria.Core.Contracts;
using Memoria.Core.Redaction;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Ingest;

/// <summary>
/// Whether a collection is redacted, decided once at startup from <c>Memoria:Ingest:Collections</c> and nowhere else.
/// A request contributes only the collection name it is looked up by, and a name not listed is redacted. This is the only
/// caller of <see cref="RedactionDisabledCollection.FromOperatorSetting"/>.
/// </summary>
public sealed class IngestRedactionPolicy
{
	private readonly Dictionary<string, RedactionDisabledCollection> _disabled = new(StringComparer.Ordinal);

	public IngestRedactionPolicy(IOptions<MemoriaOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		List<string> unrecognized = [];
		foreach ((string collection, IngestCollectionOptions settings) in options.Value.Ingest.Collections)
		{
			if (RedactionDisabledCollection.FromOperatorSetting(collection, settings.Redaction) is { } disabled)
			{
				_disabled[collection] = disabled;
			}
			else if (settings.Redaction is not null)
			{
				unrecognized.Add(collection);
			}
		}

		DisabledCollections = [.. _disabled.Keys.Order(StringComparer.Ordinal)];
		UnrecognizedSettings = [.. unrecognized.Order(StringComparer.Ordinal)];
	}

	/// <summary>Collections stored without redaction.</summary>
	public IReadOnlyList<string> DisabledCollections { get; }

	/// <summary>Collections with a setting that is not exactly the off value, so they stay redacted.</summary>
	public IReadOnlyList<string> UnrecognizedSettings { get; }

	/// <summary>The setting for a collection, or null to redact, which is the answer for every collection not listed.</summary>
	public RedactionDisabledCollection? For(string collection)
	{
		return _disabled.GetValueOrDefault(collection);
	}

	public static string NameOf(RedactionDisabledCollection? disabled)
	{
		return disabled is null ? IngestRedaction.Enabled : IngestRedaction.Disabled;
	}
}
