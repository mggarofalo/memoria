using Memoria.Core.Sessions;
using static Memoria.Core.Tests.Sessions.SessionTestKit;

namespace Memoria.Core.Tests.Sessions;

/// <summary>Exact limits and exact counts: the off-by-one and wrong-branch cases mutation testing looks for.</summary>
public sealed class SessionBoundaryTests
{
	[Fact]
	public async Task A_retained_touched_file_is_not_also_counted_as_dropped()
	{
		string snapshot = "{\"type\":\"file-history-snapshot\",\"snapshot\":{\"trackedFileBackups\":{\"a.txt\":{}}}}\n";

		UnredactedSession session = await Claude(ClaudeLine("user", "x") + snapshot);

		Assert.Equal(0, session.Report.FilesOverLimit);
		Assert.False(session.Report.HasSkippedContent);
		Assert.Equal(["a.txt"], Frontmatter(session.Documents[0].Text()).Sequence("files_touched"));
	}

	[Theory]
	[InlineData("session_meta")]
	[InlineData("turn_context")]
	[InlineData("event_msg")]
	[InlineData("compacted")]
	[InlineData("response_item")]
	public async Task Each_known_codex_type_is_not_counted_as_unrendered(string type)
	{
		Assert.Equal(0, (await Codex(CodexRecord(type, new Dictionary<string, string>()))).Report.UnrenderedRecords);
	}

	[Fact]
	public async Task A_codex_record_with_no_type_is_counted_as_unrendered()
	{
		Assert.Equal(1, (await Codex("{\"payload\":{}}\n")).Report.UnrenderedRecords);
	}

	[Fact]
	public async Task Only_a_response_item_message_adds_a_codex_message()
	{
		Dictionary<string, object> message = new() { ["type"] = "message", ["role"] = "user", ["content"] = "x" };
		Dictionary<string, object> toolCall = new() { ["type"] = "function_call", ["role"] = "user", ["content"] = "x" };

		Assert.Equal(1, (await Codex(CodexRecord("response_item", message))).Report.Messages);
		Assert.Equal(0, (await Codex(CodexRecord("response_item", toolCall))).Report.Messages);
		Assert.Equal(0, (await Codex(CodexRecord("compacted", message))).Report.Messages);
		Assert.Equal(0, (await Codex(CodexRecord("turn_context", message))).Report.Messages);
	}

	[Theory]
	[InlineData("input_text", 1)]
	[InlineData("output_text", 1)]
	[InlineData("text", 1)]
	[InlineData("image", 0)]
	[InlineData("", 0)]
	public async Task Only_codex_text_blocks_are_kept(string blockType, int messages)
	{
		Dictionary<string, object> payload = new()
		{
			["type"] = "message",
			["role"] = "assistant",
			["content"] = new object[] { new Dictionary<string, string> { ["type"] = blockType, ["text"] = "x" } },
		};

		Assert.Equal(messages, (await Codex(CodexRecord("response_item", payload))).Report.Messages);
	}

	[Fact]
	public async Task A_valid_timestamp_is_not_counted_as_invalid()
	{
		Assert.Equal(0, (await Claude(ClaudeLine("user", "x"))).Report.InvalidTimestamps);
	}

	[Fact]
	public async Task The_budget_admits_exactly_its_limit()
	{
		// A message costs its text plus an overhead of 64.
		string line = ClaudeLine("user", "abc");

		Assert.Equal(1, (await Claude(line, new SessionRenderOptions { MaxRetainedChars = 67 })).Report.Messages);
		Assert.Equal(1, (await Claude(line, new SessionRenderOptions { MaxRetainedChars = 66 })).Report.MessagesOverLimit);
	}

	[Fact]
	public async Task A_limit_of_one_is_allowed()
	{
		string line = ClaudeLine("user", "x");

		Assert.Single((await Claude(line, new SessionRenderOptions { MaxPartChars = 1 })).Documents);
		Assert.Equal(1, (await Claude(line, new SessionRenderOptions { MaxLineBytes = 1 })).Report.OversizedLines);
		Assert.Equal(1, (await Claude(line, new SessionRenderOptions { MaxRetainedChars = 1 })).Report.MessagesOverLimit);
	}

	[Fact]
	public async Task An_unterminated_tail_counts_as_a_line()
	{
		SessionReadReport report = (await Claude(ClaudeLine("user", "x") + "{\"type\":\"us")).Report;

		Assert.Equal(2, report.Lines);
		Assert.Equal(1, report.UnterminatedLines);
		Assert.Equal(0, report.MalformedLines);
	}

	[Fact]
	public async Task A_trailing_newline_leaves_no_phantom_line()
	{
		SessionReadReport report = (await Claude(ClaudeLine("user", "x"))).Report;

		Assert.Equal(1, report.Lines);
		Assert.Equal(0, report.UnterminatedLines);
		Assert.Equal(0, report.OversizedLines);
	}

	[Fact]
	public void A_leading_line_break_gives_an_empty_first_line()
	{
		Assert.Equal("", PythonText.FirstSplitLine("\nsecond"));
	}

	[Theory]
	[InlineData("/Source/demo/sub", "/", "demo")]
	[InlineData("/a/Source/demo/deep", "/z", "deep")]
	public void A_root_home_is_a_real_match_and_a_mismatch_is_no_match(string cwd, string home, string expected)
	{
		Assert.Equal(expected, SessionProject.FromClaudeCwd(cwd, home));
		Assert.Equal(expected, SessionProject.FromCodexCwd(cwd, home));
	}

	[Theory]
	[InlineData("/home/me/Source/demo/sub", "demo")]
	[InlineData("/Users/me/Source/demo/sub", "demo")]
	[InlineData("/Users/me/Documents/notes/deep", "deep")]
	[InlineData("/Users/me/SOURCE/demo/sub", "sub")]
	[InlineData("c:/users/ME/SOURCE/demo/sub", "demo")]
	public void Home_inference_and_case_rules_decide_the_project(string cwd, string expected)
	{
		Assert.Equal(expected, SessionProject.FromClaudeCwd(cwd, null));
	}

	[Fact]
	public async Task Refusals_say_what_is_wrong()
	{
		ArgumentException format = await Assert.ThrowsAsync<ArgumentException>(() => Render("", new SessionSource((SessionFormat)99, "id")));
		ArgumentException id = await Assert.ThrowsAsync<ArgumentException>(() => Claude("", sessionId: " "));
		ArgumentException limits = await Assert.ThrowsAsync<ArgumentException>(() => Claude("", new SessionRenderOptions { MaxLineBytes = 0 }));
		ArgumentException envelope = Assert.Throws<ArgumentException>(() => new DocumentEnvelope("", "").Render(""));

		Assert.StartsWith("Unknown session format.", format.Message);
		Assert.StartsWith("A session id is required.", id.Message);
		Assert.StartsWith("Render limits must be positive.", limits.Message);
		Assert.StartsWith("An envelope requires a source and a source id.", envelope.Message);
	}

	[Fact]
	public void Serialization_refusals_say_what_to_do()
	{
		NotSupportedException write = Assert.Throws<NotSupportedException>(() => System.Text.Json.JsonSerializer.Serialize(new UnredactedMarkdown("x")));
		NotSupportedException read = Assert.Throws<NotSupportedException>(() => System.Text.Json.JsonSerializer.Deserialize<UnredactedMarkdown>("\"x\""));

		Assert.Contains("redact it first", write.Message);
		Assert.Contains("cannot be deserialized", read.Message);
	}
}
