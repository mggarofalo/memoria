using System.Text.Json;
using System.Text.Json.Serialization;

namespace Memoria.Core.Redaction;

/// <summary>
/// Gitleaks's default rules, as data: <c>Redaction/Gitleaks/rules.json</c>, generated from the pinned upstream
/// <c>gitleaks.toml</c> beside it by a test in <c>Memoria.Core.Tests</c>. Patterns are already translated from RE2 to .NET,
/// and the span to replace is the <c>secret</c> group. See <c>Redaction/Gitleaks/README.md</c>.
/// </summary>
internal sealed record GitleaksRuleData(GitleaksSource Source, IReadOnlyList<GitleaksExclusion> Excluded, GitleaksAllowlist GlobalAllowlist, IReadOnlyList<GitleaksRuleSpec> Rules)
{
	private const string ResourceName = "Memoria.Core.Redaction.Gitleaks.rules.json";

	public static JsonSerializerOptions JsonOptions { get; } = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
		RespectNullableAnnotations = true,
		RespectRequiredConstructorParameters = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	};

	private static readonly Lazy<GitleaksRuleData> s_builtIn = new(Load);

	/// <summary>The data built into this assembly, read on first use.</summary>
	public static GitleaksRuleData BuiltIn => s_builtIn.Value;

	public static GitleaksRuleData Parse(string json)
	{
		return JsonSerializer.Deserialize<GitleaksRuleData>(json, JsonOptions) ?? throw new InvalidDataException("The gitleaks rule data is empty.");
	}

	private static GitleaksRuleData Load()
	{
		using Stream stream = typeof(GitleaksRuleData).Assembly.GetManifestResourceStream(ResourceName)
			?? throw new InvalidDataException($"{ResourceName} is not embedded.");
		using StreamReader reader = new(stream);
		return Parse(reader.ReadToEnd());
	}
}

/// <summary>Where the rules came from. <see cref="Sha256"/> is of the upstream file exactly as published.</summary>
internal sealed record GitleaksSource(string Project, string Version, string Commit, string File, string Sha256, string License);

/// <summary>An upstream rule left out, and why.</summary>
internal sealed record GitleaksExclusion(string Id, string Reason);

/// <summary>
/// What makes a finding not a secret. Upstream applies regexes to <see cref="Target"/> and stopwords to the secret. With
/// <see cref="GitleaksCondition.And"/> both kinds must hit; otherwise either does. Path and commit conditions are dropped,
/// because a document has neither, and an AND allowlist that had one is dropped whole, as it could otherwise allow more.
/// </summary>
internal sealed record GitleaksAllowlist(GitleaksCondition Condition, GitleaksTarget Target, IReadOnlyList<string> Regexes, IReadOnlyList<string> Stopwords);

internal enum GitleaksCondition
{
	Or,
	And,
}

internal enum GitleaksTarget
{
	Secret,
	Match,
	Line,
}

/// <summary>
/// One upstream rule. <see cref="Pattern"/> is .NET syntax with the secret as the <c>secret</c> group, or the whole match
/// when there is none. <see cref="UpstreamPattern"/> is the original, kept for review. A rule fires only when the text holds
/// one of <see cref="Keywords"/>, ignoring case, and only when the secret's Shannon entropy is above <see cref="Entropy"/>.
/// </summary>
internal sealed record GitleaksRuleSpec(
	string Id,
	string Description,
	string Pattern,
	string UpstreamPattern,
	IReadOnlyList<string> Keywords,
	double Entropy,
	IReadOnlyList<GitleaksAllowlist> Allowlists);
