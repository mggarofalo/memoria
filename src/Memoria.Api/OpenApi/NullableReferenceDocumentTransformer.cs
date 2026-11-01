using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Memoria.Api.OpenApi;

/// <summary>
/// A nullable reference to a component (e.g. <c>DocumentMetadata?</c>) is
/// emitted for OpenAPI 3.0 as <c>oneOf [{enum: [null]}, {$ref}]</c>, which
/// code generators turn into opaque union wrappers. The wrapping happens when
/// the generator deduplicates schemas into components, after schema
/// transformers have run, so this runs on the finished document and rewrites
/// every such union to the conventional 3.0 form, <c>allOf [{$ref}]</c> plus
/// <c>nullable: true</c>, which generators map to a nullable instance of the
/// referenced type.
/// </summary>
public sealed class NullableReferenceDocumentTransformer : IOpenApiDocumentTransformer
{
	public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
	{
		HashSet<IOpenApiSchema> seen = new(ReferenceEqualityComparer.Instance);

		if (document.Components?.Schemas is { } components)
		{
			foreach (IOpenApiSchema schema in components.Values)
			{
				Visit(schema, seen);
			}
		}

		if (document.Paths is { } paths)
		{
			foreach (IOpenApiPathItem path in paths.Values)
			{
				foreach (OpenApiOperation operation in path.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>())
				{
					foreach (IOpenApiParameter parameter in operation.Parameters ?? [])
					{
						Visit(parameter.Schema, seen);
					}

					foreach (OpenApiMediaType media in operation.RequestBody?.Content?.Values ?? Enumerable.Empty<OpenApiMediaType>())
					{
						Visit(media.Schema, seen);
					}

					foreach (IOpenApiResponse response in operation.Responses?.Values ?? Enumerable.Empty<IOpenApiResponse>())
					{
						foreach (OpenApiMediaType media in response.Content?.Values ?? Enumerable.Empty<OpenApiMediaType>())
						{
							Visit(media.Schema, seen);
						}
					}
				}
			}
		}

		return Task.CompletedTask;
	}

	private static void Visit(IOpenApiSchema? node, HashSet<IOpenApiSchema> seen)
	{
		if (node is not OpenApiSchema schema || !seen.Add(schema))
		{
			return;
		}

		Rewrite(schema);

		foreach (IOpenApiSchema property in schema.Properties?.Values ?? [])
		{
			Visit(property, seen);
		}

		Visit(schema.Items, seen);
		Visit(schema.AdditionalProperties, seen);

		foreach (IOpenApiSchema option in (schema.AllOf ?? []).Concat(schema.AnyOf ?? []).Concat(schema.OneOf ?? []))
		{
			Visit(option, seen);
		}
	}

	private static void Rewrite(OpenApiSchema schema)
	{
		if (schema.OneOf is not { Count: 2 } options)
		{
			return;
		}

		IOpenApiSchema? reference = options.FirstOrDefault(static o => o is OpenApiSchemaReference);
		bool hasNull = options.Any(static o => o is OpenApiSchema s
			&& (s.Type == JsonSchemaType.Null || (s.Enum is { Count: 1 } values && IsNull(values[0]))));

		if (reference is null || !hasNull)
		{
			return;
		}

		schema.OneOf = null;
		schema.AllOf = [reference];
		schema.Type = JsonSchemaType.Null;
	}

	private static bool IsNull(System.Text.Json.Nodes.JsonNode? value)
	{
		return value is null || value.GetValueKind() == System.Text.Json.JsonValueKind.Null;
	}
}
