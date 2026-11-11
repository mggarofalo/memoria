using System.Net;
using Memoria.Api.Auth;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Api.Search;
using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Tests;

/// <summary>
/// <see cref="ScopedCorpus"/> on its own, against a sidecar that answers whatever the test says, so each test can
/// make the sidecar misbehave in exactly one way and check what the caller and the audit record see.
/// </summary>
public sealed class ScopedCorpusTests
{
	private const string Mine = "mine";

	private const string Theirs = "theirs";

	private static SidecarSearchHit Hit(string collection, string file)
	{
		return new SidecarSearchHit("d1", file, collection, "a.md", "Title", 0.5, null, 1, "snippet", null);
	}

	private static SidecarDocument Doc(string collection, string? body = "body", string? file = null)
	{
		return new SidecarDocument("d1", file ?? $"{collection}/a.md", collection, "a.md", "Title", null, "2026-11-01T00:00:00.000Z", body?.Length ?? 0, null, null, body, null);
	}

	private static SidecarSearchResponse Results(params SidecarSearchHit[] hits)
	{
		return new SidecarSearchResponse(hits, hits.Length, 3, new SidecarSearchQueryInfo([Mine, Theirs], true, true, hits.Length, 10));
	}

	private static ScopedCorpus Corpus(Sidecar sidecar, string[]? collections, RecordingAudit audit, int maxMultiGetBytes = 1024, bool admin = false)
	{
		MemoriaOptions options = new() { Search = new SearchOptions { MaxMultiGetBytes = maxMultiGetBytes } };
		return new ScopedCorpus(sidecar, new FixedScope(collections, admin), audit, Options.Create(options));
	}

	// --- argument checks ------------------------------------------------------------------------------------------

	[Fact]
	public async Task NullRequests_AreRefusedAsArguments_BeforeAnythingIsAudited()
	{
		RecordingAudit audit = new();
		ScopedCorpus corpus = Corpus(new Sidecar(), [Mine], audit);

		await Assert.ThrowsAsync<ArgumentNullException>(() => corpus.SearchAsync(null!, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentNullException>(() => corpus.GetDocumentAsync("mine/a.md", null!, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentNullException>(() => corpus.MultiGetAsync(null!, CancellationToken.None));
		Assert.Empty(audit.Queries);
	}

	// --- search ---------------------------------------------------------------------------------------------------

	[Fact]
	public async Task Search_ByACallerWhoReadsNothing_EchoesTheRequest_ReachesNoSidecar_AndAuditsZeroResults()
	{
		Sidecar sidecar = new();
		RecordingAudit audit = new();

		SearchResult result = await Corpus(sidecar, [], audit).SearchAsync(
			new SidecarSearchRequest { Query = "q", Rerank = true, Filters = new SidecarSearchFilters(Project: ["p"]), Limit = 7 },
			CancellationToken.None);

		Assert.Equal(0, sidecar.Calls);
		Assert.Empty(result.Results);
		Assert.Equal(0, result.Total);
		Assert.False(result.Degraded);
		Assert.True(result.Query.Rerank);
		Assert.True(result.Query.Filtered);
		Assert.Equal(7, result.Query.Limit);
		Assert.Equal(0, result.Query.Fetched);
		Assert.Equal(([], 0), audit.Result);
	}

	[Fact]
	public async Task Search_ByACallerWhoReadsNothing_WithNoOptions_ReportsNoRerankNoFilterAndNoLimit()
	{
		SearchResult result = await Corpus(new Sidecar(), [], new RecordingAudit()).SearchAsync(new SidecarSearchRequest { Query = "q" }, CancellationToken.None);

		Assert.False(result.Query.Rerank);
		Assert.False(result.Query.Filtered);
		Assert.Equal(0, result.Query.Limit);
	}

	[Fact]
	public async Task Search_DropsAHitWhoseFileIsInAnotherCollection_EvenWhenItsCollectionFieldIsAllowed()
	{
		Sidecar sidecar = new() { Search = Results(Hit(Mine, "mine/a.md"), Hit(Mine, "theirs/secret.md"), Hit(Theirs, "mine/b.md")) };
		RecordingAudit audit = new();

		SearchResult result = await Corpus(sidecar, [Mine], audit).SearchAsync(new SidecarSearchRequest { Query = "q" }, CancellationToken.None);

		SearchHit hit = Assert.Single(result.Results);
		Assert.Equal("mine/a.md", hit.File);
		Assert.Equal(1, result.Total);
		Assert.Equal(1, result.Query.Fetched);
		Assert.Equal([Mine], result.Query.Collections);
		Assert.Equal(([Mine], 1), audit.Result);
	}

	// --- one document -------------------------------------------------------------------------------------------

	[Fact]
	public async Task GetDocument_RecordsTheReference_AndAnUnresolvedTarget_UntilItIsFound()
	{
		RecordingAudit refused = new();
		Assert.Null(await Corpus(new Sidecar { Document = Doc(Theirs) }, [Mine], refused).GetDocumentAsync("theirs/a.md", new SidecarDocumentQuery(), CancellationToken.None));
		Assert.Equal(["theirs/a.md"], refused.Queries);
		Assert.Equal(new AuditRead("[unresolved]", false), refused.Target);
		Assert.Equal(([], 0), refused.Result);

		RecordingAudit found = new();
		Assert.NotNull(await Corpus(new Sidecar { Document = Doc(Mine) }, [Mine], found).GetDocumentAsync("mine/a.md", new SidecarDocumentQuery(), CancellationToken.None));
		Assert.Equal(new AuditRead("mine/a.md", true), found.Target);
		Assert.Equal(([Mine], 1), found.Result);
	}

	[Fact]
	public async Task GetDocument_ByACallerWhoReadsNothing_ReachesNoSidecar_AndAuditsZeroResults()
	{
		Sidecar sidecar = new() { Document = Doc(Mine) };
		RecordingAudit audit = new();

		Assert.Null(await Corpus(sidecar, [], audit).GetDocumentAsync("mine/a.md", new SidecarDocumentQuery(), CancellationToken.None));

		Assert.Equal(0, sidecar.Calls);
		Assert.Equal(([], 0), audit.Result);
	}

	[Fact]
	public async Task GetDocument_AReferenceAtTheLengthLimit_IsLookedUp_AndOneLonger_IsNot()
	{
		string atLimit = "mine/" + new string('a', MultiGetPolicy.MaxReferenceLength - 5);
		Sidecar sidecar = new() { Document = Doc(Mine) };
		RecordingAudit audit = new();
		ScopedCorpus corpus = Corpus(sidecar, [Mine], audit);

		Assert.NotNull(await corpus.GetDocumentAsync(atLimit, new SidecarDocumentQuery(), CancellationToken.None));
		Assert.Equal(1, sidecar.Calls);

		Assert.Null(await corpus.GetDocumentAsync(atLimit + "a", new SidecarDocumentQuery(), CancellationToken.None));
		Assert.Equal(1, sidecar.Calls);
		Assert.Equal(([], 0), audit.Result);
	}

	[Fact]
	public async Task GetDocument_ASidecarMiss_IsNullForAScopedCaller_AndAuditedAsNothingFound()
	{
		Sidecar sidecar = new() { DocumentError = new SidecarException("not_found", "Document not found: theirs/a.md", HttpStatusCode.NotFound) };
		RecordingAudit audit = new();

		Assert.Null(await Corpus(sidecar, [Mine], audit).GetDocumentAsync("theirs/a.md", new SidecarDocumentQuery(), CancellationToken.None));

		Assert.Equal(([], 0), audit.Result);
	}

	[Fact]
	public async Task GetDocument_ASidecarFailure_IsNeverPassedOffAsAMiss()
	{
		SidecarException failure = new("internal", "boom", HttpStatusCode.InternalServerError);
		Sidecar sidecar = new() { DocumentError = failure };

		SidecarException thrown = await Assert.ThrowsAsync<SidecarException>(() => Corpus(sidecar, [Mine], new RecordingAudit()).GetDocumentAsync("mine/a.md", new SidecarDocumentQuery(), CancellationToken.None));

		Assert.Same(failure, thrown);
	}

	// --- many documents -----------------------------------------------------------------------------------------

	[Fact]
	public async Task MultiGet_RecordsThePatternOrTheIds_AsTheQuery()
	{
		RecordingAudit byPattern = new();
		await Corpus(new Sidecar(), [Mine], byPattern).MultiGetAsync(new SidecarMultiGetRequest { Pattern = "mine/*.md", Ids = ["x"] }, CancellationToken.None);
		Assert.Equal(["mine/*.md"], byPattern.Queries);

		RecordingAudit byIds = new();
		await Corpus(new Sidecar(), [Mine], byIds).MultiGetAsync(new SidecarMultiGetRequest { Ids = ["mine/a.md", "mine/b.md"] }, CancellationToken.None);
		Assert.Equal(["mine/a.md,mine/b.md"], byIds.Queries);

		RecordingAudit neither = new();
		await Corpus(new Sidecar(), [Mine], neither).MultiGetAsync(new SidecarMultiGetRequest(), CancellationToken.None);
		Assert.Equal([""], neither.Queries);
	}

	[Fact]
	public async Task MultiGet_ByACallerWhoReadsNothing_ReachesNoSidecar_IsNotTruncated_AndAuditsZeroResults()
	{
		Sidecar sidecar = new();
		RecordingAudit audit = new();

		MultiGetResult result = await Corpus(sidecar, [], audit).MultiGetAsync(new SidecarMultiGetRequest { Pattern = "*/*.md" }, CancellationToken.None);

		Assert.Equal(0, sidecar.Calls);
		Assert.Empty(result.Docs);
		Assert.False(result.Truncated);
		Assert.Equal(([], 0), audit.Result);
	}

	[Fact]
	public async Task MultiGet_AuditsTheCollectionsAndCountOfWhatItReturns()
	{
		Sidecar sidecar = new() { MultiGet = new SidecarMultiGetResponse([Doc(Mine), Doc(Theirs), Doc(Mine, file: "mine/b.md")], [], []) };
		RecordingAudit audit = new();

		MultiGetResult result = await Corpus(sidecar, [Mine], audit).MultiGetAsync(new SidecarMultiGetRequest { Pattern = "*/*.md" }, CancellationToken.None);

		Assert.Equal(2, result.Docs.Count);
		Assert.Equal(([Mine, Mine], 2), audit.Result);
	}

	[Fact]
	public async Task MultiGet_ADocumentThatExactlyFillsTheBudget_IsKept_AndTheNextIsSkipped()
	{
		Sidecar sidecar = new() { MultiGet = new SidecarMultiGetResponse([Doc(Mine, new string('x', 10)), Doc(Mine, "y", "mine/b.md")], [], []) };

		MultiGetResult result = await Corpus(sidecar, [Mine], new RecordingAudit(), maxMultiGetBytes: 10).MultiGetAsync(new SidecarMultiGetRequest { Pattern = "*/*.md" }, CancellationToken.None);

		Assert.Equal("mine/a.md", Assert.Single(result.Docs).File);
		SkippedDocument skipped = Assert.Single(result.Skipped);
		Assert.Equal("mine/b.md", skipped.File);
		Assert.Equal(10, sidecar.LastMultiGet!.MaxTotalBytes);
	}

	// --- status -------------------------------------------------------------------------------------------------

	[Fact]
	public async Task Status_AnUnrestrictedCaller_GetsTheSidecarsOwnTotal_NotTheSumOfTheCollectionsItLists()
	{
		Sidecar sidecar = new() { Status = new SidecarStatus(100, 4, true, [new SidecarStatusCollection(Mine, null, null, 3, "x"), new SidecarStatusCollection(Theirs, null, null, 5, "x")]) };
		RecordingAudit audit = new();

		IndexStatus status = await Corpus(sidecar, null, audit).GetStatusAsync(CancellationToken.None);

		Assert.Equal(100, status.TotalDocuments);
		Assert.Equal(4, status.NeedsEmbedding);
		Assert.Equal(([Mine, Theirs], 2), audit.Result);
	}

	[Fact]
	public async Task Status_AScopedCaller_GetsOnlyItsOwnCollectionsTotal_AndThatIsAudited()
	{
		Sidecar sidecar = new() { Status = new SidecarStatus(100, 4, true, [new SidecarStatusCollection(Mine, null, null, 3, "x"), new SidecarStatusCollection(Theirs, null, null, 5, "x")]) };
		RecordingAudit audit = new();

		IndexStatus status = await Corpus(sidecar, [Mine], audit).GetStatusAsync(CancellationToken.None);

		Assert.Equal(3, status.TotalDocuments);
		Assert.Null(status.NeedsEmbedding);
		Assert.Equal(([Mine], 1), audit.Result);
	}

	// --- fakes --------------------------------------------------------------------------------------------------

	private sealed class FixedScope(string[]? collections, bool admin) : ICallerScope
	{
		public IReadOnlyCollection<string>? Collections => collections;

		public bool SeesBackendDetails => admin;
	}

	private sealed class RecordingAudit : IRequestAudit
	{
		public List<string?> Queries { get; } = [];

		public (string[] Collections, int Count)? Result { get; private set; }

		public object? Target { get; private set; }

		public void SetOperation(string operation)
		{
		}

		public void SetQuery(string? query)
		{
			Queries.Add(query);
		}

		public void SetResult(IEnumerable<string> collections, int resultCount)
		{
			Result = (collections.ToArray(), resultCount);
		}

		public void SetOutcome(int status, IEnumerable<string>? attemptedCollections = null)
		{
		}

		public void SetTarget(object target)
		{
			Target = target;
		}
	}

	/// <summary>Answers the five reads <see cref="ScopedCorpus"/> makes; anything else is a test bug.</summary>
	private sealed class Sidecar : IMemoriaQmdClient
	{
		public int Calls { get; private set; }

		public SidecarSearchResponse Search { get; init; } = Results();

		public SidecarDocument? Document { get; init; }

		public SidecarException? DocumentError { get; init; }

		public SidecarMultiGetResponse MultiGet { get; init; } = new([], [], []);

		public SidecarMultiGetRequest? LastMultiGet { get; private set; }

		public SidecarStatus Status { get; init; } = new(0, 0, false, []);

		public Task<SidecarSearchResponse> SearchAsync(SidecarSearchRequest request, CancellationToken cancellationToken = default)
		{
			Calls++;
			return Task.FromResult(Search);
		}

		public Task<SidecarDocument> GetDocumentAsync(string reference, SidecarDocumentQuery? query = null, CancellationToken cancellationToken = default)
		{
			Calls++;
			return DocumentError is { } error ? Task.FromException<SidecarDocument>(error) : Task.FromResult(Document!);
		}

		public Task<SidecarMultiGetResponse> MultiGetAsync(SidecarMultiGetRequest request, CancellationToken cancellationToken = default)
		{
			Calls++;
			LastMultiGet = request;
			return Task.FromResult(MultiGet);
		}

		public Task<SidecarStatus> GetStatusAsync(CancellationToken cancellationToken = default)
		{
			Calls++;
			return Task.FromResult(Status);
		}

		public Task<SidecarCollectionsResponse> ListCollectionsAsync(CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarHealth> GetHealthAsync(CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarReadinessResult> GetReadinessAsync(bool requireModels, CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarCollectionEmptyResponse> IsCollectionEmptyAsync(string collection, CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarIngestResponse> IngestAsync(SidecarIngestRequest request, CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarDeleteResponse> DeleteDocumentAsync(string collection, string path, CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarJob> ReindexAsync(SidecarReindexRequest request, CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<IReadOnlyList<SidecarJob>> ListJobsAsync(CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarJob> GetJobAsync(string id, CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarJob> RetryJobAsync(string id, CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarJob> CancelJobAsync(string id, CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarQueue> GetQueueAsync(CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarPauseResponse> PauseQueueAsync(CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarPauseResponse> ResumeQueueAsync(CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarWorkersResponse> ListWorkersAsync(CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarWorker> DrainWorkerAsync(string name, CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task<SidecarWorker> EnableWorkerAsync(string name, CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}
	}
}
