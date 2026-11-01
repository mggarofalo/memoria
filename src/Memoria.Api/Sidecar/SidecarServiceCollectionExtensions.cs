using System.Net.Http.Headers;
using System.Reflection;
using Memoria.Api.Configuration;
using Memoria.Core.Sidecar;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Sidecar;

public static class SidecarServiceCollectionExtensions
{
	/// <summary>
	/// Registers <see cref="IMemoriaQmdClient"/> as a typed <see cref="HttpClient"/>
	/// bound to <see cref="SidecarOptions"/>. One long-lived connection pool,
	/// never a process per request: the sidecar keeps the models warm.
	/// </summary>
	public static IServiceCollection AddMemoriaQmdClient(this IServiceCollection services)
	{
		services.AddTransient<SidecarCorrelationHandler>();
		services
			.AddHttpClient<IMemoriaQmdClient, MemoriaQmdClient>((provider, http) =>
			{
				SidecarOptions options = provider.GetRequiredService<IOptions<MemoriaOptions>>().Value.Sidecar;
				http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
				http.Timeout = options.Timeout;
				http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
				http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Memoria.Api", ApiVersion));
				if (!string.IsNullOrWhiteSpace(options.Secret))
				{
					http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Secret);
				}
			})
			.AddHttpMessageHandler<SidecarCorrelationHandler>();

		return services;
	}

	/// <summary>Informational version of this build, reported by <c>/health</c> and the User-Agent.</summary>
	public static string ApiVersion { get; } =
		typeof(SidecarServiceCollectionExtensions).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
		?? typeof(SidecarServiceCollectionExtensions).Assembly.GetName().Version?.ToString()
		?? "0.0.0";
}
