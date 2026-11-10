using System.Runtime.InteropServices;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// The CPU time of the calling thread alone. The redactor runs on its caller's thread, so this is its own cost, whatever else the process
/// or the machine is doing: other tests, the garbage collector's background thread, or another test project running beside this one.
/// </summary>
internal static class ThreadCpuTime
{
	public static TimeSpan Now()
	{
		if (OperatingSystem.IsWindows())
		{
			// Kernel and user time, in the 100-nanosecond units a TimeSpan tick also counts.
			return GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user)
				? TimeSpan.FromTicks(kernel + user)
				: throw new InvalidOperationException("GetThreadTimes failed: " + Marshal.GetLastPInvokeError());
		}

		// CLOCK_THREAD_CPUTIME_ID is 3 on Linux and 16 on macOS.
		int clock = OperatingSystem.IsMacOS() ? 16 : 3;
		return clock_gettime(clock, out Timespec now) == 0
			? TimeSpan.FromTicks((now.Seconds * TimeSpan.TicksPerSecond) + (now.Nanoseconds / 100))
			: throw new InvalidOperationException("clock_gettime failed: " + Marshal.GetLastPInvokeError());
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Timespec
	{
		public long Seconds;
		public long Nanoseconds;
	}

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentThread();

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

	[DllImport("libc", SetLastError = true)]
#pragma warning disable IDE1006, SA1300 // The C function's own name.
	private static extern int clock_gettime(int clock, out Timespec time);
#pragma warning restore IDE1006, SA1300
}
