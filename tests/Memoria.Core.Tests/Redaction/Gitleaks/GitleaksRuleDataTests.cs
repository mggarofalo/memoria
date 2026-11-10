using System.Text.RegularExpressions;
using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction.Gitleaks;

/// <summary>
/// The imported rule data is generated, never edited by hand. Regenerate it after changing the pinned file:
/// <code>MEMORIA_WRITE_GITLEAKS_RULES=1 dotnet test Memoria.slnx --filter GitleaksRuleData</code>
/// </summary>
public sealed class GitleaksRuleDataTests
{
	public const string WriteVariable = "MEMORIA_WRITE_GITLEAKS_RULES";

	private static readonly string s_folder = Path.Combine(RepositoryRoot(), "src", "Memoria.Core", "Redaction", "Gitleaks");

	[Fact]
	public void Committed_rule_data_matches_the_pinned_upstream_file()
	{
		string generated = GitleaksRuleGenerator.Generate(File.ReadAllBytes(Path.Combine(s_folder, "gitleaks.toml")));
		string path = Path.Combine(s_folder, "rules.json");
		if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
		{
			File.WriteAllText(path, generated);
		}

		Assert.True(
			generated == File.ReadAllText(path).ReplaceLineEndings("\n"),
			$"src/Memoria.Core/Redaction/Gitleaks/rules.json is out of date. Regenerate with {WriteVariable}=1 dotnet test Memoria.slnx --filter GitleaksRuleData");
	}

	[Fact]
	public void The_built_in_data_is_the_committed_file()
	{
		Assert.Equal(GitleaksRuleData.Parse(File.ReadAllText(Path.Combine(s_folder, "rules.json"))), GitleaksRuleData.BuiltIn, new DataComparer());
		Assert.Equal(GitleaksRuleGenerator.Version, GitleaksRuleData.BuiltIn.Source.Version);
		Assert.Equal(GitleaksRuleGenerator.Sha256, GitleaksRuleData.BuiltIn.Source.Sha256);
	}

	[Fact]
	public void Every_pattern_compiles_as_the_engine_builds_patterns()
	{
		List<string> failed = [];
		foreach (GitleaksRuleSpec rule in GitleaksRuleData.BuiltIn.Rules)
		{
			Compile(rule.Id, rule.Pattern, RedactionRules.Pattern, failed);
			foreach (string allow in rule.Allowlists.SelectMany(static allowlist => allowlist.Regexes))
			{
				Compile(rule.Id + " allowlist", allow, RedactionRules.Pattern, failed);
			}
		}

		foreach (string allow in GitleaksRuleData.BuiltIn.GlobalAllowlist.Regexes)
		{
			Compile("global allowlist", allow, RedactionRules.Pattern, failed);
		}

		Assert.True(failed.Count == 0, string.Join('\n', failed));
	}

	[Fact]
	public void A_secret_group_is_the_only_named_group()
	{
		Assert.All(GitleaksRuleData.BuiltIn.Rules, static rule =>
			Assert.All(RedactionRules.Pattern(rule.Pattern).GetGroupNames().Where(static name => !int.TryParse(name, out _)), name => Assert.Equal("secret", name)));
	}

	[Fact]
	public void The_generic_rule_and_path_rules_are_left_out_with_a_reason()
	{
		GitleaksRuleData data = GitleaksRuleData.BuiltIn;

		Assert.DoesNotContain(data.Rules, static rule => rule.Id == "generic-api-key");
		Assert.Contains(data.Excluded, static exclusion => exclusion.Id == "generic-api-key");
		Assert.Equal(
			["freemius-secret-key", "generic-api-key", "hashicorp-tf-password", "kubernetes-secret-yaml", "nuget-config-password", "pkcs12-file"],
			data.Excluded.Select(static exclusion => exclusion.Id).Order(StringComparer.Ordinal));
		Assert.All(data.Excluded, static exclusion => Assert.False(string.IsNullOrWhiteSpace(exclusion.Reason)));
	}

	[Fact]
	public void Every_upstream_rule_is_imported_or_excluded()
	{
		GitleaksRuleData data = GitleaksRuleData.BuiltIn;
		string toml = File.ReadAllText(Path.Combine(s_folder, "gitleaks.toml"));
		int upstream = Regex.Matches(toml, @"^\[\[rules\]\]$", RegexOptions.Multiline).Count;

		Assert.Equal(222, upstream);
		Assert.Equal(upstream, data.Rules.Count + data.Excluded.Count);
	}

	[Fact]
	public void Rule_ids_are_unique_and_usable_as_rule_names()
	{
		string[] ids = [.. GitleaksRuleData.BuiltIn.Rules.Select(static rule => rule.Id)];

		Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
		Assert.All(ids, static id => Assert.Matches("^[a-z0-9-]+$", id));
	}

	[Fact]
	public void Keywords_are_lower_case_and_every_rule_has_one()
	{
		Assert.All(GitleaksRuleData.BuiltIn.Rules, static rule =>
		{
			Assert.NotEmpty(rule.Keywords);
			Assert.All(rule.Keywords, keyword => Assert.Equal(keyword.ToLowerInvariant(), keyword));
		});
	}

	[Fact]
	public void The_licence_travels_with_the_build()
	{
		string licence = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "licenses", "gitleaks", "LICENSE"));

		Assert.StartsWith("MIT License", licence, StringComparison.Ordinal);
		Assert.Contains("Zachary Rice", licence, StringComparison.Ordinal);
		Assert.Equal("MIT", GitleaksRuleData.BuiltIn.Source.License);
	}

	[Fact]
	public void Malformed_data_is_refused()
	{
		Assert.Throws<System.Text.Json.JsonException>(static () => GitleaksRuleData.Parse("""{"source":null}"""));
		Assert.Throws<System.Text.Json.JsonException>(static () => GitleaksRuleData.Parse("""{"surprise":1}"""));
		Assert.Throws<InvalidDataException>(static () => GitleaksRuleData.Parse("null"));
	}

	[Fact]
	public void A_changed_upstream_file_is_refused_until_the_pin_moves()
	{
		byte[] toml = File.ReadAllBytes(Path.Combine(s_folder, "gitleaks.toml"));
		toml[^2] ^= 1;

		Assert.Throws<InvalidDataException>(() => GitleaksRuleGenerator.Generate(toml));
	}

	private static void Compile(string what, string pattern, Func<string, Regex> build, List<string> failed)
	{
		try
		{
			build(pattern);
		}
		catch (ArgumentException exception)
		{
			failed.Add($"{what}: {exception.Message}");
		}
	}

	private static string RepositoryRoot()
	{
		string? directory = AppContext.BaseDirectory;
		while (directory is not null && !File.Exists(Path.Combine(directory, "Memoria.slnx")))
		{
			directory = Path.GetDirectoryName(directory);
		}

		return directory ?? throw new DirectoryNotFoundException("Memoria.slnx not found above " + AppContext.BaseDirectory);
	}

	/// <summary>Records hold lists, which compare by reference, so the comparison goes through their JSON.</summary>
	private sealed class DataComparer : IEqualityComparer<GitleaksRuleData>
	{
		public bool Equals(GitleaksRuleData? x, GitleaksRuleData? y)
		{
			return System.Text.Json.JsonSerializer.Serialize(x, GitleaksRuleData.JsonOptions) == System.Text.Json.JsonSerializer.Serialize(y, GitleaksRuleData.JsonOptions);
		}

		public int GetHashCode(GitleaksRuleData obj) => 0;
	}
}
