using System.Text;
using Memoria.Core.Sessions;
using static Memoria.Core.Tests.Sessions.SessionTestKit;

namespace Memoria.Core.Tests.Sessions;

/// <summary>Bad input is tolerated predictably, costs bounded memory, and is reported without its content.</summary>
public sealed class SessionReadToleranceTests
{
	private static readonly string s_good = ClaudeLine("user", "kept");

	[Fact]
	public async Task Each_kind_of_bad_line_is_counted_and_the_rest_is_kept()
	{
		string jsonl = "\n   \t\r\n{not json\n[1,2]\n\"text\"\n" + s_good + "{\"type\":\"future\"}\n" + ClaudeLine("assistant", "also kept");

		UnredactedSession session = await Claude(jsonl);

		Assert.Equal(8, session.Report.Lines);
		Assert.Equal(2, session.Report.BlankLines);
		Assert.Equal(1, session.Report.MalformedLines);
		Assert.Equal(2, session.Report.NonObjectLines);
		Assert.Equal(3, session.Report.Records);
		Assert.Equal(1, session.Report.UnrenderedRecords);
		Assert.Equal(2, session.Report.Messages);
		Assert.Equal(Encoding.UTF8.GetByteCount(jsonl), session.Report.BytesRead);
		Assert.True(session.Report.HasSkippedContent);
	}

	[Fact]
	public async Task A_clean_payload_reports_nothing_skipped_even_with_routine_unrendered_records()
	{
		UnredactedSession session = await Claude(s_good + "{\"type\":\"progress\"}\n");

		Assert.False(session.Report.HasSkippedContent);
		Assert.Equal(1, session.Report.UnrenderedRecords);
	}

	[Theory]
	[InlineData("malformed")]
	[InlineData("nonobject")]
	[InlineData("oversized")]
	[InlineData("unterminated")]
	[InlineData("invalidkey")]
	[InlineData("messagelimit")]
	[InlineData("filelimit")]
	public void HasSkippedContent_is_set_by_each_loss_on_its_own(string kind)
	{
		SessionReadReport report = new();
		switch (kind)
		{
			case "malformed": report.MalformedLines = 1; break;
			case "nonobject": report.NonObjectLines = 1; break;
			case "oversized": report.OversizedLines = 1; break;
			case "unterminated": report.UnterminatedLines = 1; break;
			case "invalidkey": report.InvalidKeys = 1; break;
			case "messagelimit": report.MessagesOverLimit = 1; break;
			default: report.FilesOverLimit = 1; break;
		}
		Assert.True(report.HasSkippedContent);
	}

	[Fact]
	public async Task The_report_exposes_only_counts_so_it_cannot_echo_content()
	{
		const string Secret = "sk-live-do-not-echo";
		UnredactedSession session = await Claude("{\"" + Secret + "\n{\"type\":\"" + Secret + "\"}\n");

		foreach (System.Reflection.PropertyInfo property in typeof(SessionReadReport).GetProperties())
		{
			Assert.True(property.PropertyType == typeof(long) || property.PropertyType == typeof(bool), property.Name);
		}
		Assert.Empty(session.Documents);
	}

	[Fact]
	public async Task A_final_line_without_a_newline_is_left_unread()
	{
		UnredactedSession session = await Claude(s_good + ClaudeLine("assistant", "complete but unterminated").TrimEnd('\n'));

		Assert.Equal(1, session.Report.UnterminatedLines);
		Assert.Equal(1, session.Report.Messages);
		Assert.DoesNotContain("unterminated", session.Documents[0].Text());
		Assert.True(session.Report.HasSkippedContent);
	}

	[Fact]
	public async Task A_byte_order_mark_does_not_lose_the_line()
	{
		UnredactedSession session = await Claude("\uFEFF" + s_good + "\uFEFF\n");

		Assert.Equal(1, session.Report.Messages);
		Assert.Equal(1, session.Report.BlankLines);
		Assert.Equal(0, session.Report.MalformedLines);
	}

	[Fact]
	public async Task Invalid_utf8_inside_a_string_is_a_malformed_line()
	{
		byte[] bad = [.. "{\"type\":\"user\",\"x\":\""u8, 0xFF, 0xFE, .. "\"}\n"u8];
		using MemoryStream stream = new([.. bad, .. Encoding.UTF8.GetBytes(s_good)]);

		UnredactedSession session = await SessionRenderer.RenderAsync(stream, new SessionSource(SessionFormat.ClaudeCode, ClaudeId));

		Assert.Equal(1, session.Report.MalformedLines);
		Assert.Equal(1, session.Report.Messages);
	}

	[Fact]
	public async Task Nesting_beyond_the_depth_limit_is_a_malformed_line_not_a_crash()
	{
		string deep = new string('[', 5000) + new string(']', 5000);

		UnredactedSession session = await Claude(deep + "\n" + s_good);

		Assert.Equal(1, session.Report.MalformedLines);
		Assert.Equal(1, session.Report.Messages);
	}

	[Fact]
	public async Task A_line_exactly_at_the_limit_is_read_and_one_byte_more_is_not()
	{
		int size = Encoding.UTF8.GetByteCount(s_good) - 1;
		SessionRenderOptions exact = new() { MaxLineBytes = size };
		SessionRenderOptions under = new() { MaxLineBytes = size - 1 };

		Assert.Equal(1, (await Claude(s_good, exact)).Report.Messages);
		SessionReadReport report = (await Claude(s_good + s_good, under)).Report;
		Assert.Equal(2, report.OversizedLines);
		Assert.Equal(0, report.Messages);
		Assert.Equal(2, report.Lines);
		Assert.True(report.HasSkippedContent);
	}

	[Fact]
	public async Task An_oversized_line_does_not_swallow_the_next_one()
	{
		SessionRenderOptions options = new() { MaxLineBytes = 200 };
		string jsonl = new string('x', 1000) + "\n" + s_good + new string('y', 1000);

		SessionReadReport report = (await Claude(jsonl, options)).Report;

		Assert.Equal(2, report.OversizedLines);
		Assert.Equal(1, report.Messages);
		Assert.Equal(0, report.UnterminatedLines);
		Assert.Equal(3, report.Lines);
	}

	[Fact]
	public async Task A_line_that_outgrows_the_initial_buffer_is_read_whole()
	{
		string text = new('z', 300_000);

		UnredactedSession session = await Claude(s_good + ClaudeLine("assistant", text) + ClaudeLine("user", "after"));

		Assert.Equal(3, session.Report.Messages);
		Assert.Contains(text, session.Documents.Single(d => d.Text().Contains('z')).Text());
		Assert.Contains("after", session.Documents[^1].Text());
	}

	[Fact]
	public async Task Lines_are_assembled_across_arbitrary_read_boundaries()
	{
		byte[] bytes = Encoding.UTF8.GetBytes(s_good + ClaudeLine("assistant", "caf\u00e9 \U0001F680"));
		using OneByteStream stream = new(bytes);

		UnredactedSession session = await SessionRenderer.RenderAsync(stream, new SessionSource(SessionFormat.ClaudeCode, ClaudeId));

		Assert.Equal(2, session.Report.Messages);
		Assert.Contains("caf\u00e9 \U0001F680", session.Documents[0].Text());
	}

	[Fact]
	public async Task A_huge_line_is_discarded_without_buffering_it()
	{
		const long HugeBytes = 256L * 1024 * 1024;
		SessionRenderOptions options = new() { MaxLineBytes = 1024 * 1024 };
		using RepeatingStream stream = new((byte)'x', HugeBytes, Encoding.UTF8.GetBytes("\n" + s_good));

		long before = GC.GetAllocatedBytesForCurrentThread();
		UnredactedSession session = await SessionRenderer.RenderAsync(stream, new SessionSource(SessionFormat.ClaudeCode, ClaudeId), options);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.Equal(1, session.Report.OversizedLines);
		Assert.Equal(1, session.Report.Messages);
		Assert.True(allocated < 32 * 1024 * 1024, $"allocated {allocated} bytes for a {HugeBytes} byte line");
	}

	[Fact]
	public async Task The_budget_keeps_a_true_prefix_and_counts_what_it_dropped()
	{
		SessionRenderOptions options = new() { MaxRetainedChars = 200 };
		string big = new('b', 200);
		string jsonl = ClaudeLine("user", "first") + ClaudeLine("assistant", big) + ClaudeLine("user", "tiny but after the cut");

		UnredactedSession session = await Claude(jsonl, options);

		Assert.Equal(1, session.Report.Messages);
		Assert.Equal(2, session.Report.MessagesOverLimit);
		Assert.DoesNotContain("tiny", session.Documents[0].Text());
		Assert.True(session.Report.HasSkippedContent);
	}

	[Fact]
	public async Task The_budget_also_counts_codex_turn_ids()
	{
		string turn = new('t', 100);
		SessionRenderOptions options = new() { MaxRetainedChars = 64 + 100 + 1 };

		SessionReadReport withTurn = (await Codex(CodexMessage("user", "ab", turn), options)).Report;
		SessionReadReport withoutTurn = (await Codex(CodexMessage("user", "ab"), options)).Report;

		Assert.Equal(1, withTurn.MessagesOverLimit);
		Assert.Equal(1, withoutTurn.Messages);
	}

	[Fact]
	public async Task The_budget_applies_to_touched_files()
	{
		SessionRenderOptions options = new() { MaxRetainedChars = 100 };
		string snapshot = "{\"type\":\"file-history-snapshot\",\"snapshot\":{\"trackedFileBackups\":{\"a.txt\":{},\"b.txt\":{}}}}\n";

		UnredactedSession session = await Claude(ClaudeLine("user", "x") + snapshot, options);

		Assert.Equal(2, session.Report.FilesOverLimit);
		Assert.Contains("files_touched: []", session.Documents[0].Text());
		Assert.True(session.Report.HasSkippedContent);
	}

	[Fact]
	public async Task Cancellation_is_honored()
	{
		using CancellationTokenSource cancelled = new();
		await cancelled.CancelAsync();
		using MemoryStream stream = new(Encoding.UTF8.GetBytes(s_good));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			SessionRenderer.RenderAsync(stream, new SessionSource(SessionFormat.ClaudeCode, ClaudeId), null, cancelled.Token));
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task A_blank_session_id_is_refused(string id)
	{
		await Assert.ThrowsAsync<ArgumentException>(() => Claude(s_good, sessionId: id));
	}

	[Theory]
	[InlineData(0, 1, 1)]
	[InlineData(1, 0, 1)]
	[InlineData(1, 1, 0)]
	public async Task Non_positive_limits_are_refused(int part, int line, int retained)
	{
		SessionRenderOptions options = new() { MaxPartChars = part, MaxLineBytes = line, MaxRetainedChars = retained };
		await Assert.ThrowsAsync<ArgumentException>(() => Claude(s_good, options));
	}

	[Fact]
	public async Task An_unknown_format_and_null_arguments_are_refused()
	{
		await Assert.ThrowsAsync<ArgumentException>(() => Render(s_good, new SessionSource((SessionFormat)99, ClaudeId)));
		await Assert.ThrowsAsync<ArgumentNullException>(() => SessionRenderer.RenderAsync(null!, new SessionSource(SessionFormat.Codex, CodexId)));
		await Assert.ThrowsAsync<ArgumentNullException>(() => SessionRenderer.RenderAsync(Stream.Null, null!));
	}

	[Fact]
	public async Task A_refusal_does_not_echo_the_session_id()
	{
		ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
			Claude(s_good, new SessionRenderOptions { MaxPartChars = 0 }, "sk-live-secret-id"));
		Assert.DoesNotContain("sk-live", error.Message);
	}

	private sealed class OneByteStream(byte[] bytes) : MemoryStream(bytes)
	{
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			return base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
		}
	}

	/// <summary>Produces a long run of one byte and then a tail, holding none of it in memory.</summary>
	private sealed class RepeatingStream(byte value, long count, byte[] tail) : Stream
	{
		private long _remaining = count;
		private int _tailOffset;

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			return ValueTask.FromResult(Read(buffer.Span));
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			return Read(buffer.AsSpan(offset, count));
		}

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		public override int Read(Span<byte> buffer)
		{
			if (_remaining > 0)
			{
				int run = (int)Math.Min(_remaining, buffer.Length);
				buffer[..run].Fill(value);
				_remaining -= run;
				return run;
			}
			int left = Math.Min(tail.Length - _tailOffset, buffer.Length);
			tail.AsSpan(_tailOffset, left).CopyTo(buffer);
			_tailOffset += left;
			return left;
		}
	}
}
