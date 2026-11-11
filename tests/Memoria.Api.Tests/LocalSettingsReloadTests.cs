using Memoria.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Memoria.Api.Tests;

/// <summary>An edit to <c>appsettings.local.json</c> reaches the running host, as an edit to the committed files does.</summary>
public sealed class LocalSettingsReloadTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("memoria-localreload-").FullName;

	[Fact]
	public async Task An_edited_local_file_is_read_again()
	{
		string path = Path.Combine(_root, LocalSettings.FileName);
		File.WriteAllText(path, """{"A":"before"}""");
		using PhysicalFileProvider files = new(_root);
		ConfigurationBuilder builder = new();
		LocalSettings.Insert(builder, files);
		IConfigurationRoot configuration = builder.Build();
		Assert.Equal("before", configuration["A"]);

		TaskCompletionSource reloaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using IDisposable registration = ChangeToken.OnChange(configuration.GetReloadToken, () =>
		{
			if (configuration["A"] == "after")
			{
				reloaded.TrySetResult();
			}
		});
		File.WriteAllText(path, """{"A":"after"}""");

		await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
		Assert.Equal("after", configuration["A"]);
	}

	[Fact]
	public void Both_arguments_are_required()
	{
		using PhysicalFileProvider files = new(_root);

		Assert.Throws<ArgumentNullException>(() => LocalSettings.Insert(null!, files));
		Assert.Throws<ArgumentNullException>(() => LocalSettings.Insert(new ConfigurationBuilder(), null!));
	}

	public void Dispose()
	{
		Directory.Delete(_root, recursive: true);
	}
}
