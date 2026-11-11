using System.Text.Json.Nodes;
using Memoria.Api.OpenApi;
using Microsoft.OpenApi;

namespace Memoria.Api.Tests;

/// <summary>
/// The transformers run once per host and the served document is cached, so their reach is checked on documents built here:
/// a nullable reference becomes <c>allOf</c> plus <c>nullable</c> wherever in the document it sits.
/// </summary>
public sealed class OpenApiTransformerTests
{
	private readonly OpenApiDocument _document = new() { Components = new OpenApiComponents { Schemas = new Dictionary<string, IOpenApiSchema>() } };

	private OpenApiSchema NullableReference()
	{
		return new OpenApiSchema { OneOf = [new OpenApiSchema { Type = JsonSchemaType.Null }, new OpenApiSchemaReference("DocumentMetadata", _document)] };
	}

	private static void AssertRewritten(OpenApiSchema union)
	{
		Assert.Null(union.OneOf);
		Assert.IsType<OpenApiSchemaReference>(Assert.Single(union.AllOf!));
		Assert.Equal(JsonSchemaType.Null, union.Type);
	}

	private async Task TransformAsync()
	{
		await new NullableReferenceDocumentTransformer().TransformAsync(_document, null!, CancellationToken.None);
	}

	private async Task<OpenApiSchema> RewriteComponentAsync(Func<OpenApiSchema, OpenApiSchema> holder)
	{
		OpenApiSchema union = NullableReference();
		_document.Components!.Schemas!["Holder"] = holder(union);
		await TransformAsync();
		return union;
	}

	private void AddOperation(OpenApiOperation operation)
	{
		_document.Paths = new OpenApiPaths
		{
			["/v1/thing"] = new OpenApiPathItem { Operations = new Dictionary<HttpMethod, OpenApiOperation> { [HttpMethod.Get] = operation } },
		};
	}

	[Fact]
	public async Task A_parameter_schema_is_rewritten()
	{
		OpenApiSchema union = NullableReference();
		AddOperation(new OpenApiOperation { Parameters = [new OpenApiParameter { Name = "p", In = ParameterLocation.Query, Schema = union }] });

		await TransformAsync();

		AssertRewritten(union);
	}

	[Fact]
	public async Task A_response_schema_is_rewritten()
	{
		OpenApiSchema union = NullableReference();
		AddOperation(new OpenApiOperation
		{
			Responses = new OpenApiResponses
			{
				["200"] = new OpenApiResponse { Description = "ok", Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new OpenApiMediaType { Schema = union } } },
			},
		});

		await TransformAsync();

		AssertRewritten(union);
	}

	[Fact]
	public async Task An_array_item_is_rewritten()
	{
		AssertRewritten(await RewriteComponentAsync(static union => new OpenApiSchema { Type = JsonSchemaType.Array, Items = union }));
	}

	[Fact]
	public async Task A_map_value_is_rewritten()
	{
		AssertRewritten(await RewriteComponentAsync(static union => new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalProperties = union }));
	}

	[Fact]
	public async Task A_union_under_all_of_is_rewritten()
	{
		AssertRewritten(await RewriteComponentAsync(static union => new OpenApiSchema { AllOf = [union] }));
	}

	[Fact]
	public async Task A_union_under_any_of_is_rewritten()
	{
		AssertRewritten(await RewriteComponentAsync(static union => new OpenApiSchema { AnyOf = [new OpenApiSchema { Type = JsonSchemaType.String }, union] }));
	}

	[Fact]
	public async Task A_union_under_a_wider_one_of_is_rewritten()
	{
		AssertRewritten(await RewriteComponentAsync(static union => new OpenApiSchema
		{
			OneOf = [new OpenApiSchema { Type = JsonSchemaType.String }, union, new OpenApiSchema { Type = JsonSchemaType.Integer }],
		}));
	}

	[Fact]
	public async Task A_null_written_as_an_enum_counts_as_null()
	{
		OpenApiSchema union = new() { OneOf = [new OpenApiSchema { Enum = [null!] }, new OpenApiSchemaReference("DocumentMetadata", _document)] };
		_document.Components!.Schemas!["Holder"] = union;

		await TransformAsync();

		AssertRewritten(union);
	}

	[Fact]
	public async Task A_single_value_enum_that_is_not_null_is_left_alone()
	{
		OpenApiSchema notNull = new() { Enum = [JsonValue.Create("x")] };
		OpenApiSchema union = new() { OneOf = [notNull, new OpenApiSchemaReference("DocumentMetadata", _document)] };
		_document.Components!.Schemas!["Holder"] = union;

		await TransformAsync();

		Assert.Equal(2, union.OneOf!.Count);
		Assert.Null(union.AllOf);
	}

	/// <summary>The document's own scheme is added beside any another transformer declared, never in place of them.</summary>
	[Fact]
	public async Task The_api_key_scheme_joins_the_schemes_already_declared()
	{
		OpenApiDocument document = new()
		{
			Components = new OpenApiComponents
			{
				SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme> { ["other"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, Name = "X-Other", In = ParameterLocation.Header } },
			},
		};

		await new MemoriaDocumentTransformer().TransformAsync(document, null!, CancellationToken.None);

		Assert.Equal(["apiKey", "other"], document.Components.SecuritySchemes.Keys.Order(StringComparer.Ordinal).ToArray());
	}
}
