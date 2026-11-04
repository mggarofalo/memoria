using System.ComponentModel;
using Memoria.Api.Http;
using Memoria.Api.Search;
using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Memoria.Api.Endpoints;

public static class DocumentEndpoints
{
	public static RouteGroupBuilder MapDocuments(this RouteGroupBuilder group)
	{
		group.MapGet("/docs/{**id}", GetDocumentAsync)
			.WithName("getDocument")
			.WithSummary("Get one document")
			.WithDescription(
				"Fetch a document by display path (`collection/path.md`), by `qmd://` URI, or by its six-character docid. "
				+ "`fromLine` and `maxLines` narrow the body to the lines around a search hit; `body=false` returns metadata only.")
			.WithTags("Documents")
			.ProducesProblem(StatusCodes.Status404NotFound);

		group.MapPost("/docs:multiGet", MultiGetAsync)
			.WithName("getDocuments")
			.WithSummary("Get several documents")
			.WithDescription(
				"Batch fetch by a list of references (paths or docids), by a glob such as `claude-sessions/2026-05*.md`, or both. "
				+ "A glob may use only `*`, `?` and whole-segment `**`; braces, brackets and extglobs are refused with 400. "
				+ "Bodies are capped at `maxBytes` each (default 64 KiB, at most 1 MiB); documents over the cap are listed under `skipped`. "
				+ "For a collection-scoped key every list is filtered per result and `errors` is always empty: "
				+ "a reference that matched nothing the key may read is simply absent.")
			.WithTags("Documents")
			.ProducesValidationProblem();

		return group;
	}

	private static async Task<Results<Ok<Document>, ProblemHttpResult>> GetDocumentAsync(
		[Description("Document reference: `collection/path.md`, `qmd://collection/path.md`, or a docid.")] string id,
		[FromQuery(Name = "fromLine")][Description("First line of the body to return (1-indexed).")] int? fromLine,
		[FromQuery(Name = "maxLines")][Description("Number of lines to return from `fromLine`.")] int? maxLines,
		[FromQuery(Name = "lineNumbers")][Description("Prefix each body line with `N: `.")] bool? lineNumbers,
		[FromQuery(Name = "body")][Description("false returns metadata only.")] bool? body,
		ScopedCorpus corpus,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			return NotFound();
		}

		Document? document = await corpus.GetDocumentAsync(
			id,
			new SidecarDocumentQuery(fromLine, maxLines, lineNumbers, body),
			cancellationToken);

		// A document outside the caller's scope is indistinguishable from a
		// missing one; do not confirm that it exists.
		return document is null ? NotFound() : TypedResults.Ok(document);
	}

	private static async Task<Results<Ok<MultiGetResult>, ValidationProblem>> MultiGetAsync(
		MultiGetRequest request,
		ScopedCorpus corpus,
		CancellationToken cancellationToken)
	{
		Dictionary<string, string[]> errors = MultiGetPolicy.Validate(request.Ids, request.Pattern, request.MaxBytes, request.MaxLines);
		if (errors.Count > 0)
		{
			return TypedResults.ValidationProblem(errors);
		}

		bool hasIds = request.Ids is { Count: > 0 };
		bool hasPattern = !string.IsNullOrWhiteSpace(request.Pattern);
		return TypedResults.Ok(await corpus.MultiGetAsync(new SidecarMultiGetRequest
		{
			Ids = hasIds ? request.Ids : null,
			Pattern = hasPattern ? request.Pattern : null,
			MaxBytes = request.MaxBytes,
			MaxLines = request.MaxLines,
			LineNumbers = request.LineNumbers,
			IncludeBody = request.IncludeBody,
		}, cancellationToken));
	}

	private static ProblemHttpResult NotFound()
	{
		return TypedResults.Problem(
			statusCode: StatusCodes.Status404NotFound,
			title: "Not found",
			detail: SidecarDisclosure.NotFoundMessage,
			extensions: new Dictionary<string, object?> { ["code"] = "not_found" });
	}
}
