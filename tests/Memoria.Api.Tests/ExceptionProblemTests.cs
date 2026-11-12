using System.Net;
using System.Text.Json;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Core.Redaction;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memoria.Api.Tests;

/// <summary>
/// The problem each failure becomes, word for word, and the operator's log line for a sidecar
/// failure. A client tells failures apart by status, title and code, so their text is the contract.
/// </summary>
public sealed class ExceptionProblemTests
{
	private readonly CapturedProblems _problems = new();

	private readonly ListLogger<MemoriaExceptionHandler> _log = new();

	private sealed record Scope(IReadOnlyCollection<string>? Collections, bool SeesBackendDetails) : ICallerScope;

	private sealed class CapturedProblems : IProblemDetailsService
	{
		public ProblemDetailsContext? Last { get; private set; }

		public ValueTask WriteAsync(ProblemDetailsContext context)
		{
			Last = context;
			return ValueTask.CompletedTask;
		}

		public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
		{
			Last = context;
			return ValueTask.FromResult(true);
		}
	}

	private async Task<(bool Handled, ProblemDetails? Problem, int Status)> HandleAsync(Exception exception)
	{
		DefaultHttpContext context = new()
		{
			RequestServices = new ServiceCollection().AddSingleton<ICallerScope>(new Scope(null, true)).BuildServiceProvider(),
		};

		bool handled = await new MemoriaExceptionHandler(_problems, _log).TryHandleAsync(context, exception, CancellationToken.None);
		return (handled, _problems.Last?.ProblemDetails, context.Response.StatusCode);
	}

	[Fact]
	public async Task AnExceptionItDoesNotKnow_IsLeftToTheFramework()
	{
		(bool handled, ProblemDetails? problem, _) = await HandleAsync(new InvalidOperationException("boom"));

		Assert.False(handled);
		Assert.Null(problem);
	}

	[Fact]
	public async Task ACollectionOutsideTheScope_Is403_NamingTheCollection()
	{
		(bool handled, ProblemDetails? problem, int status) = await HandleAsync(new CollectionForbiddenException("closed", ["open", "closed"]));

		Assert.True(handled);
		Assert.Equal(403, status);
		Assert.Equal("Collection outside caller scope", problem!.Title);
		Assert.Equal("Collection 'closed' is outside this caller's scope.", problem.Detail);
		Assert.Equal("collection_forbidden", problem.Extensions["code"]);
		Assert.Equal("closed", problem.Extensions["collection"]);
	}

	[Fact]
	public async Task AnIngestRefusal_KeepsItsStatusCodeAndMessage()
	{
		(_, ProblemDetails? problem, int status) = await HandleAsync(new IngestRefusedException(409, "stale_revision", "A newer revision is stored."));

		Assert.Equal(409, status);
		Assert.Equal("Ingest refused", problem!.Title);
		Assert.Equal("A newer revision is stored.", problem.Detail);
		Assert.Equal("stale_revision", problem.Extensions["code"]);
	}

	[Fact]
	public async Task ABodyOverTheLimit_Is413_PayloadTooLarge()
	{
		(bool handled, ProblemDetails? problem, int status) = await HandleAsync(new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

		Assert.True(handled);
		Assert.Equal(413, status);
		Assert.Equal("Ingest refused", problem!.Title);
		Assert.Equal("The request body is too large.", problem.Detail);
		Assert.Equal("payload_too_large", problem.Extensions["code"]);
	}

	[Fact]
	public async Task ABodyThatCouldNotBeRead_KeepsItsStatus_WithoutTheFrameworksWords()
	{
		(_, ProblemDetails? problem, int status) = await HandleAsync(new BadHttpRequestException("Unexpected end of request content.", StatusCodes.Status400BadRequest));

		Assert.Equal(400, status);
		Assert.Equal("Ingest refused", problem!.Title);
		Assert.Equal("The request body could not be read.", problem.Detail);
		Assert.Equal("bad_request_body", problem.Extensions["code"]);
	}

	[Fact]
	public async Task ARedactionFailure_Is422_NamingTheRuleFailureAndPart()
	{
		(_, ProblemDetails? problem, int status) = await HandleAsync(new RedactionException("aws-access-key", RedactionFailure.TimedOut, 3));

		Assert.Equal(422, status);
		Assert.Equal("Redaction could not finish", problem!.Title);
		Assert.Equal("The document was not stored, because it could not be confirmed free of credentials.", problem.Detail);
		Assert.Equal("redaction_failed", problem.Extensions["code"]);
		Assert.Equal("aws-access-key", problem.Extensions["rule"]);
		Assert.Equal("TimedOut", problem.Extensions["failure"]);
		Assert.Equal(3, problem.Extensions["part"]);
	}

	/// <summary>Status 0 stands for no HTTP answer at all.</summary>
	[Theory]
	[InlineData(0, SidecarException.Timeout, false, 504, "Search backend timed out")]
	[InlineData(0, SidecarException.Unreachable, false, 503, "Search backend unavailable")]
	[InlineData(400, "validation", false, 400, "Invalid request")]
	[InlineData(404, "not_found", false, 404, "Not found")]
	[InlineData(409, "conflict", false, 409, "Conflict")]
	[InlineData(413, "too_large", false, 413, "Payload too large")]
	[InlineData(401, "unauthorized", false, 502, "Search backend rejected Memoria's credentials")]
	[InlineData(403, "forbidden", false, 502, "Search backend rejected Memoria's credentials")]
	[InlineData(503, "busy", true, 503, "Search backend busy")]
	[InlineData(503, "warming", false, 503, "Search backend not ready")]
	[InlineData(500, "internal", false, 502, "Search backend error")]
	public async Task ASidecarFailure_HasTheStatusAndTitleForWhatWentWrong(int upstream, string code, bool retryAfter, int expectedStatus, string expectedTitle)
	{
		SidecarException exception = new(code, "sidecar words", upstream == 0 ? null : (HttpStatusCode)upstream)
		{
			RetryAfter = retryAfter ? TimeSpan.FromSeconds(2) : null,
		};

		(_, ProblemDetails? problem, int status) = await HandleAsync(exception);

		Assert.Equal(expectedStatus, status);
		Assert.Equal(expectedStatus, problem!.Status);
		Assert.Equal(expectedTitle, problem.Title);
	}

	[Fact]
	public async Task ASidecarFault_IsLoggedAsAnError_WithItsUpstreamStatus()
	{
		await HandleAsync(new SidecarException("internal", "sidecar words", HttpStatusCode.InternalServerError));

		MemoriaApiFactory.CapturedLog line = _log.Single(2000);
		Assert.Equal(LogLevel.Error, line.Level);
		Assert.Equal("internal", line.State["Code"]);
		Assert.Equal(500, line.State["UpstreamStatus"]);
		Assert.DoesNotContain(_log.Entries, static entry => entry.EventId.Id == 2001);
	}

	[Fact]
	public async Task AnUnreachableSidecar_IsLoggedAsAnError_WithNoUpstreamStatus()
	{
		await HandleAsync(new SidecarException(SidecarException.Unreachable, "refused"));

		MemoriaApiFactory.CapturedLog line = _log.Single(2000);
		Assert.Equal(LogLevel.Error, line.Level);
		Assert.Null(line.State["UpstreamStatus"]);
	}

	[Fact]
	public async Task ASidecarRejection_IsLoggedAtInformation_WithoutTheSidecarsMessage()
	{
		await HandleAsync(new SidecarException("validation", "secret/path.md is not valid", HttpStatusCode.BadRequest));

		MemoriaApiFactory.CapturedLog line = _log.Single(2001);
		Assert.Equal(LogLevel.Information, line.Level);
		Assert.Equal("validation", line.State["Code"]);
		Assert.Equal(400, line.State["UpstreamStatus"]);
		Assert.DoesNotContain("secret/path.md", line.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(_log.Entries, static entry => entry.EventId.Id == 2000);
	}
}

/// <summary>How much of a sidecar failure each kind of caller is shown.</summary>
public sealed class SidecarDisclosureTests
{
	private sealed record Scope(IReadOnlyCollection<string>? Collections, bool SeesBackendDetails) : ICallerScope;

	private static readonly Scope Admin = new(null, true);

	private static readonly Scope Reader = new(["open"], false);

	private static JsonElement Details()
	{
		using JsonDocument document = JsonDocument.Parse("""{"field":"query"}""");
		return document.RootElement.Clone();
	}

	[Fact]
	public void NoException_OrNoScope_IsRefused()
	{
		SidecarException exception = new("validation", "bad", HttpStatusCode.BadRequest);

		Assert.Throws<ArgumentNullException>(() => SidecarDisclosure.For(null!, Admin));
		Assert.Throws<ArgumentNullException>(() => SidecarDisclosure.For(exception, null!));
	}

	[Fact]
	public void ABackendFault_ShownToACallerWithoutBackendDetails_SaysOnlyToQuoteTheTraceId()
	{
		SidecarException exception = new("internal", "ENOENT /srv/index.sqlite on 10.0.0.5", HttpStatusCode.InternalServerError, Details());

		SidecarFailureView view = SidecarDisclosure.For(exception, new Scope(null, false));

		Assert.Equal(new SidecarFailureView("internal", "The search backend could not complete this request. Quote the trace id to the operator.", null, null), view);
	}

	[Fact]
	public void AMiss_ShownToACollectionScopedCaller_IsTheGenericNotFound()
	{
		SidecarException exception = new("document_missing", "No document secret/plans.md; similar: secret/plan.md", HttpStatusCode.NotFound, Details());

		SidecarFailureView view = SidecarDisclosure.For(exception, Reader);

		Assert.Equal(new SidecarFailureView("not_found", SidecarDisclosure.NotFoundMessage, null, null), view);
	}

	[Fact]
	public void AMiss_ShownToAnAdmin_KeepsTheSidecarsWords_ButNoDetails()
	{
		SidecarException exception = new("document_missing", "No document notes/a.md", HttpStatusCode.NotFound, Details());

		SidecarFailureView view = SidecarDisclosure.For(exception, Admin);

		Assert.Equal(new SidecarFailureView("document_missing", "No document notes/a.md", 404, null), view);
	}

	[Fact]
	public void AValidationFailure_KeepsItsDetails_OnlyForAnUnrestrictedCaller()
	{
		SidecarException exception = new("validation", "query is required", HttpStatusCode.BadRequest, Details());

		SidecarFailureView unrestricted = SidecarDisclosure.For(exception, Admin);
		SidecarFailureView restricted = SidecarDisclosure.For(exception, new Scope(["open"], true));

		Assert.Equal("query", unrestricted.Details?.GetProperty("field").GetString());
		Assert.Equal(400, unrestricted.UpstreamStatus);
		Assert.Null(restricted.Details);
		Assert.Equal(("validation", "query is required", (int?)400), (restricted.Code, restricted.Message, restricted.UpstreamStatus));
	}
}
