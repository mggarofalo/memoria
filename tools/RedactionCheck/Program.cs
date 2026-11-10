// Redacts markdown in memory, has gitleaks read the result on its standard input, and prints counts.
// Nothing else is ever printed: no value, no path, no line. Gitleaks runs with --redact=100 and only its
// rule ids are read. No copy of any text is written to disk, so there is nothing for a killed run to leave.
// Needs gitleaks on PATH, unless told --no-gitleaks, and no network. Run it through
// scripts/redaction-report.sh or `memoria migrate report`, on a local machine: it reads real
// content, so it does not belong in CI.
//
// Each list of files is a directory, or with --lists-on-stdin a JSON array of { root, paths } on its standard
// input, so a list of names is never on disk. With --json it prints one line, an array with one result per
// list, in the order given.
//
// Gitleaks is best effort: it can miss a token at a chunk boundary, so 0 findings is not proof that nothing is left.
//
// --gitleaks-ignore <file> reads a .gitleaksignore of findings a person reviewed: one fingerprint a line, '#' for comments. A
// fingerprint is <list>/<path in it>:<rule>:<line>, gitleaks's own form for a file, with the line counted in the redacted
// document. A listed finding is counted as ignored and does not fail the run; any other still does. --gitleaks-findings <file>
// writes the fingerprints of the findings that are not ignored, for that review. Both files hold paths, so keep them private.
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
			Console.Error.WriteLine("usage: RedactionCheck [--json] [--no-gitleaks] [--gitleaks-ignore <file>] [--gitleaks-findings <file>] <directory of .md files> ... | --lists-on-stdin");
			return 2;
		}

		HashSet<string> ignored = options.IgnoreFile is null ? [] : Fingerprints.Read(options.IgnoreFile);
		List<string> remaining = [];
		List<Result> results = [.. options.Lists.Select(list => Check(list, options.Gitleaks, ignored, remaining))];
		if (options.FindingsFile is not null)
		{
			File.WriteAllLines(options.FindingsFile, remaining);
		}

		Report(results, options.Json);
		return results.All(static result => result.IsClean) ? 0 : 1;
	}

	private static Result Check(FileList list, bool gitleaks, HashSet<string> ignored, List<string> remaining)
	{
		using Gitleaks? scan = gitleaks ? Gitleaks.Start() : null;
		Summary summary = Redact(list, scan);
		(Dictionary<string, int> findings, int ignoredCount) = Sort(list.Name, scan?.Finish() ?? [], ignored, remaining);
		return new Result(summary.Files, summary.Changed, summary.Refused, summary.Skipped, summary.CredentialNames, scan?.Unscanned ?? 0, summary.Counts, summary.FilesByRule, gitleaks, findings, ignoredCount);
	}

	/// <summary>Counts a finding the ignore file lists as ignored, and every other one by rule, keeping its fingerprint for the findings file.</summary>
	private static (Dictionary<string, int> Findings, int Ignored) Sort(string list, List<(string Rule, string? Document, int Line)> found, HashSet<string> ignored, List<string> remaining)
	{
		Dictionary<string, int> findings = [];
		int ignoredCount = 0;
		foreach ((string rule, string? document, int line) in found)
		{
			string? fingerprint = document is null ? null : Fingerprints.Of(list, document, rule, line);
			if (fingerprint is not null && ignored.Contains(fingerprint))
			{
				ignoredCount++;
				continue;
			}

			findings[rule] = findings.GetValueOrDefault(rule) + 1;
			remaining.Add(fingerprint ?? "# a finding outside any document, which cannot be ignored: " + rule);
		}

		return (findings, ignoredCount);
	}

	private static Summary Redact(FileList list, Gitleaks? scan)
	{
		Redactor redactor = new();
		Summary summary = new();
		foreach (string file in list.Files())
		{
			summary.Files++;
			summary.CredentialNames += Redactor.HoldsCredential(list.NameOf(file)) ? 1 : 0;
			if (Read(file) is not { } text)
			{
				summary.Skipped++;
				continue;
			}

			try
			{
				RedactedMarkdown result = redactor.Redact(UnredactedMarkdown.FromText(text));
				summary.Add(result.CountsByRule);
				scan?.Write(result.Text, list.NameOf(file));
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
		Console.WriteLine($"file names that look like a credential, which a server refuses: {result.CredentialNames}");
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
		Console.WriteLine($"gitleaks findings listed in the ignore file: {result.GitleaksIgnored}");
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
	private readonly List<(int StartLine, string Name)> _starts = [];
	private int _documents;
	private int _nextLine = 2;
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

	/// <summary>
	/// Each document goes on after the last, with a line break. Its first line's number in the stream is kept, so a finding's line can
	/// be told as its document and its line there: the stream's own fingerprints, <c>:rule:line</c>, have no path, and shift whenever an
	/// earlier document changes, so they cannot say which finding a person reviewed.
	/// </summary>
	public void Write(string text, string name)
	{
		_documents++;
		_starts.Add((_nextLine, name));
		_nextLine += text.Count(static c => c == '\n') + 1;
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

	/// <summary>Each finding's rule, the document it starts in, and its line there. A finding before the first document has none.</summary>
	public List<(string Rule, string? Document, int Line)> Finish()
	{
		CloseInput();
		string report = _report.GetAwaiter().GetResult();
		_errors.GetAwaiter().GetResult();
		_process.WaitForExit();
		List<(string Rule, string? Document, int Line)>? findings = _process.ExitCode == 0 && !_failed ? Parse(report, _starts) : null;
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
	private static List<(string Rule, string? Document, int Line)>? Parse(string report, List<(int StartLine, string Name)> starts)
	{
		try
		{
			List<(string Rule, string? Document, int Line)> findings = [];
			using JsonDocument document = JsonDocument.Parse(report);
			foreach (JsonElement finding in document.RootElement.EnumerateArray())
			{
				string rule = finding.GetProperty("RuleID").GetString() ?? "unknown";
				int line = finding.GetProperty("StartLine").GetInt32();
				int index = Fingerprints.DocumentAt(starts, line);
				findings.Add(index < 0 ? (rule, null, 0) : (rule, starts[index].Name, line - starts[index].StartLine + 1));
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

/// <summary>gitleaks's fingerprint for a finding in a file, <c>path:rule:line</c>, with the path led by the list's name, and reading a .gitleaksignore of them.</summary>
internal static class Fingerprints
{
	public static string Of(string list, string document, string rule, int line)
	{
		return $"{list}/{document}:{rule}:{line}";
	}

	/// <summary>Every line but blanks and '#' comments, trimmed, as gitleaks reads the file.</summary>
	public static HashSet<string> Read(string path)
	{
		return [.. File.ReadAllLines(path).Select(static line => line.Trim()).Where(static line => line.Length > 0 && line[0] != '#')];
	}

	/// <summary>The index of the last document starting at or before the line, or -1 when it is before them all.</summary>
	public static int DocumentAt(List<(int StartLine, string Name)> starts, int line)
	{
		int low = 0, high = starts.Count - 1, found = -1;
		while (low <= high)
		{
			int middle = (low + high) / 2;
			if (starts[middle].StartLine <= line)
			{
				found = middle;
				low = middle + 1;
			}
			else
			{
				high = middle - 1;
			}
		}

		return found;
	}
}

internal sealed record FileList(string? Root, string? Directory, IReadOnlyList<string>? Paths, string Name)
{
	/// <summary>The path as a server would see it, relative to the collection.</summary>
	public string NameOf(string file)
	{
		string? root = Root ?? Directory;
		return (root is null ? file : Path.GetRelativePath(root, file)).Replace('\\', '/');
	}

	public IEnumerable<string> Files()
	{
		return Paths ?? System.IO.Directory.EnumerateFiles(Directory!, "*.md", SearchOption.AllDirectories);
	}
}

internal sealed record Options(bool Json, bool Gitleaks, List<FileList> Lists, string? IgnoreFile, string? FindingsFile)
{
	public static Options? Parse(string[] args)
	{
		Parsing parsing = new();
		for (int i = 0; i < args.Length && !parsing.Bad; i++)
		{
			parsing.Take(args, ref i);
		}

		return parsing.Build();
	}

	/// <summary>The options as they are read, one argument at a time.</summary>
	private sealed class Parsing
	{
		private readonly List<FileList> _lists = [];
		private bool _json;
		private bool _gitleaks = true;
		private string? _ignore;
		private string? _findings;

		public bool Bad { get; private set; }

		public void Take(string[] args, ref int i)
		{
			if (ValueOf(args, ref i, "--gitleaks-ignore") is { } ignore)
			{
				_ignore = ignore;
				Bad |= !File.Exists(ignore);
			}
			else if (ValueOf(args, ref i, "--gitleaks-findings") is { } findings)
			{
				_findings = findings;
				Bad |= findings.Length == 0;
			}
			else
			{
				TakeFlagOrList(args[i]);
			}
		}

		private void TakeFlagOrList(string arg)
		{
			switch (arg)
			{
				case "--json":
					_json = true;
					break;
				case "--no-gitleaks":
					_gitleaks = false;
					break;
				case "--lists-on-stdin":
					_lists.AddRange(ReadLists());
					break;
				default:
					Bad |= !Directory.Exists(arg);
					_lists.Add(new FileList(null, arg, null, Path.GetFileName(Path.TrimEndingDirectorySeparator(arg))));
					break;
			}
		}

		/// <summary>Ignoring or listing findings means nothing without gitleaks, and a typo must not pass as a clean run.</summary>
		public Options? Build()
		{
			bool valid = !Bad && _lists.Count > 0 && (_gitleaks || (_ignore is null && _findings is null));
			return valid ? new Options(_json, _gitleaks, _lists, _ignore, _findings) : null;
		}
	}

	/// <summary>The value after an option that takes one, moving past it, or null when this is not that option. An option with no value is a typo.</summary>
	private static string? ValueOf(string[] args, ref int i, string option)
	{
		if (args[i] != option)
		{
			return null;
		}

		i++;
		return i < args.Length ? args[i] : "";
	}

	/// <summary>From the raw stream, as Console.In would decode by the console's code page rather than UTF-8.</summary>
	private static IEnumerable<FileList> ReadLists()
	{
		using Stream input = Console.OpenStandardInput();
		List<ListOnStdin> lists = JsonSerializer.Deserialize<List<ListOnStdin>>(input, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
		return lists.Select(static (list, index) => new FileList(list.Root, null, list.Paths, list.Name ?? $"list-{index + 1}"));
	}
}

internal sealed record ListOnStdin(string? Root, List<string> Paths, string? Name);

internal sealed record Result(
	int Files,
	int Changed,
	int Refused,
	int Skipped,
	int CredentialNames,
	int GitleaksUnscanned,
	Dictionary<string, int> Replacements,
	Dictionary<string, int> FilesByRule,
	bool GitleaksRan,
	Dictionary<string, int> GitleaksFindings,
	int GitleaksIgnored)
{
	[JsonIgnore]
	public bool IsClean => GitleaksFindings.Count == 0 && GitleaksUnscanned == 0 && CredentialNames == 0 && Refused == 0 && Skipped == 0;
}

internal sealed class Summary
{
	public int Files { get; set; }

	public int Changed { get; private set; }

	public int Refused { get; set; }

	public int Skipped { get; set; }

	public int CredentialNames { get; set; }

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
