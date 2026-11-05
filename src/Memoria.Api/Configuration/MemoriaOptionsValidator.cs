using System.Net;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Configuration;

/// <summary>Fails startup on a configuration that could not work, with a message naming the key to fix.</summary>
public sealed class MemoriaOptionsValidator : IValidateOptions<MemoriaOptions>
{
	private const int MaxMultiGetBytesCeiling = 256 * 1024 * 1024;

	public ValidateOptionsResult Validate(string? name, MemoriaOptions options)
	{
		List<string> failures = [];
		ValidateSidecar(options.Sidecar, failures);
		ValidateSearch(options.Search, failures);
		ValidateMcp(options.Mcp, failures);
		ValidateIngest(options.Ingest, failures);
		ValidateDigestKey(options, failures);
		ValidateAuthMode(options.Auth, failures);
		ValidateTrustedProxies(options.Auth.TrustedProxies, failures);
		ValidateFailedAuth(options.Auth.FailedAuth, failures);
		ValidateRateLimit(options.Auth.RateLimit, failures);
		Require(options.Auth.Audit.MaxRowsPerKey >= 1, "Memoria:Auth:Audit:MaxRowsPerKey must be at least 1.", failures);
		return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
	}

	private static void ValidateSidecar(SidecarOptions sidecar, List<string> failures)
	{
		if (!IsHttpUrl(sidecar.BaseUrl, out Uri? baseUrl))
		{
			failures.Add($"Memoria:Sidecar:BaseUrl must be an absolute http(s) URL; got '{sidecar.BaseUrl}'.");
		}
		else if (string.IsNullOrWhiteSpace(sidecar.Secret) && !IsLoopback(baseUrl))
		{
			failures.Add("Memoria:Sidecar:Secret is required when the sidecar is not on loopback; the sidecar itself refuses unauthenticated non-loopback traffic.");
		}

		Require(sidecar.Timeout > TimeSpan.Zero, "Memoria:Sidecar:Timeout must be positive.", failures);
		Require(sidecar.ReadinessTimeout > TimeSpan.Zero, "Memoria:Sidecar:ReadinessTimeout must be positive.", failures);
	}

	private static void ValidateSearch(SearchOptions search, List<string> failures)
	{
		Require(search.MaxLimit is >= 1 and <= 100, "Memoria:Search:MaxLimit must be between 1 and 100 (the sidecar's own ceiling).", failures);
		Require(search.DefaultLimit >= 1 && search.DefaultLimit <= search.MaxLimit, "Memoria:Search:DefaultLimit must be between 1 and Memoria:Search:MaxLimit.", failures);
		Require(search.MaxMultiGetBytes is >= 1 and <= MaxMultiGetBytesCeiling, $"Memoria:Search:MaxMultiGetBytes must be between 1 and {MaxMultiGetBytesCeiling} (the sidecar's own ceiling).", failures);
	}

	private static void ValidateMcp(McpOptions mcp, List<string> failures)
	{
		bool valid = !string.IsNullOrWhiteSpace(mcp.Path) && mcp.Path.StartsWith('/') && !mcp.Path.StartsWith("/v1", StringComparison.OrdinalIgnoreCase);
		Require(!mcp.Enabled || valid, "Memoria:Mcp:Path must start with '/' and must not sit under /v1.", failures);
	}

	private const int MinDocumentBytes = 64 * 1024;

	private const int MinDigestKeyChars = 32;

	/// <summary>Only ingest uses it, and ingest needs API keys.</summary>
	private static void ValidateDigestKey(MemoriaOptions options, List<string> failures)
	{
		if (!options.Auth.IsDisabled && (options.Ingest.DigestKey?.Length ?? 0) < MinDigestKeyChars)
		{
			failures.Add($"Memoria:Ingest:DigestKey is required when API keys are on, at least {MinDigestKeyChars} characters. Keep it outside the database, as with Memoria:Sidecar:Secret: it stops the database confirming a guessed secret.");
		}
	}

	private static void ValidateIngest(IngestOptions ingest, List<string> failures)
	{
		Require(ingest.MaxDocumentBytes >= MinDocumentBytes, $"Memoria:Ingest:MaxDocumentBytes must be at least {MinDocumentBytes}, as frontmatter alone can take 8 KiB.", failures);
		Require(ingest.MaxSessionBytes >= 1024, "Memoria:Ingest:MaxSessionBytes must be at least 1024.", failures);
		Require(ingest.MaxConcurrentLargeUploads >= 1, "Memoria:Ingest:MaxConcurrentLargeUploads must be at least 1.", failures);
		Require(ingest.MaxConcurrentLargeDocuments >= 1, "Memoria:Ingest:MaxConcurrentLargeDocuments must be at least 1.", failures);
		Require(ingest.MaxConcurrentRenders >= 1, "Memoria:Ingest:MaxConcurrentRenders must be at least 1.", failures);
		Require(ingest.MinUploadBytesPerSecond >= 1, "Memoria:Ingest:MinUploadBytesPerSecond must be at least 1.", failures);
		Require(ingest.SessionUploadDeadline > TimeSpan.Zero, "Memoria:Ingest:SessionUploadDeadline must be positive.", failures);
		Require(ingest.DocumentUploadDeadline > TimeSpan.Zero, "Memoria:Ingest:DocumentUploadDeadline must be positive.", failures);
		Require(ingest.DeleteWriteWait > TimeSpan.Zero, "Memoria:Ingest:DeleteWriteWait must be positive.", failures);
		Require(ingest.MinReindexInterval >= TimeSpan.Zero, "Memoria:Ingest:MinReindexInterval may not be negative.", failures);
	}

	private static void ValidateAuthMode(AuthOptions auth, List<string> failures)
	{
		if (auth.Mode is not (AuthModes.ApiKey or AuthModes.Disabled))
		{
			failures.Add("Memoria:Auth:Mode must be exactly ApiKey or Disabled, case-sensitive; numbers, lists, surrounding whitespace and empty values are refused.");
			return;
		}

		Require(auth.IsDisabled || !string.IsNullOrWhiteSpace(auth.DatabasePath), "Memoria:Auth:DatabasePath is required when Memoria:Auth:Mode is ApiKey (the default). For anonymous development on a trusted LAN, set Memoria:Auth:Mode to Disabled explicitly.", failures);
	}

	private static void ValidateTrustedProxies(string[]? entries, List<string> failures)
	{
		foreach (string entry in (entries ?? []).Where(static e => !string.IsNullOrWhiteSpace(e)))
		{
			Require(Auth.TrustedProxies.TryParse(entry.Trim(), out _, out _), $"Memoria:Auth:TrustedProxies has an invalid entry '{entry}'. Use an IP address or a CIDR range; a /0 range is refused because it would believe every caller's X-Forwarded-For.", failures);
		}
	}

	private static void ValidateFailedAuth(FailedAuthOptions failedAuth, List<string> failures)
	{
		Require(failedAuth.PermitLimit >= 1, "Memoria:Auth:FailedAuth:PermitLimit must be at least 1.", failures);
		Require(failedAuth.Window > TimeSpan.Zero, "Memoria:Auth:FailedAuth:Window must be positive.", failures);
		Require(failedAuth.MaxTrackedAddresses >= 2, "Memoria:Auth:FailedAuth:MaxTrackedAddresses must be at least 2.", failures);
	}

	private static void ValidateRateLimit(RateLimitOptions rateLimit, List<string> failures)
	{
		Require(rateLimit.PermitLimit >= 1, "Memoria:Auth:RateLimit:PermitLimit must be at least 1.", failures);
		Require(rateLimit.Window > TimeSpan.Zero, "Memoria:Auth:RateLimit:Window must be positive.", failures);
	}

	private static void Require(bool condition, string failure, List<string> failures)
	{
		if (!condition)
		{
			failures.Add(failure);
		}
	}

	private static bool IsHttpUrl(string value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? url)
	{
		return Uri.TryCreate(value, UriKind.Absolute, out url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);
	}

	private static bool IsLoopback(Uri url)
	{
		return url.IsLoopback || (IPAddress.TryParse(url.Host, out IPAddress? ip) && IPAddress.IsLoopback(ip));
	}
}
