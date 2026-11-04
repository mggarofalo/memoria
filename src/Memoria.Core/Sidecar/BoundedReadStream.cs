namespace Memoria.Core.Sidecar;

/// <summary>Reads through to another stream and fails once more than <paramref name="limit"/> bytes have come through.</summary>
internal sealed class BoundedReadStream(Stream inner, long limit) : Stream
{
	private long _read;

	public override bool CanRead => true;

	public override bool CanSeek => false;

	public override bool CanWrite => false;

	public override long Length => throw new NotSupportedException();

	public override long Position
	{
		get => throw new NotSupportedException();
		set => throw new NotSupportedException();
	}

	public override void Flush()
	{
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		return Counted(inner.Read(buffer, offset, count));
	}

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		return Counted(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
	}

	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
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

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			inner.Dispose();
		}

		base.Dispose(disposing);
	}

	private int Counted(int bytes)
	{
		_read += bytes;
		if (_read > limit)
		{
			throw new SidecarException(SidecarException.ResponseTooLarge, $"The sidecar's response passed the {limit}-byte limit.", System.Net.HttpStatusCode.OK);
		}

		return bytes;
	}
}
