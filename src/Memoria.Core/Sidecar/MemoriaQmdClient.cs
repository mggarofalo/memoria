using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Memoria.Core.Sidecar;

/// <summary>
/// HTTP implementation of <see cref="IMemoriaQmdClient"/>. The caller supplies
/// an <see cref="HttpClient"/> whose <c>BaseAddress</c>, timeout and bearer
/// header are already configured, so this class holds no configuration of its
/// own and tests can drive it with a fake <see cref="HttpMessageHandler"/>.
/// </summary>
public sealed class MemoriaQmdClient(HttpClient http) : IMemoriaQmdClient
{
	private static readonly MediaTypeHeaderValue Json = new("application/json");

	private const int DefaultMultiGetBudget = 16 * 1024 * 1024;

	private const long MultiGetMetadataAllowance = 16 * 1024 * 1024;

	public Task<SidecarHealth> GetHealthAsync(CancellationToken cancellationToken = default)
	{
		return GetAsync("health", SidecarJsonContext.Default.SidecarHealth, cancellationToken);
	}

	public async Task<SidecarReadinessResult> GetReadinessAsync(bool requireModels, CancellationToken cancellationToken = default)
	{
		string path = requireModels ? "ready?requireModels=true" : "ready";
		using HttpResponseMessage response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken).ConfigureAwait(false);

		// /ready is the one route that returns its full body with 503 as well as
		// 200, so a "not ready" answer is data, not an error.
		if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable && IsJson(response))
		{
			SidecarReadiness details = await ReadAsync(response, SidecarJsonContext.Default.SidecarReadiness, cancellationToken).ConfigureAwait(false);
			return new SidecarReadinessResult(response.StatusCode == HttpStatusCode.OK, details);
		}

		throw await ToExceptionAsync(response, cancellationToken).ConfigureAwait(false);
	}

	public Task<SidecarStatus> GetStatusAsync(CancellationToken cancellationToken = default)
	{
		return GetAsync("status", SidecarJsonContext.Default.SidecarStatus, cancellationToken);
	}

	public Task<SidecarSearchResponse> SearchAsync(SidecarSearchRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		return PostAsync("search", request, SidecarJsonContext.Default.SidecarSearchRequest, SidecarJsonContext.Default.SidecarSearchResponse, cancellationToken);
	}

	public Task<SidecarDocument> GetDocumentAsync(string reference, SidecarDocumentQuery? query = null, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reference);

		// Dot segments survive percent-encoding and HttpClient would normalize them,
		// turning `x/../../status` into a call to a different sidecar route.
		if (reference.Split('/').Any(static segment => segment is "." or ".."))
		{
			throw new SidecarException("not_found", $"Document not found: {reference}", HttpStatusCode.NotFound);
		}

		return GetAsync(DocumentPath(reference, query), SidecarJsonContext.Default.SidecarDocument, cancellationToken);
	}

	public async Task<SidecarMultiGetResponse> MultiGetAsync(SidecarMultiGetRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		using HttpRequestMessage message = new(HttpMethod.Post, "./docs:multiGet")
		{
			Content = JsonContent.Create(request, SidecarJsonContext.Default.SidecarMultiGetRequest, Json),
		};
		using HttpResponseMessage response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw await ToExceptionAsync(response, cancellationToken).ConfigureAwait(false);
		}

		// The body is deserialized as it arrives, so a sidecar that ignores the budget
		// is cut off while reading instead of being buffered whole.
		await using Stream body = new BoundedReadStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), MaxMultiGetWireBytes(request));
		try
		{
			return await JsonSerializer.DeserializeAsync(body, SidecarJsonContext.Default.SidecarMultiGetResponse, cancellationToken).ConfigureAwait(false)
				?? throw new SidecarException(SidecarException.Malformed, "The sidecar returned an empty body.", response.StatusCode);
		}
		catch (JsonException ex)
		{
			throw new SidecarException(SidecarException.Malformed, $"The sidecar returned a body that is not valid JSON: {ex.Message}", response.StatusCode, innerException: ex);
		}
	}

	/// <summary>The budget at JSON's worst escaping (six bytes per character), plus room for metadata.</summary>
	internal static long MaxMultiGetWireBytes(SidecarMultiGetRequest request)
	{
		return ((long)(request.MaxTotalBytes ?? DefaultMultiGetBudget) * 6) + MultiGetMetadataAllowance;
	}

	public Task<SidecarCollectionsResponse> ListCollectionsAsync(CancellationToken cancellationToken = default)
	{
		return GetAsync("collections", SidecarJsonContext.Default.SidecarCollectionsResponse, cancellationToken);
	}

	public Task<SidecarIngestResponse> IngestAsync(SidecarIngestRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		return PostAsync("documents", request, SidecarJsonContext.Default.SidecarIngestRequest, SidecarJsonContext.Default.SidecarIngestResponse, cancellationToken);
	}

	public async Task<SidecarJob> ReindexAsync(SidecarReindexRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		SidecarJobResponse response = await PostAsync("reindex", request, SidecarJsonContext.Default.SidecarReindexRequest, SidecarJsonContext.Default.SidecarJobResponse, cancellationToken).ConfigureAwait(false);
		return response.Job;
	}

	public async Task<IReadOnlyList<SidecarJob>> ListJobsAsync(CancellationToken cancellationToken = default)
	{
		SidecarJobsResponse response = await GetAsync("reindex/jobs", SidecarJsonContext.Default.SidecarJobsResponse, cancellationToken).ConfigureAwait(false);
		return response.Jobs;
	}

	public async Task<SidecarJob> GetJobAsync(string id, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		SidecarJobResponse response = await GetAsync("reindex/jobs/" + Uri.EscapeDataString(id), SidecarJsonContext.Default.SidecarJobResponse, cancellationToken).ConfigureAwait(false);
		return response.Job;
	}

	/// <summary>
	/// Builds the relative URL for <c>GET /docs/{reference}</c>. Slashes are kept
	/// so <c>collection/path.md</c> and <c>qmd://</c> URIs route through the
	/// sidecar's wildcard; everything else (notably <c>#</c> on a docid) is
	/// percent-encoded. Line ranges travel as query parameters so a reference
	/// containing <c>:digits</c> is never misread.
	/// </summary>
	internal static string DocumentPath(string reference, SidecarDocumentQuery? query)
	{
		string path = "docs/" + string.Join('/', reference.Split('/').Select(Uri.EscapeDataString));
		string[] parameters = query is null ? [] : QueryParameters(query);
		return parameters.Length == 0 ? path : $"{path}?{string.Join('&', parameters)}";
	}

	private static string[] QueryParameters(SidecarDocumentQuery query)
	{
		return
		[
			.. Parameter("fromLine", query.FromLine),
			.. Parameter("maxLines", query.MaxLines),
			.. Parameter("lineNumbers", query.LineNumbers),
			.. Parameter("body", query.Body),
			.. (query.Collections ?? []).Select(static collection => $"collections={Uri.EscapeDataString(collection)}"),
			.. Parameter("similarFiles", query.SimilarFiles),
		];
	}

	private static IEnumerable<string> Parameter(string name, int? value)
	{
		return value is int number ? [$"{name}={number.ToString(System.Globalization.CultureInfo.InvariantCulture)}"] : [];
	}

	private static IEnumerable<string> Parameter(string name, bool? value)
	{
		return value is bool flag ? [$"{name}={(flag ? "true" : "false")}"] : [];
	}

	private async Task<TResponse> GetAsync<TResponse>(string path, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken).ConfigureAwait(false);
		return await ReadSuccessAsync(response, responseType, cancellationToken).ConfigureAwait(false);
	}

	private async Task<TResponse> PostAsync<TRequest, TResponse>(
		string path,
		TRequest body,
		JsonTypeInfo<TRequest> requestType,
		JsonTypeInfo<TResponse> responseType,
		CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = new(HttpMethod.Post, path)
		{
			Content = JsonContent.Create(body, requestType, Json),
		};
		using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
		return await ReadSuccessAsync(response, responseType, cancellationToken).ConfigureAwait(false);
	}

	private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		try
		{
			return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		}
		catch (HttpRequestException ex)
		{
			throw new SidecarException(SidecarException.Unreachable, $"The memoria-qmd sidecar at {http.BaseAddress} could not be reached: {ex.Message}", innerException: ex);
		}
		catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
		{
			throw new SidecarException(SidecarException.Timeout, $"The memoria-qmd sidecar at {http.BaseAddress} did not answer within {http.Timeout}.", innerException: ex);
		}
	}

	private static async Task<TResponse> ReadSuccessAsync<TResponse>(HttpResponseMessage response, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
	{
		if (!response.IsSuccessStatusCode)
		{
			throw await ToExceptionAsync(response, cancellationToken).ConfigureAwait(false);
		}

		return await ReadAsync(response, responseType, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<TResponse> ReadAsync<TResponse>(HttpResponseMessage response, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
	{
		try
		{
			TResponse? value = await response.Content.ReadFromJsonAsync(responseType, cancellationToken).ConfigureAwait(false);
			return value ?? throw new SidecarException(SidecarException.Malformed, "The sidecar returned an empty body.", response.StatusCode);
		}
		catch (JsonException ex)
		{
			throw new SidecarException(SidecarException.Malformed, $"The sidecar returned a body that is not valid JSON for {typeof(TResponse).Name}: {ex.Message}", response.StatusCode, innerException: ex);
		}
	}

	private static async Task<SidecarException> ToExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (IsJson(response))
		{
			try
			{
				SidecarError? error = await response.Content.ReadFromJsonAsync(SidecarJsonContext.Default.SidecarError, cancellationToken).ConfigureAwait(false);
				if (error is { Error.Length: > 0 })
				{
					return new SidecarException(error.Error, error.Message, response.StatusCode, error.Details);
				}
			}
			catch (JsonException)
			{
				// Fall through to the generic message below.
			}
		}

		return new SidecarException(SidecarException.Malformed, $"The sidecar answered {(int)response.StatusCode} {response.ReasonPhrase} without an error envelope.", response.StatusCode);
	}

	private static bool IsJson(HttpResponseMessage response)
	{
		return string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);
	}
}
