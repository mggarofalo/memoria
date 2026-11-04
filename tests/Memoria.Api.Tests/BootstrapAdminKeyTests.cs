using System.Net;
using Memoria.Api.Auth;
using Memoria.Api.Hosting;
using Memoria.Api.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Memoria.Api.Tests;

public sealed class BootstrapAdminKeyTests : IAsyncLifetime
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "memoria-tests", Guid.NewGuid().ToString("N"));

	private string DatabasePath => Path.Combine(_directory, "control.db");

	public Task InitializeAsync()
	{
		return Task.CompletedTask;
	}

	public Task DisposeAsync()
	{
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}

		return Task.CompletedTask;
	}

	private IConfiguration Configuration(string? path)
	{
		return new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["Memoria:Auth:DatabasePath"] = path })
			.Build();
	}

	private async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
	{
		using StringWriter output = new();
		using StringWriter error = new();
		int code = await BootstrapAdminKey.RunAsync(args, Configuration(DatabasePath), output, error);
		return (code, output.ToString().Trim(), error.ToString());
	}

	[Theory]
	[InlineData(new[] { "--bootstrap-admin-key", "x" }, true)]
	[InlineData(new[] { "--health-check" }, false)]
	[InlineData(new string[0], false)]
	public void IsRequested_LooksAtTheFirstArgument(string[] args, bool expected)
	{
		Assert.Equal(expected, BootstrapAdminKey.IsRequested(args));
	}

	[Fact]
	public void HostArguments_RemovesThisCommandsSwitches()
	{
		string[] rest = BootstrapAdminKey.HostArguments(["--bootstrap-admin-key", "first", "--allow-additional-admin", "--Memoria:Auth:DatabasePath=x.db"]);

		Assert.Equal(["--Memoria:Auth:DatabasePath=x.db"], rest);
	}

	[Fact]
	public async Task CreatesAnAdminOnlyKey_PrintsOnlyTheTokenOnStdout()
	{
		(int code, string output, string error) = await RunAsync("--bootstrap-admin-key", "first");

		Assert.Equal(0, code);
		Assert.True(ApiKeyToken.TryParseId(output, out string? id));
		Assert.Contains(id, error);
		Assert.DoesNotContain(output, error);

		ApiKeyRecord record = (await new SqliteApiKeyStore(new ControlDatabase(DatabasePath)).FindAsync(id))!;
		Assert.Equal("first", record.Label);
		Assert.Equal([MemoriaScopes.Admin], record.Scopes);
		Assert.Empty(record.Collections);
		Assert.False(record.AllCollections);
		Assert.Equal(ApiKeyToken.Hash(output), record.Sha256Hash);
	}

	[Fact]
	public async Task RefusesWhileAnActiveAdminExists_UnlessExplicitlyAllowed()
	{
		await RunAsync("--bootstrap-admin-key", "first");

		(int refused, string refusedOutput, string refusedError) = await RunAsync("--bootstrap-admin-key", "second");
		Assert.Equal(1, refused);
		Assert.Empty(refusedOutput);
		Assert.Contains(BootstrapAdminKey.AllowAdditionalSwitch, refusedError);

		(int allowed, string allowedOutput, _) = await RunAsync("--bootstrap-admin-key", "second", BootstrapAdminKey.AllowAdditionalSwitch);
		Assert.Equal(0, allowed);
		Assert.True(ApiKeyToken.TryParseId(allowedOutput, out _));
	}

	[Fact]
	public async Task RevokingTheLastAdmin_AllowsBootstrapAgain()
	{
		(_, string token, _) = await RunAsync("--bootstrap-admin-key", "first");
		ApiKeyToken.TryParseId(token, out string? id);
		await new SqliteApiKeyStore(new ControlDatabase(DatabasePath)).RevokeAsync(id!, DateTimeOffset.UtcNow);

		(int code, _, _) = await RunAsync("--bootstrap-admin-key", "replacement");

		Assert.Equal(0, code);
	}

	[Theory]
	[InlineData("--bootstrap-admin-key")]
	[InlineData("--bootstrap-admin-key", "--allow-additional-admin")]
	[InlineData("--bootstrap-admin-key", "   ")]
	public async Task MissingLabel_IsAUsageError(params string[] args)
	{
		(int code, string output, string error) = await RunAsync(args);

		Assert.Equal(2, code);
		Assert.Empty(output);
		Assert.Contains("Usage", error);
		Assert.False(Directory.Exists(_directory));
	}

	[Fact]
	public async Task MissingDatabasePath_Fails()
	{
		using StringWriter output = new();
		using StringWriter error = new();

		int code = await BootstrapAdminKey.RunAsync(["--bootstrap-admin-key", "first"], Configuration(null), output, error);

		Assert.Equal(1, code);
		Assert.Contains("Memoria:Auth:DatabasePath", error.ToString());
	}
}

public sealed class BootstrapEndToEndTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	[Fact]
	public async Task BootstrappedKey_WorksAgainstTheAdminApi_AndNotTheCorpus()
	{
		using StringWriter output = new();
		using StringWriter error = new();
		int code = await BootstrapAdminKey.RunAsync(factory.Store, "first", false, TimeProvider.System, output, error);
		Assert.Equal(0, code);

		HttpClient admin = AuthApiFactory.Authorized(factory.CreateClient(), output.ToString().Trim());

		Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/v1/admin/keys")).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/v1/collections")).StatusCode);
	}
}
