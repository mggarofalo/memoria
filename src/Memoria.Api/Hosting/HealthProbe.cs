namespace Memoria.Api.Hosting;

/// <summary>
/// <c>dotnet Memoria.Api.dll --health-check</c>: the container HEALTHCHECK.
/// The aspnet base image ships no curl or wget, so the app probes itself.
/// Exit 0 when <c>/health</c> answers 200 within the timeout, 1 otherwise.
/// </summary>
public static class HealthProbe
{
	public const string Switch = "--health-check";

	public static bool IsRequested(string[] args)
	{
		return args.Length > 0 && string.Equals(args[0], Switch, StringComparison.Ordinal);
	}

	/// <summary>
	/// The URL to probe: the first configured listen address, rewritten to a
	/// loopback literal. Not <c>localhost</c>: in the container it resolves to
	/// <c>::1</c> first while Kestrel on <c>0.0.0.0</c> listens on IPv4 only.
	/// </summary>
	public static Uri ResolveUrl(string? configuredUrls)
	{
		string first = (configuredUrls ?? "http://0.0.0.0:8080").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
		UriBuilder builder = new(first.Replace("+", "127.0.0.1", StringComparison.Ordinal).Replace("*", "127.0.0.1", StringComparison.Ordinal));
		if (builder.Host is "0.0.0.0" or "localhost")
		{
			builder.Host = "127.0.0.1";
		}
		else if (builder.Host is "[::]" or "::")
		{
			builder.Host = "[::1]";
		}

		builder.Path = "/health";
		return builder.Uri;
	}

	public static async Task<int> RunAsync(HttpClient http, Uri url, CancellationToken cancellationToken = default)
	{
		try
		{
			using HttpResponseMessage response = await http.GetAsync(url, cancellationToken);
			if (response.IsSuccessStatusCode)
			{
				return 0;
			}

			Console.Error.WriteLine($"{url} answered {(int)response.StatusCode}");
			return 1;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
		{
			Console.Error.WriteLine($"{url} unreachable: {ex.Message}");
			return 1;
		}
	}

	public static async Task<int> RunAsync(string? configuredUrls)
	{
		using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(4) };
		return await RunAsync(http, ResolveUrl(configuredUrls));
	}
}
