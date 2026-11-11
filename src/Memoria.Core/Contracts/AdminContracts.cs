namespace Memoria.Core.Contracts;

/// <summary>Result of <c>GET /v1/admin/workers</c>. <see cref="Mode"/> is <c>local</c> or <c>remote</c>; local mode has no workers.</summary>
public sealed record WorkerList(string Mode, IReadOnlyList<WorkerInfo> Workers);

/// <summary>
/// One inference worker, as the sidecar sees it. <see cref="Drained"/> means an
/// operator took it out of rotation; it finishes its <see cref="InFlight"/> calls and
/// gets no new ones until it is enabled or the sidecar restarts. A model-compatible flag
/// that is false means the worker reports another model than the sidecar's for that role,
/// and gets no work in it; null means not checked yet. Never carries a token.
/// </summary>
public sealed record WorkerInfo(
	string Name,
	string Host,
	string State,
	bool Drained,
	string Circuit,
	WorkerCircuits Circuits,
	IReadOnlyList<string> Capabilities,
	int Priority,
	int MaxConcurrent,
	int InFlight,
	double QueueDepth,
	int Calls,
	double ErrorRate,
	string? LastHeartbeat,
	bool? EmbedModelCompatible,
	bool EmbedDisagrees,
	bool? RerankModelCompatible = null,
	bool? GenerateModelCompatible = null);

/// <summary>Each of a worker's circuits: <c>closed</c>, <c>open</c> or <c>half_open</c>. <see cref="Documents"/> is document embedding.</summary>
public sealed record WorkerCircuits(string Embed, string Rerank, string Generate, string Documents);

/// <summary>Result of <c>GET /v1/admin/queue</c>: background work now, and the jobs the sidecar remembers, newest first.</summary>
public sealed record QueueStatus(
	bool Paused,
	JobCounts Counts,
	bool Persistent,
	int DeadLetters,
	PriorityLoad Interactive,
	PriorityLoad Background,
	IReadOnlyList<JobInfo> Jobs);

/// <summary>Jobs by state. <see cref="Held"/> counts the queued jobs waiting on the debounce or a retry delay.</summary>
public sealed record JobCounts(int Queued, int Held, int Running, int Succeeded, int Failed);

/// <summary>Model calls in one priority class: running now, and waiting for a worker's slot.</summary>
public sealed record PriorityLoad(int InFlight, int Waiting);

/// <summary>Result of <c>POST /v1/admin/queue:pause</c> and <c>:resume</c>.</summary>
public sealed record QueueState(bool Paused, JobCounts Counts);

/// <summary>One reindex job. <see cref="Collections"/> is null for every collection.</summary>
public sealed record JobInfo(
	string Id,
	string State,
	IReadOnlyList<string>? Collections,
	bool Embed,
	bool Force,
	string Reason,
	string CreatedAt,
	string? StartedAt,
	string? FinishedAt,
	string? Error,
	int? Attempts,
	string? Worker);

/// <summary>
/// Body of <c>POST /v1/admin/reindex</c>. Omitting <see cref="Collections"/> means
/// every collection; an empty list is refused rather than read as everything.
/// </summary>
public sealed record ReindexRequest(IReadOnlyList<string>? Collections, bool? Embed, bool? Force);
