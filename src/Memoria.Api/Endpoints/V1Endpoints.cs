using Memoria.Api.Http;

namespace Memoria.Api.Endpoints;

public static class V1Endpoints
{
	/// <summary>
	/// The public read surface. Every route here needs the <see cref="MemoriaScopes.Read"/>
	/// scope, and every route can fail with the auth statuses, the rate limit, and
	/// the gateway statuses the sidecar mapping produces.
	/// </summary>
	public static IEndpointRouteBuilder MapV1(this IEndpointRouteBuilder app)
	{
		RouteGroupBuilder v1 = app.MapGroup("/v1")
			.WithMemoriaScope(MemoriaScopes.Read)
			.DecideContentTypeAfterAuthorization()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status429TooManyRequests)
			.ProducesProblem(StatusCodes.Status502BadGateway)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable)
			.ProducesProblem(StatusCodes.Status504GatewayTimeout);

		v1.MapSearch();
		v1.MapDocuments();
		v1.MapCollections();

		return app;
	}
}
