using System.ComponentModel;
using Memoria.Api.Auth;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Api.Search;
using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Memoria.Api.Mcp;

/// <summary>
/// The outward MCP surface. Each tool is a thin translation to
/// <see cref="ScopedCorpus"/> through the same request factory the REST
/// endpoints use, so a Claude Code session and a <c>curl</c> see the same
/// defaults and the same collections. Memoria owns this surface; QMD's own MCP
/// transport is never forwarded.
/// </summary>
[McpServerToolType]
public sealed class MemoriaTools(ScopedCorpus corpus, ICallerScope scope, IRequestAudit audit, IOptions<MemoriaOptions> options)
{
	[McpServerTool(Name = "search", Title = "Search Memoria", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
	[Description(
		"Search the accumulated record (agent transcripts, issues, notes) with hybrid BM25 + vector retrieval and LLM reranking. "
		+ "Use a natural-language query; add `intent` to say what you are trying to accomplish so reranking and snippets favour it. "
		+ "Reranked searches take a few seconds; pass rerank=false for a sub-second fused result when breadth matters more than precision. "
		+ "Each hit carries a `line`: pass it to get_document as fromLine to read the matching passage.")]
	public Task<SearchResult> SearchAsync(
		[Description("Natural-language query.")] string query,
		[Description("Background context: what you are trying to do or decide. Steers reranking and snippet selection.")] string? intent = null,
		[Description("Collections to search. Omit for the default set; list_collections shows what exists.")] string[]? collections = null,
		[Description("Maximum results, 1 to 100. Default 10.")] int? limit = null,
		[Description("Drop hits scoring below this, 0 to 1.")] double? minScore = null,
		[Description("Run the LLM reranker. Default true. false is much faster but less precise.")] bool? rerank = null,
		[Description("Frontmatter filter: documents whose `project` is any of these.")] string[]? project = null,
		[Description("Frontmatter filter: documents whose git branch is any of these.")] string[]? branch = null,
		[Description("Frontmatter filter: only documents starting on or after this date (YYYY-MM-DD or ISO timestamp).")] string? since = null,
		[Description("Frontmatter filter: only documents starting on or before this date.")] string? until = null,
		[Description("Frontmatter filter: every listed tag must be present.")] string[]? tags = null,
		CancellationToken cancellationToken = default)
	{
		audit.SetOperation("mcp.search");
		return Guarded(async () =>
		{
			SearchInput input = new(query, collections, limit, minScore, rerank, intent, null, project, branch, since, until, tags);
			SidecarSearchRequest? request = SearchRequestFactory.TryBuild(input, options.Value.Search, scope, out Dictionary<string, string[]> errors);
			if (request is null)
			{
				throw new McpException(string.Join(" ", errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}")));
			}

			return await corpus.SearchAsync(request, cancellationToken);
		});
	}

	[McpServerTool(Name = "get_document", Title = "Get a document", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
	[Description(
		"Fetch one document by its `file` (collection/path.md) or six-character `docid` from a search hit. "
		+ "Documents can be long: pass fromLine (a hit's `line`) and maxLines to read just the relevant passage, "
		+ "or includeBody=false for metadata only.")]
	public Task<Document> GetDocumentAsync(
		[Description("`collection/path.md`, `qmd://collection/path.md`, or a docid.")] string reference,
		[Description("First line to return, 1-indexed.")] int? fromLine = null,
		[Description("How many lines to return from fromLine.")] int? maxLines = null,
		[Description("Prefix each line with its number.")] bool? lineNumbers = null,
		[Description("false returns metadata only. Default true.")] bool? includeBody = null,
		CancellationToken cancellationToken = default)
	{
		audit.SetOperation("mcp.get_document");
		if (string.IsNullOrWhiteSpace(reference))
		{
			throw new McpException("reference is required.");
		}

		return Guarded(async () =>
		{
			Document? document = await corpus.GetDocumentAsync(reference, new SidecarDocumentQuery(fromLine, maxLines, lineNumbers, includeBody), cancellationToken);
			return document ?? throw new McpException($"not_found: {SidecarDisclosure.NotFoundMessage}");
		});
	}

	[McpServerTool(Name = "get_documents", Title = "Get several documents", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
	[Description(
		"Fetch several documents at once by a list of references (files or docids) or by a glob such as `claude-sessions/2026-05*.md`. "
		+ "Bodies are capped at maxBytes each (default 64 KiB); documents over the cap are listed under `skipped`. Use ids or pattern, not both.")]
	public Task<MultiGetResult> GetDocumentsAsync(
		[Description("Files (collection/path.md) or docids, up to 200.")] string[]? ids = null,
		[Description("Glob over `collection/path`, e.g. `claude-sessions/2026-10*.md`.")] string? pattern = null,
		[Description("Per-document body cap in bytes. Default 65536.")] int? maxBytes = null,
		[Description("Truncate each body to this many lines.")] int? maxLines = null,
		[Description("false returns metadata only. Default true.")] bool? includeBody = null,
		CancellationToken cancellationToken = default)
	{
		audit.SetOperation("mcp.get_documents");
		bool hasIds = ids is { Length: > 0 };
		bool hasPattern = !string.IsNullOrWhiteSpace(pattern);
		if (!hasIds && !hasPattern)
		{
			throw new McpException("Provide ids, pattern, or both.");
		}

		if (ids is { Length: > 200 })
		{
			throw new McpException("At most 200 ids per call.");
		}

		return Guarded(() => corpus.MultiGetAsync(new SidecarMultiGetRequest
		{
			Ids = hasIds ? ids : null,
			Pattern = hasPattern ? pattern : null,
			MaxBytes = maxBytes,
			MaxLines = maxLines,
			IncludeBody = includeBody,
		}, cancellationToken));
	}

	[McpServerTool(Name = "list_collections", Title = "List collections", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
	[Description("List the collections you can search, with document counts and a short description of what each holds.")]
	public Task<CollectionList> ListCollectionsAsync(CancellationToken cancellationToken = default)
	{
		audit.SetOperation("mcp.list_collections");
		return Guarded(() => corpus.ListCollectionsAsync(cancellationToken));
	}

	[McpServerTool(Name = "status", Title = "Index status", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
	[Description("Totals for the collections you can read: documents indexed, whether vector search is available, and per-collection counts. needsEmbedding covers the whole index, so it is null for a collection-scoped key.")]
	public Task<IndexStatus> GetStatusAsync(CancellationToken cancellationToken = default)
	{
		audit.SetOperation("mcp.status");
		return Guarded(() => corpus.GetStatusAsync(cancellationToken));
	}

	/// <summary>
	/// The SDK hides the message of any exception that is not an <see cref="McpException"/>;
	/// surface the sidecar's code and whatever <see cref="SidecarDisclosure"/> lets
	/// this caller see, so the model can act on "not_found" or "sidecar_unreachable"
	/// instead of a generic failure.
	/// </summary>
	private async Task<T> Guarded<T>(Func<Task<T>> action)
	{
		try
		{
			return await action();
		}
		catch (SidecarException ex)
		{
			SidecarFailureView view = SidecarDisclosure.For(ex, scope);
			throw new McpException($"{view.Code}: {view.Message}", ex);
		}
		catch (CollectionForbiddenException ex)
		{
			throw new McpException(ex.Message, ex);
		}
	}
}
