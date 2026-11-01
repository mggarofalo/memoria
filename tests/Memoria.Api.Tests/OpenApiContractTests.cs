using System.Text;
using System.Text.Json;

namespace Memoria.Api.Tests;

/// <summary>
/// The served OpenAPI document must equal the committed <c>src/Memoria.Api/openapi.json</c>,
/// which the Go CLI generates its client from and CI lints. After changing an
/// endpoint, regenerate with:
/// <code>MEMORIA_WRITE_OPENAPI=1 dotnet test Memoria.slnx --filter OpenApiContract</code>
/// </summary>
public sealed class OpenApiContractTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	public const string WriteVariable = "MEMORIA_WRITE_OPENAPI";

	private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	[Fact]
	public async Task ServedDocument_MatchesCommittedFile()
	{
		HttpClient client = factory.CreateClient();
		string served = Normalize(await client.GetStringAsync("/openapi/v1.json"));
		string path = CommittedPath();

		if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
		{
			await File.WriteAllTextAsync(path, served, new UTF8Encoding(false));
			return;
		}

		Assert.True(File.Exists(path), $"{path} is missing; run with {WriteVariable}=1 to create it.");
		string committed = Normalize(await File.ReadAllTextAsync(path));
		Assert.True(
			string.Equals(committed, served, StringComparison.Ordinal),
			$"src/Memoria.Api/openapi.json is out of date with the served document. Regenerate with {WriteVariable}=1 dotnet test Memoria.slnx --filter OpenApiContract");
	}

	/// <summary>Stable formatting so whitespace and key escaping never register as drift.</summary>
	private static string Normalize(string json)
	{
		using JsonDocument document = JsonDocument.Parse(json);
		return JsonSerializer.Serialize(document.RootElement, Pretty).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
	}

	private static string CommittedPath()
	{
		string? directory = AppContext.BaseDirectory;
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory, "Memoria.slnx")))
			{
				return Path.Combine(directory, "src", "Memoria.Api", "openapi.json");
			}

			directory = Path.GetDirectoryName(directory);
		}

		throw new FileNotFoundException("Memoria.slnx not found above " + AppContext.BaseDirectory);
	}
}
