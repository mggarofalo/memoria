using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Memoria.Api.OpenApi;

/// <summary>
/// Document-level metadata for the published spec. The only server is the
/// relative root, so the committed document does not depend on where it was
/// generated and clients supply the base URL.
/// </summary>
public sealed class MemoriaDocumentTransformer : IOpenApiDocumentTransformer
{
	public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
	{
		document.Info = new OpenApiInfo
		{
			Title = "Memoria API",
			Version = "v1",
			Description =
				"Search API over your own accumulated record: agent transcripts, issues, pull requests and notes, "
				+ "served from a QMD index behind one authenticated front door. Retrieval is QMD's hybrid BM25 plus vector "
				+ "search with LLM reranking; Memoria adds network exposure, collection scoping and client tooling.",
			License = new OpenApiLicense { Name = "MIT", Url = new Uri("https://github.com/mggarofalo/memoria/blob/main/LICENSE") },
			Contact = new OpenApiContact { Name = "Memoria", Url = new Uri("https://github.com/mggarofalo/memoria") },
		};
		document.Servers = [new OpenApiServer { Url = "/", Description = "The origin this document was fetched from." }];
		document.Components ??= new OpenApiComponents();
		document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
		document.Components.SecuritySchemes[SecurityOperationTransformer.SchemeName] = new OpenApiSecurityScheme
		{
			Type = SecuritySchemeType.Http,
			Scheme = "bearer",
			BearerFormat = "mem_<id>_<secret>",
			Description = "A Memoria API key.",
		};
		return Task.CompletedTask;
	}
}
