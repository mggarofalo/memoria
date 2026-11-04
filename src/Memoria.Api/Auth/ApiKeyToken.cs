using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Memoria.Api.Auth;

/// <summary>
/// The wire format of an API key: <c>mem_&lt;id&gt;_&lt;secret&gt;</c>. The id is
/// public and is what the store is keyed on; the secret is 256 random bits. Only
/// the SHA-256 hash of the whole token is ever stored. A fast hash is sound here
/// because the input is high-entropy random data, not a password.
/// </summary>
public static class ApiKeyToken
{
	public const string Prefix = "mem";

	/// <summary>Hex characters in the public id (64 bits).</summary>
	public const int IdLength = 16;

	/// <summary>Hex characters in the secret (256 bits).</summary>
	public const int SecretLength = 64;

	/// <summary>Creates a new id for a key.</summary>
	public static string NewId()
	{
		return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(IdLength / 2));
	}

	/// <summary>Creates the plaintext token for <paramref name="id"/> with a fresh secret.</summary>
	public static string NewToken(string id)
	{
		return $"{Prefix}_{id}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(SecretLength / 2))}";
	}

	/// <summary>
	/// Extracts the id from a well-formed token. Strict on purpose: a rejected
	/// value never reaches the store, and an accepted id is safe to write to logs
	/// because it can only contain lower-case hex.
	/// </summary>
	public static bool TryParseId(string? token, [NotNullWhen(true)] out string? id)
	{
		id = null;
		if (token is null || token.Length != Prefix.Length + 1 + IdLength + 1 + SecretLength)
		{
			return false;
		}

		string[] parts = token.Split('_');
		if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal) || !IsLowerHex(parts[1], IdLength) || !IsLowerHex(parts[2], SecretLength))
		{
			return false;
		}

		id = parts[1];
		return true;
	}

	/// <summary>The value stored for a token and compared on every request.</summary>
	public static byte[] Hash(string token)
	{
		return SHA256.HashData(Encoding.UTF8.GetBytes(token));
	}

	/// <summary>Constant-time comparison of two hashes.</summary>
	public static bool HashesMatch(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
	{
		return CryptographicOperations.FixedTimeEquals(left, right);
	}

	private static bool IsLowerHex(string value, int length)
	{
		return value.Length == length && value.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
	}
}
