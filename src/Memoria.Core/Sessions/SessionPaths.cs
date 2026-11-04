using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Memoria.Core.Sessions;

/// <summary>
/// File names for rendered sessions, flat in the collection root. A hostile session id must not
/// be able to escape the collection or fail the sidecar's path validator: an id with anything
/// outside <c>[A-Za-z0-9._-]</c> is replaced by a digest, so it can neither inject a separator nor
/// collide with an id that merely sanitizes to the same text. The project is never part of a path.
/// </summary>
public static partial class SessionPaths
{
	private const int MaxSegmentChars = 64;

	/// <summary>The length of <c>yyyy-MM-dd_</c>.</summary>
	private const int DatePrefixChars = 11;

	/// <summary>
	/// Reduces any string to one path segment that is safe on every platform and is the same for
	/// the same input. A value already in that form is returned unchanged.
	/// </summary>
	public static string SafeSegment(string value)
	{
		ArgumentNullException.ThrowIfNull(value);
		if (IsSafeSegment(value))
		{
			return value;
		}
		string readable = UnsafeRun().Replace(value, "-").Trim('.');
		string head = readable[..Math.Min(readable.Length, MaxSegmentChars / 2)];
		return head.Length == 0 ? Digest(value) : $"{head}-{Digest(value)}";
	}

	/// <summary>The first eight characters of a clean id, as the exporter uses, otherwise a digest.</summary>
	internal static string ClaudeShortId(string sessionId)
	{
		return SafeOnly().IsMatch(sessionId) ? sessionId[..Math.Min(sessionId.Length, 8)] : Digest(sessionId);
	}

	internal static string ClaudePart(string date, string shortId, int part)
	{
		return part == 1 ? $"{date}_{shortId}.md" : $"{date}_{shortId}_part-{part:D3}.md";
	}

	internal static string CodexPart(string date, string safeId, int part)
	{
		return $"{date}_codex_{safeId}_chunk-{part:D5}.md";
	}

	internal static bool IsClaudeFamily(string path, string shortId)
	{
		return IsFamily(path, shortId, ClaudeSuffix());
	}

	internal static bool IsCodexFamily(string path, string safeId)
	{
		return IsFamily(path, $"codex_{safeId}", CodexSuffix());
	}

	private static bool IsFamily(string path, string stem, Regex suffix)
	{
		return DatePrefix().IsMatch(path)
			&& path.AsSpan(DatePrefixChars).StartsWith(stem, StringComparison.Ordinal)
			&& suffix.IsMatch(path.AsSpan(DatePrefixChars + stem.Length));
	}

	private static bool IsSafeSegment(string value)
	{
		return value.Length is > 0 and <= MaxSegmentChars
			&& SafeOnly().IsMatch(value)
			&& value[0] != '.'
			&& value[^1] != '.';
	}

	internal static string Digest(string value)
	{
		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
	}

	[GeneratedRegex(@"^[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
	private static partial Regex SafeOnly();

	[GeneratedRegex("[^A-Za-z0-9._-]+", RegexOptions.CultureInvariant)]
	private static partial Regex UnsafeRun();

	[GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}_", RegexOptions.CultureInvariant)]
	private static partial Regex DatePrefix();

	[GeneratedRegex(@"^(?:_part-[0-9]{3,})?\.md\z", RegexOptions.CultureInvariant)]
	private static partial Regex ClaudeSuffix();

	[GeneratedRegex(@"^_chunk-[0-9]{5,}\.md\z", RegexOptions.CultureInvariant)]
	private static partial Regex CodexSuffix();
}
