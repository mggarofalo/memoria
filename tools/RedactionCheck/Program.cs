// Redacts markdown into a temporary copy, runs gitleaks over the copy, and prints counts. Nothing
// else is ever printed: no value, no path, no line. Gitleaks runs with --redact=100 and only its
// rule ids are read. The temporary files are deleted on every exit path.
// Needs gitleaks on PATH, unless told --no-gitleaks, and no network. Run it through
// scripts/redaction-report.sh or `memoria migrate report`, on a local machine: it reads real
// content, so it does not belong in CI.
//
// Each list of files is a directory, or --manifest and a file naming one path per line. With
// --json it prints one line, an array with one result per list, in the order given.
using System.Diagnostics;
using System.Text.Json;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;

return RedactionReport.Main(args);

internal static class RedactionReport
{
	public static int Main(string[] args)
	{
		Options? options = Options.Parse(args);
		if (options is null)
		{
			Console.Error.WriteLine("usage: RedactionCheck [--json] [--no-gitleaks] <directory of .md files | --manifest file> ...");
			return 2;
		}

		string work = Directory.CreateTempSubdirectory("memoria-redaction-check-").FullName;
		try
		{
			return Run(options, work);
		}
		finally
		{
			Directory.Delete(work, recursive: true);
		}
	}

	private static int Run(Options options, string work)
	{
		List<Result> results = [];
		for (int i = 0; i < options.Lists.Count; i++)
		{
			string redacted = Path.Combine(work, $"redacted-{i}");
			Summary summary = Redact(options.Lists[i].Files(), redacted);
			Dictionary<string, int> findings = options.Gitleaks ? RunGitleaks(redacted, Path.Combine(work, $"report-{i}.json")) : [];
			results.Add(new Result(summary.Files, summary.Changed, summary.Refused, summary.Counts, summary.FilesByRule, options.Gitleaks, findings));
		}

		Report(results, options.Json);
		return results.All(static result => result.GitleaksFindings.Count == 0 && result.Refused == 0) ? 0 : 1;
	}

	private static Summary Redact(IEnumerable<string> files, string target)
	{
		Directory.CreateDirectory(target);
		Redactor redactor = new();
		Summary summary = new();
		foreach (string file in files)
		{
			summary.Files++;
			try
			{
				RedactedMarkdown result = redactor.Redact(UnredactedMarkdown.FromText(File.ReadAllText(file)));
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

	private static Dictionary<string, int> RunGitleaks(string directory, string report)
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

	private static void Report(List<Result> results, bool json)
	{
		if (json)
		{
			Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
			return;
		}

		for (int i = 0; i < results.Count; i++)
		{
			Console.WriteLine($"== directory {i + 1} of {results.Count}");
			Print(results[i]);
		}
	}

	private static void Print(Result result)
	{
		Console.WriteLine($"files: {result.Files}");
		Console.WriteLine($"documents changed: {result.Changed}");
		Console.WriteLine($"documents refused: {result.Refused}");
		Console.WriteLine("replacements by rule:");
		foreach ((string rule, int count) in result.Replacements.OrderByDescending(static pair => pair.Value))
		{
			Console.WriteLine($"  {rule}: {count}");
		}

		if (!result.GitleaksRan)
		{
			Console.WriteLine("gitleaks: not run");
			return;
		}

		Console.WriteLine($"gitleaks findings in redacted output: {result.GitleaksFindings.Values.Sum()}");
		foreach ((string rule, int count) in result.GitleaksFindings.OrderByDescending(static pair => pair.Value))
		{
			Console.WriteLine($"  {rule}: {count}");
		}
	}
}

internal sealed record FileList(string? Directory, string? Manifest)
{
	public IEnumerable<string> Files()
	{
		return Manifest is null
			? System.IO.Directory.EnumerateFiles(Directory!, "*.md", SearchOption.AllDirectories)
			: File.ReadAllLines(Manifest).Where(static line => line.Length > 0);
	}
}

internal sealed record Options(bool Json, bool Gitleaks, List<FileList> Lists)
{
	public static Options? Parse(string[] args)
	{
		bool json = false, gitleaks = true;
		List<FileList> lists = [];
		for (int i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--json":
					json = true;
					break;
				case "--no-gitleaks":
					gitleaks = false;
					break;
				case "--manifest" when i + 1 < args.Length && File.Exists(args[i + 1]):
					lists.Add(new FileList(null, args[++i]));
					break;
				default:
					if (!Directory.Exists(args[i]))
					{
						return null;
					}

					lists.Add(new FileList(args[i], null));
					break;
			}
		}

		return lists.Count == 0 ? null : new Options(json, gitleaks, lists);
	}
}

internal sealed record Result(
	int Files,
	int Changed,
	int Refused,
	Dictionary<string, int> Replacements,
	Dictionary<string, int> FilesByRule,
	bool GitleaksRan,
	Dictionary<string, int> GitleaksFindings);

internal sealed class Summary
{
	public int Files { get; set; }

	public int Changed { get; private set; }

	public int Refused { get; set; }

	public Dictionary<string, int> Counts { get; } = [];

	public Dictionary<string, int> FilesByRule { get; } = [];

	public void Add(IReadOnlyDictionary<string, int> counts)
	{
		Changed += counts.Count > 0 ? 1 : 0;
		foreach ((string rule, int count) in counts)
		{
			Counts[rule] = Counts.GetValueOrDefault(rule) + count;
			FilesByRule[rule] = FilesByRule.GetValueOrDefault(rule) + 1;
		}
	}
}
