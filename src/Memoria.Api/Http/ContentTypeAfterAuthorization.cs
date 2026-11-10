using Microsoft.AspNetCore.Http.Metadata;

namespace Memoria.Api.Http;

/// <summary>
/// A JSON body parameter makes minimal APIs add <see cref="IAcceptsMetadata"/>, and routing turns that
/// into a 415 while choosing the endpoint, before authorization runs: a key without the scope learned
/// the route's body format instead of getting 403. Removing the metadata leaves the check to the body's
/// own binding, which runs inside the endpoint, after authorization, and answers the same 415.
/// </summary>
public static class ContentTypeAfterAuthorization
{
	public static TBuilder DecideContentTypeAfterAuthorization<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
	{
		ArgumentNullException.ThrowIfNull(builder);
		builder.Finally(static endpoint =>
		{
			for (int i = endpoint.Metadata.Count - 1; i >= 0; i--)
			{
				if (endpoint.Metadata[i] is IAcceptsMetadata)
				{
					endpoint.Metadata.RemoveAt(i);
				}
			}
		});
		return builder;
	}
}
