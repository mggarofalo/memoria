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
	public void The_upstream_allowlist_that_hid_secrets_is_corrected_and_recorded()
	{
		Assert.Contains(GitleaksRuleData.BuiltIn.Patches, static patch => patch.Where == "global allowlist");
		Regex corrected = RedactionRules.Pattern(Assert.Single(GitleaksRuleData.BuiltIn.GlobalAllowlist.Regexes, static regex => regex.Contains("false", StringComparison.Ordinal)));

		Assert.Matches(corrected, "False");
		Assert.DoesNotMatch(corrected, "a8falsez0Qx");
		Assert.DoesNotMatch(corrected, "trueQ8x0aZ");
	}

	[Fact]
	public void Sourcegraph_no_longer_reads_a_commit_hash_as_its_token()
	{
		Assert.Contains(GitleaksRuleData.BuiltIn.Patches, static patch => patch.Where == "sourcegraph-access-token");
		Regex pattern = RedactionRules.Pattern(GitleaksRuleData.BuiltIn.Rules.Single(static rule => rule.Id == "sourcegraph-access-token").Pattern);
		string hash = "fd9b778c57587e23e883c887350ea537710bee7c";

		Assert.DoesNotMatch(pattern, "def ixasgp(): \"commit\":\"" + hash + "\"");
		Assert.Equal("sg" + "p_" + hash, pattern.Match("token sg" + "p_" + hash + " ").Groups["secret"].Value);
	}

	[Theory]
	[InlineData("é", "é")]
	[InlineData("トークン", "。")]
	[InlineData("‍", "‍")]
	[InlineData("", "")]
	public void A_token_touching_a_non_ascii_letter_keeps_its_boundary(string before, string after)
	{
		string token = "AK" + "IA" + "QWERTYUIOPASDFGH";
		Regex pattern = RedactionRules.Pattern(GitleaksRuleData.BuiltIn.Rules.Single(static rule => rule.Id == "aws-access-token").Pattern);

		Assert.Equal(token, pattern.Match(before + token + after).Groups["secret"].Value);
		Assert.DoesNotMatch(pattern, "x" + token);
		Assert.DoesNotMatch(pattern, token + "x");
	}

	/// <summary>
	/// A boundary or terminator outside the secret takes a character, so the data assumes the next search starts where the secret
	/// ended, not where the match did, as the engine's does. Then the separator between two tokens is read again.
	/// </summary>
	[Theory]
	[InlineData("aws-access-token", " ")]
	[InlineData("aws-access-token", ",")]
	[InlineData("aws-access-token", "\n")]
	[InlineData("stripe-access-token", " ")]
	[InlineData("stripe-access-token", "\n")]
	[InlineData("perplexity-api-key", " ")]
	public void Tokens_one_separator_apart_are_each_found_when_the_search_resumes_at_the_secret_s_end(string id, string separator)
	{
		string[] tokens = id switch
		{
			"aws-access-token" => ["AK" + "IAQWERTYUIOPASDFGH", "AK" + "IAZXCVBNMLKJHGFDSQ"],
			"stripe-access-token" => ["sk" + "_live_51Hq2xYzAbCdEfGhIjKlMnOp", "sk" + "_live_61Zq2xYzAbCdEfGhIjKlMnOq"],
			_ => ["pp" + "lx-" + Synthetic.Body(48, 4102), "pp" + "lx-" + Synthetic.Body(48, 4103)],
		};
		Regex pattern = RedactionRules.Pattern(GitleaksRuleData.BuiltIn.Rules.Single(rule => rule.Id == id).Pattern);
		string text = string.Join(separator, tokens);

		List<string> found = [];
		for (Match match = pattern.Match(text); match.Success; match = pattern.Match(text, match.Groups["secret"].Index + match.Groups["secret"].Length))
		{
			found.Add(match.Groups["secret"].Value);
		}

		Assert.Equal(tokens, found);
	}

	/// <summary>
	/// A <c>\b</c> left in a pattern is Unicode-aware, so it is allowed only away from the secret's edges, where it reads a keyword
	/// or a command name. Any new one is a decision to make, not an accident.
	/// </summary>
	[Fact]
	public void Only_these_rules_keep_a_unicode_word_boundary()
	{
		string[] kept = [.. GitleaksRuleData.BuiltIn.Rules.Where(static rule => rule.Pattern.Contains(@"\b", StringComparison.Ordinal)).Select(static rule => rule.Id).Order(StringComparer.Ordinal)];

		Assert.Equal(["atlassian-api-token", "curl-auth-header", "curl-auth-user", "openai-api-key", "sidekiq-sensitive-url"], kept);
	}

	[Fact]
	public void A_token_that_ends_with_a_terminator_does_not_take_it()
	{
		Regex pattern = RedactionRules.Pattern(GitleaksRuleData.BuiltIn.Rules.Single(static rule => rule.Id == "sentry-org-token").Pattern);
		string token = "sntrys_" + "eyJpYXQiO" + "abcdefghij" + "LCJyZWdpb25fdXJs" + "abcdefghij" + "_" + new string('Q', 43);

		Assert.Equal(token, pattern.Match("\"" + token + "\" next").Groups["secret"].Value);
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
