// Redacts markdown in memory, has gitleaks read the result on its standard input, and prints counts.
// Nothing else is ever printed: no value, no path, no line. Gitleaks runs with --redact=100 and only its
// rule ids are read. No copy of any text is written to disk, so there is nothing for a killed run to leave.
// Needs gitleaks on PATH, unless told --no-gitleaks, and no network. Run it through
// scripts/redaction-report.sh or `memoria migrate report`, on a local machine: it reads real
// content, so it does not belong in CI.
//
// Each list of files is a directory, or --manifest and a file naming one path per line. With
// --json it prints one line, an array with one result per list, in the order given.
//
// Exits 0 when clean, 1 when something was refused, found or skipped, 2 on bad usage, 3 when it could not finish.
using System.Diagnostics;
using System.Text.Json;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;

return RedactionReport.Main(args);

internal static class RedactionReport
{
	public static int Main(string[] args)
	{
		// An unhandled exception would print its message, which can name a file, and put it in the Windows event log.
		try
		{
			return Run(args);
		}
		catch (Exception)
		{
			Console.Error.WriteLine("The redaction check could not finish, so it reports nothing. Its error is left out, as it could name a file.");
			return 3;
		}
	}

	private static int Run(string[] args)
	{
		Options? options = Options.Parse(args);
		if (options is null)
		{
			Console.Error.WriteLine("usage: RedactionCheck [--json] [--no-gitleaks] <directory of .md files | --manifest file> ...");
			return 2;
		}

		List<Result> results = [.. options.Lists.Select(list => Check(list, options.Gitleaks))];
		Report(results, options.Json);
		return results.All(static result => result.GitleaksFindings.Count == 0 && result.Refused == 0 && result.Skipped == 0) ? 0 : 1;
	}

	private static Result Check(FileList list, bool gitleaks)
	{
		using Gitleaks? scan = gitleaks ? Gitleaks.Start() : null;
		Summary summary = Redact(list.Files(), scan);
		Dictionary<string, int> findings = scan?.Finish() ?? [];
		return new Result(summary.Files, summary.Changed, summary.Refused, summary.Skipped, summary.Counts, summary.FilesByRule, gitleaks, findings);
	}

	private static Summary Redact(IEnumerable<string> files, Gitleaks? scan)
	{
		Redactor redactor = new();
		Summary summary = new();
		foreach (string file in files)
		{
			summary.Files++;
			if (Read(file) is not { } text)
			{
				summary.Skipped++;
				continue;
			}

			try
			{
				RedactedMarkdown result = redactor.Redact(UnredactedMarkdown.FromText(text));
				summary.Add(result.CountsByRule);
				scan?.Write(result.Text);
			}
			catch (RedactionException)
			{
				summary.Refused++;
			}
		}

		return summary;
	}

	/// <summary>Null for a file that cannot be read. It is counted, and one bad file does not end the whole report.</summary>
	private static string? Read(string file)
	{
		try
		{
			return File.ReadAllText(file);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
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
		Console.WriteLine($"files skipped: {result.Skipped}");
		Console.WriteLine("replacements by rule:");
		foreach ((string rule, int count) in result.Replacements.OrderByDescending(static pair => pair.Value))
		{
			Console.WriteLine($"  {rule}: {count}");
		}

		if (!result.GitleaksRan)
		{
			Console.WriteLine("gitleaks: NOT RUN. This is a weaker check: it says what the rules replaced, and nothing about what they missed.");
			return;
		}

		Console.WriteLine($"gitleaks findings in redacted output: {result.GitleaksFindings.Values.Sum()}");
		foreach ((string rule, int count) in result.GitleaksFindings.OrderByDescending(static pair => pair.Value))
		{
			Console.WriteLine($"  {rule}: {count}");
		}
	}
}

/// <summary>One gitleaks process reading one list's redacted text on its standard input, and reporting on its standard output.</summary>
internal sealed class Gitleaks : IDisposable
{
	private readonly Process _process;
	private readonly Task<string> _report;
	private readonly Task _errors;

	private Gitleaks(Process process)
	{
		_process = process;
		_report = process.StandardOutput.ReadToEndAsync();
		_errors = process.StandardError.ReadToEndAsync();
	}

	public static Gitleaks Start()
	{
		ProcessStartInfo start = new("gitleaks")
		{
			ArgumentList =
			{
				"stdin", "--config", Path.Combine(AppContext.BaseDirectory, "gitleaks-redacted.toml"),
				"--redact=100", "--no-banner", "--log-level", "error", "--exit-code", "0", "--report-format", "json", "--report-path", "-",
			},
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		return new Gitleaks(Process.Start(start) ?? throw new InvalidOperationException("gitleaks did not start."));
	}

	public void Write(string text)
	{
		_process.StandardInput.Write(text);
		_process.StandardInput.Write('\n');
	}

	public Dictionary<string, int> Finish()
	{
		_process.StandardInput.Close();
		string report = _report.GetAwaiter().GetResult();
		_errors.GetAwaiter().GetResult();
		_process.WaitForExit();
		if (_process.ExitCode != 0)
		{
			throw new InvalidOperationException($"gitleaks exited with {_process.ExitCode}.");
		}

		Dictionary<string, int> findings = [];
		using JsonDocument document = JsonDocument.Parse(report);
		foreach (JsonElement finding in document.RootElement.EnumerateArray())
		{
			string rule = finding.GetProperty("RuleID").GetString() ?? "unknown";
			findings[rule] = findings.GetValueOrDefault(rule) + 1;
		}

		return findings;
	}

	public void Dispose()
	{
		_process.Dispose();
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
	int Skipped,
	Dictionary<string, int> Replacements,
	Dictionary<string, int> FilesByRule,
	bool GitleaksRan,
	Dictionary<string, int> GitleaksFindings);

internal sealed class Summary
{
	public int Files { get; set; }

	public int Changed { get; private set; }

	public int Refused { get; set; }

	public int Skipped { get; set; }

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
