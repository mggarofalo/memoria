using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Memoria.Core.Redaction;
using Tomlyn;
using Tomlyn.Model;

namespace Memoria.Core.Tests.Redaction.Gitleaks;

/// <summary>
/// Turns the pinned upstream <c>gitleaks.toml</c> into <c>rules.json</c>. To move to a new release, replace the TOML and the LICENSE
/// beside it with that release's, change <see cref="Version"/>, <see cref="Commit"/> and <see cref="Sha256"/>, and regenerate:
/// <code>MEMORIA_WRITE_GITLEAKS_RULES=1 dotnet test Memoria.slnx --filter GitleaksRuleData</code>
/// </summary>
public static class GitleaksRuleGenerator
{
	public const string Version = "v8.30.1";
	public const string Commit = "83d9cd684c87d95d656c1458ef04895a7f1cbd8e";
	public const string Sha256 = "e163e53b9e7e8a8511e77271e2b323ed057759542a6d988258afe3a1fa329caf";

	/// <summary>Rules left out by choice, with the reason. Rules gated on a file path are left out by <see cref="Exclusion"/>.</summary>
	private static readonly Dictionary<string, string> s_leftOut = new(StringComparer.Ordinal)
	{
		["generic-api-key"] = "Left out on purpose. Its only judge is entropy, which reads commit hashes, UUIDs and module checksums under a key-like name as secrets. "
			+ "Memoria's secret-assignment rule covers the same assignments with a judge that reads the name and the value.",
	};

	/// <summary>
	/// Upstream patterns corrected on the way in, by their exact upstream text. Only a correction that stops a pattern hiding
	/// secrets belongs here. Each must still apply, which <see cref="Generate"/> checks.
	/// </summary>
	private static readonly Dictionary<string, (string Replacement, string Reason)> s_patches = new(StringComparer.Ordinal)
	{
		["(?i)^true|false|null$"] = ("(?i)^(?:true|false|null)$", "Without a group the anchors bind to the first and last words only, so any secret containing \"false\", starting with \"true\" or ending in \"null\" was allowed."),
	};

	private static readonly JsonSerializerOptions s_write = new(GitleaksRuleData.JsonOptions)
	{
		WriteIndented = true,
		IndentSize = 2,
		NewLine = "\n",
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// <summary>The JSON that <c>rules.json</c> must hold, from the TOML's exact bytes.</summary>
	public static string Generate(byte[] toml)
	{
		string hash = Convert.ToHexStringLower(SHA256.HashData(toml));
		if (hash != Sha256)
		{
			throw new InvalidDataException($"gitleaks.toml has SHA-256 {hash}, not the pinned {Sha256}. Update the pin with the file.");
		}

		TomlTable model = TomlSerializer.Deserialize<TomlTable>(Encoding.UTF8.GetString(toml))!;
		List<GitleaksExclusion> excluded = [];
		List<GitleaksRuleSpec> rules = [];
		List<GitleaksPatch> patches = [];
		foreach (TomlTable rule in (TomlTableArray)model["rules"])
		{
			if (Exclusion(rule) is { } exclusion)
			{
				excluded.Add(exclusion);
			}
			else
			{
				rules.Add(Rule(rule, patches));
			}
		}

		GitleaksAllowlist global = Global((TomlTable)model["allowlist"], patches);
		string[] unused = [.. s_patches.Keys.Except(patches.Select(static patch => patch.Upstream), StringComparer.Ordinal)];
		if (unused.Length > 0)
		{
			throw new InvalidDataException("These patches no longer apply to the pinned file: " + string.Join(", ", unused));
		}

		GitleaksSource source = new("gitleaks", Version, Commit, "config/gitleaks.toml", hash, "MIT");
		GitleaksRuleData data = new(source, excluded, patches, global, rules);
		return JsonSerializer.Serialize(data, s_write) + "\n";
	}

	/// <summary>The allowlist pattern translated, with any correction applied and recorded.</summary>
	private static string AllowPattern(string upstream, string where, List<GitleaksPatch> patches)
	{
		if (!s_patches.TryGetValue(upstream, out (string Replacement, string Reason) patch))
		{
			return Re2Translator.TranslatePlain(upstream);
		}

		patches.Add(new GitleaksPatch(where, upstream, patch.Replacement, patch.Reason));
		return Re2Translator.TranslatePlain(patch.Replacement);
	}

	private static GitleaksExclusion? Exclusion(TomlTable rule)
	{
		string id = (string)rule["id"];
		if (s_leftOut.TryGetValue(id, out string? reason))
		{
			return new GitleaksExclusion(id, reason);
		}

		if (rule.TryGetValue("path", out object? path))
		{
			return new GitleaksExclusion(id, $"Upstream applies it only to files whose path matches {path}. A document has no path, so applying it everywhere would change what it means.");
		}

		return rule.ContainsKey("regex") ? null : new GitleaksExclusion(id, "It has no pattern to match text with.");
	}

	private static GitleaksRuleSpec Rule(TomlTable rule, List<GitleaksPatch> patches)
	{
		string upstream = (string)rule["regex"];
		int secretGroup = rule.TryGetValue("secretGroup", out object? group) ? Convert.ToInt32(group, System.Globalization.CultureInfo.InvariantCulture) : 0;
		return new GitleaksRuleSpec(
			(string)rule["id"],
			(string)rule["description"],
			Re2Translator.TranslateRule(upstream, secretGroup),
			upstream,
			[.. Strings(rule, "keywords").Select(static keyword => keyword.ToLowerInvariant()).Distinct(StringComparer.Ordinal)],
			rule.TryGetValue("entropy", out object? entropy) ? Convert.ToDouble(entropy, System.Globalization.CultureInfo.InvariantCulture) : 0,
			[.. Allowlists(rule).Select(allowlist => Allowlist(allowlist, (string)rule["id"], patches)).OfType<GitleaksAllowlist>()]);
	}

	private static IEnumerable<TomlTable> Allowlists(TomlTable rule)
	{
		IEnumerable<TomlTable> many = rule.TryGetValue("allowlists", out object? list) ? ((TomlTableArray)list).Cast<TomlTable>() : [];
		return rule.TryGetValue("allowlist", out object? one) ? many.Append((TomlTable)one) : many;
	}

	/// <summary>Null when nothing is left once paths and commits are gone, or when an AND allowlist needed one of them.</summary>
	private static GitleaksAllowlist? Allowlist(TomlTable allowlist, string id, List<GitleaksPatch> patches)
	{
		bool and = allowlist.TryGetValue("condition", out object? condition) && ((string)condition).Equals("AND", StringComparison.OrdinalIgnoreCase);
		bool hadPathOrCommit = allowlist.ContainsKey("paths") || allowlist.ContainsKey("commits");
		string[] regexes = [.. Strings(allowlist, "regexes").Select(regex => AllowPattern(regex, id, patches))];
		string[] stopwords = [.. Strings(allowlist, "stopwords")];
		if ((and && hadPathOrCommit) || (regexes.Length == 0 && stopwords.Length == 0))
		{
			return null;
		}

		return new GitleaksAllowlist(and ? GitleaksCondition.And : GitleaksCondition.Or, Target(allowlist), regexes, stopwords);
	}

	private static GitleaksTarget Target(TomlTable allowlist)
	{
		return allowlist.TryGetValue("regexTarget", out object? target)
			? Enum.Parse<GitleaksTarget>((string)target, ignoreCase: true)
			: GitleaksTarget.Secret;
	}

	private static GitleaksAllowlist Global(TomlTable allowlist, List<GitleaksPatch> patches)
	{
		string[] regexes = [.. Strings(allowlist, "regexes").Select(regex => AllowPattern(regex, "global allowlist", patches))];
		return new GitleaksAllowlist(GitleaksCondition.Or, GitleaksTarget.Secret, regexes, [.. Strings(allowlist, "stopwords")]);
	}

	private static IEnumerable<string> Strings(TomlTable table, string key)
	{
		return table.TryGetValue(key, out object? value) ? ((TomlArray)value).Cast<string>() : [];
	}
}
