namespace Memoria.Api.Auth;

/// <summary>
/// <c>Memoria:Auth:Mode = Disabled</c> makes every caller an all-collections reader, so the host refuses to start with it
/// unless every address it is configured to listen on is loopback, or <c>Memoria:Auth:AllowDisabledOnNetwork</c> says the
/// operator means it. The addresses are read from configuration before anything binds: <c>Kestrel:Endpoints</c>, then
/// <c>urls</c> (<c>ASPNETCORE_URLS</c>, <c>--urls</c>, or the host's own default), then <c>http_ports</c> and
/// <c>https_ports</c>. All of them are checked, even where Kestrel would ignore some, so the guard errs toward refusing.
/// </summary>
public static class DisabledAuthGuard
{
	/// <summary>Every address configuration names for the host to listen on.</summary>
	public static IReadOnlyList<string> ListenAddresses(IConfiguration configuration)
	{
		List<string> addresses = [];
		foreach (IConfigurationSection endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
		{
			addresses.Add(endpoint["Url"] ?? string.Empty);
		}

		string? urls = configuration[WebHostDefaults.ServerUrlsKey];
		if (!string.IsNullOrWhiteSpace(urls))
		{
			addresses.AddRange(urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
			return addresses;
		}

		addresses.AddRange(Ports(configuration["http_ports"], "http"));
		addresses.AddRange(Ports(configuration["https_ports"], "https"));
		return addresses;
	}

	/// <summary>The addresses that are not loopback. Anything that does not parse as a URL with a loopback host counts: <c>*</c>, <c>+</c>, <c>0.0.0.0</c>, a name, a socket path.</summary>
	public static string[] OnNetwork(IEnumerable<string> addresses)
	{
		return [.. addresses.Where(static address => !IsLoopback(address))];
	}

	/// <summary>
	/// <c>localhost</c>, which Kestrel binds to loopback only, or a loopback IP. The address is parsed the way Kestrel
	/// parses it, not with <see cref="Uri"/>, which rewrites the name <c>loopback</c> to <c>localhost</c>: Kestrel treats
	/// that name like any other and binds on every interface.
	/// </summary>
	public static bool IsLoopback(string address)
	{
		BindingAddress parsed;
		try
		{
			parsed = BindingAddress.Parse(address);
		}
		catch (Exception ex) when (ex is FormatException or ArgumentException)
		{
			return false;
		}

		if (parsed.IsUnixPipe || parsed.IsNamedPipe || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
		{
			return false;
		}

		return string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase)
			|| (System.Net.IPAddress.TryParse(parsed.Host.Trim('[', ']'), out System.Net.IPAddress? ip) && System.Net.IPAddress.IsLoopback(ip));
	}

	private static IEnumerable<string> Ports(string? ports, string scheme)
	{
		return (ports ?? string.Empty)
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(port => $"{scheme}://*:{port}");
	}
}
