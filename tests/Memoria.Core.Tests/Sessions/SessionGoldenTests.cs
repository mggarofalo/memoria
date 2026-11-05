using System.Text.Json;
using Memoria.Core.Sessions;

namespace Memoria.Core.Tests.Sessions;

/// <summary>
/// Byte parity with the Python exporters. The goldens are their output on synthetic inputs,
/// produced by Fixtures/generate_goldens.py; nothing here came from a real transcript. Rendering
/// always redacts, so this also proves redaction changes nothing in non-secret content. Where a
/// session does hold a secret the output differs from the exporters by design: titles are cut from
/// redacted text, parts are sized by it, and <c>message_sha256</c> is computed over it.
/// </summary>
public sealed class SessionGoldenTests
{
	private static readonly string s_fixtures = Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures");

	public static TheoryData<string> CaseNames()
	{
		return new TheoryData<string>(LoadCases().Select(static testCase => testCase.GetProperty("name").GetString()!));
	}

	[Theory]
	[MemberData(nameof(CaseNames))]
	public async Task Output_matches_the_exporter_byte_for_byte(string name)
	{
		JsonElement testCase = LoadCases().Single(c => c.GetProperty("name").GetString() == name);
		RedactedSession session = await RenderCaseAsync(testCase, name);

		string[] expected = testCase.GetProperty("documents").EnumerateArray().Select(static d => d.GetString()!).ToArray();
		Assert.Equal(expected, session.Documents.Select(static document => document.Path));
		foreach (RedactedSessionDocument document in session.Documents)
		{
			byte[] golden = await File.ReadAllBytesAsync(Path.Combine(s_fixtures, "goldens", name, document.Path));
			Assert.Equal(golden, System.Text.Encoding.UTF8.GetBytes(document.Text()));
		}

		// The Claude exporter reports its malformed count, so the two must agree on what they skipped.
		if (testCase.GetProperty("malformed_lines").ValueKind == JsonValueKind.Number)
		{
			Assert.Equal(testCase.GetProperty("malformed_lines").GetInt64(), session.Report.MalformedLines);
		}
	}

	[Fact]
	public void The_goldens_cover_both_formats_and_part_splitting()
	{
		JsonElement[] cases = LoadCases();
		Assert.Contains(cases, static c => c.GetProperty("format").GetString() == "claude-code" && c.GetProperty("documents").GetArrayLength() >= 3);
		Assert.Contains(cases, static c => c.GetProperty("format").GetString() == "codex" && c.GetProperty("documents").GetArrayLength() >= 2);
	}

	[Fact]
	public async Task The_part_split_counts_code_points_not_utf16_units()
	{
		JsonElement testCase = LoadCases().Single(static c => c.GetProperty("name").GetString() == "claude-parts");
		RedactedSession session = await RenderCaseAsync(testCase, "claude-parts");

		// Part one holds 40000 As and 25000 rockets, which is 65025 code points but 90000 UTF-16 units.
		Assert.Equal(2, session.Documents[0].MessageCount);
		Assert.Equal(2, session.Documents[1].MessageStart);
		Assert.Equal(1, session.Documents[1].MessageCount);
	}

	private static async Task<RedactedSession> RenderCaseAsync(JsonElement testCase, string name)
	{
		SessionFormat format = testCase.GetProperty("format").GetString() == "claude-code" ? SessionFormat.ClaudeCode : SessionFormat.Codex;
		SessionSource source = new(format, testCase.GetProperty("session_id").GetString()!)
		{
			ProjectDirectory = testCase.GetProperty("project_dir").GetString(),
			FallbackStart = DateTimeOffset.FromUnixTimeSeconds(testCase.GetProperty("fallback_unix").GetInt64()),
		};
		await using FileStream input = File.OpenRead(Path.Combine(s_fixtures, "inputs", $"{name}.jsonl"));
		return await SessionRenderer.RenderAsync(input, source);
	}

	private static JsonElement[] LoadCases()
	{
		using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(s_fixtures, "cases.json")));
		return document.RootElement.EnumerateArray().Select(static element => element.Clone()).ToArray();
	}
}
