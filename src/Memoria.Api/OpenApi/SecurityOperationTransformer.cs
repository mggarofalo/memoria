using Memoria.Api.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Memoria.Api.OpenApi;

/// <summary>
/// Marks every scoped operation as needing a bearer API key and says which
/// scope. Anonymous routes (<c>/health</c>, <c>/ready</c>) stay unmarked.
/// </summary>
public sealed class SecurityOperationTransformer : IOpenApiOperationTransformer
{
	public const string SchemeName = "apiKey";

	public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
	{
		MemoriaScopeAttribute? scope = context.Description.ActionDescriptor.EndpointMetadata.OfType<MemoriaScopeAttribute>().FirstOrDefault();
		if (scope is null)
		{
			return Task.CompletedTask;
		}

		operation.Security =
		[
			new OpenApiSecurityRequirement
			{
				[new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = [],
			},
		];
		operation.Description = $"{operation.Description} Requires the `{scope.Scope}` scope.".TrimStart();
		return Task.CompletedTask;
	}
}
