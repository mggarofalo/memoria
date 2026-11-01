using Memoria.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace Memoria.Api.Tests;

public sealed class LocalSettingsTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("memoria-localsettings-").FullName;

	[Fact]
	public void LocalFile_BeatsCommittedFiles_ButNotEnvironmentOrCommandLine()
	{
		File.WriteAllText(Path.Combine(_root, "appsettings.json"), """{"A":"base","B":"base","C":"base","D":"base"}""");
		File.WriteAllText(Path.Combine(_root, "appsettings.Production.json"), """{"B":"env-file","C":"env-file","D":"env-file"}""");
		File.WriteAllText(Path.Combine(_root, LocalSettings.FileName), """{"C":"local","D":"local","E":"local"}""");
		using PhysicalFileProvider files = new(_root);

		// The same shape WebApplication.CreateBuilder produces: json files, then
		// the host's environment variables, then the command line.
		ConfigurationBuilder builder = new();
		builder.AddJsonFile(files, "appsettings.json", optional: false, reloadOnChange: false);
		builder.AddJsonFile(files, "appsettings.Production.json", optional: true, reloadOnChange: false);
		builder.AddInMemoryCollection(new Dictionary<string, string?> { ["D"] = "environment" });
		builder.AddCommandLine(["--E=command-line"]);

		LocalSettings.Insert(builder, files);
		IConfigurationRoot configuration = builder.Build();

		Assert.Equal("base", configuration["A"]);
		Assert.Equal("env-file", configuration["B"]);
		Assert.Equal("local", configuration["C"]);
		Assert.Equal("environment", configuration["D"]);
		Assert.Equal("command-line", configuration["E"]);
	}

	[Fact]
	public void MissingLocalFile_IsOptional()
	{
		File.WriteAllText(Path.Combine(_root, "appsettings.json"), """{"A":"base"}""");
		using PhysicalFileProvider files = new(_root);
		ConfigurationBuilder builder = new();
		builder.AddJsonFile(files, "appsettings.json", optional: false, reloadOnChange: false);

		LocalSettings.Insert(builder, files);

		Assert.Equal("base", builder.Build()["A"]);
	}

	[Fact]
	public void WithoutJsonSources_LocalFileIsFirst()
	{
		File.WriteAllText(Path.Combine(_root, LocalSettings.FileName), """{"A":"local"}""");
		using PhysicalFileProvider files = new(_root);
		ConfigurationBuilder builder = new();
		builder.AddInMemoryCollection(new Dictionary<string, string?> { ["A"] = "environment" });

		LocalSettings.Insert(builder, files);

		Assert.Equal("environment", builder.Build()["A"]);
	}

	public void Dispose()
	{
		Directory.Delete(_root, recursive: true);
	}
}
