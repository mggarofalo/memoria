using System.Net;
using System.Text.RegularExpressions;
using Memoria.Api.Http;
using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Memoria.Api.Endpoints;

/// <summary>
/// The control plane: the worker pool, the background queue, and reindexing. Each route
/// proxies one sidecar admin route over the shared-secret channel, so the sidecar keeps
/// the state and the API keeps the authorization. These act on every collection at once,
/// so they need the <c>admin</c> scope and a key that is not bound to particular
/// collections, and they are unavailable when authentication is disabled.
/// </summary>
public static partial class AdminControlEndpoints
{
	public static IEndpointRouteBuilder MapAdminControl(this IEndpointRouteBuilder app)
	{
		RouteGroupBuilder admin = app.MapGroup("/v1/admin")
			.WithMemoriaScope(MemoriaScopes.Admin)
			.WithTags("Operations")
			.AddEndpointFilter(AdminKeyEndpoints.RequireApiKeyModeAsync)
			.WithMetadata(new MemoriaUnboundKeyAttribute())
			.AddEndpointFilter(ForwardRefusalsAsync)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status429TooManyRequests)
			.ProducesProblem(StatusCodes.Status502BadGateway)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable)
			.ProducesProblem(StatusCodes.Status504GatewayTimeout);

		MapWorkers(admin);
		MapQueue(admin);

		admin.MapPost("/reindex", ReindexAsync)
			.WithName("reindex")
			.WithSummary("Queue a reindex")
			.WithDescription(
				"Rescans the collections' directories and, unless `embed` is false, embeds what changed. Omit `collections` for every "
				+ "collection; an empty list is refused. `force` re-embeds every document, which is expensive. Jobs run one at a time, "
				+ "and an identical job already waiting is returned instead of a new one.")
			.Produces<JobInfo>(StatusCodes.Status202Accepted)
			.ProducesValidationProblem();

		return app;
	}

	private static void MapWorkers(RouteGroupBuilder admin)
	{
		admin.MapGet("/workers", ListWorkersAsync)
			.WithName("listWorkers")
			.WithSummary("List inference workers")
			.WithDescription("Each worker's state, circuits, load and error rate, and whether it is drained. Empty in local LLM mode. Never includes tokens.");

		admin.MapPost("/workers/{name}:drain", DrainWorkerAsync)
			.WithName("drainWorker")
			.WithSummary("Drain a worker")
			.WithDescription(
				"Takes the worker out of rotation. Calls in flight finish and it gets no new ones; `inFlight` shows them leave. "
				+ "Held in the sidecar's memory, so a sidecar restart puts it back. Draining a drained worker changes nothing.")
			.ProducesValidationProblem();

		admin.MapPost("/workers/{name}:enable", EnableWorkerAsync)
			.WithName("enableWorker")
			.WithSummary("Put a drained worker back")
			.WithDescription("Returns the worker to rotation. Enabling a worker that is not drained changes nothing.")
			.ProducesValidationProblem();
	}

	private static void MapQueue(RouteGroupBuilder admin)
	{
		admin.MapGet("/queue", GetQueueAsync)
			.WithName("getQueueStatus")
			.WithSummary("Queue status")
			.WithDescription(
				"Jobs by state, whether the queue is paused, whether jobs persist, documents set aside, model calls by priority class, "
				+ "and the jobs the sidecar remembers, newest first.");

		admin.MapPost("/queue:pause", PauseQueueAsync)
			.WithName("pauseQueue")
			.WithSummary("Pause background work")
			.WithDescription(
				"Starts no more reindex or embedding jobs. A running job runs to its end; cancel it to stop it sooner. Writes still land "
				+ "and queue, so until the queue resumes new documents are not searchable and deleted ones still are. Held in the sidecar's memory, so a sidecar restart resumes.");

		admin.MapPost("/queue:resume", ResumeQueueAsync)
			.WithName("resumeQueue")
			.WithSummary("Resume background work")
			.WithDescription("Lets waiting jobs start again.");

		admin.MapPost("/queue/jobs/{id}:retry", RetryJobAsync)
			.WithName("retryJob")
			.WithSummary("Retry a failed job")
			.WithDescription("Queues a failed job again under the same id. 409 unless it has failed.")
			.Produces<JobInfo>(StatusCodes.Status202Accepted)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesValidationProblem();

		admin.MapPost("/queue/jobs/{id}:cancel", CancelJobAsync)
			.WithName("cancelJob")
			.WithSummary("Cancel a job")
			.WithDescription(
				"Stops a job that has not finished. A waiting job ends at once; a running one stops at its next safe point, so it may still "
				+ "read `running` here. A cancelled job reports `failed` with the error `Cancelled`. 409 if it has already finished.")
			.Produces<JobInfo>(StatusCodes.Status202Accepted)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesValidationProblem();
	}

	/// <summary>
	/// The sidecar's refusals here are about a worker, a job or a collection the caller
	/// named, and the caller already sees every collection, so its code and message pass
	/// through. The shared handler would describe a 404 as a missing document. Backend
	/// faults still go to that handler, which decides how much of them to show.
	/// </summary>
	internal static async ValueTask<object?> ForwardRefusalsAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
	{
		try
		{
			return await next(context);
		}
		catch (SidecarException ex) when (ex.StatusCode == HttpStatusCode.NotFound && ex.Code is "no_route" or SidecarException.Malformed)
		{
			// The sidecar has no such route: it is older than this API, not a name the caller got wrong.
			return TypedResults.Problem(
				statusCode: StatusCodes.Status502BadGateway,
				title: "Search backend does not offer this operation",
				detail: "The sidecar does not have this route. Run the API and the sidecar from the same release.",
				extensions: new Dictionary<string, object?> { ["code"] = "sidecar_route_missing", ["upstreamStatus"] = 404 });
		}
		catch (SidecarException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
		{
			LogRefusal(context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AdminControlEndpoints).FullName!), ex.Code, (int)ex.StatusCode!.Value);
			(int status, string title) = MemoriaExceptionHandler.Map(ex);
			return TypedResults.Problem(
				statusCode: status,
				title: title,
				detail: ex.Message,
				extensions: new Dictionary<string, object?> { ["code"] = ex.Code, ["upstreamStatus"] = (int)ex.StatusCode!.Value });
		}
	}

	[LoggerMessage(EventId = 3400, Level = LogLevel.Information, Message = "Sidecar refused an operation with {Code} ({UpstreamStatus})")]
	private static partial void LogRefusal(ILogger logger, string code, int upstreamStatus);

	private static async Task<Ok<WorkerList>> ListWorkersAsync(IMemoriaQmdClient sidecar, CancellationToken cancellationToken)
	{
		SidecarWorkersResponse response = await sidecar.ListWorkersAsync(cancellationToken);
		return TypedResults.Ok(new WorkerList(response.Mode, response.Workers.Select(static w => w.ToContract()).ToArray()));
	}

	private static async Task<Results<Ok<WorkerInfo>, ValidationProblem>> DrainWorkerAsync(string name, IMemoriaQmdClient sidecar, CancellationToken cancellationToken)
	{
		if (!WorkerName().IsMatch(name))
		{
			return InvalidName();
		}

		return TypedResults.Ok((await sidecar.DrainWorkerAsync(name, cancellationToken)).ToContract());
	}

	private static async Task<Results<Ok<WorkerInfo>, ValidationProblem>> EnableWorkerAsync(string name, IMemoriaQmdClient sidecar, CancellationToken cancellationToken)
	{
		if (!WorkerName().IsMatch(name))
		{
			return InvalidName();
		}

		return TypedResults.Ok((await sidecar.EnableWorkerAsync(name, cancellationToken)).ToContract());
	}

	private static async Task<Ok<QueueStatus>> GetQueueAsync(IMemoriaQmdClient sidecar, CancellationToken cancellationToken)
	{
		Task<SidecarQueue> queue = sidecar.GetQueueAsync(cancellationToken);
		Task<IReadOnlyList<SidecarJob>> jobs = sidecar.ListJobsAsync(cancellationToken);
		await Task.WhenAll(queue, jobs);
		return TypedResults.Ok((await queue).ToContract(await jobs));
	}

	private static async Task<Ok<QueueState>> PauseQueueAsync(IMemoriaQmdClient sidecar, CancellationToken cancellationToken)
	{
		return TypedResults.Ok((await sidecar.PauseQueueAsync(cancellationToken)).ToContract());
	}

	private static async Task<Ok<QueueState>> ResumeQueueAsync(IMemoriaQmdClient sidecar, CancellationToken cancellationToken)
	{
		return TypedResults.Ok((await sidecar.ResumeQueueAsync(cancellationToken)).ToContract());
	}

	private static async Task<Results<Accepted<JobInfo>, ValidationProblem>> RetryJobAsync(string id, IMemoriaQmdClient sidecar, CancellationToken cancellationToken)
	{
		if (!JobId().IsMatch(id))
		{
			return InvalidJobId();
		}

		return TypedResults.Accepted((string?)null, (await sidecar.RetryJobAsync(id, cancellationToken)).ToContract());
	}

	private static async Task<Results<Accepted<JobInfo>, ValidationProblem>> CancelJobAsync(string id, IMemoriaQmdClient sidecar, CancellationToken cancellationToken)
	{
		if (!JobId().IsMatch(id))
		{
			return InvalidJobId();
		}

		return TypedResults.Accepted((string?)null, (await sidecar.CancelJobAsync(id, cancellationToken)).ToContract());
	}

	private static async Task<Results<Accepted<JobInfo>, ValidationProblem>> ReindexAsync(ReindexRequest? request, IMemoriaQmdClient sidecar, CancellationToken cancellationToken)
	{
		IReadOnlyList<string>? collections = request?.Collections;
		if (collections is not null && CollectionsProblem(collections) is { } problem)
		{
			return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["collections"] = [problem] });
		}

		SidecarReindexRequest forward = new(collections?.Distinct(StringComparer.Ordinal).ToArray(), request?.Embed, request?.Force);
		SidecarJob job = await sidecar.ReindexAsync(forward, cancellationToken);
		return TypedResults.Accepted((string?)null, job.ToContract());
	}

	private static string? CollectionsProblem(IReadOnlyList<string> collections)
	{
		if (collections.Count == 0)
		{
			return "Omit collections to reindex every collection. An empty list is refused.";
		}

		if (collections.Count > AdminKeyEndpoints.MaxCollectionsPerKey)
		{
			return $"At most {AdminKeyEndpoints.MaxCollectionsPerKey} collections per request.";
		}

		return collections.All(AdminKeyEndpoints.IsValidCollectionName) ? null : "Collection names must be 1 to 200 characters with no slash or surrounding whitespace. They are case-sensitive.";
	}

	private static ValidationProblem InvalidName()
	{
		return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A worker name is 1 to 64 letters, digits, dots, dashes or underscores, or a host and port, as `GET /v1/admin/workers` lists it."] });
	}

	private static ValidationProblem InvalidJobId()
	{
		return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["id"] = ["A job id is 1 to 64 letters, digits or dashes, as `GET /v1/admin/queue` lists them."] });
	}

	/// <summary>
	/// As the sidecar allows a name, so nothing else reaches its URL: a configured name, or
	/// the host and port its single-worker shorthand uses, such as <c>[::1]:8383</c>.
	/// </summary>
	[GeneratedRegex(@"^(?!\.{1,2}$)[A-Za-z0-9_.:\[\]-]{1,64}$")]
	private static partial Regex WorkerName();

	[GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
	private static partial Regex JobId();
}
