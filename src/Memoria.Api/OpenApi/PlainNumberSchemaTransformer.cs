using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Memoria.Api.OpenApi;

/// <summary>
/// The generator types numeric members as <c>number | string</c> (with a
/// digit pattern) because System.Text.Json's web defaults accept numbers
/// written as strings; serialized to OpenAPI 3.0 that becomes
/// <c>anyOf [number, string]</c>. Memoria never emits numbers as strings, and
/// the union makes code generators (oapi-codegen for the Go CLI) produce
/// awkward types, so keep only the numeric type.
/// </summary>
public sealed class PlainNumberSchemaTransformer : IOpenApiSchemaTransformer
{
	public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
	{
		if (schema.Type is JsonSchemaType type && type.HasFlag(JsonSchemaType.String))
		{
			JsonSchemaType numeric = type & (JsonSchemaType.Number | JsonSchemaType.Integer);
			if (numeric != 0)
			{
				schema.Type = numeric | (type & JsonSchemaType.Null);
				schema.Pattern = null;
			}
		}

		return Task.CompletedTask;
	}
}
