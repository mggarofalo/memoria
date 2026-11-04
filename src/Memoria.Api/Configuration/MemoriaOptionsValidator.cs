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

		if (options.Mcp.Enabled && (string.IsNullOrWhiteSpace(options.Mcp.Path) || !options.Mcp.Path.StartsWith('/') || options.Mcp.Path.StartsWith("/v1", StringComparison.OrdinalIgnoreCase)))
		{
			failures.Add("Memoria:Mcp:Path must start with '/' and must not sit under /v1.");
		}

		if (options.Auth.Mode is not (AuthModes.ApiKey or AuthModes.Disabled))
		{
			failures.Add("Memoria:Auth:Mode must be exactly ApiKey or Disabled, case-sensitive; numbers, lists, surrounding whitespace and empty values are refused.");
		}
		else if (!options.Auth.IsDisabled && string.IsNullOrWhiteSpace(options.Auth.DatabasePath))
		{
			failures.Add("Memoria:Auth:DatabasePath is required when Memoria:Auth:Mode is ApiKey (the default). For anonymous development on a trusted LAN, set Memoria:Auth:Mode to Disabled explicitly.");
		}

		foreach (string entry in (options.Auth.TrustedProxies ?? []).Where(static e => !string.IsNullOrWhiteSpace(e)))
		{
			if (!Auth.TrustedProxies.TryParse(entry.Trim(), out _, out _))
			{
				failures.Add($"Memoria:Auth:TrustedProxies has an invalid entry '{entry}'. Use an IP address or a CIDR range; a /0 range is refused because it would believe every caller's X-Forwarded-For.");
			}
		}

		if (options.Auth.FailedAuth.PermitLimit < 1)
		{
			failures.Add("Memoria:Auth:FailedAuth:PermitLimit must be at least 1.");
		}

		if (options.Auth.FailedAuth.Window <= TimeSpan.Zero)
		{
			failures.Add("Memoria:Auth:FailedAuth:Window must be positive.");
		}

		if (options.Auth.FailedAuth.MaxTrackedAddresses < 2)
		{
			failures.Add("Memoria:Auth:FailedAuth:MaxTrackedAddresses must be at least 2.");
		}

		if (options.Auth.Audit.MaxRowsPerKey < 1)
		{
			failures.Add("Memoria:Auth:Audit:MaxRowsPerKey must be at least 1.");
		}

		if (options.Auth.RateLimit.PermitLimit < 1)
		{
			failures.Add("Memoria:Auth:RateLimit:PermitLimit must be at least 1.");
		}

		if (options.Auth.RateLimit.Window <= TimeSpan.Zero)
		{
			failures.Add("Memoria:Auth:RateLimit:Window must be positive.");
		}

		return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
	}

	private static bool IsLoopback(Uri url)
	{
		return url.IsLoopback || (IPAddress.TryParse(url.Host, out IPAddress? ip) && IPAddress.IsLoopback(ip));
	}
}
