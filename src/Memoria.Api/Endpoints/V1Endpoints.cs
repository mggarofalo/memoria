using Memoria.Api.Http;

namespace Memoria.Api.Endpoints;

public static class V1Endpoints
{
	/// <summary>
	/// The public read surface. Every route here needs the <see cref="MemoriaScopes.Read"/>
	/// scope once API keys are enforced, and every route can fail with the
	/// gateway statuses the sidecar mapping produces.
	/// </summary>
	public static IEndpointRouteBuilder MapV1(this IEndpointRouteBuilder app)
	{
		RouteGroupBuilder v1 = app.MapGroup("/v1")
			.WithMemoriaScope(MemoriaScopes.Read)
			.ProducesProblem(StatusCodes.Status502BadGateway)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable)
			.ProducesProblem(StatusCodes.Status504GatewayTimeout);

		v1.MapSearch();
		v1.MapDocuments();
		v1.MapCollections();

		return app;
	}
}
