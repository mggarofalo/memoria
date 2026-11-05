namespace Memoria.Core.Redaction;

/// <summary>
/// An operator's setting that one collection is stored without redaction. It is what
/// <see cref="RedactedMarkdown.PassThroughForDisabledCollection"/> requires, so skipping redaction takes one of these, and the
/// only way to get one is <see cref="FromOperatorSetting"/>. Feed that from operator configuration and nothing else:
/// a caller's request must never be able to reach it.
/// </summary>
public sealed class RedactionDisabledCollection
{
	/// <summary>The one value that turns redaction off. Compared exactly, so a typo or a change of case leaves it on.</summary>
	public const string SettingValue = "Disabled";

	private RedactionDisabledCollection(string collection)
	{
		Collection = collection;
	}

	/// <summary>The collection the setting is for. Text passed through for it cannot be sent to another.</summary>
	public string Collection { get; }

	/// <summary>Reads one collection's setting. Null, meaning redact, unless the value is exactly <see cref="SettingValue"/>.</summary>
	public static RedactionDisabledCollection? FromOperatorSetting(string collection, string? configuredValue)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(collection);
		return string.Equals(configuredValue, SettingValue, StringComparison.Ordinal) ? new RedactionDisabledCollection(collection) : null;
	}
}
