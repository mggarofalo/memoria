using Memoria.Api.Auth;
using Memoria.Api.Hosting;
using Microsoft.Data.Sqlite;

namespace Memoria.Api.Tests;

/// <summary>The bootstrap command's entry point refuses a missing collaborator by name.</summary>
public sealed class BootstrapAdminKeyArgumentTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "memoria-tests", Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}

	[Fact]
	public async Task EachMissingArgument_IsRefusedByName()
	{
		IApiKeyStore store = new SqliteApiKeyStore(new ControlDatabase(Path.Combine(_directory, "control.db")));
		using StringWriter output = new();
		using StringWriter error = new();

		ArgumentNullException noStore = await Assert.ThrowsAsync<ArgumentNullException>(() => BootstrapAdminKey.RunAsync(null!, "label", false, TimeProvider.System, output, error));
		ArgumentNullException clock = await Assert.ThrowsAsync<ArgumentNullException>(() => BootstrapAdminKey.RunAsync(store, "label", false, null!, output, error));
		ArgumentNullException stdout = await Assert.ThrowsAsync<ArgumentNullException>(() => BootstrapAdminKey.RunAsync(store, "label", false, TimeProvider.System, null!, error));
		ArgumentNullException stderr = await Assert.ThrowsAsync<ArgumentNullException>(() => BootstrapAdminKey.RunAsync(store, "label", false, TimeProvider.System, output, null!));

		Assert.Equal(("store", "timeProvider", "output", "error"), (noStore.ParamName, clock.ParamName, stdout.ParamName, stderr.ParamName));
	}
}
