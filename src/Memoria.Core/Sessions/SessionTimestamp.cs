using System.Globalization;
using System.Text.RegularExpressions;

namespace Memoria.Core.Sessions;

/// <summary>
/// A timestamp as the exporters treat it. The offset is kept rather than normalized, since the
/// corpus's dates are local to it, and a timestamp with no offset stays that way when printed.
/// <see cref="DateTimeOffset"/> cannot do either, and it rejects offsets beyond 14 hours.
/// </summary>
internal readonly partial record struct SessionTimestamp
{
	private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
	private const int MaxInputChars = 64;

	private SessionTimestamp(long localTicks, int? offsetSeconds)
	{
		LocalTicks = localTicks;
		OffsetSeconds = offsetSeconds;
	}

	public long LocalTicks { get; }

	public int? OffsetSeconds { get; }

	/// <summary>The instant used for ordering. A timestamp with no offset is ordered as UTC.</summary>
	public long Instant => LocalTicks - ((OffsetSeconds ?? 0) * TimeSpan.TicksPerSecond);

	public string DateText => new DateTime(LocalTicks, DateTimeKind.Unspecified).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	public static SessionTimestamp? TryParse(string value)
	{
		if (value.Length > MaxInputChars)
		{
			return null;
		}
		Match match = IsoPattern().Match(value);
		if (!match.Success)
		{
			return null;
		}
		// The pattern fixes the shape and the calendar check rejects impossible dates and times.
		string local = $"{match.Groups[1].Value}T{GroupOrZero(match, 2)}:{GroupOrZero(match, 3)}:{GroupOrZero(match, 4)}";
		if (!DateTime.TryParseExact(local, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
		{
			return null;
		}
		long microseconds = long.Parse(match.Groups[5].Value.PadRight(6, '0')[..6], CultureInfo.InvariantCulture);
		return new SessionTimestamp(parsed.Ticks + (microseconds * TicksPerMicrosecond), ParseZone(match.Groups[6].Value));
	}

	public static SessionTimestamp FromFallback(DateTimeOffset? fallback)
	{
		DateTimeOffset value = fallback ?? DateTimeOffset.UnixEpoch;
		return new SessionTimestamp(value.Ticks, (int)value.Offset.TotalSeconds);
	}

	/// <summary>Python's <c>datetime.isoformat()</c>: microseconds only when nonzero, then the offset if there is one.</summary>
	public string ToIsoString()
	{
		string text = new DateTime(LocalTicks, DateTimeKind.Unspecified).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
		long microseconds = LocalTicks % TimeSpan.TicksPerSecond / TicksPerMicrosecond;
		if (microseconds != 0)
		{
			text += "." + microseconds.ToString("D6", CultureInfo.InvariantCulture);
		}
		return OffsetSeconds is { } offset ? text + FormatOffset(offset) : text;
	}

	private static string FormatOffset(int offset)
	{
		int magnitude = Math.Abs(offset);
		string text = $"{(offset < 0 ? '-' : '+')}{magnitude / 3600:D2}:{magnitude / 60 % 60:D2}";
		return magnitude % 60 == 0 ? text : $"{text}:{magnitude % 60:D2}";
	}

	private static int? ParseZone(string zone)
	{
		if (zone.Length == 0)
		{
			return null;
		}
		// A Z has no digits after it, so it reads as a zero offset with no case of its own.
		string digits = zone[1..].Replace(":", "", StringComparison.Ordinal).PadRight(6, '0');
		int offset = (int.Parse(digits.AsSpan(0, 2), CultureInfo.InvariantCulture) * 3600)
			+ (int.Parse(digits.AsSpan(2, 2), CultureInfo.InvariantCulture) * 60)
			+ int.Parse(digits.AsSpan(4, 2), CultureInfo.InvariantCulture);
		return zone[0] == '-' ? -offset : offset;
	}

	private static string GroupOrZero(Match match, int index)
	{
		return match.Groups[index].Success ? match.Groups[index].Value : "00";
	}

	[GeneratedRegex(@"^([0-9]{4}-[0-9]{2}-[0-9]{2})(?:[T ]([0-9]{2})(?::([0-9]{2})(?::([0-9]{2})(?:[.,]([0-9]+))?)?)?(Z|[+-](?:[01][0-9]|2[0-3])(?::?[0-5][0-9](?::?[0-5][0-9])?)?)?)?\z", RegexOptions.CultureInvariant)]
	private static partial Regex IsoPattern();
}
