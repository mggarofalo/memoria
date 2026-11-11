using Memoria.Api.Configuration;
using Memoria.Api.Ingest;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Tests;

/// <summary>When a body takes one of the few places for large uploads, and when it gives it back.</summary>
public sealed class LargeUploadPlaceTests
{
	private static LargeUploads OnePlace()
	{
		return new LargeUploads(Options.Create(new MemoriaOptions { Ingest = new IngestOptions { MaxConcurrentLargeUploads = 1, MaxConcurrentLargeDocuments = 1 } }));
	}

	[Fact]
	public void A_body_declared_exactly_at_the_line_takes_no_place()
	{
		LargeUploads uploads = OnePlace();

		using Stream first = uploads.Watch(new MemoryStream(), LargeUploads.LargeBytes);
		using Stream second = uploads.Watch(new MemoryStream(), LargeUploads.LargeBytes);
		using Stream document = uploads.WatchDocument(new MemoryStream(), LargeUploads.LargeDocumentBytes);
		using Stream another = uploads.WatchDocument(new MemoryStream(), LargeUploads.LargeDocumentBytes);
	}

	[Fact]
	public void Past_the_places_the_refusal_says_to_come_back()
	{
		LargeUploads uploads = OnePlace();
		using Stream held = uploads.Watch(new MemoryStream(), LargeUploads.LargeBytes + 1);

		IngestRefusedException refused = Assert.Throws<IngestRefusedException>(() => uploads.Watch(new MemoryStream(), LargeUploads.LargeBytes + 1));

		Assert.Equal(503, refused.Status);
		Assert.Equal("ingest_busy", refused.Code);
		Assert.Equal("Too many large uploads are running. Try again shortly.", refused.Message);
		Assert.Equal(30, refused.RetryAfterSeconds);
	}

	[Fact]
	public void A_watched_body_reads_and_cannot_be_written()
	{
		using Stream body = OnePlace().Watch(new MemoryStream(), null);

		Assert.True(body.CanRead);
		Assert.False(body.CanWrite);
		Assert.False(body.CanSeek);
	}

	[Fact]
	public void Reading_past_the_line_without_a_declared_length_takes_the_place()
	{
		LargeUploads uploads = OnePlace();
		using Stream body = uploads.Watch(new MemoryStream(new byte[LargeUploads.LargeBytes + 1]), null);
		byte[] buffer = new byte[1024 * 1024];
		long total = 0;
		for (int read; (read = body.Read(buffer, 0, buffer.Length)) > 0;)
		{
			total += read;
		}

		Assert.Equal(LargeUploads.LargeBytes + 1, total);
		Assert.Throws<IngestRefusedException>(() => uploads.Watch(new MemoryStream(), LargeUploads.LargeBytes + 1));
	}

	[Fact]
	public async Task The_array_overload_of_an_async_read_reads()
	{
		await using Stream body = OnePlace().Watch(new MemoryStream([1, 2, 3, 4]), null);
		byte[] buffer = new byte[8];

		int read = await body.ReadAsync(buffer, 2, 4, CancellationToken.None);

		Assert.Equal(4, read);
		Assert.Equal(new byte[] { 0, 0, 1, 2, 3, 4, 0, 0 }, buffer);
	}

	[Fact]
	public void Disposing_twice_gives_the_place_back_once()
	{
		LargeUploads uploads = OnePlace();
		Stream held = uploads.Watch(new MemoryStream(), LargeUploads.LargeBytes + 1);

		held.Dispose();
		held.Dispose();

		using Stream next = uploads.Watch(new MemoryStream(), LargeUploads.LargeBytes + 1);
		Assert.Throws<IngestRefusedException>(() => uploads.Watch(new MemoryStream(), LargeUploads.LargeBytes + 1));
	}
}
