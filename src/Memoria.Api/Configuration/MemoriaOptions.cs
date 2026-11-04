namespace Memoria.Api.Configuration;

/// <summary>
/// Root of the <c>Memoria</c> configuration section. Each child is one
/// concern the host owns. Sections that later epics add keep this shape:
/// API-key auth adds <c>Auth</c> (keys and collection scopes), the control plane adds
/// <c>Workers</c> (the inference pool: name, url, token reference,
/// capabilities, priority, availability thresholds) and <c>Queue</c>
/// (concurrency, batch budget, priority classes). See docs/configuration.md.
/// </summary>
public sealed class MemoriaOptions
{
	public const string SectionName = "Memoria";

	public SidecarOptions Sidecar { get; set; } = new();

	public SearchOptions Search { get; set; } = new();

	public McpOptions Mcp { get; set; } = new();
}

/// <summary>The outward MCP server Claude clients connect to.</summary>
public sealed class McpOptions
{
	/// <summary>Serve the MCP endpoint. Off leaves only REST.</summary>
	public bool Enabled { get; set; } = true;

	/// <summary>Route of the Streamable HTTP endpoint.</summary>
	public string Path { get; set; } = "/mcp";
}

/// <summary>How to reach the memoria-qmd sidecar.</summary>
public sealed class SidecarOptions
{
	/// <summary>Base URL of the sidecar, e.g. <c>http://192.168.1.20:8282</c>. Never hardcode it; the host moves.</summary>
	public string BaseUrl { get; set; } = "http://127.0.0.1:8282";

	/// <summary>The <c>MEMORIA_QMD_SECRET</c> the sidecar expects. Required unless the sidecar is on loopback.</summary>
	public string? Secret { get; set; }

	/// <summary>Per-request timeout. Reranked searches on a cold sidecar can take 15 s or more.</summary>
	public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

	/// <summary>Timeout for the <c>/ready</c> probe only; probes must fail fast.</summary>
	public TimeSpan ReadinessTimeout { get; set; } = TimeSpan.FromSeconds(5);

	/// <summary>When true, <c>/ready</c> reports not-ready until the sidecar's models are resident.</summary>
	public bool ReadyRequiresModels { get; set; } = true;
}

/// <summary>Defaults and ceilings for <c>GET /v1/search</c>.</summary>
public sealed class SearchOptions
{
	/// <summary>Used when the caller omits <c>limit</c>.</summary>
	public int DefaultLimit { get; set; } = 10;

	/// <summary>Largest <c>limit</c> a caller may ask for; the sidecar itself caps at 100.</summary>
	public int MaxLimit { get; set; } = 100;

	/// <summary>Full reranked hybrid search unless the caller passes <c>rerank=false</c>.</summary>
	public bool RerankByDefault { get; set; } = true;

	/// <summary>Collections searched when the caller names none. Null defers to QMD's default-included set.</summary>
	public string[]? DefaultCollections { get; set; }
}
