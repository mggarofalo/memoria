using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Memoria.Api.Configuration;
using Memoria.Api.Http;

namespace Memoria.Api.Tests;

/// <summary>
/// <c>docs/configuration.md</c> against the code it describes. Its key tables must name every option and no other,
/// with the default <see cref="MemoriaOptions"/> holds, and its route tables must name every operation in
/// <c>src/Memoria.Api/openapi.json</c>. <c>appsettings.json</c> must repeat the code's defaults, since it is what applies.
/// A default written in backticks is compared as text; any other wording, such as "unset", means the code holds none.
/// </summary>
public sealed partial class DocsMatchCodeTests
{
	/// <summary>Keys the host reads from configuration directly, outside <see cref="MemoriaOptions"/>.</summary>
	private static readonly string[] UnboundKeys = [RequestDetailLogging.Setting];

	[Fact]
	public void EveryOptionIsDocumented_AndEveryDocumentedKeyIsAnOption()
	{
		IEnumerable<string> known = Defaults().Keys.Concat(UnboundKeys);
		Dictionary<string, string> documented = DocumentedKeys();

		string[] missing = [.. known.Except(documented.Keys).Order(StringComparer.Ordinal)];
		string[] unknown = [.. documented.Keys.Except(known).Order(StringComparer.Ordinal)];

		Assert.True(missing.Length == 0, "Options missing from docs/configuration.md: " + string.Join(", ", missing));
		Assert.True(unknown.Length == 0, "docs/configuration.md documents keys that are not options: " + string.Join(", ", unknown));
	}

	[Fact]
	public void DocumentedDefaults_MatchTheCode()
	{
		Dictionary<string, string?> defaults = Defaults();
		List<string> wrong = [];
		foreach ((string key, string cell) in DocumentedKeys())
		{
			if (!defaults.TryGetValue(key, out string? actual))
			{
				continue;
			}

			Match literal = Backticked().Match(cell);
			string? documented = literal.Success ? literal.Groups[1].Value : null;
			if (!string.Equals(documented, actual, StringComparison.Ordinal))
			{
				wrong.Add($"{key}: documented {cell}, code has {actual ?? "none"}");
			}
		}

		Assert.True(wrong.Count == 0, "docs/configuration.md defaults differ from MemoriaOptions:\n" + string.Join('\n', wrong));
	}

	[Fact]
	public void AppSettings_RepeatsTheCodeDefaults()
	{
		Dictionary<string, string?> defaults = Defaults();
		using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(RepoFile("src", "Memoria.Api", "appsettings.json")));
		Dictionary<string, string?> committed = [];
		Flatten(settings.RootElement.GetProperty(MemoriaOptions.SectionName), MemoriaOptions.SectionName, committed);

		List<string> wrong = [];
		foreach ((string key, string? value) in committed)
		{
			if (!defaults.TryGetValue(key, out string? actual))
			{
				wrong.Add($"{key}: not an option");
			}
			else if (!string.Equals(value, actual, StringComparison.Ordinal))
			{
				wrong.Add($"{key}: appsettings.json has {value ?? "null"}, code has {actual ?? "none"}");
			}
		}

		Assert.True(wrong.Count == 0, "appsettings.json differs from MemoriaOptions:\n" + string.Join('\n', wrong));
	}

	[Fact]
	public void EveryOperation_IsInARouteTable_AndEveryDocumentedRouteExists()
	{
		using JsonDocument spec = JsonDocument.Parse(File.ReadAllText(RepoFile("src", "Memoria.Api", "openapi.json")));
		HashSet<string> operations = new(StringComparer.Ordinal);
		foreach (JsonProperty path in spec.RootElement.GetProperty("paths").EnumerateObject())
		{
			foreach (JsonProperty method in path.Value.EnumerateObject())
			{
				operations.Add($"{method.Name.ToUpperInvariant()} {path.Name}");
			}
		}

		HashSet<string> documented = new(StringComparer.Ordinal);
		foreach (string line in File.ReadLines(RepoFile("docs", "configuration.md")))
		{
			Match row = RouteRow().Match(line);
			if (row.Success)
			{
				documented.Add(row.Groups[1].Value);
			}
		}

		string[] missing = [.. operations.Except(documented).Order(StringComparer.Ordinal)];
		string[] unknown = [.. documented.Except(operations).Order(StringComparer.Ordinal)];
		Assert.True(missing.Length == 0, "Operations missing from docs/configuration.md's route tables: " + string.Join(", ", missing));
		Assert.True(unknown.Length == 0, "docs/configuration.md lists routes openapi.json does not have: " + string.Join(", ", unknown));
	}

	/// <summary>Every bound key under <c>Memoria</c>, with its default as the docs write it. A dictionary's key is <c>&lt;name&gt;</c>.</summary>
	private static Dictionary<string, string?> Defaults()
	{
		Dictionary<string, string?> keys = new(StringComparer.Ordinal);
		Walk(new MemoriaOptions(), MemoriaOptions.SectionName, keys);
		return keys;
	}

	private static void Walk(object options, string prefix, Dictionary<string, string?> keys)
	{
		foreach (PropertyInfo property in options.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (!property.CanWrite)
			{
				continue;
			}

			string key = $"{prefix}:{property.Name}";
			Type type = property.PropertyType;
			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
			{
				Walk(Activator.CreateInstance(type.GetGenericArguments()[1])!, key + ":<name>", keys);
			}
			else if (type.IsClass && type.Namespace == typeof(MemoriaOptions).Namespace)
			{
				Walk(property.GetValue(options)!, key, keys);
			}
			else
			{
				keys.Add(key, Format(property.GetValue(options)));
			}
		}
	}

	/// <summary>A default as the docs write it, or null for none: unset, or an empty list.</summary>
	private static string? Format(object? value)
	{
		return value switch
		{
			null => null,
			string text => text,
			bool flag => flag ? "true" : "false",
			TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
			IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
			IEnumerable list when !list.GetEnumerator().MoveNext() => null,
			_ => throw new InvalidOperationException($"No doc format for {value.GetType()}; add one here."),
		};
	}

	private static void Flatten(JsonElement element, string prefix, Dictionary<string, string?> keys)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty child in element.EnumerateObject())
			{
				Flatten(child.Value, $"{prefix}:{child.Name}", keys);
			}

			return;
		}

		keys.Add(prefix, element.ValueKind switch
		{
			JsonValueKind.Null => null,
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			JsonValueKind.String => element.GetString(),
			JsonValueKind.Array when element.GetArrayLength() == 0 => null,
			_ => element.GetRawText(),
		});
	}

	/// <summary>Key to default cell, from each <c>| Key | Default |</c> table under a <c>## `Memoria:…`</c> heading.</summary>
	private static Dictionary<string, string> DocumentedKeys()
	{
		Dictionary<string, string> keys = new(StringComparer.Ordinal);
		string? section = null;
		bool inTable = false;
		foreach (string line in File.ReadLines(RepoFile("docs", "configuration.md")))
		{
			if (line.StartsWith("## ", StringComparison.Ordinal))
			{
				Match heading = SectionHeading().Match(line);
				section = heading.Success ? heading.Groups[1].Value : null;
			}

			if (!line.StartsWith('|'))
			{
				inTable = false;
				continue;
			}

			if (line.StartsWith("| Key | Default |", StringComparison.Ordinal))
			{
				inTable = section is not null;
				continue;
			}

			Match row = KeyRow().Match(line);
			if (inTable && row.Success)
			{
				keys.Add($"{section}:{row.Groups[1].Value}", row.Groups[2].Value.Trim());
			}
		}

		return keys;
	}

	private static string RepoFile(params string[] parts)
	{
		string? directory = AppContext.BaseDirectory;
		while (directory is not null && !File.Exists(Path.Combine(directory, "Memoria.slnx")))
		{
			directory = Path.GetDirectoryName(directory);
		}

		return Path.Combine([directory ?? throw new FileNotFoundException("Memoria.slnx not found above " + AppContext.BaseDirectory), .. parts]);
	}

	[GeneratedRegex(@"^## `(Memoria:\w+)`")]
	private static partial Regex SectionHeading();

	[GeneratedRegex(@"^\| `([^`]+)` \| ([^|]*) \|")]
	private static partial Regex KeyRow();

	[GeneratedRegex(@"^`([^`]*)`$")]
	private static partial Regex Backticked();

	[GeneratedRegex(@"^\| `((?:GET|POST|PUT|PATCH|DELETE) /[^`]*)` \|")]
	private static partial Regex RouteRow();
}
