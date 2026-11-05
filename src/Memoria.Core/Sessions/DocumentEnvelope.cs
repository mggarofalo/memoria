using System.Globalization;
using System.Security.Cryptography;
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
	private const int MaxLabelChars = 32;

	/// <summary>128 bits: enough that no two identities share a path, even chosen on purpose.</summary>
	private const int DigestChars = 32;

	/// <summary>
	/// The document's collection-relative path, the same for the same source and id, so re-sending
	/// overwrites. The id never appears in it and only a digest stands for it, so nothing in it can
	/// escape the collection or carry a credential into a file name. A source redaction would change
	/// is left out too. The name is flat and lower-case, as QMD stores names, and cannot match a
	/// session's, which starts with a date and an underscore.
	/// </summary>
	/// <exception cref="ArgumentException">The source or source id is blank.</exception>
	public string StoragePath()
	{
		RequireIdentity();
		string source = Source.Trim();
		string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{source.Length}:{source}{SourceId.Trim()}")))[..DigestChars];
		string label = Redactor.Shared.RedactFragment(source).Text == source ? PathLabel(source) : "";
		return label.Length == 0 ? $"doc-{digest}.md" : $"{label}-{digest}.md";
	}

	/// <summary>Renders frontmatter followed by the body. The result still needs redaction, as both parts may hold secrets.</summary>
	/// <param name="body">The document's text.</param>
	/// <param name="fieldRedactions">When given, receives what redacting the fields replaced. The later pass over the document cannot see those, as they are already gone.</param>
	/// <exception cref="ArgumentException">The source or source id is blank.</exception>
	public UnredactedMarkdown Render(string body, ICollection<EnvelopeFieldRedaction>? fieldRedactions = null)
	{
		ArgumentNullException.ThrowIfNull(body);
		return new UnredactedMarkdown(RenderCore(body, (field, value, maxBytes) => RedactThenClip(field, value, maxBytes, fieldRedactions)));
	}

	/// <summary>
	/// The same document with its fields left as they came, already through the redaction decision: for the one
	/// collection <paramref name="disabled"/> names. Every other caller uses <see cref="Render"/>.
	/// </summary>
	public RedactedMarkdown RenderWithoutRedactionForDisabledCollection(string body, RedactionDisabledCollection disabled)
	{
		ArgumentNullException.ThrowIfNull(disabled);
		string text = RenderCore(body, static (_, value, maxBytes) => SessionFrontmatter.Clip(value, maxBytes: maxBytes));
		return RedactedMarkdown.PassThroughForDisabledCollection(new UnredactedMarkdown(text), disabled);
	}

	private string RenderCore(string body, Func<string, string, int, string> clean)
	{
		RequireIdentity();

		StringBuilder output = new();
		output.Append("---\n");
		WriteField(output, "source", Source, clean);
		WriteField(output, "source_id", SourceId, clean);
		WriteField(output, "url", Url, clean);
		WriteField(output, "title", Title, clean);
		if (Updated is { } updated)
		{
			WriteField(output, "updated", updated.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture), clean);
			WriteField(output, "date", updated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), clean);
		}
		WriteField(output, "project", Project, clean);
		SessionFrontmatter.Sequence(output, "tags", CleanTags(clean));
		output.Append("---\n\n").Append(body);
		return output.ToString();
	}

	private void RequireIdentity()
	{
		if (string.IsNullOrWhiteSpace(Source) || string.IsNullOrWhiteSpace(SourceId))
		{
			throw new ArgumentException("An envelope requires a source and a source id.");
		}
	}

	private static string PathLabel(string source)
	{
		StringBuilder label = new();
		foreach (char c in source.ToLowerInvariant())
		{
			label.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-');
		}

		string trimmed = label.ToString().Trim('-');
		return trimmed[..Math.Min(trimmed.Length, MaxLabelChars)].TrimEnd('-');
	}

	private static void WriteField(StringBuilder output, string key, string? value, Func<string, string, int, string> clean)
	{
		string? trimmed = value?.Trim();
		if (!string.IsNullOrEmpty(trimmed))
		{
			SessionFrontmatter.Scalar(output, key, clean(key, trimmed, MaxFieldBytes));
		}
	}

	/// <summary>Cutting first would leave a token's head too short for any rule to recognize, and the later pass over the document would miss it.</summary>
	private static string RedactThenClip(string field, string value, int maxBytes, ICollection<EnvelopeFieldRedaction>? redactions)
	{
		RedactionResult result = Redactor.Shared.RedactFragment(value);
		if (redactions is not null)
		{
			foreach ((string rule, int count) in result.CountsByRule)
			{
				redactions.Add(new EnvelopeFieldRedaction(field, rule, count));
			}
		}

		return SessionFrontmatter.Clip(result.Text, maxBytes: maxBytes);
	}

	private List<string> CleanTags(Func<string, string, int, string> clean)
	{
		return (Tags ?? [])
			.Where(static tag => !string.IsNullOrWhiteSpace(tag))
			.Select(tag => clean("tags", tag.Trim(), MaxTagBytes))
			.Take(MaxTags)
			.ToList();
	}
}

/// <summary>A frontmatter field that held something a rule replaced: the field and the rule, never the value.</summary>
public sealed record EnvelopeFieldRedaction(string Field, string Rule, int Count);
