using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace Memoria.Api.Configuration;

/// <summary>
/// Adds <c>appsettings.local.json</c>, the gitignored file for machine-local
/// values such as the sidecar secret. It is placed directly after the other
/// <c>appsettings*.json</c> sources, so it overrides the committed files but
/// environment variables and command-line switches still win, exactly as the
/// standard ASP.NET Core precedence promises. Appending it at the end would
/// let a stale local file silently defeat a rotated <c>Memoria__Sidecar__Secret</c>.
/// </summary>
public static class LocalSettings
{
	public const string FileName = "appsettings.local.json";

	public static void Insert(IConfigurationBuilder configuration, IFileProvider fileProvider)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		ArgumentNullException.ThrowIfNull(fileProvider);

		int insertAt = 0;
		for (int i = 0; i < configuration.Sources.Count; i++)
		{
			if (configuration.Sources[i] is JsonConfigurationSource)
			{
				insertAt = i + 1;
			}
		}

		configuration.Sources.Insert(insertAt, new JsonConfigurationSource
		{
			Path = FileName,
			Optional = true,
			ReloadOnChange = true,
			FileProvider = fileProvider,
		});
	}
}
