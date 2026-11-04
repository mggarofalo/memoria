using System.Net;
using System.Net.Sockets;
using Memoria.Api.Configuration;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Auth;

/// <summary>
/// Per-address budget for failed authentication. Key-based limits cannot apply
/// until a key is valid, so without this every bad token costs a control-database
/// lookup. Only failures spend the budget. Once an address is over it, its requests
/// with a credential are refused before any lookup until the window ends.
/// <para>
/// Memory is bounded by two generations of half the cap each: a full current
/// generation replaces the previous one, in constant time. A flood of distinct
/// addresses can therefore make the limiter forget an address early, but it cannot
/// grow it, slow it down, or spend another address's budget.
/// </para>
/// </summary>
public sealed class FailedAuthLimiter
{
	private readonly Lock _lock = new();

	private readonly FailedAuthOptions _options;

	private readonly int _generationSize;

	private Dictionary<string, Entry> _current = new(StringComparer.Ordinal);

	private Dictionary<string, Entry> _previous = new(StringComparer.Ordinal);

	public FailedAuthLimiter(IOptions<MemoriaOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_options = options.Value.Auth.FailedAuth;
		_generationSize = Math.Max(1, _options.MaxTrackedAddresses / 2);
	}

	/// <summary>Addresses held right now. Never more than <see cref="FailedAuthOptions.MaxTrackedAddresses"/>.</summary>
	public int TrackedAddresses
	{
		get
		{
			lock (_lock)
			{
				return _current.Count + _previous.Count;
			}
		}
	}

	/// <summary>True when this address must be refused without a lookup, with how long until its window ends.</summary>
	public bool IsBlocked(string address, DateTimeOffset now, out TimeSpan retryAfter)
	{
		retryAfter = TimeSpan.Zero;
		if (!_options.Enabled)
		{
			return false;
		}

		lock (_lock)
		{
			if (Find(address) is not { } entry || entry.Count < _options.PermitLimit)
			{
				return false;
			}

			TimeSpan remaining = entry.WindowStart + _options.Window - now;
			if (remaining <= TimeSpan.Zero)
			{
				return false;
			}

			retryAfter = remaining;
			return true;
		}
	}

	/// <summary>Spends one failure. True exactly when this one used up the budget, so the crossing is logged once.</summary>
	public bool RecordFailure(string address, DateTimeOffset now)
	{
		if (!_options.Enabled)
		{
			return false;
		}

		lock (_lock)
		{
			Entry? entry = Find(address);
			if (entry is null || now - entry.WindowStart >= _options.Window)
			{
				entry = new Entry(now);
			}

			entry.Count++;
			_previous.Remove(address);
			if (!_current.ContainsKey(address) && _current.Count >= _generationSize)
			{
				_previous = _current;
				_current = new Dictionary<string, Entry>(StringComparer.Ordinal);
			}

			_current[address] = entry;
			return entry.Count == _options.PermitLimit;
		}
	}

	/// <summary>
	/// The identity to budget on. IPv6 is grouped by /64, because one customer
	/// normally holds a whole /64 and could otherwise never run out of addresses.
	/// </summary>
	public static string KeyFor(IPAddress? address)
	{
		if (address is null)
		{
			return "unknown";
		}

		if (address.IsIPv4MappedToIPv6)
		{
			address = address.MapToIPv4();
		}

		if (address.AddressFamily != AddressFamily.InterNetworkV6)
		{
			return address.ToString();
		}

		byte[] bytes = address.GetAddressBytes();
		Array.Clear(bytes, 8, 8);
		return $"{new IPAddress(bytes)}/64";
	}

	private Entry? Find(string address)
	{
		return _current.TryGetValue(address, out Entry? entry) || _previous.TryGetValue(address, out entry) ? entry : null;
	}

	private sealed class Entry(DateTimeOffset windowStart)
	{
		public DateTimeOffset WindowStart { get; } = windowStart;

		public int Count { get; set; }
	}
}

/// <summary>Marks a request refused by <see cref="FailedAuthLimiter"/>, so the challenge can answer 429.</summary>
public sealed record FailedAuthThrottled(TimeSpan RetryAfter);

/// <summary>Turns <c>Memoria:Auth:TrustedProxies</c> into forwarded-header handling.</summary>
public static class TrustedProxies
{
	/// <summary>Parses one IP or CIDR entry. A /0 is refused: it would trust everyone's X-Forwarded-For.</summary>
	public static bool TryParse(string entry, out IPAddress? address, out System.Net.IPNetwork? network)
	{
		address = null;
		network = null;
		if (IPAddress.TryParse(entry, out IPAddress? parsed) && !entry.Contains('/', StringComparison.Ordinal))
		{
			address = parsed;
			return true;
		}

		if (System.Net.IPNetwork.TryParse(entry, out System.Net.IPNetwork cidr) && cidr.PrefixLength > 0)
		{
			network = cidr;
			return true;
		}

		return false;
	}

	/// <summary>
	/// With no proxy configured nothing is registered, so the socket address is the
	/// client and X-Forwarded-For means nothing. The framework's defaults trust
	/// loopback, so they are cleared: only what the operator listed counts. The
	/// chain is walked from the right and stops at the first address that is not a
	/// trusted proxy, so whatever a client writes on the left is ignored.
	/// </summary>
	public static IApplicationBuilder UseMemoriaForwardedHeaders(this IApplicationBuilder app, IEnumerable<string>? entries)
	{
		ForwardedHeadersOptions options = new()
		{
			ForwardedHeaders = ForwardedHeaders.XForwardedFor,
			ForwardLimit = null,
		};
		options.KnownProxies.Clear();
		options.KnownIPNetworks.Clear();

		string[] configured = (entries ?? []).Where(static entry => !string.IsNullOrWhiteSpace(entry)).ToArray();
		if (configured.Length == 0)
		{
			return app;
		}

		foreach (string entry in configured)
		{
			if (!TryParse(entry.Trim(), out IPAddress? address, out System.Net.IPNetwork? network))
			{
				throw new InvalidOperationException($"Memoria:Auth:TrustedProxies has an invalid entry: '{entry}'.");
			}

			if (address is not null)
			{
				options.KnownProxies.Add(address);
			}
			else
			{
				options.KnownIPNetworks.Add(network!.Value);
			}
		}

		return app.UseForwardedHeaders(options);
	}
}
