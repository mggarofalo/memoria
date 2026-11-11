using System.Security.Claims;
using Memoria.Api.Auth;
using Memoria.Api.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memoria.Api.Tests;

/// <summary>
/// Disabled mode once the host is up: a listener the configuration did not name, or a configuration reload that moves the
/// host off loopback, stops it. And the two rules that keep a key inside what its issuer holds, on their own.
/// </summary>
public sealed class DisabledModeWatchTests
{
	/// <summary>The host stops itself, and disposes its services as it does, so the token is taken before.</summary>
	private static bool Stops(CancellationToken stopping)
	{
		for (DateTime deadline = DateTime.UtcNow.AddSeconds(10); !stopping.IsCancellationRequested && DateTime.UtcNow < deadline;)
		{
			Thread.Sleep(20);
		}

		return stopping.IsCancellationRequested;
	}

	[Fact]
	public void Refusing_to_start_says_where_it_would_have_listened()
	{
		using MemoriaApiFactory baseFactory = new();
		using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(static builder => builder.UseSetting("urls", "http://0.0.0.0:8080"));

		Exception failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

		Assert.Contains("Memoria:Auth:Mode is Disabled and the host would listen on http://0.0.0.0:8080, which is not loopback.", failure.ToString(), StringComparison.Ordinal);
		MemoriaApiFactory.CapturedLog refused = Assert.Single(baseFactory.Logs, static l => l.EventId.Id == 3203);
		Assert.Equal("Memoria.Api.Auth", refused.Category);
	}

	/// <summary>Plays a listener the configuration never named: the server reports an address off loopback once it is up.</summary>
	private sealed class ReportsNetworkAddress : IStartupFilter
	{
		public CancellationToken Stopping { get; private set; }

		public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
		{
			return app =>
			{
				Stopping = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
				IServer server = app.ApplicationServices.GetRequiredService<IServer>();
				IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>() ?? new ServerAddressesFeature();
				addresses.Addresses.Add("http://0.0.0.0:8080");
				server.Features.Set(addresses);
				next(app);
			};
		}
	}

	[Fact]
	public void A_listener_off_loopback_that_configuration_never_named_stops_the_host_once_it_is_up()
	{
		ReportsNetworkAddress listener = new();
		using MemoriaApiFactory baseFactory = new();
		using WebApplicationFactory<Program> host = baseFactory.WithWebHostBuilder(builder =>
			builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(listener)));
		// The host can stop, and dispose its services, before the factory has finished starting it.
		try
		{
			_ = host.CreateClient();
		}
		catch (ObjectDisposedException)
		{
		}

		Assert.True(Stops(listener.Stopping));
		MemoriaApiFactory.CapturedLog refused = Assert.Single(baseFactory.Logs, static l => l.EventId.Id == 3203);
		Assert.Equal(LogLevel.Critical, refused.Level);
		Assert.Contains("0.0.0.0", refused.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void A_reload_that_moves_the_host_off_loopback_stops_it()
	{
		using MemoriaApiFactory baseFactory = new();
		using WebApplicationFactory<Program> host = baseFactory.WithWebHostBuilder(static _ => { });
		_ = host.CreateClient();
		IConfigurationRoot configuration = (IConfigurationRoot)host.Services.GetRequiredService<IConfiguration>();
		CancellationToken stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
		Assert.False(stopping.IsCancellationRequested);

		configuration["urls"] = "http://0.0.0.0:8080";
		// Other reload listeners can find the stopped host's services disposed; only the stop matters here.
		try
		{
			configuration.Reload();
		}
		catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(static e => e is ObjectDisposedException))
		{
		}

		Assert.True(Stops(stopping));
		Assert.Contains(baseFactory.Logs, static l => l.EventId.Id == 3203);
	}

	[Fact]
	public void A_key_may_not_grant_a_scope_it_lacks_even_beside_one_it_holds()
	{
		(ApiKeyRecord reader, _) = ApiKeyRecord.Issue("reader", [MemoriaScopes.Read], [], true, null, DateTimeOffset.UtcNow);

		Assert.Null(KeyGrant.Exceeds(reader, [MemoriaScopes.Read], [], true, null));
		Assert.Equal("scopes", KeyGrant.Exceeds(reader, [MemoriaScopes.Read, MemoriaScopes.Ingest], [], true, null));
	}

	[Fact]
	public async Task The_unbound_key_requirement_refuses_any_key_with_a_collection()
	{
		MemoriaUnboundKeyHandler handler = new();
		MemoriaUnboundKeyRequirement requirement = new();
		ClaimsPrincipal bound = new(new ClaimsIdentity([new Claim(MemoriaClaims.Scope, MemoriaScopes.Admin), new Claim(MemoriaClaims.Collection, "c")], "test"));
		ClaimsPrincipal unbound = new(new ClaimsIdentity([new Claim(MemoriaClaims.Scope, MemoriaScopes.Admin)], "test"));
		ClaimsPrincipal anonymous = new(new ClaimsIdentity());

		async Task<bool> Succeeds(ClaimsPrincipal user)
		{
			AuthorizationHandlerContext context = new([requirement], user, null);
			await handler.HandleAsync(context);
			return context.HasSucceeded;
		}

		Assert.False(await Succeeds(bound));
		Assert.True(await Succeeds(unbound));
		Assert.False(await Succeeds(anonymous));
	}
}
