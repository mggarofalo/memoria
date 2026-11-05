using System.Globalization;
using System.Text;
using Memoria.Core.Redaction;

namespace Memoria.Core.Sessions;

/// <summary>
/// The metadata a document carries into the corpus. Every value is treated as attacker-influenced:
/// values are quoted, cut to a limit, and can only ever be YAML values.
/// </summary>
/// <param name="Source">Where the document came from, for example <c>obsidian</c>. Required.</param>
/// <param name="SourceId">Its identity within that source. Required. Source and id together are the idempotency key.</param>
/// <param name="Url">A link back to the original, if there is one.</param>
/// <param name="Title">The document's title.</param>
/// <param name="Updated">When the original last changed. Also written as <c>date</c>, which date filters read.</param>
/// <param name="Project">The project, matched by the project filter.</param>
/// <param name="Tags">Tags, matched by the tags filter. Blank ones are dropped and order is kept.</param>
public sealed record DocumentEnvelope(
	string Source,
	string SourceId,
	string? Url = null,
	string? Title = null,
	DateTimeOffset? Updated = null,
	string? Project = null,
	IReadOnlyList<string>? Tags = null)
{
	private const int MaxFieldBytes = SessionFrontmatter.MaxFieldBytes;
	private const int MaxTags = 32;
	private const int MaxTagBytes = 64;

	/// <summary>Renders frontmatter followed by the body. The result still needs redaction, as both parts may hold secrets.</summary>
	/// <exception cref="ArgumentException">The source or source id is blank.</exception>
	public UnredactedMarkdown Render(string body)
	{
		ArgumentNullException.ThrowIfNull(body);
		if (string.IsNullOrWhiteSpace(Source) || string.IsNullOrWhiteSpace(SourceId))
		{
			throw new ArgumentException("An envelope requires a source and a source id.");
		}

		StringBuilder output = new();
		output.Append("---\n");
		WriteField(output, "source", Source);
		WriteField(output, "source_id", SourceId);
		WriteField(output, "url", Url);
		WriteField(output, "title", Title);
		if (Updated is { } updated)
		{
			WriteField(output, "updated", updated.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));
			WriteField(output, "date", updated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
		}
		WriteField(output, "project", Project);
		SessionFrontmatter.Sequence(output, "tags", CleanTags());
		output.Append("---\n\n").Append(body);
		return new UnredactedMarkdown(output.ToString());
	}

	private static void WriteField(StringBuilder output, string key, string? value)
	{
		string? trimmed = value?.Trim();
		if (!string.IsNullOrEmpty(trimmed))
		{
			SessionFrontmatter.Scalar(output, key, RedactThenClip(trimmed, MaxFieldBytes));
		}
	}

	/// <summary>Cutting first would leave a token's head too short for any rule to recognize, and the later pass over the document would miss it.</summary>
	private static string RedactThenClip(string value, int maxBytes)
	{
		return SessionFrontmatter.Clip(Redactor.Shared.RedactFragment(value).Text, maxBytes: maxBytes);
	}

	private List<string> CleanTags()
	{
		return (Tags ?? [])
			.Where(static tag => !string.IsNullOrWhiteSpace(tag))
			.Select(static tag => RedactThenClip(tag.Trim(), MaxTagBytes))
			.Take(MaxTags)
			.ToList();
	}
}
