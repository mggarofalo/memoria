using System.Diagnostics;
using System.Text;
using Memoria.Core.Redaction;
using Memoria.Core.Tests.Redaction.Gitleaks;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// The numbers a redaction change is held to. Lines of this repository's own non-test files that redaction changes, though none holds a
/// real credential. The import regression bar: how many of the imported rules' samples are caught, each drawn from its rule's own
/// pattern, so it shows the import kept the rules and not that they know real tokens. Real-format provider tokens caught, which
/// <see cref="Gitleaks.ProviderFormats"/> writes by hand, in each context. And benign transcript lines changed, from
/// <c>Fixtures/benign-lines.txt</c>, which must be 0. The report is opt-in, as it reads every tracked
/// file. Run it with
/// <code>MEMORIA_REDACTION_REPORT=&lt;directory&gt; dotnet test tests/Memoria.Core.Tests --filter RedactionMeasurement</code>
/// and compare <c>redaction-report.txt</c> before and after a change. It lists every changed line, so a new one shows in a diff.
/// </summary>
public sealed class RedactionMeasurementTests
{
	public const string ReportVariable = "MEMORIA_REDACTION_REPORT";

	/// <summary>Upstream's own sample secrets, in the vendored rule data and the page generated from it. Counted apart, as they never change.</summary>
	private static readonly string[] s_vendored = ["src/Memoria.Core/Redaction/Gitleaks/", "docs/gitleaks-rules.md"];

	[Fact]
	public void Report_redaction_over_this_repository()
	{
		string? directory = Environment.GetEnvironmentVariable(ReportVariable);
		if (string.IsNullOrEmpty(directory))
		{
			return;
		}

		string root = RepositoryRoot();
		List<string> changed = [];
		List<string> vendored = [];
		int files = 0;
		foreach (string relative in TrackedFiles(root).Where(static path => !IsTestPath(path)))
		{
			string? text = ReadText(Path.Combine(root, relative));
			if (text is null)
			{
				continue;
			}

			files++;
			List<string> target = s_vendored.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal)) ? vendored : changed;
			target.AddRange(ChangedLines(text, Redactor.Shared.Redact(text).Text).Select(line => relative + ": " + line));
		}

		string[] missed = [.. ImportedCases.Forms.Where(static form => Redactor.Shared.RedactFragment(form.Text).Text.Contains(form.Secret, StringComparison.Ordinal)).Select(static form => form.Rule)];
		string[] uncaught = [.. ProviderFormats.Tokens.SelectMany(static token => ProviderFormats.Contexts.Select(context => (token.Provider, Text: context.Replace("{T}", token.Token, StringComparison.Ordinal), token.Token)))
			.Where(static form => Redactor.Shared.RedactFragment(form.Text).Text.Contains(form.Token, StringComparison.Ordinal)).Select(static form => form.Provider)];
		int forms = ProviderFormats.Tokens.Count * ProviderFormats.Contexts.Count;
		int benignChanged = BenignLines.All.Count(static line => Redactor.Shared.RedactFragment(line).Text != line);

		StringBuilder report = new();
		report.AppendLine($"non-test files: {files}");
		report.AppendLine($"wrongly changed lines: {changed.Count}");
		report.AppendLine($"vendored sample lines changed: {vendored.Count}");
		// Each sample is drawn from its rule's own pattern and kept only if that rule matches it, so this says the import kept the rules.
		report.AppendLine($"import regression bar, samples drawn from each rule's own pattern: {ImportedCases.Forms.Count - missed.Length} of {ImportedCases.Forms.Count}");
		report.AppendLine($"real-format provider tokens caught, in {ProviderFormats.Contexts.Count} contexts each: {forms - uncaught.Length} of {forms}");
		report.AppendLine($"benign transcript lines changed, which must be 0: {benignChanged} of {BenignLines.All.Count}");
		report.AppendLine();
		report.AppendLine("missed sampled rules:");
		missed.ToList().ForEach(rule => report.AppendLine("  " + rule));
		report.AppendLine("providers missed in some context:");
		uncaught.Distinct().ToList().ForEach(provider => report.AppendLine("  " + provider));
		report.AppendLine();
		report.AppendLine("changed lines:");
		changed.ForEach(line => report.AppendLine("  " + line));
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "redaction-report.txt"), report.ToString());
	}

	/// <summary>
	/// Shapes gitleaks reported in a real corpus after redaction, each reviewed and found to hold no credential, so the report's ignore
	/// file lists them. Redaction must go on leaving them alone: a rule that starts taking them is a false positive of its own. The
	/// values are put together here so this repository's own gitleaks scan does not report them.
	/// </summary>
	public static TheoryData<string> GitleaksFalsePositiveShapes()
	{
		string keys = "key" + "s";
		string colon = ":";
		string pair = "a" + colon + "REAL";
		string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData("a blob"u8));
		return
		[
			// curl-auth-user: a review note about curl -u. The rule looks ahead from the heading to the prose example two lines on, and
			// takes its one-letter user and all-caps placeholder as user:password.
			$"**H5. `curl -u` catches only the last match on a line.**\n- Rule: `CommandLineRules.cs:228` (the greedy pattern).\n- `curl -u {pair} x && curl -u b{colon}OTHER y` leaves `REAL`.\n- Fix: match the flag itself.",
			// generic-api-key: a list of git blob hashes, the keyword in a file name and the value a SHA-1.
			$"- api.d.ts: `{hash}`",
			// generic-api-key: prose listing test cases, the keyword an English plural.
			$"- archive restore, changed manifest membership, missing entries, wrong {keys}, duplicate/unknown/missing JSON fields",
			$"No other concrete blockers found. Strict payload {keys}, duplicate/orphan rejection, membership checks, prior lineage.",
		];
	}

	[Theory]
	[MemberData(nameof(GitleaksFalsePositiveShapes))]
	public void A_line_gitleaks_reports_without_a_credential_stays_as_written(string text)
	{
		Assert.Equal(text, Redactor.Shared.Redact(text).Text);
	}

	[Theory]
	[InlineData("tests/Memoria.Core.Tests/X.cs", true)]
	[InlineData("sidecar/test/store.test.ts", true)]
	[InlineData("worker/src/a.test.ts", true)]
	[InlineData("cli/cmd/root_test.go", true)]
	[InlineData("cli/internal/x/testdata/a.json", true)]
	[InlineData("src/Memoria.Core/Redaction/Redactor.cs", false)]
	[InlineData("docs/configuration.md", false)]
	[InlineData("scripts/e2e/compose.ts", false)]
	public void A_test_path_is_known(string path, bool expected)
	{
		Assert.Equal(expected, IsTestPath(path));
	}

	[Theory]
	[InlineData("a\nb\nc", "a\nb\nc", "")]
	[InlineData("a\nb\nc", "a\nX\nc", "b")]
	[InlineData("a\nb\nb", "a\nb\nX", "b")]
	[InlineData("a\r\nb\r\n", "a\r\nX\r\n", "b")]
	[InlineData("a\nb\nc", "a\nX", "b|c")]
	public void Changed_lines_are_the_lines_the_redacted_text_lost(string before, string after, string expected)
	{
		Assert.Equal(expected, string.Join('|', ChangedLines(before, after)));
	}

	/// <summary>Where test fixtures live in this repository: the .NET test projects, the TypeScript and Go tests, and Go's testdata.</summary>
	internal static bool IsTestPath(string path)
	{
		return path.StartsWith("tests/", StringComparison.Ordinal) || path.Contains("/test/", StringComparison.Ordinal) || path.Contains(".test.", StringComparison.Ordinal)
			|| path.EndsWith("_test.go", StringComparison.Ordinal) || path.Contains("/testdata/", StringComparison.Ordinal);
	}

	/// <summary>
	/// The original lines that no longer appear in the redacted text, each counted as often as it was lost. A placeholder can span lines,
	/// so the texts are compared as multisets of lines rather than line by line.
	/// </summary>
	internal static IReadOnlyList<string> ChangedLines(string before, string after)
	{
		Dictionary<string, int> left = [];
		foreach (string line in after.Split('\n'))
		{
			left[line.TrimEnd('\r')] = left.GetValueOrDefault(line.TrimEnd('\r')) + 1;
		}

		List<string> changed = [];
		foreach (string line in before.Split('\n').Select(static line => line.TrimEnd('\r')))
		{
			if (left.GetValueOrDefault(line) > 0)
			{
				left[line]--;
			}
			else
			{
				changed.Add(line);
			}
		}

		return changed;
	}

	private static IEnumerable<string> TrackedFiles(string root)
	{
		// Unquoted, so a path with non-ASCII characters is read rather than skipped.
		ProcessStartInfo start = new("git", "-c core.quotePath=false ls-files") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
		using Process git = Process.Start(start)!;
		string output = git.StandardOutput.ReadToEnd();
		git.WaitForExit();
		return git.ExitCode == 0
			? output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			: throw new InvalidOperationException("git ls-files failed, so the report would count nothing");
	}

	/// <summary>The file's text, or null for a binary or very large file.</summary>
	private static string? ReadText(string path)
	{
		if (!File.Exists(path) || new FileInfo(path).Length > 8_000_000)
		{
			return null;
		}

		string text = File.ReadAllText(path);
		return text.Contains('\0', StringComparison.Ordinal) ? null : text;
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
