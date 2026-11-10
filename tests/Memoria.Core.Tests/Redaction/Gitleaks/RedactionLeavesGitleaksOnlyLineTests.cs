using System.Text.Json;
using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction.Gitleaks;

/// <summary>
/// The end-to-end check of <c>migrate report</c> plants the line in <c>scripts/e2e/gitleaks-only.json</c> and needs real gitleaks
/// to find it in the redacted text. That only proves gitleaks read the text while Memoria's redaction leaves the line alone.
/// gitleaks's <c>generic-api-key</c> catches it, and that rule is the one left out of the import. If a rule change starts removing
/// the line, this test fails, so the change has to pick a new line, rather than the end-to-end run failing with a misleading message.
/// </summary>
public sealed class RedactionLeavesGitleaksOnlyLineTests
{
	/// <summary>The contexts the end-to-end check writes the line in.</summary>
	[Theory]
	[InlineData("# Doc\n{L}\n")]
	[InlineData("%PDF-1.7\n{L}\n")]
	[InlineData("MZ \n{L}\n")]
	[InlineData("PK\u0003\u0004\n{L}\n")]
	[InlineData("# Doc\n\ngit{M} is named here, {L}\n")]
	public void Redaction_leaves_the_line_gitleaks_alone_finds(string template)
	{
		string text = template.Replace("{L}", Line(), StringComparison.Ordinal).Replace("{M}", "leaks:allow", StringComparison.Ordinal);

		Assert.Equal(text, Redactor.Shared.RedactFragment(text).Text);
	}

	private static string Line()
	{
		string path = Path.Combine(RepositoryRoot(), "scripts", "e2e", "gitleaks-only.json");
		using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(path));
		string name = fixture.RootElement.GetProperty("name").GetString()!;
		string value = string.Concat(fixture.RootElement.GetProperty("value").EnumerateArray().Select(static part => part.GetString()));
		return name + ": " + value;
	}

	private static string RepositoryRoot()
	{
		string? directory = AppContext.BaseDirectory;
		while (directory is not null && !File.Exists(Path.Combine(directory, "Memoria.slnx")))
		{
			directory = Path.GetDirectoryName(directory);
		}

		return directory ?? throw new DirectoryNotFoundException("Memoria.slnx not found above " + AppContext.BaseDirectory);
	}
}
