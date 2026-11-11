using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Memoria.Api.Hosting;

namespace Memoria.Api.Tests;

/// <summary>
/// The two commands <c>Memoria.Api.dll</c> runs without starting a host, and what the health probe tells the operator on stderr.
/// These swap the process's console and environment, so they run alone.
/// </summary>
[Collection(nameof(ProcessStateCollection))]
public sealed class CommandLineEntryTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("memoria-entry-").FullName;

	public void Dispose()
	{
		Directory.Delete(_root, recursive: true);
	}

	private static int RunProgram(params string[] args)
	{
		MethodInfo entry = typeof(Program).Assembly.EntryPoint!;
		return Assert.IsType<int>(entry.Invoke(null, [args]));
	}

	private static async Task<(T Result, string Error)> CapturingStderrAsync<T>(Func<Task<T>> action)
	{
		TextWriter original = Console.Error;
		using StringWriter error = new();
		Console.SetError(error);
		try
		{
			T result = await action();
			return (result, error.ToString());
		}
		finally
		{
			Console.SetError(original);
		}
	}

	private static (T Result, string Output, string Error) CapturingConsole<T>(Func<T> action)
	{
		TextWriter originalOut = Console.Out;
		TextWriter originalError = Console.Error;
		using StringWriter output = new();
		using StringWriter error = new();
		Console.SetOut(output);
		Console.SetError(error);
		try
		{
			T result = action();
			return (result, output.ToString(), error.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
		}
	}

	[Fact]
	public async Task A_failing_status_is_reported_on_stderr()
	{
		using HttpClient http = new(new FakeSidecar().On("GET", "/health", HttpStatusCode.ServiceUnavailable, "{}"));
		Uri url = new("http://127.0.0.1:9/health");

		(int exit, string error) = await CapturingStderrAsync(() => HealthProbe.RunAsync(http, url));

		Assert.Equal(1, exit);
		Assert.Equal($"http://127.0.0.1:9/health answered 503{Environment.NewLine}", error);
	}

	[Fact]
	public async Task An_unreachable_instance_is_reported_on_stderr()
	{
		using HttpClient http = new(new FakeSidecar().Unreachable());
		Uri url = new("http://127.0.0.1:9/health");

		(int exit, string error) = await CapturingStderrAsync(() => HealthProbe.RunAsync(http, url));

		Assert.Equal(1, exit);
		Assert.Equal($"http://127.0.0.1:9/health unreachable: No connection could be made because the target machine actively refused it.{Environment.NewLine}", error);
	}

	[Fact]
	public async Task The_configured_address_is_probed_and_a_closed_port_fails()
	{
		int port = ClosedPort();

		(int exit, string error) = await CapturingStderrAsync(() => HealthProbe.RunAsync($"http://127.0.0.1:{port}"));

		Assert.Equal(1, exit);
		Assert.StartsWith($"http://127.0.0.1:{port}/health unreachable: ", error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task The_health_check_command_probes_the_address_in_ASPNETCORE_URLS()
	{
		using TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		Task serving = AnswerOkAsync(listener);
		string? before = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
		Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
		try
		{
			(int exit, _, string error) = CapturingConsole(() => RunProgram(HealthProbe.Switch));

			Assert.True(exit == 0, error);
		}
		finally
		{
			Environment.SetEnvironmentVariable("ASPNETCORE_URLS", before);
		}

		await serving.WaitAsync(TimeSpan.FromSeconds(10));
	}

	/// <summary>The command's own switches are not configuration: were they, a flag with no value would swallow the next switch.</summary>
	[Fact]
	public void The_bootstrap_command_hands_only_the_other_switches_to_configuration()
	{
		string database = Path.Combine(_root, "control.db");

		(int exit, string output, string error) = CapturingConsole(() => RunProgram(
			"--bootstrap-admin-key", "first-admin", "--allow-additional-admin", $"--Memoria:Auth:DatabasePath={database}"));

		Assert.True(exit == 0, error);
		Assert.StartsWith("mem_", output, StringComparison.Ordinal);
		Assert.Contains("('first-admin')", error, StringComparison.Ordinal);
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
	}

	private static int ClosedPort()
	{
		using TcpListener probe = new(IPAddress.Loopback, 0);
		probe.Start();
		int port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		return port;
	}

	private static async Task AnswerOkAsync(TcpListener listener)
	{
		using TcpClient client = await listener.AcceptTcpClientAsync();
		NetworkStream stream = client.GetStream();
		byte[] buffer = new byte[4096];
		StringBuilder request = new();
		while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
		{
			int read = await stream.ReadAsync(buffer);
			if (read == 0)
			{
				return;
			}

			request.Append(Encoding.ASCII.GetString(buffer, 0, read));
		}

		await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
	}
}

[CollectionDefinition(nameof(ProcessStateCollection), DisableParallelization = true)]
public sealed class ProcessStateCollection;
