using Memoria.Api.Ingest;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Memoria.Api.Endpoints;

/// <summary>
/// The media type an ingest route reads its body as. Deliberately not <c>IAcceptsMetadata</c>,
/// which <c>.Accepts()</c> adds: routing acts on that, so a wrong content type was answered 415
/// before authorization ran, and a key without the scope learned the route's body format
/// instead of getting 403. The check is an endpoint filter here, after authorization.
/// </summary>
public sealed record IngestBodyMetadata(string ContentType);

public static class IngestBody
{
	/// <summary>
	/// Declares the body, as <c>.Accepts&lt;T&gt;(contentType)</c> would in the OpenAPI document, without
	/// the routing constraint. A class is a named component, referenced; anything else is inline.
	/// </summary>
	public static RouteHandlerBuilder TakesBody<T>(this RouteHandlerBuilder builder, string contentType)
	{
		ArgumentNullException.ThrowIfNull(builder);
		return builder
			.WithMetadata(new IngestBodyMetadata(contentType))
			.AddOpenApiOperationTransformer(async (operation, context, cancellationToken) =>
			{
				OpenApiSchema schema = await context.GetOrCreateSchemaAsync(typeof(T), null, cancellationToken);
				operation.RequestBody = new OpenApiRequestBody
				{
					Required = true,
					Content = new Dictionary<string, OpenApiMediaType>
					{
						[contentType] = new OpenApiMediaType { Schema = Named(typeof(T), schema, context.Document) },
					},
				};
			});
	}

	/// <summary>Runs after authorization, so only a caller who may use the route learns what it reads.</summary>
	public static async ValueTask<object?> RequireContentTypeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(next);
		IngestBodyMetadata? body = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IngestBodyMetadata>();
		if (body is not null && !Matches(context.HttpContext.Request.ContentType, body.ContentType))
		{
			throw new IngestRefusedException(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", $"This route reads a {body.ContentType} body.");
		}

		return await next(context);
	}

	/// <summary>
	/// As routing's check did, with the same comparison: no content type at all is let through, and
	/// otherwise the sent type must be a subset of the route's, so parameters such as <c>charset</c> pass.
	/// </summary>
	public static bool Matches(string? sent, string expected)
	{
		if (string.IsNullOrEmpty(sent))
		{
			return true;
		}

		return new MediaType(sent).IsSubsetOf(new MediaType(expected));
	}

	private static IOpenApiSchema Named(Type type, OpenApiSchema schema, OpenApiDocument? document)
	{
		if (document is null || type == typeof(string))
		{
			return schema;
		}

		document.AddComponent(type.Name, schema);
		return new OpenApiSchemaReference(type.Name, document);
	}
}
