using System.Net.Sockets;
using System.Text;
using Memoria.Api.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Memoria.Api.Tests;

/// <summary>
/// An upload over the limit on real Kestrel, sent the way a simple client sends it: the whole body, then the answer. The test
/// server has no sockets, so it cannot show the reset that hid the 413 from such a client.
/// </summary>
public sealed class KestrelUploadLimitTests : IDisposable
{
	private const int Limit = 16 * 1024 * 1024;

	/// <summary>Past the limit, and within the room the server leaves to read its answer: more than a socket's buffers hold.</summary>
	private const int Oversized = 30 * 1024 * 1024;

	private readonly AuthApiFactory _factory = new();

	private readonly WebApplicationFactory<Program> _server;

	public KestrelUploadLimitTests()
	{
		_server = _factory.WithWebHostBuilder(static builder => builder
			.UseSetting("Memoria:Ingest:MaxSessionBytes", Limit.ToString(System.Globalization.CultureInfo.InvariantCulture))
			.UseSetting("urls", "http://127.0.0.1:0"));
		_server.UseKestrel();
		_server.StartServer();
	}

	public void Dispose()
	{
		_server.Dispose();
		_factory.Dispose();
	}

	[Fact]
	public async Task A_stream_over_the_limit_sent_whole_reads_its_413()
	{
		string answer = await SendWholeAsync(chunked: true);

		Assert.StartsWith("HTTP/1.1 413", answer, StringComparison.Ordinal);
		Assert.Contains("\"code\":\"payload_too_large\"", answer, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_declared_length_over_the_limit_sent_whole_reads_its_413()
	{
		string answer = await SendWholeAsync(chunked: false);

		Assert.StartsWith("HTTP/1.1 413", answer, StringComparison.Ordinal);
		Assert.Contains("\"code\":\"payload_too_large\"", answer, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_stream_whose_rest_takes_longer_than_kestrel_waits_still_reads_its_413()
	{
		// About 9 seconds past the limit, which Kestrel's own reading of the rest, after an answer, does not wait out.
		string answer = await SendWholeAsync(chunked: true, pastTheLimit: TimeSpan.FromMilliseconds(40));

		Assert.StartsWith("HTTP/1.1 413", answer, StringComparison.Ordinal);
		Assert.Contains("\"code\":\"payload_too_large\"", answer, StringComparison.Ordinal);
	}

	/// <summary>Writes the whole body before reading anything, as Python's urllib does, and returns what came back. Past the limit,
	/// each 64 KiB waits <paramref name="pastTheLimit"/>, as a slow link would.</summary>
	private async Task<string> SendWholeAsync(bool chunked, TimeSpan pastTheLimit = default)
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
		_ = new FakeCorpus(_factory.Sidecar);
		(_, string key) = await _factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);
		Uri address = new(_server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
		using TcpClient tcp = new();
		await tcp.ConnectAsync(address.Host, address.Port);
		NetworkStream stream = tcp.GetStream();
		string length = chunked ? "Transfer-Encoding: chunked" : $"Content-Length: {Oversized}";
		string head = $"POST /v1/ingest/session?collection=c&format=codex&sessionId=11111111-2222-4333-8444-555555555555&updated=2026-01-15T12:00:00Z HTTP/1.1\r\nHost: {address.Authority}\r\nAuthorization: Bearer {key}\r\nContent-Type: application/x-ndjson\r\nConnection: close\r\n{length}\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(head), timeout.Token);
		byte[] line = Encoding.ASCII.GetBytes(new string('x', 1023) + "\n");
		byte[] block = [.. Enumerable.Range(0, 64).SelectMany(_ => line)];
		byte[] frame = chunked ? [.. Encoding.ASCII.GetBytes($"{block.Length:x}\r\n"), .. block, .. "\r\n"u8.ToArray()] : block;
		for (int sent = 0; sent < Oversized; sent += block.Length)
		{
			await stream.WriteAsync(frame, timeout.Token);
			if (sent > Limit && pastTheLimit > TimeSpan.Zero)
			{
				await Task.Delay(pastTheLimit, timeout.Token);
			}
		}

		if (chunked)
		{
			await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), timeout.Token);
		}

		using StreamReader reader = new(stream, Encoding.UTF8);
		return await reader.ReadToEndAsync(timeout.Token);
	}
}
