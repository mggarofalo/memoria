using System.Text;
using System.Text.Json;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;
using Memoria.Core.Sidecar;
using static Memoria.Core.Tests.Sessions.SessionTestKit;

namespace Memoria.Core.Tests.Redaction;

/// <summary>The one way past the redactor stays narrow, and says what it did.</summary>
public sealed class RedactionDecisionTests
{
	private static RedactionDisabledCollection Off(string collection = "open")
	{
		return RedactionDisabledCollection.FromOperatorSetting(collection, RedactionDisabledCollection.SettingValue)!;
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("disabled")]
	[InlineData("DISABLED")]
	[InlineData(" Disabled")]
	[InlineData("Disabled ")]
	[InlineData("Off")]
	[InlineData("Disabled,Enabled")]
	public void Only_the_exact_value_disables(string? value)
	{
		Assert.Null(RedactionDisabledCollection.FromOperatorSetting("open", value));
	}

	[Fact]
	public void The_exact_value_disables_that_collection()
	{
		Assert.Equal("open", Off().Collection);
	}

	[Fact]
	public void A_collection_name_is_required()
	{
		Assert.Throws<ArgumentException>(() => RedactionDisabledCollection.FromOperatorSetting(" ", RedactionDisabledCollection.SettingValue));
	}

	[Fact]
	public void There_is_no_public_way_to_build_the_setting_or_the_markdown_except_the_named_ones()
	{
		Assert.Empty(typeof(RedactionDisabledCollection).GetConstructors());
		Assert.Empty(typeof(RedactedMarkdown).GetConstructors());
		string[] factories = [.. typeof(RedactedMarkdown).GetMethods().Where(static m => m.IsStatic && m.ReturnType == typeof(RedactedMarkdown)).Select(static m => m.Name)];
		Assert.Equal([nameof(RedactedMarkdown.PassThroughForDisabledCollection)], factories);
	}

	[Fact]
	public void Redacted_text_does_not_claim_to_be_passed_through_and_passed_through_text_does()
	{
		RedactedMarkdown redacted = new Redactor().Redact(new DocumentEnvelope("s", "i").Render("body"));
		RedactedMarkdown passed = new DocumentEnvelope("s", "i").RenderWithoutRedactionForDisabledCollection("body", Off());

		Assert.Null(redacted.PassThroughCollection);
		Assert.Equal("open", passed.PassThroughCollection);
		Assert.Empty(passed.CountsByRule);
	}

	[Fact]
	public void An_envelope_passed_through_keeps_its_text_and_still_cannot_be_injected()
	{
		string token = Synthetic.GitHubClassic('p');

		string text = new DocumentEnvelope("s", "i", Title: "a\"\nforged: true " + token).RenderWithoutRedactionForDisabledCollection("body " + token, Off()).Text;

		Assert.Contains("body " + token, text, StringComparison.Ordinal);
		string[] frontmatter = text.Split('\n')[1..Array.IndexOf(text.Split('\n'), "---", 1)];
		Assert.DoesNotContain(frontmatter, static line => line.StartsWith("forged", StringComparison.Ordinal));
		Assert.Contains(frontmatter, line => line.Contains(token, StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_transcript_passed_through_keeps_its_text_and_its_part_is_unchecked()
	{
		string token = Synthetic.GitHubClassic('p');
		string jsonl = ClaudeLine("user", "key " + token, branch: token);
		SessionSource source = new(SessionFormat.ClaudeCode, Sessions.SessionTestKit.ClaudeId);

		RedactedSession session = await SessionRenderer.RenderWithoutRedactionForDisabledCollectionAsync(new MemoryStream(Encoding.UTF8.GetBytes(jsonl)), source, Off());

		RedactedSessionDocument document = Assert.Single(session.Documents);
		Assert.Contains("key " + token, document.Text(), StringComparison.Ordinal);
		Assert.Equal("open", document.Content.PassThroughCollection);
		Assert.Empty(session.CountsByRule);
		Assert.Empty(session.Refusals);
	}

	[Fact]
	public async Task A_codex_transcript_passed_through_has_the_same_paths_even_for_an_id_holding_a_credential()
	{
		string token = Synthetic.GitHubClassic('p');
		string jsonl = CodexMeta() + CodexMessage("user", "hello");
		SessionSource source = new(SessionFormat.Codex, token);

		RedactedSession on = await SessionRenderer.RenderAsync(new MemoryStream(Encoding.UTF8.GetBytes(jsonl)), source);
		RedactedSession off = await SessionRenderer.RenderWithoutRedactionForDisabledCollectionAsync(new MemoryStream(Encoding.UTF8.GetBytes(jsonl)), source, Off());

		Assert.Equal(on.Documents[0].Path, off.Documents[0].Path);
		Assert.DoesNotContain(token, off.Documents[0].Path, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Text_passed_through_for_one_collection_cannot_be_sent_to_another()
	{
		using HttpClient http = new(new NeverCalled()) { BaseAddress = new Uri("http://sidecar.test/") };
		MemoriaQmdClient client = new(http);
		RedactedMarkdown passed = new DocumentEnvelope("s", "i").RenderWithoutRedactionForDisabledCollection("body", Off("open"));

		await Assert.ThrowsAsync<InvalidOperationException>(() => client.IngestAsync(new SidecarIngestRequest("closed", "a.md", passed)));
	}

	[Fact]
	public void Reading_one_back_would_forge_the_type_and_is_refused()
	{
		Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<RedactedMarkdown>("\"text\""));
	}

	private sealed class NeverCalled : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			throw new HttpRequestException("the request should not have been sent");
		}
	}
}
