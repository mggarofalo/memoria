using System.Text.RegularExpressions;

namespace Memoria.Core.Sessions;

/// <summary>
/// Derives the project name. The exporters compare against the local home directory, which a
/// server does not share with the transcript's machine, so here the path is split lexically on
/// both separators and home is the caller's value or is inferred from the path's shape.
/// </summary>
internal static partial class SessionProject
{
	private const string Home = "Home";

	private static readonly string[] s_sourceSuffixes = ["-Source", "-source"];

	/// <summary><c>~/Source/&lt;project&gt;/...</c> gives the project, <c>~</c> gives Home, anything else the leaf.</summary>
	public static string FromClaudeCwd(string cwd, string? home)
	{
		(string[] segments, StringComparison comparison) = Split(cwd);
		int homeLength = HomeLength(segments, comparison, home);
		if (homeLength < 0)
		{
			return Leaf(segments);
		}
		if (segments.Length == homeLength)
		{
			return Home;
		}
		string first = segments[homeLength];
		return first.Equals("Source", comparison) || first.Equals("source", comparison)
			? ProjectUnderSource(segments, homeLength)
			: Leaf(segments);
	}

	/// <summary>Codex differs on purpose in the exporter: only <c>Source</c> is special, and <c>~</c> is its leaf.</summary>
	public static string FromCodexCwd(string cwd, string? home)
	{
		(string[] segments, StringComparison comparison) = Split(cwd);
		int homeLength = HomeLength(segments, comparison, home);
		return homeLength >= 0 && segments.Length > homeLength && segments[homeLength].Equals("Source", comparison)
			? ProjectUnderSource(segments, homeLength)
			: Leaf(segments);
	}

	/// <summary>The fallback for a session with no cwd: decode the encoded project directory name.</summary>
	public static string FromClaudeDirectory(string? directory, string? home)
	{
		if (string.IsNullOrEmpty(directory))
		{
			return "unknown";
		}
		string name = WorktreeSuffix().Replace(directory, "");
		return home is null ? DecodeInferred(name) : DecodeWithHome(name, NonAlphanumeric().Replace(home, "-"));
	}

	private static string DecodeWithHome(string name, string encodedHome)
	{
		if (name == encodedHome)
		{
			return Home;
		}
		foreach (string suffix in s_sourceSuffixes)
		{
			if (name == encodedHome + suffix)
			{
				return Home;
			}
			string prefix = $"{encodedHome}{suffix}-";
			if (name.StartsWith(prefix, StringComparison.Ordinal))
			{
				return name.Length > prefix.Length ? name[prefix.Length..] : Home;
			}
		}
		return name;
	}

	private static string DecodeInferred(string name)
	{
		if (EncodedHome().IsMatch(name))
		{
			return Home;
		}
		Match underSource = EncodedSource().Match(name);
		if (!underSource.Success)
		{
			return name;
		}
		return underSource.Groups[1].Length > 0 ? underSource.Groups[1].Value : Home;
	}

	private static (string[] Segments, StringComparison Comparison) Split(string path)
	{
		string[] segments = path
			.Replace('\\', '/')
			.Split('/', StringSplitOptions.RemoveEmptyEntries)
			.Where(static segment => segment != ".")
			.ToArray();
		return (segments, HasDrive(segments) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
	}

	private static bool HasDrive(string[] segments)
	{
		return segments.Length > 0 && segments[0].Length == 2 && segments[0][1] == ':' && char.IsAsciiLetter(segments[0][0]);
	}

	private static int HomeLength(string[] segments, StringComparison comparison, string? home)
	{
		return home is null ? InferredHomeLength(segments, comparison) : ExplicitHomeLength(segments, comparison, home);
	}

	private static int ExplicitHomeLength(string[] segments, StringComparison comparison, string home)
	{
		string[] homeSegments = Split(home).Segments;
		if (homeSegments.Length > segments.Length)
		{
			return -1;
		}
		for (int i = 0; i < homeSegments.Length; i++)
		{
			if (!segments[i].Equals(homeSegments[i], comparison))
			{
				return -1;
			}
		}
		return homeSegments.Length;
	}

	private static int InferredHomeLength(string[] segments, StringComparison comparison)
	{
		int start = HasDrive(segments) ? 1 : 0;
		if (start == 0 && segments.Length > 0 && segments[0] == "root")
		{
			return 1;
		}
		bool isHomeRoot = segments.Length > start + 1
			&& (segments[start].Equals("Users", comparison) || segments[start].Equals("home", comparison));
		return isHomeRoot ? start + 2 : -1;
	}

	private static string ProjectUnderSource(string[] segments, int homeLength)
	{
		return segments.Length > homeLength + 1 ? segments[homeLength + 1] : Home;
	}

	private static string Leaf(string[] segments)
	{
		return segments.Length == 0 || (segments.Length == 1 && HasDrive(segments)) ? Home : segments[^1];
	}

	[GeneratedRegex(@"--claude-worktrees-[\w-]+\z", RegexOptions.CultureInvariant)]
	private static partial Regex WorktreeSuffix();

	[GeneratedRegex("[^A-Za-z0-9]", RegexOptions.CultureInvariant)]
	private static partial Regex NonAlphanumeric();

	[GeneratedRegex(@"^(?:[A-Za-z]-)?-(?:Users|home)-[^-]+\z", RegexOptions.CultureInvariant)]
	private static partial Regex EncodedHome();

	[GeneratedRegex(@"^(?:[A-Za-z]-)?-(?:Users|home)-.+?-[Ss]ource(?:-(.*))?\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
	private static partial Regex EncodedSource();
}
