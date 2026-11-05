using System.Net;
using Memoria.Api.Auth;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Api.Search;
using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Tests;

/// <summary>
/// The corpus as a scoped caller sees it, against a sidecar that does not cooperate:
/// it can answer with anything, so nothing the caller gets may depend on it behaving.
/// </summary>
public sealed class ScopedCorpusTests
{
	private sealed class Scope(params string[]? collections) : ICallerScope
	{
		public IReadOnlyCollection<string>? Collections { get; } = collections;

		public bool SeesBackendDetails => false;
	}

	private sealed class RecordingAudit : IRequestAudit
	{
		public string? Query { get; private set; }

		public string[]? Collections { get; private set; }

		public int? Count { get; private set; }

		public void SetOperation(string operation)
		{
		}

		public void SetQuery(string? query)
		{
			Query = query;
		}

		public void SetResult(IEnumerable<string> collections, int resultCount)
		{
			Collections = collections.ToArray();
			Count = resultCount;
		}

		public void SetOutcome(int status, IEnumerable<string>? attemptedCollections = null)
		{
		}

		public void SetTarget(AuditTarget target)
		{
		}
	}

	private sealed class StubSidecar : IMemoriaQmdClient
	{
		public int Calls { get; private set; }

		public SidecarSearchResponse? Search { get; init; }

		public Func<SidecarDocument>? Document { get; init; }

		public SidecarMultiGetResponse? MultiGet { get; init; }

		public SidecarStatus? Status { get; init; }

		public SidecarDocumentQuery? LastDocumentQuery { get; private set; }

		public SidecarMultiGetRequest? LastMultiGet { get; private set; }

		public Task<SidecarSearchResponse> SearchAsync(SidecarSearchRequest request, CancellationToken cancellationToken = default)
		{
			Calls++;
			return Task.FromResult(Search!);
		}

		public Task<SidecarDocument> GetDocumentAsync(string reference, SidecarDocumentQuery? query = null, CancellationToken cancellationToken = default)
		{
			Calls++;
			LastDocumentQuery = query;
			return Task.FromResult(Document!());
		}

		public Task<SidecarMultiGetResponse> MultiGetAsync(SidecarMultiGetRequest request, CancellationToken cancellationToken = default)
		{
			Calls++;
			LastMultiGet = request;
			return Task.FromResult(MultiGet!);
		}

		public Task<SidecarStatus> GetStatusAsync(CancellationToken cancellationToken = default)
		{
			Calls++;
			return Task.FromResult(Status!);
		}

		public Task<SidecarHealth> GetHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<SidecarReadinessResult> GetReadinessAsync(bool requireModels, CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<SidecarCollectionsResponse> ListCollectionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<SidecarIngestResponse> IngestAsync(SidecarIngestRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<SidecarJob> ReindexAsync(SidecarReindexRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<IReadOnlyList<SidecarJob>> ListJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<SidecarJob> GetJobAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private static ScopedCorpus Corpus(IMemoriaQmdClient sidecar, ICallerScope scope, IRequestAudit audit, int budget = 1024)
	{
		return new ScopedCorpus(sidecar, scope, audit, Options.Create(new MemoriaOptions { Search = new SearchOptions { MaxMultiGetBytes = budget } }));
	}

	private static SidecarSearchHit Hit(string collection, string file)
	{
		return new SidecarSearchHit("doc", file, collection, "a.md", "A", 1, null, 1, "snippet", null);
	}

	private static SidecarDocument Doc(string collection, string? body = "body")
	{
		return new SidecarDocument("doc", $"{collection}/a.md", collection, "a.md", "A", null, "2026-01-01", body?.Length ?? 0, null, null, body, null);
	}

	private static SidecarSearchResponse SearchResponse(params SidecarSearchHit[] hits)
	{
		return new SidecarSearchResponse(hits, hits.Length, 5, new SidecarSearchQueryInfo(["work", "private"], true, true, hits.Length, 10));
	}

	[Fact]
	public async Task Search_DropsAHit_WhenEitherItsCollectionOrItsFileIsOutOfScope()
	{
		StubSidecar sidecar = new()
		{
			Search = SearchResponse(
				Hit("work", "work/kept.md"),
				Hit("work", "private/lying-collection.md"),
				Hit("private", "work/lying-file.md")),
		};
		RecordingAudit audit = new();

		SearchResult result = await Corpus(sidecar, new Scope("work"), audit).SearchAsync(new SidecarSearchRequest { Query = "q" }, default);

		SearchHit kept = Assert.Single(result.Results);
		Assert.Equal("work/kept.md", kept.File);
		Assert.Equal(1, result.Total);
		Assert.Equal(1, result.Query.Fetched);
		Assert.Equal(["work"], result.Query.Collections);
		Assert.Equal("q", audit.Query);
		Assert.Equal(["work"], audit.Collections!);
		Assert.Equal(1, audit.Count);
	}

	[Fact]
	public async Task Search_ByACallerWithNoCollections_NeverLeaves_AndEchoesTheRequest()
	{
		StubSidecar sidecar = new();
		RecordingAudit audit = new();
		Scope scope = new([]);

		SearchResult plain = await Corpus(sidecar, scope, audit).SearchAsync(new SidecarSearchRequest { Query = "q" }, default);
		Assert.False(plain.Query.Rerank);
		Assert.False(plain.Query.Filtered);
		Assert.Equal(0, plain.Query.Limit);

		SearchResult full = await Corpus(sidecar, scope, audit).SearchAsync(new SidecarSearchRequest { Query = "q", Rerank = true, Limit = 7, Filters = new SidecarSearchFilters(Tags: ["a"]) }, default);
		Assert.True(full.Query.Rerank);
		Assert.True(full.Query.Filtered);
		Assert.Equal(7, full.Query.Limit);
		Assert.Empty(full.Results);

		Assert.Equal(0, sidecar.Calls);
		Assert.Empty(audit.Collections!);
		Assert.Equal(0, audit.Count);
	}

	[Fact]
	public async Task NullArguments_AreRefused()
	{
		ScopedCorpus corpus = Corpus(new StubSidecar(), new Scope("work"), new RecordingAudit());
		await Assert.ThrowsAsync<ArgumentNullException>(() => corpus.SearchAsync(null!, default));
		await Assert.ThrowsAsync<ArgumentNullException>(() => corpus.GetDocumentAsync("a.md", null!, default));
		await Assert.ThrowsAsync<ArgumentNullException>(() => corpus.MultiGetAsync(null!, default));
	}

	[Fact]
	public async Task GetDocument_RecordsTheReference_AndNarrowsTheSidecarToTheScope()
	{
		StubSidecar sidecar = new() { Document = static () => Doc("work") };
		RecordingAudit audit = new();

		Document? document = await Corpus(sidecar, new Scope("work"), audit).GetDocumentAsync("work/a.md", new SidecarDocumentQuery(SimilarFiles: true), default);

		Assert.NotNull(document);
		Assert.Equal("work/a.md", audit.Query);
		Assert.Equal(["work"], sidecar.LastDocumentQuery!.Collections);
		Assert.False(sidecar.LastDocumentQuery.SimilarFiles);
		Assert.Equal(["work"], audit.Collections!);
		Assert.Equal(1, audit.Count);
	}

	[Theory]
	[InlineData(MultiGetPolicy.MaxReferenceLength, 1)]
	[InlineData(MultiGetPolicy.MaxReferenceLength + 1, 0)]
	public async Task GetDocument_OnlyTooLongAReferenceStaysHome(int length, int calls)
	{
		StubSidecar sidecar = new() { Document = static () => Doc("work") };
		RecordingAudit audit = new();

		Document? document = await Corpus(sidecar, new Scope("work"), audit).GetDocumentAsync(new string('r', length), new SidecarDocumentQuery(), default);

		Assert.Equal(calls, sidecar.Calls);
		Assert.Equal(calls == 1, document is not null);
		Assert.Equal(calls, audit.Count);
	}

	[Fact]
	public async Task GetDocument_ByACallerWithNoCollections_RecordsNothingFound()
	{
		StubSidecar sidecar = new();
		RecordingAudit audit = new();

		Assert.Null(await Corpus(sidecar, new Scope([]), audit).GetDocumentAsync("a.md", new SidecarDocumentQuery(), default));

		Assert.Equal(0, sidecar.Calls);
		Assert.Empty(audit.Collections!);
		Assert.Equal(0, audit.Count);
	}

	[Fact]
	public async Task GetDocument_OutOfScopeAnswer_LooksMissing()
	{
		RecordingAudit audit = new();
		StubSidecar sidecar = new() { Document = static () => Doc("private") };

		Assert.Null(await Corpus(sidecar, new Scope("work"), audit).GetDocumentAsync("a.md", new SidecarDocumentQuery(), default));

		Assert.Empty(audit.Collections!);
		Assert.Equal(0, audit.Count);
	}

	[Fact]
	public async Task GetDocument_AMiss_LooksMissingToAScopedCaller_AndIsAnErrorToAnUnrestrictedOne()
	{
		SidecarException miss = new("not_found", "no such document", HttpStatusCode.NotFound);
		StubSidecar sidecar = new() { Document = () => throw miss };
		RecordingAudit audit = new();

		Assert.Null(await Corpus(sidecar, new Scope("work"), audit).GetDocumentAsync("a.md", new SidecarDocumentQuery(), default));
		Assert.Empty(audit.Collections!);
		Assert.Equal(0, audit.Count);

		await Assert.ThrowsAsync<SidecarException>(() => Corpus(sidecar, new Scope(null), audit).GetDocumentAsync("a.md", new SidecarDocumentQuery(), default));
	}

	[Fact]
	public async Task GetDocument_AnyOtherFailure_StaysAFailure_EvenForAScopedCaller()
	{
		StubSidecar sidecar = new() { Document = static () => throw new SidecarException("internal", "boom", HttpStatusCode.InternalServerError) };

		await Assert.ThrowsAsync<SidecarException>(() => Corpus(sidecar, new Scope("work"), new RecordingAudit()).GetDocumentAsync("a.md", new SidecarDocumentQuery(), default));
	}

	[Fact]
	public async Task MultiGet_ByACallerWithNoCollections_NeverLeaves()
	{
		StubSidecar sidecar = new();
		RecordingAudit audit = new();

		MultiGetResult result = await Corpus(sidecar, new Scope([]), audit).MultiGetAsync(new SidecarMultiGetRequest { Pattern = "*.md" }, default);

		Assert.Equal(0, sidecar.Calls);
		Assert.Empty(result.Docs);
		Assert.False(result.Truncated);
		Assert.Empty(audit.Collections!);
		Assert.Equal(0, audit.Count);
	}

	[Theory]
	[InlineData("*.md", null, "*.md")]
	[InlineData(null, "a.md,b.md", "a.md,b.md")]
	[InlineData(null, null, "")]
	public async Task MultiGet_RecordsWhatWasAskedFor(string? pattern, string? ids, string expected)
	{
		RecordingAudit audit = new();
		SidecarMultiGetRequest request = new() { Pattern = pattern, Ids = ids?.Split(',') };

		await Corpus(new StubSidecar(), new Scope([]), audit).MultiGetAsync(request, default);

		Assert.Equal(expected, audit.Query);
	}

	[Fact]
	public async Task MultiGet_HoldsTheBudget_ToTheByte()
	{
		StubSidecar sidecar = new()
		{
			MultiGet = new SidecarMultiGetResponse([Doc("work", "1234"), Doc("work", "x"), Doc("work", null)], [], []),
		};
		RecordingAudit audit = new();

		MultiGetResult result = await Corpus(sidecar, new Scope("work"), audit, budget: 4).MultiGetAsync(new SidecarMultiGetRequest { Pattern = "*" }, default);

		Assert.Equal(2, result.Docs.Count);
		SkippedDocument skipped = Assert.Single(result.Skipped);
		Assert.Equal("Response size budget reached", skipped.Reason);
		Assert.Equal(4, sidecar.LastMultiGet!.MaxTotalBytes);
		Assert.Equal(2, audit.Count);
	}

	[Fact]
	public async Task MultiGet_HidesErrorsFromAScopedCallerOnly_AndFiltersSkippedByFile()
	{
		SidecarMultiGetResponse response = new(
			[Doc("work"), Doc("private")],
			[new SidecarSkippedDocument("work/big.md", "too big"), new SidecarSkippedDocument("private/big.md", "too big")],
			["did you mean private/secret.md?"],
			true);

		MultiGetResult scoped = await Corpus(new StubSidecar { MultiGet = response }, new Scope("work"), new RecordingAudit()).MultiGetAsync(new SidecarMultiGetRequest { Pattern = "*" }, default);
		Assert.Empty(scoped.Errors);
		Assert.Equal("work/big.md", Assert.Single(scoped.Skipped).File);
		Assert.Equal("work", Assert.Single(scoped.Docs).Collection);
		Assert.True(scoped.Truncated);

		MultiGetResult everyone = await Corpus(new StubSidecar { MultiGet = response }, new Scope(null), new RecordingAudit()).MultiGetAsync(new SidecarMultiGetRequest { Pattern = "*" }, default);
		Assert.NotEmpty(everyone.Errors);
	}

	[Fact]
	public async Task Status_TotalsCoverOnlyWhatAScopedCallerMaySee()
	{
		SidecarStatus status = new(100, 9, true, [new SidecarStatusCollection("work", null, null, 3, "t"), new SidecarStatusCollection("private", null, null, 97, "t")]);
		RecordingAudit audit = new();

		IndexStatus scoped = await Corpus(new StubSidecar { Status = status }, new Scope("work"), audit).GetStatusAsync(default);
		Assert.Equal(3, scoped.TotalDocuments);
		Assert.Null(scoped.NeedsEmbedding);
		Assert.Equal(["work"], audit.Collections!);
		Assert.Equal(1, audit.Count);

		IndexStatus everyone = await Corpus(new StubSidecar { Status = status }, new Scope(null), audit).GetStatusAsync(default);
		Assert.Equal(100, everyone.TotalDocuments);
		Assert.Equal(9, everyone.NeedsEmbedding);
	}

	[Fact]
	public void Allows_RefusesNoCollectionAtAll_EvenForAnUnrestrictedCaller()
	{
		Assert.False(new Scope(null).Allows(null));
		Assert.False(new Scope("work").Allows(null));
	}
}
