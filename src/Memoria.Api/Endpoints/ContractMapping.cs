using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;

namespace Memoria.Api.Endpoints;

/// <summary>
/// Sidecar wire types to public v1 contracts. Kept as one explicit mapping so
/// the public API can stay stable while the sidecar contract moves.
/// </summary>
internal static class ContractMapping
{
	public static SearchResult ToContract(this SidecarSearchResponse response)
	{
		return new SearchResult(
			response.Results.Select(ToContract).ToArray(),
			response.Total,
			response.TookMs,
			new SearchQueryInfo(
				response.Query.Collections,
				response.Query.Rerank,
				response.Query.Filtered,
				response.Query.Fetched,
				response.Query.Limit),
			response.Degraded,
			KnownReasons(response.DegradedReasons));
	}

	/// <summary>Only the tokens the contract names go out, so a reason can never carry backend detail.</summary>
	public static string[] KnownReasons(IReadOnlyList<string>? reasons)
	{
		return (reasons ?? []).Where(DegradedReason.All.Contains).ToArray();
	}

	public static SearchHit ToContract(this SidecarSearchHit hit)
	{
		return new SearchHit(
			hit.Docid,
			hit.File,
			hit.Collection,
			hit.Path,
			hit.Title,
			hit.Score,
			hit.Line,
			hit.Snippet,
			hit.Context,
			hit.Metadata);
	}

	public static Document ToContract(this SidecarDocument document)
	{
		return new Document(
			document.Docid,
			document.File,
			document.Collection,
			document.Path,
			document.Title,
			document.Context,
			document.ModifiedAt,
			document.BodyLength,
			document.FromLine,
			document.MaxLines,
			document.Body,
			document.Metadata);
	}

	public static CollectionInfo ToContract(this SidecarCollection collection)
	{
		return new CollectionInfo(
			collection.Name,
			collection.Documents,
			collection.LastModified,
			collection.IncludeByDefault,
			collection.Context);
	}

	public static IndexCollectionStatus ToContract(this SidecarStatusCollection collection)
	{
		return new IndexCollectionStatus(collection.Name, collection.Documents, collection.LastUpdated);
	}

	public static WorkerInfo ToContract(this SidecarWorker worker)
	{
		return new WorkerInfo(
			worker.Name,
			worker.Host,
			worker.State,
			worker.Drained,
			worker.Circuit,
			new WorkerCircuits(worker.Circuits.Embed, worker.Circuits.Rerank, worker.Circuits.Generate, worker.Circuits.Documents),
			worker.Capabilities,
			worker.Priority,
			worker.MaxConcurrent,
			worker.InFlight,
			worker.QueueDepth,
			worker.Calls,
			worker.ErrorRate,
			worker.LastHeartbeat,
			worker.EmbedModelCompatible,
			worker.EmbedDisagrees,
			worker.RerankModelCompatible,
			worker.GenerateModelCompatible);
	}

	public static JobCounts ToContract(this SidecarJobCounts counts)
	{
		return new JobCounts(counts.Queued, counts.Held, counts.Running, counts.Succeeded, counts.Failed);
	}

	public static QueueState ToContract(this SidecarPauseResponse response)
	{
		return new QueueState(response.Paused, response.Jobs.ToContract());
	}

	public static QueueStatus ToContract(this SidecarQueue queue, IReadOnlyList<SidecarJob> jobs)
	{
		return new QueueStatus(
			queue.Paused,
			queue.Jobs.ToContract(),
			queue.Persistence.Healthy,
			queue.DeadLetters,
			new PriorityLoad(queue.Classes.Interactive.InFlight, queue.Classes.Interactive.Waiting),
			new PriorityLoad(queue.Classes.Background.InFlight, queue.Classes.Background.Waiting),
			jobs.Select(ToContract).ToArray());
	}

	public static JobInfo ToContract(this SidecarJob job)
	{
		return new JobInfo(
			job.Id,
			job.State,
			job.Request.Collections,
			job.Request.Embed,
			job.Request.Force,
			job.Request.Reason,
			job.CreatedAt,
			job.StartedAt,
			job.FinishedAt,
			job.Error,
			job.Attempts,
			job.Worker);
	}
}
