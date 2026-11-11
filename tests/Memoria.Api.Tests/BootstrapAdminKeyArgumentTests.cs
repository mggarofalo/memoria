using Memoria.Api.Auth;
using Memoria.Api.Hosting;

namespace Memoria.Api.Tests;

/// <summary>The bootstrap command's entry point refuses a missing collaborator before it touches the database.</summary>
public sealed class BootstrapAdminKeyArgumentTests
{
	private static readonly IApiKeyStore Store = new SqliteApiKeyStore(new ControlDatabase(Path.Combine(Path.GetTempPath(), "memoria-tests", "never-opened", "control.db")));

	[Fact]
	public async Task EachMissingArgument_IsRefusedByName()
	{
		using StringWriter output = new();
		using StringWriter error = new();

		ArgumentNullException store = await Assert.ThrowsAsync<ArgumentNullException>(() => BootstrapAdminKey.RunAsync(null!, "label", false, TimeProvider.System, output, error));
		ArgumentNullException clock = await Assert.ThrowsAsync<ArgumentNullException>(() => BootstrapAdminKey.RunAsync(Store, "label", false, null!, output, error));
		ArgumentNullException stdout = await Assert.ThrowsAsync<ArgumentNullException>(() => BootstrapAdminKey.RunAsync(Store, "label", false, TimeProvider.System, null!, error));
		ArgumentNullException stderr = await Assert.ThrowsAsync<ArgumentNullException>(() => BootstrapAdminKey.RunAsync(Store, "label", false, TimeProvider.System, output, null!));

		Assert.Equal(("store", "timeProvider", "output", "error"), (store.ParamName, clock.ParamName, stdout.ParamName, stderr.ParamName));
		Assert.False(Directory.Exists(Path.Combine(Path.GetTempPath(), "memoria-tests", "never-opened")));
	}
}
