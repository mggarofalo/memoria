using System.Collections.Frozen;
using System.Text;

namespace Memoria.Core.Redaction;

/// <summary>
/// Decides whether encoded text holds a credential, by decoding it and looking for a format that speaks for itself. Rules that
/// go by what surrounds a value stay out of it: percent-encoded prose saying <c>password: adminx</c> is not a password.
/// <para>
/// Encoding inside encoding is found by the same question, so this recurses on text a caller chose. A stack overflow ends the
/// process and cannot be caught, so <see cref="MaxDepth"/> is a hard limit: <c>%FF</c> decodes to itself, and <c>%252525...</c> shrinks
/// by only two characters a round.
/// </para>
/// </summary>
internal static class EncodedTokens
{
	/// <summary>Larger runs are data such as images. Decoding each one would make a document's cost depend on its attachments.</summary>
	private const int MaxEncodedChars = 16 * 1024;

	private const int MinDecodedChars = 16;

	/// <summary>Layers of encoding looked through. Real values have one or two.</summary>
	private const int MaxDepth = 4;

	/// <summary>
	/// A long rule name in a placeholder is itself a run of base64. Each one would be decoded and judged on every later sweep, which
	/// a document of many short secrets turns into a large share of its time.
	/// </summary>
	private static readonly FrozenSet<string> RuleNames = RedactionRules.All.Select(rule => rule.Name).ToFrozenSet(StringComparer.Ordinal);

	[ThreadStatic]
	private static int t_depth;

	public static bool IsPercentEncodedCredential(SecretCandidate candidate)
	{
		string value = candidate.Value;
		if (value.Length > MaxEncodedChars)
		{
			return false;
		}

		string decoded = Uri.UnescapeDataString(value);
		return decoded.Length >= MinDecodedChars && HoldsSelfEvidentToken(decoded);
	}

	public static bool IsBase64EncodedCredential(SecretCandidate candidate)
	{
		string value = candidate.Value;
		return value.Length <= MaxEncodedChars && !RuleNames.Contains(value) && DecodeBase64(value) is { } decoded && HoldsSelfEvidentToken(decoded);
	}

	private static bool HoldsSelfEvidentToken(string decoded)
	{
		if (t_depth >= MaxDepth)
		{
			return false;
		}

		t_depth++;
		try
		{
			foreach (RedactionRule rule in RedactionRules.SelfEvident)
			{
				if (rule.TryFindNext(decoded, 0, out _, out _))
				{
					return true;
				}
			}

			return false;
		}
		finally
		{
			t_depth--;
		}
	}

	/// <summary>Standard or URL-safe, padded or not. As Latin-1, so a token inside binary data such as a pickle can still be read.</summary>
	private static string? DecodeBase64(string value)
	{
		string body = value.TrimEnd('=').Replace('-', '+').Replace('_', '/');
		if (body.Length % 4 == 1)
		{
			return null;
		}

		Span<byte> buffer = new byte[(body.Length * 3 / 4) + 3];
		string padded = body.PadRight(body.Length + ((4 - (body.Length % 4)) % 4), '=');
		if (!Convert.TryFromBase64String(padded, buffer, out int written) || written < MinDecodedChars)
		{
			return null;
		}

		return Encoding.Latin1.GetString(buffer[..written]);
	}
}
