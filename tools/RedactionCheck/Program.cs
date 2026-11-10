// Redacts markdown in memory, has gitleaks and trufflehog read the result on their standard input, and prints counts.
// Nothing else is ever printed: no value, no path, no line. Gitleaks runs with --redact=100 and only its rule ids are
// read; trufflehog runs with --no-verification, so it calls no service, and of its output only the detector's name is
// kept, and the matched text in memory, to find which document and line it came from. No copy of any text is written to
// disk, so there is nothing for a killed run to leave. Needs gitleaks and trufflehog on PATH, unless told --no-gitleaks or
// --no-trufflehog, and no network. Run it through
// scripts/redaction-report.sh or `memoria migrate report`, on a local machine: it reads real
// content, so it does not belong in CI.
//
// Each list of files is a directory, or with --lists-on-stdin a JSON array of { root, paths } on its standard
// input, so a list of names is never on disk. With --json it prints one line, an array with one result per
// list, in the order given.
//
// Gitleaks is best effort: it can miss a token at a chunk boundary, so 0 findings is not proof that nothing is left.
// Its rules are also the ones the redaction engine imports, so a 0 from it says mostly that the import is faithful, and
// covers only the few rules the engine leaves out. trufflehog's detectors are its own, so it is the independent look.
// A 0 from both is still not proof.
//
// --gitleaks-ignore <file> reads a .gitleaksignore of findings a person reviewed: one fingerprint a line, '#' for comments. A
// fingerprint is <list>/<path in it>:<rule>:<line>, gitleaks's own form for a file, with the line counted in the redacted
// document. A trufflehog finding's rule is trufflehog-<detector>, so one file lists both. Each line listed excuses one finding: it is counted as ignored and does not fail the run, while any other, even a
// second on the same line, still does, so two findings on one line need the fingerprint listed twice. --gitleaks-findings
// <file> writes the fingerprints of the findings that are not ignored, one a finding, for that review. It is deleted before
// the run starts, so a run that cannot finish leaves none from an earlier one. Both files hold paths, so keep them private.
//
// Exits 0 when clean, 1 when something was refused, found, skipped or not scanned, 2 on bad usage, 3 when it could not finish.
using System.Diagnostics;
using System.Text;
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
			Console.Error.WriteLine("usage: RedactionCheck [--json] [--no-gitleaks] [--no-trufflehog] [--gitleaks-ignore <file>] [--gitleaks-findings <file>] <directory of .md files> ... | --lists-on-stdin");
			return 2;
		}

		if (options.FindingsFile is not null)
		{
			File.Delete(options.FindingsFile);
		}

		Dictionary<string, int> ignored = options.IgnoreFile is null ? [] : Fingerprints.Read(options.IgnoreFile);
		List<string> remaining = [];
		List<Result> results = [.. options.Lists.Select(list => Check(list, options, ignored, remaining))];
		if (options.FindingsFile is not null)
		{
			File.WriteAllLines(options.FindingsFile, remaining);
		}

		Report(results, options.Json);
		return results.All(static result => result.IsClean) ? 0 : 1;
	}

	private static Result Check(FileList list, Options options, Dictionary<string, int> ignored, List<string> remaining)
	{
		using Gitleaks? scan = options.Gitleaks ? Gitleaks.Start() : null;
		using Trufflehog? second = options.Trufflehog ? Trufflehog.Start() : null;
		Summary summary = Redact(list, scan, second);
		(Dictionary<string, int> findings, int ignoredCount) = Sort(list.Name, scan?.Finish() ?? [], ignored, remaining);
		Result result = new(summary.Files, summary.Changed, summary.Refused, summary.Skipped, summary.CredentialNames, scan?.Unscanned ?? 0, summary.Counts, summary.FilesByRule, options.Gitleaks, findings, ignoredCount);
		return second is null ? result : WithTrufflehog(result, list, second, ignored, remaining);
	}

	private static Result WithTrufflehog(Result result, FileList list, Trufflehog second, Dictionary<string, int> ignored, List<string> remaining)
	{
		(Dictionary<string, int> findings, int ignoredCount) = Sort(list.Name, Locate(list, second.Finish()), ignored, remaining);
		return result with { TrufflehogRan = true, TrufflehogUnscanned = second.Unscanned, TrufflehogFindings = findings, TrufflehogIgnored = ignoredCount };
	}

	/// <summary>
	/// trufflehog says what it found and not where, so each distinct finding is looked for again in the redacted documents, and every
	/// place it appears is a finding of its own. One it found only in a decoded form, which the text does not hold as such, has no place.
	/// </summary>
	private static List<(string Rule, string? Document, int Line)> Locate(FileList list, List<(string Rule, string Text)> found)
	{
		List<(string Rule, string Text)> distinct = [.. found.Distinct()];
		if (distinct.Count == 0)
		{
			return [];
		}

		List<(string Rule, string? Document, int Line)> located = [];
		bool[] placed = new bool[distinct.Count];
		Redactor redactor = new();
		foreach (string file in list.Files())
		{
			if (RedactedText(redactor, file) is { } text)
			{
				located.AddRange(PlacesIn(text, list.NameOf(file), distinct, placed));
			}
		}

		located.AddRange(distinct.Where((_, index) => !placed[index]).Select(static finding => (finding.Rule, (string?)null, 0)));
		return located;
	}

	private static string? RedactedText(Redactor redactor, string file)
	{
		try
		{
			return Read(file) is { } text ? redactor.Redact(UnredactedMarkdown.FromText(text)).Text : null;
		}
		catch (RedactionException)
		{
			return null;
		}
	}

	/// <summary>Each line of the document where a finding's text starts, marking that finding placed.</summary>
	private static IEnumerable<(string Rule, string? Document, int Line)> PlacesIn(string text, string document, List<(string Rule, string Text)> findings, bool[] placed)
	{
		for (int i = 0; i < findings.Count; i++)
		{
			for (int at = text.IndexOf(findings[i].Text, StringComparison.Ordinal); at >= 0 && findings[i].Text.Length > 0; at = text.IndexOf(findings[i].Text, at + 1, StringComparison.Ordinal))
			{
				placed[i] = true;
				yield return (findings[i].Rule, document, text.AsSpan(0, at).Count('\n') + 1);
			}
		}
	}

	/// <summary>Counts a finding the ignore file lists as ignored, using up one listing, and every other one by rule, keeping its fingerprint for the findings file.</summary>
	private static (Dictionary<string, int> Findings, int Ignored) Sort(string list, List<(string Rule, string? Document, int Line)> found, Dictionary<string, int> ignored, List<string> remaining)
	{
		Dictionary<string, int> findings = [];
		int ignoredCount = 0;
		foreach ((string rule, string? document, int line) in found)
		{
			string? fingerprint = document is null ? null : Fingerprints.Of(list, document, rule, line);
			if (fingerprint is not null && ignored.GetValueOrDefault(fingerprint) > 0)
			{
				ignored[fingerprint]--;
				ignoredCount++;
				continue;
			}

			findings[rule] = findings.GetValueOrDefault(rule) + 1;
			remaining.Add(fingerprint ?? "# a finding outside any document, which cannot be ignored: " + rule);
		}

		return (findings, ignoredCount);
	}

	private static Summary Redact(FileList list, Gitleaks? scan, Trufflehog? second)
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
				second?.Write(result.Text);
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

		Console.WriteLine("Gitleaks's rules are also the redaction engine's, so its 0 says mostly that they were imported faithfully.");
		PrintTrufflehog(result);
	}

	private static void PrintTrufflehog(Result result)
	{
		if (!result.TrufflehogRan)
		{
			Console.WriteLine("trufflehog: NOT RUN. Gitleaks's rules are the engine's own, so without it nothing independent looked at what is left.");
			return;
		}

		Console.WriteLine($"trufflehog findings in redacted output: {result.TrufflehogFindings.Values.Sum()}. Its detectors are its own, unverified, so this is the independent look; 0 is still not proof.");
		Console.WriteLine($"documents trufflehog could not read: {result.TrufflehogUnscanned}");
		Console.WriteLine($"trufflehog findings listed in the ignore file: {result.TrufflehogIgnored}");
		foreach ((string rule, int count) in result.TrufflehogFindings.OrderByDescending(static pair => pair.Value))
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

/// <summary>
/// trufflehog reading one list's redacted text on its standard input, with verification off, so it calls no service. Its detectors are
/// its own rather than gitleaks's, which the redaction engine imports, so it is the independent look at what is left.
/// <para>
/// It keeps what it reads in memory up to 10 MB, and past that writes it to a temporary file, which a killed run would leave behind. So
/// it is given at most <see cref="BatchBytes"/> at a time, each batch to a process of its own, and a document longer than that goes in
/// overlapping pieces, so a token at a seam is seen whole in one of them. Where a finding is does not depend on the batches, as it is
/// found again in the documents.
/// </para>
/// <para>
/// It reads in chunks of at most 13 KB, and pairs a keyword with a value anywhere in one, so a keyword at the end of one document once
/// made a session id at the start of the next a finding. Blanks longer than a chunk keep each chunk to one document.
/// </para>
/// </summary>
internal sealed class Trufflehog : IDisposable
{
	/// <summary>Well under trufflehog's 10 MB, so nothing it reads goes to disk.</summary>
	internal const int BatchBytes = 8 * 1024 * 1024;

	/// <summary>Longer than any one token it finds, a private key's block included.</summary>
	internal const int OverlapChars = 64 * 1024;

	/// <summary>Blanks between documents, longer than the 13 KB it reads at a time.</summary>
	internal static readonly string Separator = new string(' ', 16 * 1024) + "\n";

	private readonly List<(string Rule, string Text)> _findings = [];
	private TrufflehogProcess? _batch;
	private readonly HashSet<int> _batchDocuments = [];
	private readonly HashSet<int> _unscanned = [];
	private int _documents;

	/// <summary>The documents in batches it did not read, as it stopped or gave nothing usable.</summary>
	public int Unscanned => _unscanned.Count;

	public static Trufflehog Start()
	{
		return new Trufflehog();
	}

	public void Write(string text)
	{
		// A document is noted in each batch it is in, so a batch that fails counts every document it did not read in full, each once.
		int document = _documents++;
		foreach (string piece in Pieces(text))
		{
			if (_batch is not null && _batch.Bytes + Encoding.UTF8.GetByteCount(piece) + 1 + Separator.Length > BatchBytes)
			{
				EndBatch();
			}

			_batch ??= TrufflehogProcess.Start();
			_batch.Write(piece);
			_batchDocuments.Add(document);
		}
	}

	/// <summary>The text, or for a long one, pieces of at most a batch each, each ending at a line break where one is near, and each after the first starting a little before the last ended.</summary>
	internal static List<string> Pieces(string text)
	{
		int room = BatchBytes - TrufflehogProcess.SniffedBytes - Separator.Length - 1;
		int limit = room / 4;
		if (Encoding.UTF8.GetByteCount(text) <= room)
		{
			return [text];
		}

		List<string> pieces = [];
		for (int from = 0; from < text.Length;)
		{
			int to = Math.Min(text.Length, from + limit);
			int cut = to == text.Length ? to : text.LastIndexOf('\n', to - 1, to - from - OverlapChars) + 1;
			to = cut > from + OverlapChars ? cut : to;
			pieces.Add(text[from..to]);
			from = to == text.Length ? to : to - OverlapChars;
		}

		return pieces;
	}

	private void EndBatch()
	{
		if (_batch is null)
		{
			return;
		}

		List<(string Rule, string Text)>? found = _batch.Finish();
		if (found is null)
		{
			_unscanned.UnionWith(_batchDocuments);
		}

		_findings.AddRange(found ?? []);
		_batch.Dispose();
		_batch = null;
		_batchDocuments.Clear();
	}

	/// <summary>Each finding's rule, <c>trufflehog-&lt;detector&gt;</c>, and the text it matched, which stays in memory and is never printed.</summary>
	public List<(string Rule, string Text)> Finish()
	{
		EndBatch();
		return _findings;
	}

	public void Dispose()
	{
		_batch?.Dispose();
	}
}

/// <summary>One trufflehog process, reading one batch on its standard input and reporting on its standard output.</summary>
internal sealed class TrufflehogProcess : IDisposable
{
	/// <summary>
	/// Like gitleaks, it looks at the start of its input to decide if it is binary, and then reports nothing while exiting 0. A Markdown
	/// file can start with %PDF, which it still took for one behind 512 blanks, so each batch starts with more than it looks at.
	/// </summary>
	internal const int SniffedBytes = 8192;

	private readonly Process _process;
	private readonly Task<string> _report;
	private readonly Task _errors;
	private bool _failed;

	private TrufflehogProcess(Process process)
	{
		_process = process;
		_report = process.StandardOutput.ReadToEndAsync();
		_errors = process.StandardError.ReadToEndAsync();
		Send(new string(' ', SniffedBytes) + "\n");
	}

	public long Bytes { get; private set; }

	public static TrufflehogProcess Start()
	{
		ProcessStartInfo start = new("trufflehog")
		{
			// Its time limit covers reading all of its input, so the limit is one no batch reaches.
			ArgumentList = { "stdin", "--no-verification", "--json", "--no-update", "--no-color", "--force-skip-archives", "--archive-timeout", "24h" },
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardInputEncoding = new UTF8Encoding(false),
			StandardOutputEncoding = Encoding.UTF8,
			UseShellExecute = false,
		};
		return new TrufflehogProcess(Process.Start(start) ?? throw new InvalidOperationException("trufflehog did not start."));
	}

	public void Write(string text)
	{
		Send(text + "\n" + Trufflehog.Separator);
	}

	/// <summary>It may stop reading at any time, and that must be counted rather than end the whole report.</summary>
	private void Send(string text)
	{
		Bytes += Encoding.UTF8.GetByteCount(text);
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

	/// <summary>Its findings, or null when it stopped or gave something other than its report.</summary>
	public List<(string Rule, string Text)>? Finish()
	{
		try
		{
			_process.StandardInput.Close();
		}
		catch (IOException)
		{
			_failed = true;
		}

		string report = _report.GetAwaiter().GetResult();
		_errors.GetAwaiter().GetResult();
		_process.WaitForExit();
		return _process.ExitCode == 0 && !_failed ? Parse(report) : null;
	}

	/// <summary>Null when it gave something other than one JSON result a line.</summary>
	private static List<(string Rule, string Text)>? Parse(string report)
	{
		try
		{
			List<(string Rule, string Text)> findings = [];
			foreach (string line in report.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				using JsonDocument result = JsonDocument.Parse(line);
				string detector = result.RootElement.GetProperty("DetectorName").GetString() ?? "unknown";
				string raw = result.RootElement.GetProperty("Raw").GetString() ?? "";
				findings.Add(("trufflehog-" + detector.ToLowerInvariant(), raw));
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

	/// <summary>Every line but blanks and '#' comments, trimmed, as gitleaks reads the file, with how many times each is listed.</summary>
	public static Dictionary<string, int> Read(string path)
	{
		return File.ReadAllLines(path)
			.Select(static line => line.Trim())
			.Where(static line => line.Length > 0 && line[0] != '#')
			.CountBy(static line => line, StringComparer.Ordinal)
			.ToDictionary(StringComparer.Ordinal);
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

internal sealed record Options(bool Json, bool Gitleaks, bool Trufflehog, List<FileList> Lists, string? IgnoreFile, string? FindingsFile)
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
		private bool _trufflehog = true;
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
				case "--no-trufflehog":
					_trufflehog = false;
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

		/// <summary>Ignoring or listing findings means nothing without a scanner, and a typo must not pass as a clean run.</summary>
		public Options? Build()
		{
			bool valid = !Bad && _lists.Count > 0 && (_gitleaks || _trufflehog || (_ignore is null && _findings is null));
			return valid ? new Options(_json, _gitleaks, _trufflehog, _lists, _ignore, _findings) : null;
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
	public bool TrufflehogRan { get; init; }

	/// <summary>The documents trufflehog was meant to read and did not, as it stopped or gave nothing usable.</summary>
	public int TrufflehogUnscanned { get; init; }

	public Dictionary<string, int> TrufflehogFindings { get; init; } = [];

	public int TrufflehogIgnored { get; init; }

	[JsonIgnore]
	public bool IsClean => GitleaksFindings.Count == 0 && GitleaksUnscanned == 0 && TrufflehogFindings.Count == 0 && TrufflehogUnscanned == 0 && CredentialNames == 0 && Refused == 0 && Skipped == 0;
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
