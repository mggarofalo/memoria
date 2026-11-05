namespace Memoria.Api.Configuration;

/// <summary>
/// Root of the <c>Memoria</c> configuration section. Each child is one
/// concern the host owns. Sections that later epics add keep this shape:
/// the control plane adds
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

	public AuthOptions Auth { get; set; } = new();

	public IngestOptions Ingest { get; set; } = new();
}

/// <summary>Limits on writing into the corpus.</summary>
public sealed class IngestOptions
{
	/// <summary>The largest stored document, in UTF-8 bytes. Matches the sidecar's <c>MEMORIA_QMD_MAX_DOCUMENT_BYTES</c>.</summary>
	public int MaxDocumentBytes { get; set; } = 2 * 1024 * 1024;

	/// <summary>The largest raw transcript one request may send. It is read as a stream, so this limits work, not memory.</summary>
	public long MaxSessionBytes { get; set; } = 384L * 1024 * 1024;

	/// <summary>Transcripts over 8 MiB handled at once. It keeps the API inside its memory limit. Past it, an upload gets 503 with <c>Retry-After</c>. A slow large upload holds its place until it ends.</summary>
	public int MaxConcurrentLargeUploads { get; set; } = 2;

	/// <summary>Redactions and renders run at once, for documents and transcripts together. This is what bounds CPU and most of the memory. Reading a body does not take one.</summary>
	public int MaxConcurrentRenders { get; set; } = 2;

	/// <summary>The slowest a body may arrive, after a few seconds' grace. Below it the server cuts the request off with 408.</summary>
	public int MinUploadBytesPerSecond { get; set; } = 16 * 1024;

	/// <summary>The longest a transcript's body may take to arrive, however steadily. Past it, the request gets 408 and nothing changes.</summary>
	public TimeSpan SessionUploadDeadline { get; set; } = TimeSpan.FromHours(1);

	/// <summary>The same for a document.</summary>
	public TimeSpan DocumentUploadDeadline { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>How long a delete waits for a write of its path to answer. Past it, the delete answers 503 <c>delete_unconfirmed</c>, having changed nothing, and repeating it is safe.</summary>
	public TimeSpan DeleteWriteWait { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// The key for digests of content, kept only so a tombstone or an upload can be compared with what was stored. It keeps the database from confirming a guessed
	/// secret, so it lives outside it, as the sidecar's secret does. At least 32 characters. Required whenever API keys are on.
	/// </summary>
	public string? DigestKey { get; set; }

	/// <summary>Older keys, still accepted when comparing, so rotating does not refuse what was stored under them. Drop one once what it covered has been sent again.</summary>
	public List<string> PreviousDigestKeys { get; set; } = [];

	/// <summary>The least time between index updates a delete starts in one collection. A delete with none recently starts one at once; a burst shares one when this ends.</summary>
	public TimeSpan MinReindexInterval { get; set; } = TimeSpan.FromSeconds(10);

	/// <summary>Per-collection settings, by exact collection name. A collection not listed here cannot be written by ingest.</summary>
	public Dictionary<string, IngestCollectionOptions> Collections { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>One collection's ingest settings.</summary>
public sealed class IngestCollectionOptions
{
	/// <summary>
	/// Exactly <c>Disabled</c> stores this collection's text unredacted. Anything else, including <c>disabled</c>, <c>off</c>
	/// and a stray space, leaves redaction on, and the server warns at startup about a value it did not recognize.
	/// A string for the same reason as <see cref="AuthOptions.Mode"/>: an enum would accept values nobody meant as this.
	/// </summary>
	public string? Redaction { get; set; }

	/// <summary>Opens this collection to ingest, which refuses unless its directory is empty when it first uses it. Everything inside then belongs to Memoria.</summary>
	public bool Enabled { get; set; }
}

/// <summary>How callers authenticate.</summary>
public static class AuthModes
{
	/// <summary>Every <c>/v1</c> and MCP route requires a key. The default: omitting the setting fails closed.</summary>
	public const string ApiKey = "ApiKey";

	/// <summary>No authentication at all, for development on a trusted LAN. Must be set explicitly.</summary>
	public const string Disabled = "Disabled";
}

/// <summary>API keys, their rate limits and the audit log.</summary>
public sealed class AuthOptions
{
	/// <summary>
	/// Exactly <see cref="AuthModes.ApiKey"/> or <see cref="AuthModes.Disabled"/>;
	/// the validator stops the host on anything else. A string, not an enum: binding
	/// an enum accepts <c>1</c>, <c>disabled</c>, <c> Disabled </c> and
	/// <c>ApiKey,Disabled</c>, each of which turns authentication off.
	/// </summary>
	public string? Mode { get; set; } = AuthModes.ApiKey;

	/// <summary>True only for the exact opt-out, so an invalid value can never open anything.</summary>
	public bool IsDisabled => string.Equals(Mode, AuthModes.Disabled, StringComparison.Ordinal);

	/// <summary>The SQLite control database holding keys and audit records. Durable state: put it on a volume, apart from QMD's index. Required in <see cref="AuthMode.ApiKey"/> mode.</summary>
	public string? DatabasePath { get; set; }

	public RateLimitOptions RateLimit { get; set; } = new();

	public AuditOptions Audit { get; set; } = new();

	public FailedAuthOptions FailedAuth { get; set; } = new();

	/// <summary>
	/// Proxies whose X-Forwarded-For is believed, as IPs or CIDR ranges. Empty by
	/// default: the socket address is the client, and the header means nothing.
	/// </summary>
	public string[]? TrustedProxies { get; set; }
}

/// <summary>Per-client-address budget for failed authentication. See <c>FailedAuthLimiter</c>.</summary>
public sealed class FailedAuthOptions
{
	public bool Enabled { get; set; } = true;

	/// <summary>Failures one address may have per <see cref="Window"/> before its credentials stop being looked up.</summary>
	public int PermitLimit { get; set; } = 20;

	public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

	/// <summary>Addresses tracked at once. A flood of distinct addresses cannot grow memory past this.</summary>
	public int MaxTrackedAddresses { get; set; } = 10_000;
}

/// <summary>Per-key request limits. Requests without a key are not limited here; they cannot reach the corpus.</summary>
public sealed class RateLimitOptions
{
	public bool Enabled { get; set; } = true;

	/// <summary>Requests one key may make per <see cref="Window"/>.</summary>
	public int PermitLimit { get; set; } = 120;

	public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>What the audit log keeps.</summary>
public sealed class AuditOptions
{
	/// <summary>Also record search queries and document references. Off by default: queries can be as sensitive as the corpus.</summary>
	public bool IncludeQueries { get; set; }

	/// <summary>Days to keep audit records. Zero or less keeps them forever.</summary>
	public int RetentionDays { get; set; } = 90;

	/// <summary>Records kept per key, newest first, so a burst from one key cannot grow the table without bound or evict another key's history.</summary>
	public int MaxRowsPerKey { get; set; } = 100_000;
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

	/// <summary>Total bytes of document bodies in one batch response. Documents past it are listed under <c>skipped</c> and are not read.</summary>
	public int MaxMultiGetBytes { get; set; } = 16 * 1024 * 1024;
}
