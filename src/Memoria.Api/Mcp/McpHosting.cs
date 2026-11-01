using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Api.Sidecar;
using ModelContextProtocol.Protocol;

namespace Memoria.Api.Mcp;

public static class McpHosting
{
	/// <summary>
	/// Registers the outward MCP server over Streamable HTTP in stateless mode:
	/// no session ids, no server-to-client requests, so any number of Claude
	/// Code sessions on any machine can talk to the same endpoint without
	/// affinity. Tools are activated per request from the request's services,
	/// which is how <see cref="ICallerScope"/> reaches them.
	/// </summary>
	public static IServiceCollection AddMemoriaMcpServer(this IServiceCollection services)
	{
		services
			.AddMcpServer(options =>
			{
				options.ServerInfo = new Implementation { Name = "memoria", Title = "Memoria", Version = SidecarServiceCollectionExtensions.ApiVersion };
				options.ServerInstructions =
					"Memoria searches the user's own accumulated record: Claude Code and Codex transcripts, issues, notes. "
					+ "Start with `search` (add `intent` for better ranking), then `get_document` with the hit's `line` as fromLine "
					+ "to read the passage. `list_collections` shows what is indexed. Reranked searches take a few seconds.";
			})
			.WithHttpTransport(options => options.Stateless = true)
			.WithTools<MemoriaTools>();

		return services;
	}

	/// <summary>Maps the MCP endpoint at <see cref="McpOptions.Path"/> inside the read scope.</summary>
	public static IEndpointConventionBuilder MapMemoriaMcp(this IEndpointRouteBuilder app, McpOptions options)
	{
		return app.MapMcp(options.Path).WithMemoriaScope(MemoriaScopes.Read);
	}
}
