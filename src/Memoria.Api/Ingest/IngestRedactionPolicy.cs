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

	public IngestRedactionPolicy(IOptions<MemoriaOptions> options, IConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(options);
		AmbiguousSettings = AmbiguousNames(configuration);
		List<string> unrecognized = [];
		foreach ((string collection, IngestCollectionOptions settings) in options.Value.Ingest.Collections)
		{
			if (AmbiguousSettings.Contains(collection, StringComparer.OrdinalIgnoreCase))
			{
				continue;
			}

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

	/// <summary>Names spelled in more than one case across configuration sources. Keys ignore case when merged, so a later layer naming <c>notes</c> can move <c>Notes</c>'s setting onto a different collection. Every spelling stays redacted.</summary>
	public IReadOnlyList<string> AmbiguousSettings { get; }

	private static string[] AmbiguousNames(IConfiguration configuration)
	{
		if (configuration is not IConfigurationRoot root)
		{
			return [];
		}

		IEnumerable<string> names = root.Providers.SelectMany(static provider => provider.GetChildKeys([], Section));
		return [.. names
			.Distinct(StringComparer.Ordinal)
			.GroupBy(static name => name, StringComparer.OrdinalIgnoreCase)
			.Where(static spellings => spellings.Count() > 1)
			.Select(static spellings => spellings.Key)
			.Order(StringComparer.Ordinal)];
	}

	private const string Section = "Memoria:Ingest:Collections";

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
