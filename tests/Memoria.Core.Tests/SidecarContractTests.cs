using System.Collections;
using System.Reflection;
using System.Text.Json;
using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;

namespace Memoria.Core.Tests;

/// <summary>
/// Pins the hand-written client to the committed sidecar contract. For every
/// route the client calls, the path and method must exist; for every body the
/// client reads or writes, the record's properties must be a subset of the
/// schema's and must cover every required property. A sidecar change that
/// renames or removes a field fails here, before it fails in production.
/// </summary>
public sealed class SidecarContractTests
{
	private static readonly Lazy<JsonDocument> Spec = new(LoadSpec);

	public static TheoryData<string, string> Routes => new()
	{
		{ "get", "/health" },
		{ "get", "/ready" },
		{ "get", "/status" },
		{ "post", "/search" },
		{ "get", "/docs/{reference}" },
		{ "post", "/docs:multiGet" },
		{ "get", "/collections" },
		{ "post", "/documents" },
		{ "post", "/reindex" },
		{ "get", "/reindex/jobs" },
		{ "get", "/reindex/jobs/{id}" },
	};

	[Theory]
	[MemberData(nameof(Routes))]
	public void Route_ExistsInCommittedSpec(string method, string path)
	{
		JsonElement paths = Spec.Value.RootElement.GetProperty("paths");

		Assert.True(paths.TryGetProperty(path, out JsonElement operations), $"{path} is not in sidecar/openapi.json");
		Assert.True(operations.TryGetProperty(method, out _), $"{method.ToUpperInvariant()} {path} is not in sidecar/openapi.json");
	}

	[Fact]
	public void Spec_RequiresBearerAuth()
	{
		JsonElement scheme = Spec.Value.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("bearer");

		Assert.Equal("http", scheme.GetProperty("type").GetString());
		Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
	}

	public static TheoryData<string, string, string, Type> Responses => new()
	{
		{ "get", "/health", "200", typeof(SidecarHealth) },
		{ "get", "/ready", "200", typeof(SidecarReadiness) },
		{ "get", "/ready", "503", typeof(SidecarReadiness) },
		{ "get", "/status", "200", typeof(SidecarStatus) },
		{ "post", "/search", "200", typeof(SidecarSearchResponse) },
		{ "get", "/docs/{reference}", "200", typeof(SidecarDocument) },
		{ "post", "/docs:multiGet", "200", typeof(SidecarMultiGetResponse) },
		{ "get", "/collections", "200", typeof(SidecarCollectionsResponse) },
		{ "post", "/documents", "202", typeof(SidecarIngestResponse) },
		{ "post", "/reindex", "202", typeof(SidecarJobResponse) },
		{ "get", "/reindex/jobs", "200", typeof(SidecarJobsResponse) },
		{ "get", "/reindex/jobs/{id}", "200", typeof(SidecarJobResponse) },
		{ "get", "/status", "500", typeof(SidecarError) },
	};

	[Theory]
	[MemberData(nameof(Responses))]
	public void ResponseType_MatchesSchema(string method, string path, string status, Type type)
	{
		JsonElement schema = Spec.Value.RootElement
			.GetProperty("paths").GetProperty(path).GetProperty(method)
			.GetProperty("responses").GetProperty(status)
			.GetProperty("content").GetProperty("application/json").GetProperty("schema");

		AssertMatches(schema, type, $"{method} {path} {status}");
	}

	public static TheoryData<string, string, Type> RequestBodies => new()
	{
		{ "post", "/search", typeof(SidecarSearchRequest) },
		{ "post", "/docs:multiGet", typeof(SidecarMultiGetRequest) },
		{ "post", "/documents", typeof(SidecarIngestRequest) },
		{ "post", "/reindex", typeof(SidecarReindexRequest) },
	};

	[Theory]
	[MemberData(nameof(RequestBodies))]
	public void RequestType_MatchesSchema(string method, string path, Type type)
	{
		JsonElement schema = Spec.Value.RootElement
			.GetProperty("paths").GetProperty(path).GetProperty(method)
			.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");

		AssertMatches(schema, type, $"{method} {path} body");
	}

	[Fact]
	public void DocumentQuery_MatchesQueryParameters()
	{
		JsonElement parameters = Spec.Value.RootElement
			.GetProperty("paths").GetProperty("/docs/{reference}").GetProperty("get").GetProperty("parameters");
		HashSet<string> names = parameters.EnumerateArray()
			.Where(p => p.GetProperty("in").GetString() == "query")
			.Select(p => p.GetProperty("name").GetString()!)
			.ToHashSet(StringComparer.Ordinal);

		foreach (PropertyInfo property in typeof(SidecarDocumentQuery).GetProperties())
		{
			Assert.Contains(JsonNamingPolicy.CamelCase.ConvertName(property.Name), names);
		}
	}

	private static void AssertMatches(JsonElement schema, Type type, string where)
	{
		schema = Resolve(schema);

		if (schema.TryGetProperty("anyOf", out JsonElement anyOf))
		{
			// The sidecar uses anyOf for "T or null" and for "string or string[]";
			// the client is right as long as one non-null branch matches.
			List<string> reasons = [];
			foreach (JsonElement option in anyOf.EnumerateArray())
			{
				JsonElement branch = Resolve(option);
				if (branch.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String && t.GetString() == "null")
				{
					continue;
				}

				try
				{
					AssertMatches(branch, type, where);
					return;
				}
				catch (Xunit.Sdk.XunitException ex)
				{
					reasons.Add(ex.Message);
				}
			}

			Assert.Fail($"{where}: no anyOf branch matches {type.Name}: {string.Join(" | ", reasons)}");
		}

		if (Nullable.GetUnderlyingType(type) is Type underlying)
		{
			type = underlying;
		}

		// On the wire it is a string; the type only proves the text went through the redactor.
		if (type == typeof(Memoria.Core.Redaction.RedactedMarkdown))
		{
			type = typeof(string);
		}

		if (type == typeof(JsonElement))
		{
			return;
		}

		if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
		{
			Assert.True(SchemaAllows(schema, "array"), $"{where}: client expects an array but schema is {Describe(schema)}");
			Type element = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
			AssertMatches(schema.GetProperty("items"), element, where + "[]");
			return;
		}

		if (type.Namespace is not null && type.Namespace.StartsWith("Memoria.Core", StringComparison.Ordinal))
		{
			Assert.True(SchemaAllows(schema, "object"), $"{where}: client expects an object but schema is {Describe(schema)}");
			AssertObjectMatches(schema, type, where);
			return;
		}

		string expected = type switch
		{
			_ when type == typeof(string) => "string",
			_ when type == typeof(bool) => "boolean",
			_ when type == typeof(int) || type == typeof(long) => "integer",
			_ when type == typeof(double) => "number",
			_ => throw new InvalidOperationException($"{where}: no schema mapping for {type}"),
		};

		bool ok = SchemaAllows(schema, expected) || (expected == "number" && SchemaAllows(schema, "integer"));
		Assert.True(ok, $"{where}: client expects {expected} but schema is {Describe(schema)}");
	}

	private static void AssertObjectMatches(JsonElement schema, Type type, string where)
	{
		Dictionary<string, JsonElement> schemaProperties = schema.TryGetProperty("properties", out JsonElement props)
			? props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)
			: [];
		HashSet<string> required = schema.TryGetProperty("required", out JsonElement req)
			? req.EnumerateArray().Select(r => r.GetString()!).ToHashSet(StringComparer.Ordinal)
			: [];

		Dictionary<string, PropertyInfo> clientProperties = type
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is null)
			.Where(p => p.CanWrite || p.GetMethod?.IsPublic == true)
			.Where(p => !IsComputed(p))
			.ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p => p, StringComparer.Ordinal);

		foreach (string name in required)
		{
			Assert.True(clientProperties.ContainsKey(name), $"{where}: schema requires '{name}' but {type.Name} has no such property");
		}

		foreach ((string name, PropertyInfo property) in clientProperties)
		{
			Assert.True(schemaProperties.TryGetValue(name, out JsonElement propertySchema), $"{where}: {type.Name}.{property.Name} ('{name}') is not in the schema");
			AssertMatches(propertySchema, property.PropertyType, $"{where}.{name}");
		}
	}

	/// <summary>Convenience members on records (e.g. <see cref="SidecarSearchFilters.IsEmpty"/>) that are not serialized.</summary>
	private static bool IsComputed(PropertyInfo property)
	{
		return property.Name == nameof(SidecarSearchFilters.IsEmpty) && property.DeclaringType == typeof(SidecarSearchFilters);
	}

	private static JsonElement Resolve(JsonElement schema)
	{
		if (schema.TryGetProperty("$ref", out JsonElement reference))
		{
			string[] parts = reference.GetString()!.Split('/');
			JsonElement current = Spec.Value.RootElement;
			foreach (string part in parts.Skip(1))
			{
				current = current.GetProperty(part);
			}

			return Resolve(current);
		}

		return schema;
	}

	private static bool SchemaAllows(JsonElement schema, string type)
	{
		if (!schema.TryGetProperty("type", out JsonElement declared))
		{
			return type == "object" && schema.TryGetProperty("properties", out _);
		}

		return declared.ValueKind switch
		{
			JsonValueKind.String => declared.GetString() == type,
			JsonValueKind.Array => declared.EnumerateArray().Any(t => t.GetString() == type),
			_ => false,
		};
	}

	private static string Describe(JsonElement schema)
	{
		return schema.TryGetProperty("type", out JsonElement t) ? t.ToString() : schema.ToString();
	}

	private static JsonDocument LoadSpec()
	{
		string? directory = AppContext.BaseDirectory;
		while (directory is not null)
		{
			string candidate = Path.Combine(directory, "sidecar", "openapi.json");
			if (File.Exists(candidate))
			{
				return JsonDocument.Parse(File.ReadAllText(candidate));
			}

			directory = Path.GetDirectoryName(directory);
		}

		throw new FileNotFoundException("sidecar/openapi.json not found above " + AppContext.BaseDirectory);
	}
}
