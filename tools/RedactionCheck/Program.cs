// Redacts markdown in memory, has gitleaks read the result on its standard input, and prints counts.
// Nothing else is ever printed: no value, no path, no line. Gitleaks runs with --redact=100 and only its
// rule ids are read. No copy of any text is written to disk, so there is nothing for a killed run to leave.
// Needs gitleaks on PATH, unless told --no-gitleaks, and no network. Run it through
// scripts/redaction-report.sh or `memoria migrate report`, on a local machine: it reads real
// content, so it does not belong in CI.
//
// Each list of files is a directory, or with --lists-on-stdin a JSON array of arrays of paths on its standard
// input, so a list of names is never on disk. With --json it prints one line, an array with one result per
// list, in the order given.
//
// Gitleaks is best effort: it can miss a token at a chunk boundary, so 0 findings is not proof that nothing is left.
//
// Exits 0 when clean, 1 when something was refused, found, skipped or not scanned, 2 on bad usage, 3 when it could not finish.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;

return RedactionReport.Guarded(args);

internal static class RedactionReport
{
	public static int Guarded(string[] args)
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
			Console.Error.WriteLine("usage: RedactionCheck [--json] [--no-gitleaks] <directory of .md files> ... | --lists-on-stdin");
			return 2;
		}

		List<Result> results = [.. options.Lists.Select(list => Check(list, options.Gitleaks))];
		Report(results, options.Json);
		return results.All(static result => result.IsClean) ? 0 : 1;
	}

	private static Result Check(FileList list, bool gitleaks)
	{
		using Gitleaks? scan = gitleaks ? Gitleaks.Start() : null;
		Summary summary = Redact(list.Files(), scan);
		Dictionary<string, int> findings = scan?.Finish() ?? [];
		return new Result(summary.Files, summary.Changed, summary.Refused, summary.Skipped, scan?.Unscanned ?? 0, summary.Counts, summary.FilesByRule, gitleaks, findings);
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

		Console.WriteLine($"gitleaks findings in redacted output: {result.GitleaksFindings.Values.Sum()}. Best effort: it can miss a token at a chunk boundary, so 0 is not proof that nothing is left.");
		Console.WriteLine($"documents gitleaks could not read: {result.GitleaksUnscanned}");
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
	private int _documents;
	private bool _failed;

	private Gitleaks(Process process)
	{
		_process = process;
		_report = process.StandardOutput.ReadToEndAsync();
		_errors = process.StandardError.ReadToEndAsync();
		Send(new string(' ', SniffedBytes) + "\n");
	}

	/// <summary>
	/// Gitleaks looks at the start of its input to decide if it is binary or an archive, and if so reads no further and reports nothing
	/// while exiting 0. A Markdown file can start with %PDF or MZ, so the input starts with blanks instead, longer than what it looks at.
	/// </summary>
	private const int SniffedBytes = 512;

	/// <summary>The documents it did not read, which is all of them once it stops or gives nothing usable.</summary>
	public int Unscanned => _failed ? _documents : 0;

	public static Gitleaks Start()
	{
		ProcessStartInfo start = new("gitleaks")
		{
			ArgumentList =
			{
				"stdin", "--config", Path.Combine(AppContext.BaseDirectory, "gitleaks-redacted.toml"),
				"--ignore-gitleaks-allow", "--redact=100", "--no-banner", "--log-level", "error", "--exit-code", "0", "--report-format", "json", "--report-path", "-",
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
		_documents++;
		Send(text + "\n");
	}

	/// <summary>Gitleaks may stop reading at any time, and that must be counted rather than end the whole report.</summary>
	private void Send(string text)
	{
		if (_failed)
		{
			return;
		}

		try
		{
			_process.StandardInput.Write(text);
		}
		catch (IOException)
		{
			_failed = true;
		}
	}

	public Dictionary<string, int> Finish()
	{
		CloseInput();
		string report = _report.GetAwaiter().GetResult();
		_errors.GetAwaiter().GetResult();
		_process.WaitForExit();
		Dictionary<string, int>? findings = _process.ExitCode == 0 && !_failed ? Parse(report) : null;
		_failed = findings is null;
		return findings ?? [];
	}

	private void CloseInput()
	{
		try
		{
			_process.StandardInput.Close();
		}
		catch (IOException)
		{
			_failed = true;
		}
	}

	/// <summary>Null when gitleaks gave something other than its report.</summary>
	private static Dictionary<string, int>? Parse(string report)
	{
		try
		{
			Dictionary<string, int> findings = [];
			using JsonDocument document = JsonDocument.Parse(report);
			foreach (JsonElement finding in document.RootElement.EnumerateArray())
			{
				string rule = finding.GetProperty("RuleID").GetString() ?? "unknown";
				findings[rule] = findings.GetValueOrDefault(rule) + 1;
			}

			return findings;
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
		{
			return null;
		}
	}

	public void Dispose()
	{
		_process.Dispose();
	}
}

internal sealed record FileList(string? Directory, IReadOnlyList<string>? Paths)
{
	public IEnumerable<string> Files()
	{
		return Paths ?? System.IO.Directory.EnumerateFiles(Directory!, "*.md", SearchOption.AllDirectories);
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
				case "--lists-on-stdin":
					lists.AddRange(ReadLists());
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

	/// <summary>From the raw stream, as Console.In would decode by the console's code page rather than UTF-8.</summary>
	private static IEnumerable<FileList> ReadLists()
	{
		using Stream input = Console.OpenStandardInput();
		List<List<string>> lists = JsonSerializer.Deserialize<List<List<string>>>(input) ?? [];
		return lists.Select(static paths => new FileList(null, paths));
	}
}

internal sealed record Result(
	int Files,
	int Changed,
	int Refused,
	int Skipped,
	int GitleaksUnscanned,
	Dictionary<string, int> Replacements,
	Dictionary<string, int> FilesByRule,
	bool GitleaksRan,
	Dictionary<string, int> GitleaksFindings)
{
	[JsonIgnore]
	public bool IsClean => GitleaksFindings.Count == 0 && GitleaksUnscanned == 0 && Refused == 0 && Skipped == 0;
}

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
