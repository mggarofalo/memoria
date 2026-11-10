using System.Buffers;
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

	public static bool IsHexEncodedCredential(SecretCandidate candidate)
	{
		string value = candidate.Value;
		return value.Length <= MaxEncodedChars && HoldsSelfEvidentToken(Encoding.Latin1.GetString(Convert.FromHexString(value)));
	}

	public static bool IsUnicodeEscapedCredential(SecretCandidate candidate)
	{
		string value = candidate.Value;
		if (value.Length > MaxEncodedChars)
		{
			return false;
		}

		string decoded = UnescapeUnicode(value);
		return (decoded.Length >= MinDecodedChars && HoldsSelfEvidentToken(decoded))
			|| (LetterAfterBackslash(candidate) is char letter && HoldsSelfEvidentToken(letter + decoded));
	}

	/// <summary>
	/// The letter of an escape such as <c>\n</c> just before the run. In <c>C:\tools\npm\u005f…</c> it is the token's own first letter, so the
	/// run is also judged with it. Only the run is replaced, which leaves that one letter showing.
	/// </summary>
	private static char? LetterAfterBackslash(SecretCandidate candidate)
	{
		int start = candidate.Secret.Index;
		return start >= 2 && candidate.Input[start - 2] == '\\' && candidate.Input[start - 1] is 'n' or 'r' or 't' or 'f' or 'b' or 'v' or '0'
			? candidate.Input[start - 1]
			: null;
	}

	/// <summary>
	/// Each <c>\uXXXX</c> as its character, and each <c>\\uXXXX</c>, as JSON inside JSON escapes it. The rule's pattern puts four hex digits
	/// after every <c>\u</c> it takes.
	/// </summary>
	private static string UnescapeUnicode(string value)
	{
		StringBuilder output = new(value.Length);
		for (int i = 0; i < value.Length; i++)
		{
			int at = i + 1 < value.Length && value[i] == '\\' && value[i + 1] == '\\' ? i + 1 : i;
			bool escape = value[at] == '\\' && at + 5 < value.Length && value[at + 1] == 'u';
			output.Append(escape ? (char)Convert.ToInt32(value.Substring(at + 2, 4), 16) : value[i]);
			i = escape ? at + 5 : i;
		}

		return output.ToString();
	}

	/// <summary>
	/// Base64 whose decoded form holds a token, or reads as text that a rule of ours would find a credential in, as a base64 .env file
	/// does. Percent-encoded text is judged only by tokens, since a URL's query is prose more often than configuration.
	/// </summary>
	public static bool IsBase64EncodedCredential(SecretCandidate candidate)
	{
		string value = candidate.Value;
		if (value.Length > MaxEncodedChars || RuleNames.Contains(value) || DecodeBase64(value) is not { } bytes)
		{
			return false;
		}

		return HoldsSelfEvidentToken(AsLatin1OrUtf16(bytes)) || (ReadableText(bytes) is { } text && HoldsContextualCredential(text));
	}

	/// <summary>
	/// The bytes as text a person wrote: UTF-16 when every second byte is zero, or else UTF-8 that decodes without error, with no more than
	/// one character in <see cref="TextShare"/> a control character. Binary data fails one test or the other. Null when it is not text.
	/// </summary>
	private static string? ReadableText(byte[] bytes)
	{
		string? text = IsUtf16(WithoutBom(bytes)) ? Encoding.Unicode.GetString(WithoutBom(bytes)) : StrictUtf8(bytes);
		return text is not null && text.Count(static c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) * TextShare <= text.Length ? text : null;
	}

	private static string? StrictUtf8(byte[] bytes)
	{
		try
		{
			return s_strictUtf8.GetString(bytes);
		}
		catch (DecoderFallbackException)
		{
			return null;
		}
	}

	private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	/// <summary>One character in this many, and no more, may be a control character in text.</summary>
	private const int TextShare = 20;

	/// <summary>
	/// Markers at least one of which every rule in <see cref="RedactionRules.Contextual"/> needs in the text: a secret's name, a URL's
	/// userinfo, a header's scheme, a webhook's host, or a command and its flag. Text with none of them is not read by those rules,
	/// which keeps decoding cheap. A rule added there needs its marker added here; a test checks every rule finds its case.
	/// </summary>
	private static readonly SearchValues<string> ContextHints = SearchValues.Create(
		[
			"pass", "pwd", "secret", "token", "key", "auth", "cred", "cookie", "sess", "sig", "@", "akia", "asia", "twilio", "hooks.slack",
			"discord", "webhook", "ssws", "memoria_", "basic", "digest", "code=", "bearer", "-u", "--user", "-b", "-p", "login", "redis-cli",
			"ssh-keygen", "cmdkey", "net use", "setx", "securestring", "sign", "identified", "curl", "sid", "psk", "%40",
		],
		StringComparison.OrdinalIgnoreCase);

	/// <summary>The rules that go by a name read only text, so there is no layer below this one; the depth reached so far still bounds it.</summary>
	private static bool HoldsContextualCredential(string decoded)
	{
		if (t_depth >= MaxDepth || decoded.AsSpan().IndexOfAny(ContextHints) < 0)
		{
			return false;
		}

		foreach (RedactionRule rule in RedactionRules.Contextual)
		{
			if (rule.TryFindNext(decoded, 0, out _, out _))
			{
				return true;
			}
		}

		return false;
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

	/// <summary>Standard or URL-safe, padded or not.</summary>
	private static byte[]? DecodeBase64(string value)
	{
		string body = value.TrimEnd('=').Replace('-', '+').Replace('_', '/');
		if (body.Length % 4 == 1)
		{
			return null;
		}

		byte[] buffer = new byte[(body.Length * 3 / 4) + 3];
		string padded = body.PadRight(body.Length + ((4 - (body.Length % 4)) % 4), '=');
		return Convert.TryFromBase64String(padded, buffer, out int written) && written >= MinDecodedChars ? buffer[..written] : null;
	}

	/// <summary>As Latin-1, so a token inside binary data such as a pickle can still be read, or as UTF-16 when every second byte is zero.</summary>
	private static string AsLatin1OrUtf16(byte[] bytes)
	{
		ReadOnlySpan<byte> text = WithoutBom(bytes);
		return IsUtf16(text) ? Encoding.Unicode.GetString(text) : Encoding.Latin1.GetString(bytes);
	}

	/// <summary>The bytes after a UTF-16 little-endian byte order mark, as PowerShell and Notepad write one.</summary>
	private static ReadOnlySpan<byte> WithoutBom(byte[] bytes)
	{
		return bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? bytes.AsSpan(2) : bytes;
	}

	/// <summary>
	/// Little-endian UTF-16 of mostly Latin text: an even length, and a zero in nine of every ten second bytes, so a dash or a curly
	/// quote among ASCII does not stop it being read.
	/// </summary>
	private static bool IsUtf16(ReadOnlySpan<byte> bytes)
	{
		if (bytes.Length % 2 != 0)
		{
			return false;
		}

		int zeros = 0;
		for (int i = 1; i < bytes.Length; i += 2)
		{
			zeros += bytes[i] == 0 ? 1 : 0;
		}

		return zeros * 10 >= bytes.Length / 2 * 9;
	}
}
