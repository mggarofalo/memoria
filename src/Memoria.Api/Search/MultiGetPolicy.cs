using System.Text.RegularExpressions;

namespace Memoria.Api.Search;

/// <summary>
/// What a caller may send to the batch document route, enforced before anything
/// reaches the sidecar. QMD compiles a glob with picomatch and runs it over every
/// path on the sidecar's single event loop, so one crafted pattern can wedge it
/// until it restarts. The grammar is an allowlist: <c>*</c>, <c>?</c> and
/// whole-segment <c>**</c>, nothing else. Braces are refused because expansion can
/// be exponential, brackets because nothing here needs them. The sidecar enforces
/// the same rules, since it is the sink; <c>sidecar/test/glob-policy-cases.json</c>
/// pins the two together.
/// </summary>
public static partial class MultiGetPolicy
{
	public const int MaxGlobLength = 256;

	public const int MaxGlobWildcards = 12;

	public const int MaxGlobstars = 2;

	public const int MaxGlobsPerRequest = 10;

	public const int MaxReferenceLength = 1024;

	public const int MaxReferencesPerRequest = 200;

	/// <summary>Largest <c>maxBytes</c> a caller may ask for, per document: 1 MiB. Longer documents can be read a range at a time.</summary>
	public const int MaxBytesCeiling = 1_048_576;

	/// <summary>Returns one message per offending field. Empty means the request may go to the sidecar.</summary>
	public static Dictionary<string, string[]> Validate(IReadOnlyList<string>? ids, string? pattern, int? maxBytes, int? maxLines)
	{
		Dictionary<string, string[]> errors = [];
		bool hasIds = ids is { Count: > 0 };
		bool hasPattern = !string.IsNullOrWhiteSpace(pattern);

		if (!hasIds && !hasPattern)
		{
			errors["ids"] = ["Provide `ids`, `pattern`, or both."];
		}

		if (ids is { Count: > MaxReferencesPerRequest })
		{
			errors["ids"] = [$"At most {MaxReferencesPerRequest} ids per request."];
		}
		else if (ids is not null && ids.Any(static id => id is not null && id.Contains(',', StringComparison.Ordinal)))
		{
			errors["ids"] = ["An id may not contain a comma."];
		}

		IEnumerable<string> fromPattern = hasPattern ? pattern!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
		IEnumerable<string> fromIds = ids?.Where(static id => !string.IsNullOrWhiteSpace(id)).Select(static id => id.Trim()) ?? [];
		string[] selectors = fromPattern.Concat(fromIds).Distinct(StringComparer.Ordinal).ToArray();
		string[] globs = selectors.Where(static s => GlobMeta().IsMatch(s)).ToArray();
		string[] references = selectors.Where(static s => !GlobMeta().IsMatch(s)).ToArray();

		string? violation = globs.Select(GlobViolation).FirstOrDefault(static v => v is not null);
		if (violation is not null)
		{
			errors[hasPattern && !errors.ContainsKey("pattern") ? "pattern" : "ids"] = [violation];
		}
		else if (globs.Length > MaxGlobsPerRequest)
		{
			errors["pattern"] = [$"At most {MaxGlobsPerRequest} globs per request."];
		}

		if (references.Length > MaxReferencesPerRequest)
		{
			errors["pattern"] = [$"At most {MaxReferencesPerRequest} references per request."];
		}
		else if (references.Any(static r => r.Length > MaxReferenceLength))
		{
			errors["ids"] = [$"A reference may be at most {MaxReferenceLength} characters."];
		}

		if (maxBytes is < 1 or > MaxBytesCeiling)
		{
			errors["maxBytes"] = [$"maxBytes must be between 1 and {MaxBytesCeiling}."];
		}

		if (maxLines is < 1)
		{
			errors["maxLines"] = ["maxLines must be at least 1."];
		}

		return errors;
	}

	/// <summary>Why a glob is refused, or null when it is within the grammar.</summary>
	public static string? GlobViolation(string glob)
	{
		ArgumentNullException.ThrowIfNull(glob);

		if (glob.Length > MaxGlobLength)
		{
			return $"A glob may be at most {MaxGlobLength} characters.";
		}

		if (!GlobChars().IsMatch(glob))
		{
			return "A glob may contain only letters, digits, space, '.', '_', '-', '/', ':', '*' and '?'. Braces, brackets, parentheses and extglobs are refused.";
		}

		if (glob.Contains("*?", StringComparison.Ordinal) || glob.Contains("?*", StringComparison.Ordinal))
		{
			return "A glob may not put '*' next to '?'.";
		}

		string[] segments = glob.Split('/');
		if (segments.Any(static s => s.Contains("**", StringComparison.Ordinal) && s != "**"))
		{
			return "'**' must be a whole path segment.";
		}

		if (segments.Count(static s => s == "**") > MaxGlobstars)
		{
			return $"A glob may contain at most {MaxGlobstars} '**' segments.";
		}

		int wildcards = glob.Replace("**", "*", StringComparison.Ordinal).Count(static c => c is '*' or '?');
		return wildcards > MaxGlobWildcards ? $"A glob may contain at most {MaxGlobWildcards} wildcards." : null;
	}

	/// <summary>Any of these marks a selector as a glob, which must then pass the grammar.</summary>
	[GeneratedRegex(@"[*?{}\[\]()|!^$\\]", RegexOptions.CultureInvariant)]
	private static partial Regex GlobMeta();

	[GeneratedRegex(@"\A[\p{L}\p{N}._\-/:*? ]+\z", RegexOptions.CultureInvariant)]
	private static partial Regex GlobChars();
}
