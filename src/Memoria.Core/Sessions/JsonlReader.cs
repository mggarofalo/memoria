using System.Buffers;
using System.Text.Json;
using System.Text.Unicode;

namespace Memoria.Core.Sessions;

/// <summary>
/// Reads JSONL with one bounded line buffer. A line longer than the limit is discarded as it
/// arrives rather than buffered, so a hostile payload costs its limit and no more. Nothing
/// from a bad line, including the parser's own message, leaves this class.
/// </summary>
internal sealed class JsonlReader : IDisposable
{
	private const int ChunkBytes = 64 * 1024;

	private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

	private static readonly JsonDocumentOptions s_jsonOptions = new() { MaxDepth = 128 };

	private readonly int _maxLineBytes;
	private readonly SessionReadReport _report;
	private readonly Action<JsonElement> _onRecord;
	private byte[] _line;
	private int _length;
	private bool _oversized;

	private JsonlReader(int maxLineBytes, SessionReadReport report, Action<JsonElement> onRecord)
	{
		_maxLineBytes = maxLineBytes;
		_report = report;
		_onRecord = onRecord;
		_line = ArrayPool<byte>.Shared.Rent(Math.Min(ChunkBytes, maxLineBytes));
	}

	public static async Task ReadAsync(
		Stream stream,
		int maxLineBytes,
		SessionReadReport report,
		Action<JsonElement> onRecord,
		CancellationToken cancellationToken)
	{
		using JsonlReader reader = new(maxLineBytes, report, onRecord);
		byte[] chunk = ArrayPool<byte>.Shared.Rent(ChunkBytes);
		try
		{
			int read;
			while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
			{
				report.BytesRead += read;
				reader.Accept(chunk.AsSpan(0, read));
			}
			reader.Finish();
		}
		finally
		{
			// Pooled buffers outlive this call and hold raw transcript text.
			ArrayPool<byte>.Shared.Return(chunk, clearArray: true);
		}
	}

	public void Dispose()
	{
		ArrayPool<byte>.Shared.Return(_line, clearArray: true);
	}

	private void Accept(ReadOnlySpan<byte> chunk)
	{
		int newline;
		while ((newline = chunk.IndexOf((byte)'\n')) >= 0)
		{
			Buffer(chunk[..newline]);
			EndLine();
			chunk = chunk[(newline + 1)..];
		}
		Buffer(chunk);
	}

	private void Buffer(ReadOnlySpan<byte> piece)
	{
		if (_oversized)
		{
			return;
		}
		if (piece.Length > _maxLineBytes - _length)
		{
			_oversized = true;
			_length = 0;
			return;
		}
		EnsureCapacity(_length + piece.Length);
		piece.CopyTo(_line.AsSpan(_length));
		_length += piece.Length;
	}

	private void EnsureCapacity(int needed)
	{
		if (needed <= _line.Length)
		{
			return;
		}
		int wanted = (int)Math.Min(Math.Max(_line.Length * 2L, needed), _maxLineBytes);
		byte[] larger = ArrayPool<byte>.Shared.Rent(wanted);
		_line.AsSpan(0, _length).CopyTo(larger);
		ArrayPool<byte>.Shared.Return(_line, clearArray: true);
		_line = larger;
	}

	private void EndLine()
	{
		_report.Lines++;
		if (_oversized)
		{
			_report.OversizedLines++;
			_oversized = false;
		}
		else
		{
			ProcessLine(_line.AsMemory(0, _length));
		}
		_length = 0;
	}

	private void Finish()
	{
		if (_oversized)
		{
			_report.Lines++;
			_report.OversizedLines++;
		}
		else if (_length > 0)
		{
			_report.Lines++;
			_report.UnterminatedLines++;
		}
	}

	private void ProcessLine(ReadOnlyMemory<byte> line)
	{
		if (line.Span.StartsWith(ByteOrderMark))
		{
			line = line[3..];
		}
		if (line.Span.IndexOfAnyExcept(" \t\r\v\f"u8) < 0)
		{
			_report.BlankLines++;
			return;
		}
		using JsonDocument? document = TryParse(line);
		if (document is null)
		{
			_report.MalformedLines++;
		}
		else if (document.RootElement.ValueKind != JsonValueKind.Object)
		{
			_report.NonObjectLines++;
		}
		else
		{
			_report.Records++;
			_onRecord(document.RootElement);
		}
	}

	private static JsonDocument? TryParse(ReadOnlyMemory<byte> line)
	{
		// The JSON reader does not validate UTF-8 inside strings until they are decoded.
		if (!Utf8.IsValid(line.Span))
		{
			return null;
		}
		try
		{
			return JsonDocument.Parse(line, s_jsonOptions);
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
