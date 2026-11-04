// Redacts a directory of markdown into a temporary copy, runs gitleaks over the copy, and prints
// counts. Nothing else is ever printed: no value, no path, no line. Gitleaks runs with --redact=100
// and only its rule ids are read. The temporary files are deleted on every exit path.
// Needs gitleaks on PATH and no network. Run it through scripts/redaction-report.sh, on a local
// machine: it reads real content, so it does not belong in CI.
using System.Diagnostics;
using System.Text.Json;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
	Console.Error.WriteLine("usage: RedactionCheck <directory of .md files>");
	return 2;
}

string work = Directory.CreateTempSubdirectory("memoria-redaction-check-").FullName;
try
{
	Summary summary = Redact(args[0], Path.Combine(work, "redacted"));
	Dictionary<string, int> findings = RunGitleaks(Path.Combine(work, "redacted"), Path.Combine(work, "report.json"));
	Print(summary, findings);
	return findings.Count == 0 && summary.Refused == 0 ? 0 : 1;
}
finally
{
	Directory.Delete(work, recursive: true);
}

static Summary Redact(string source, string target)
{
	Directory.CreateDirectory(target);
	Redactor redactor = new();
	Summary summary = new();
	foreach (string file in Directory.EnumerateFiles(source, "*.md", SearchOption.AllDirectories))
	{
		summary.Files++;
		try
		{
			RedactedMarkdown result = redactor.Redact(new UnredactedMarkdown(File.ReadAllText(file)));
			summary.Add(result.CountsByRule);
			File.WriteAllText(Path.Combine(target, $"{summary.Files}.md"), result.Text);
		}
		catch (RedactionException)
		{
			summary.Refused++;
		}
	}

	return summary;
}

static Dictionary<string, int> RunGitleaks(string directory, string report)
{
	ProcessStartInfo start = new("gitleaks")
	{
		ArgumentList =
		{
			"dir", directory, "--config", Path.Combine(AppContext.BaseDirectory, "gitleaks-redacted.toml"),
			"--redact=100", "--no-banner", "--log-level", "error", "--exit-code", "0", "--report-format", "json", "--report-path", report,
		},
		RedirectStandardOutput = true,
		RedirectStandardError = true,
		UseShellExecute = false,
	};
	using Process process = Process.Start(start) ?? throw new InvalidOperationException("gitleaks did not start.");
	process.StandardError.ReadToEndAsync();
	process.StandardOutput.ReadToEndAsync();
	process.WaitForExit();
	if (process.ExitCode != 0)
	{
		throw new InvalidOperationException($"gitleaks exited with {process.ExitCode}.");
	}

	Dictionary<string, int> findings = [];
	using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
	foreach (JsonElement finding in document.RootElement.EnumerateArray())
	{
		string rule = finding.GetProperty("RuleID").GetString() ?? "unknown";
		findings[rule] = findings.GetValueOrDefault(rule) + 1;
	}

	return findings;
}

static void Print(Summary summary, Dictionary<string, int> findings)
{
	Console.WriteLine($"files: {summary.Files}");
	Console.WriteLine($"documents changed: {summary.Changed}");
	Console.WriteLine($"documents refused: {summary.Refused}");
	Console.WriteLine("replacements by rule:");
	foreach ((string rule, int count) in summary.Counts.OrderByDescending(static pair => pair.Value))
	{
		Console.WriteLine($"  {rule}: {count}");
	}

	Console.WriteLine($"gitleaks findings in redacted output: {findings.Values.Sum()}");
	foreach ((string rule, int count) in findings.OrderByDescending(static pair => pair.Value))
	{
		Console.WriteLine($"  {rule}: {count}");
	}
}

internal sealed class Summary
{
	public int Files { get; set; }

	public int Changed { get; private set; }

	public int Refused { get; set; }

	public Dictionary<string, int> Counts { get; } = [];

	public void Add(IReadOnlyDictionary<string, int> counts)
	{
		Changed += counts.Count > 0 ? 1 : 0;
		foreach ((string rule, int count) in counts)
		{
			Counts[rule] = Counts.GetValueOrDefault(rule) + count;
		}
	}
}
