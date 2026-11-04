using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;

namespace Memoria.Api.Http;

/// <summary>
/// Makes a missing <c>Memoria:Auth:TrustedProxies</c> visible. A request from a
/// private peer that carries <c>X-Forwarded-For</c>, which the forwarded-headers step
/// did not consume, looks like a reverse proxy this API was not told about. In that
/// state every client shares the proxy's failed-authentication budget. It only warns:
/// the header is never believed here. At most one line per hour, so a caller cannot
/// use it to fill the log.
/// </summary>
public sealed partial class ForwardedHeaderWarning(RequestDelegate next, ILogger<ForwardedHeaderWarning> logger, TimeProvider timeProvider)
{
	public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

	private long _nextWarningTicks;

	public Task InvokeAsync(HttpContext context)
	{
		if (LooksLikeAnUntrustedProxy(context) && TakeTurn())
		{
			LogUntrustedProxy(logger, context.Connection.RemoteIpAddress?.ToString());
		}

		return next(context);
	}

	/// <summary>Loopback, RFC 1918, link-local, and IPv6 unique-local addresses.</summary>
	public static bool IsPrivate(IPAddress? address)
	{
		if (address is null)
		{
			return false;
		}

		IPAddress plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
		if (IPAddress.IsLoopback(plain))
		{
			return true;
		}

		byte[] bytes = plain.GetAddressBytes();
		return plain.AddressFamily == AddressFamily.InterNetwork ? IsPrivateIpv4(bytes) : IsPrivateIpv6(plain, bytes);
	}

	private static bool IsPrivateIpv4(byte[] bytes)
	{
		return bytes[0] == 10
			|| (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
			|| (bytes[0] == 192 && bytes[1] == 168)
			|| (bytes[0] == 169 && bytes[1] == 254);
	}

	private static bool IsPrivateIpv6(IPAddress address, byte[] bytes)
	{
		return address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
	}

	/// <summary>The forwarded-headers step records <c>X-Original-For</c> when it consumed the header from a trusted peer.</summary>
	private static bool LooksLikeAnUntrustedProxy(HttpContext context)
	{
		return context.Request.Headers.ContainsKey(ForwardedHeadersDefaults.XForwardedForHeaderName)
			&& !context.Request.Headers.ContainsKey(ForwardedHeadersDefaults.XOriginalForHeaderName)
			&& IsPrivate(context.Connection.RemoteIpAddress);
	}

	private bool TakeTurn()
	{
		long now = timeProvider.GetUtcNow().UtcTicks;
		long next = Interlocked.Read(ref _nextWarningTicks);
		return now >= next && Interlocked.CompareExchange(ref _nextWarningTicks, now + Interval.Ticks, next) == next;
	}

	[LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "A request from private address {PeerAddress} carried X-Forwarded-For, but that address is not in Memoria:Auth:TrustedProxies, so the header was ignored. If this is your reverse proxy, set Memoria:Auth:TrustedProxies: until then every client shares one failed-authentication budget, and a few bad tokens lock out everyone whose key is not yet known to this process.")]
	private static partial void LogUntrustedProxy(ILogger logger, string? peerAddress);
}
