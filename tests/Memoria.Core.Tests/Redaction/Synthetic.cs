using System.Text;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// Builds credential-shaped values at runtime. Every prefix is split across a
/// concatenation and every body comes from a seeded generator, so this repository
/// contains nothing a secret scanner could mistake for a real credential.
/// </summary>
internal static class Synthetic
{
	/// <summary>Half of a key marker, so no fixture line spells one whole for a secret scanner.</summary>
	public const string PrivateWord = "PRIV" + "ATE";

	private const string Alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
	private const string Base64Alphabet = Alphanumeric + "+/";
	private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

	public static string Body(int length, int seed = 1) => Generate(Alphanumeric, length, seed);

	public static string Base64(int length, int seed = 1) => Generate(Base64Alphabet, length, seed);

	public static string Upper(int length, int seed = 1) => Generate(Base32Alphabet, length, seed);

	public static string Hex(int length, int seed = 1) => Generate("0123456789abcdef", length, seed);

	public static string Anthropic => "sk-" + "ant-" + "api03-" + Body(80, 2);

	public static string OpenAiLegacy => "s" + "k-" + Body(48, 3);

	public static string OpenAiProject => "sk-" + "proj-" + Body(30, 4) + "-" + Body(30, 5) + "_" + Body(20, 6);

	public static string OpenAiHyphenated => "s" + "k-" + Body(20, 7) + "-" + Body(20, 8);

	public static string GitHubClassic(char kind) => "gh" + kind + "_" + Body(36, 9 + kind);

	public static string GitHubFineGrained => "github" + "_pat_" + Body(22, 10) + "_" + Body(59, 11);

	public static string AwsAccessKey => "AK" + "IA" + Upper(16, 12);

	public static string AwsTemporaryKey => "AS" + "IA" + Upper(16, 13);

	public static string AwsSecret => Base64(40, 14);

	public static string Jwt => "ey" + "J" + Body(20, 15) + ".ey" + "J" + Body(40, 16) + "." + Body(43, 17);

	public static string SlackBot => "xo" + "xb-" + Digits(12, 18) + "-" + Digits(12, 19) + "-" + Body(24, 20);

	public static string SlackApp => "xa" + "pp-1-" + Upper(11, 21) + "-" + Digits(13, 22) + "-" + Hex(64, 23);

	public static string SlackWebhookPath => "T" + Upper(10, 24) + "/B" + Upper(10, 25) + "/" + Body(24, 26);

	public static string GoogleApiKey => "AI" + "za" + Body(35, 27);

	public static string GoogleAccess => "ya" + "29." + Body(120, 28);

	public static string GoogleClientSecret => "GOC" + "SPX-" + Body(28, 29);

	public static string GoogleRefresh => "1/" + "/0" + Body(60, 30);

	public static string StripeLive => "sk" + "_live_" + Body(24, 31);

	public static string StripeRestricted => "rk" + "_live_" + Body(24, 32);

	public static string StripeTest => "sk" + "_test_" + Body(24, 33);

	public static string StripeWebhook => "wh" + "sec_" + Body(32, 34);

	public static string Npm => "np" + "m_" + Body(36, 35);

	public static string HuggingFace => "h" + "f_" + Body(34, 36);

	public static string GitLab => "gl" + "pat-" + Body(20, 37);

	public static string Memoria => "me" + "m_" + Body(8, 38) + "_" + Body(32, 39);

	public static string Digits(int length, int seed) => Generate("0123456789", length, seed);

	public static string PemBody(int lines = 3, int seed = 40)
	{
		StringBuilder builder = new();
		for (int i = 0; i < lines; i++)
		{
			builder.Append(Base64(64, seed + i)).Append('\n');
		}

		return builder.ToString().TrimEnd('\n');
	}

	private static string Generate(string alphabet, int length, int seed)
	{
		StringBuilder builder = new(length);
		uint state = (uint)seed * 2654435761u + 1;
		for (int i = 0; i < length; i++)
		{
			state = (state * 1664525u) + 1013904223u;
			builder.Append(alphabet[(int)((state >> 16) % (uint)alphabet.Length)]);
		}

		return builder.ToString();
	}
}
