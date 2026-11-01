using System.Net;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Configuration;

/// <summary>Fails startup on a configuration that could not work, with a message naming the key to fix.</summary>
public sealed class MemoriaOptionsValidator : IValidateOptions<MemoriaOptions>
{
	public ValidateOptionsResult Validate(string? name, MemoriaOptions options)
	{
		List<string> failures = [];

		if (!Uri.TryCreate(options.Sidecar.BaseUrl, UriKind.Absolute, out Uri? baseUrl)
			|| (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
		{
			failures.Add($"Memoria:Sidecar:BaseUrl must be an absolute http(s) URL; got '{options.Sidecar.BaseUrl}'.");
		}
		else if (string.IsNullOrWhiteSpace(options.Sidecar.Secret) && !IsLoopback(baseUrl))
		{
			failures.Add("Memoria:Sidecar:Secret is required when the sidecar is not on loopback; the sidecar itself refuses unauthenticated non-loopback traffic.");
		}

		if (options.Sidecar.Timeout <= TimeSpan.Zero)
		{
			failures.Add("Memoria:Sidecar:Timeout must be positive.");
		}

		if (options.Sidecar.ReadinessTimeout <= TimeSpan.Zero)
		{
			failures.Add("Memoria:Sidecar:ReadinessTimeout must be positive.");
		}

		if (options.Search.MaxLimit is < 1 or > 100)
		{
			failures.Add("Memoria:Search:MaxLimit must be between 1 and 100 (the sidecar's own ceiling).");
		}

		if (options.Search.DefaultLimit < 1 || options.Search.DefaultLimit > options.Search.MaxLimit)
		{
			failures.Add("Memoria:Search:DefaultLimit must be between 1 and Memoria:Search:MaxLimit.");
		}

		return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
	}

	private static bool IsLoopback(Uri url)
	{
		return url.IsLoopback || (IPAddress.TryParse(url.Host, out IPAddress? ip) && IPAddress.IsLoopback(ip));
	}
}
