using System.ComponentModel;
using Memoria.Api.Http;
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
				+ "Bodies are capped at `maxBytes` each (default 64 KiB); documents over the cap are listed under `skipped`.")
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
		IMemoriaQmdClient sidecar,
		ICallerScope scope,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			return NotFound(id);
		}

		SidecarDocument document = await sidecar.GetDocumentAsync(
			id,
			new SidecarDocumentQuery(fromLine, maxLines, lineNumbers, body),
			cancellationToken);

		// A document outside the caller's scope is indistinguishable from a
		// missing one; do not confirm that it exists.
		return scope.Allows(document.Collection) ? TypedResults.Ok(document.ToContract()) : NotFound(id);
	}

	private static async Task<Results<Ok<MultiGetResult>, ValidationProblem>> MultiGetAsync(
		MultiGetRequest request,
		IMemoriaQmdClient sidecar,
		ICallerScope scope,
		CancellationToken cancellationToken)
	{
		Dictionary<string, string[]> errors = [];
		bool hasIds = request.Ids is { Count: > 0 };
		bool hasPattern = !string.IsNullOrWhiteSpace(request.Pattern);

		if (!hasIds && !hasPattern)
		{
			errors["ids"] = ["Provide `ids`, `pattern`, or both."];
		}

		if (request.Ids is { Count: > 200 })
		{
			errors["ids"] = ["At most 200 ids per request."];
		}

		if (request.MaxBytes is < 1)
		{
			errors["maxBytes"] = ["maxBytes must be at least 1."];
		}

		if (request.MaxLines is < 1)
		{
			errors["maxLines"] = ["maxLines must be at least 1."];
		}

		if (errors.Count > 0)
		{
			return TypedResults.ValidationProblem(errors);
		}

		SidecarMultiGetResponse response = await sidecar.MultiGetAsync(new SidecarMultiGetRequest
		{
			Ids = hasIds ? request.Ids : null,
			Pattern = hasPattern ? request.Pattern : null,
			MaxBytes = request.MaxBytes,
			MaxLines = request.MaxLines,
			LineNumbers = request.LineNumbers,
			IncludeBody = request.IncludeBody,
		}, cancellationToken);

		return TypedResults.Ok(new MultiGetResult(
			response.Docs.Where(d => scope.Allows(d.Collection)).Select(ContractMapping.ToContract).ToArray(),
			response.Skipped.Select(s => new SkippedDocument(s.File, s.Reason)).ToArray(),
			response.Errors));
	}

	private static ProblemHttpResult NotFound(string id)
	{
		return TypedResults.Problem(
			statusCode: StatusCodes.Status404NotFound,
			title: "Not found",
			detail: $"No document matches '{id}'.",
			extensions: new Dictionary<string, object?> { ["code"] = "not_found" });
	}
}
