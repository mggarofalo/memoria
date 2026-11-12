using Memoria.Api.Ingest;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Memoria.Api.Tests;

/// <summary>Where a body's limits fall exactly, and how much of a refused body is read before the answer goes.</summary>
public sealed class RequestBodyEdgeTests
{
	private static DefaultHttpContext Context(Stream body, long? declared = null, long? serverLimit = null)
	{
		DefaultHttpContext context = new();
		context.Request.Body = body;
		context.Request.ContentLength = declared;
		if (serverLimit is not null)
		{
			context.Features.Set<IHttpMaxRequestBodySizeFeature>(new LimitFeature { MaxRequestBodySize = serverLimit });
		}

		return context;
	}

	[Fact]
	public void Missing_contexts_are_refused_as_missing()
	{
		Assert.Throws<ArgumentNullException>(static () => RequestBody.Open(null!, 10));
		Assert.Throws<ArgumentNullException>(static () => RequestBody.AllowUpTo(null!, 10));
		Assert.Throws<ArgumentNullException>(static () => RequestBody.RequireRate(null!, 10));
	}

	[Fact]
	public async Task A_missing_context_is_refused_before_anything_is_read()
	{
		await Assert.ThrowsAsync<ArgumentNullException>(static () => RequestBody.DiscardRestAsync(null!, TimeSpan.FromSeconds(1), default));
	}

	[Fact]
	public void A_body_declared_exactly_at_the_limit_is_opened_and_the_server_limit_raised_to_match()
	{
		DefaultHttpContext context = Context(new MemoryStream(new byte[4096]), declared: 4096, serverLimit: 30);

		using Stream body = RequestBody.Open(context, 4096);
		body.ReadExactly(new byte[4096]);

		Assert.Equal(8192, context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize);
	}

	[Fact]
	public void A_declared_length_over_the_limit_says_what_the_limit_is()
	{
		IngestRefusedException refused = Assert.Throws<IngestRefusedException>(() => RequestBody.Open(Context(new MemoryStream(), declared: 4097), 4096));

		Assert.Equal(StatusCodes.Status413PayloadTooLarge, refused.Status);
		Assert.Equal("The request body may be at most 4096 bytes.", refused.Message);
	}

	[Fact]
	public void A_limit_of_exactly_half_the_largest_size_is_still_doubled()
	{
		DefaultHttpContext context = Context(new MemoryStream(), serverLimit: 30);

		RequestBody.AllowUpTo(context, long.MaxValue / 2);

		Assert.Equal(long.MaxValue - 1, context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize);
	}

	[Theory]
	[InlineData(50L)]
	[InlineData(100L)]
	public async Task A_refused_body_within_the_server_limit_is_read_to_its_end(long declared)
	{
		MemoryStream body = new(new byte[declared]);

		await RequestBody.DiscardRestAsync(Context(body, declared, serverLimit: 100), TimeSpan.FromSeconds(10), default);

		Assert.Equal(declared, body.Position);
	}

	[Fact]
	public async Task A_body_declared_past_the_server_limit_is_not_read()
	{
		MemoryStream body = new(new byte[101]);

		await RequestBody.DiscardRestAsync(Context(body, 101, serverLimit: 100), TimeSpan.FromSeconds(10), default);

		Assert.Equal(0, body.Position);
	}

	[Fact]
	public async Task A_body_the_caller_sends_only_when_told_to_continue_is_not_read()
	{
		MemoryStream body = new(new byte[50]);
		DefaultHttpContext context = Context(body, 50, serverLimit: 100);
		context.Request.Headers.Expect = "100-continue";

		await RequestBody.DiscardRestAsync(context, TimeSpan.FromSeconds(10), default);

		Assert.Equal(0, body.Position);
	}

	[Fact]
	public async Task A_body_that_never_ends_is_read_only_until_the_deadline()
	{
		await RequestBody.DiscardRestAsync(Context(new EndlessStream(), serverLimit: 100), TimeSpan.FromMilliseconds(50), default).WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task A_connection_that_breaks_while_the_rest_is_read_still_lets_the_answer_go()
	{
		await RequestBody.DiscardRestAsync(Context(new BrokenStream(), serverLimit: 100), TimeSpan.FromSeconds(10), default);
	}

	private sealed class LimitFeature : IHttpMaxRequestBodySizeFeature
	{
		public bool IsReadOnly => false;

		public long? MaxRequestBodySize { get; set; }
	}

	/// <summary>A body whose next byte never arrives: a read returns only when it is cancelled.</summary>
	private sealed class EndlessStream : Stream
	{
		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => throw new NotSupportedException();

		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return 0;
		}

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	/// <summary>A body whose connection drops part way.</summary>
	private sealed class BrokenStream : Stream
	{
		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => throw new NotSupportedException();

		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			return ValueTask.FromException<int>(new IOException("The connection was reset."));
		}

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
