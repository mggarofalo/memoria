using System.Text;
using System.Text.RegularExpressions;

namespace Memoria.Core.Tests.Redaction;

/// <summary>A way of filling a token's random parts, chosen to look as little random as a real token can.</summary>
public enum TokenShape
{
	/// <summary>The provider's whole alphabet.</summary>
	Random,

	/// <summary>Its alphabet with no digit.</summary>
	NoDigits,

	/// <summary>Lower-case letters only, or a-f for hex.</summary>
	LowerOnly,

	/// <summary>Upper-case letters only.</summary>
	UpperOnly,

	/// <summary>Mixed-case letters split into short pieces by the provider's own separators.</summary>
	SeparatorsInBody,

	/// <summary>Lower-case letters split the same way: the closest a real token gets to a name.</summary>
	LowerWithSeparators,

	/// <summary>Letters with a separator first and last.</summary>
	SeparatorsAtBoundaries,
}

/// <summary>
/// One rule's real format. <c>{n}</c> in <see cref="Template"/> is n characters from the alphabet, and
/// <see cref="Context"/> is the text around it, with <c>{S}</c> for the token.
/// </summary>
public sealed record TokenSpec(string Rule, string Template, string Alphabet, string Context = "the key is {S} here")
{
	public override string ToString() => $"{Rule} {Template}";
}

/// <summary>
/// Every rule whose decision reads a value's shape, in real formats. Prefixes are split, as in <see cref="Synthetic"/>,
/// so nothing here reads as a credential to a scanner. Rules with no shape judgement, such as private key blocks and
/// the digest response, have nothing for a shape to get past, so they are not here.
/// </summary>
public static partial class TokenShapes
{
	public const string Alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
	public const string UpperDigits = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
	public const string UrlSafe = Alnum + "-_";
	public const string Hex = "0123456789abcdef";
	public const string Base64 = Alnum + "+/";

	public static readonly IReadOnlyList<TokenSpec> Prefixed =
	[
		new("anthropic-key", "sk-" + "ant-" + "api03-{93}", UrlSafe),
		new("anthropic-key", "sk-" + "ant-{20}", UrlSafe),
		new("openai-key", "s" + "k-{48}", Alnum),
		new("openai-key", "sk-" + "proj-{156}", UrlSafe),
		new("openai-key", "sk-" + "svcacct-{40}", UrlSafe),
		new("openai-key", "sk-" + "admin-{40}", UrlSafe),
		new("github-token", "gh" + "p_{36}", Alnum),
		new("github-token", "github" + "_pat_{22}_{59}", Alnum),
		new("aws-access-key", "AK" + "IA{16}", UpperDigits),
		new("aws-access-key", "AS" + "IA{16}", UpperDigits),
		new("jwt", "ey" + "J{20}.ey" + "J{40}.{43}", UrlSafe),
		new("slack-token", "xo" + "xb-123456789012-123456789012-{24}", Alnum),
		new("slack-token", "xa" + "pp-1-{11}-1234567890123-{64}", Alnum),
		new("google-api-key", "AI" + "za{35}", UrlSafe),
		new("google-oauth-token", "ya" + "29.{120}", UrlSafe),
		new("google-oauth-token", "GOC" + "SPX-{28}", UrlSafe),
		new("stripe-key", "sk" + "_live_{24}", Alnum),
		new("stripe-key", "rk" + "_live_{24}", Alnum),
		new("stripe-key", "wh" + "sec_{32}", Alnum),
		new("npm-token", "np" + "m_{36}", Alnum),
		new("huggingface-token", "h" + "f_{34}", Alnum),
		new("gitlab-token", "gl" + "pat-{20}", UrlSafe),
		new("gitlab-token", "gl" + "pat-{40}", UrlSafe),
		new("gitlab-token", "gl" + "dt-{20}", UrlSafe),
		new("gitlab-token", "gl" + "rt-{20}", UrlSafe),
		new("gitlab-token", "gl" + "ptt-{40}", UrlSafe),
		new("gitlab-token", "gl" + "soat-{20}", UrlSafe),
		new("gitlab-token", "gl" + "cbt-{20}", UrlSafe),
		new("gitlab-token", "gl" + "oas-{40}", UrlSafe),
		new("gitlab-token", "gl" + "agent-{50}", UrlSafe),
		new("gitlab-token", "gl" + "imt-{20}", UrlSafe),
		new("gitlab-token", "GR" + "1348941{20}", UrlSafe),
		new("memoria-api-key", "me" + "m_{16}_{64}", Hex),
		new("sendgrid-key", "S" + "G.{22}.{43}", UrlSafe),
		new("twilio-key", "A" + "C{32}", Hex),
		new("twilio-key", "S" + "K{32}", Hex),
		new("mailgun-key", "ke" + "y-{32}", Hex),
		new("digitalocean-token", "do" + "p_v1_{64}", Hex),
		new("vercel-token", "vc" + "p_{24}", Alnum),
		new("supabase-token", "sb" + "p_{40}", Alnum),
		new("azure-client-secret", "{3}8" + "Q~{31}", Alnum),
		new("pypi-token", "py" + "pi-{100}", UrlSafe),
		new("pypi-token", "py" + "pi-{32}", UrlSafe),
		new("docker-token", "dc" + "kr_pat_{27}", UrlSafe),
		new("telegram-token", "1234567890:{35}", UrlSafe),
		new("discord-token", "M{25}.{6}.{38}", UrlSafe),
		new("notion-token", "sec" + "ret_{43}", Alnum),
		new("notion-token", "nt" + "n_{46}", Alnum),
		new("linear-token", "li" + "n_api_{40}", Alnum),
		new("plane-token", "pl" + "ane_api_{32}", Alnum),
		new("age-secret-key", "AGE-" + "SECRET-KEY-1{58}", UpperDigits),
		new("vault-token", "hv" + "s.{24}", UrlSafe),
		new("vault-token", "hv" + "b.{90}", UrlSafe),
		new("databricks-token", "da" + "pi{32}", Hex),
		new("shopify-token", "sh" + "pat_{32}", Hex),
		new("tailscale-key", "ts" + "key-auth-{20}-{32}", Alnum),
		new("tailscale-key", "ts" + "key-{20}", UrlSafe.Replace("_", "", StringComparison.Ordinal)),
		new("atlassian-token", "AT" + "ATT3{180}", UrlSafe),
		new("groq-key", "gs" + "k_{52}", Alnum),
		new("xai-key", "xa" + "i-{80}", Alnum),
		new("langsmith-key", "ls" + "v2_pt_{32}_{10}", Alnum),
		new("onepassword-token", "op" + "s_{80}", UrlSafe),
		new("cloudflare-token", "cf" + "ut_{40}", UrlSafe),
		new("sentry-token", "sn" + "trys_{64}", UrlSafe),
		new("grafana-token", "gl" + "sa_{32}_{8}", Alnum),
		new("grafana-token", "gl" + "c_{60}", UrlSafe),
		new("figma-token", "fi" + "gd_{40}", UrlSafe),
		new("tavily-key", "tv" + "ly-{32}", UrlSafe),
		new("perplexity-key", "pp" + "lx-{48}", Alnum),
		new("replicate-token", "r" + "8_{37}", Alnum),
		new("render-key", "rn" + "d_{32}", Alnum),
		new("netlify-token", "nf" + "p_{36}", Alnum),
		new("planetscale-token", "ps" + "cale_tkn_{43}", UrlSafe),
		new("doppler-token", "dp" + ".st.{40}", UrlSafe),
		new("doppler-token", "dp" + ".pt.{43}", Alnum),
		new("doppler-token", "dp" + ".ct.{43}", Alnum),
		new("doppler-token", "dp" + ".sa.{43}", Alnum),
		new("doppler-token", "dp" + ".scim.{43}", Alnum),
		new("doppler-token", "dp" + ".audit.{43}", Alnum),
		new("alibaba-key", "LT" + "AI{20}", Alnum),
		new("square-token", "sq" + "0atp-{22}", UrlSafe),
		new("square-token", "sq" + "0csp-{43}", UrlSafe),
		new("square-production-token", "EA" + "AA{60}", UrlSafe),
		new("hubspot-token", "pa" + "t-na1-{8}-{4}-{4}-{4}-{12}", Hex),
		new("atlassian-cookie-token", "AT" + "CTT3{180}", UrlSafe),
		new("supabase-secret", "sb" + "_secret_{32}", UrlSafe),
		new("facebook-token", "EA" + "AC{100}", Alnum),
		new("huggingface-org-token", "ap" + "i_org_{34}", Alnum),
		new("gitlab-feature-flag-token", "gl" + "ffct-{20}", UrlSafe),
		new("slack-webhook", "T{10}/B{10}/{24}", Alnum, "hooks.slack.com/services/{S}"),
		new("discord-webhook", "{68}", UrlSafe, "https://discord.com/api/webhooks/1234567890/{S}"),
		new("teams-webhook", "{36}@{36}/IncomingWebhook/{32}/{36}", Alnum, "https://x.webhook.office.com/webhookb2/{S}"),
	];

	/// <summary>Rules that know a secret by what surrounds it. The value has no prefix to speak for it.</summary>
	public static readonly IReadOnlyList<TokenSpec> Contextual =
	[
		new("aws-secret-key", "{40}", Base64, "aws_secret_access_key={S}"),
		new("aws-secret-near-id", "{40}", Base64, "the id AK" + "IAABCDEFGHIJKLMNOP has the secret {S}"),
		new("twilio-auth-token", "{32}", Hex, "twilio auth token {S}"),
		new("azure-storage-key", "{88}", Base64, "AccountKey={S};EndpointSuffix=core.windows.net"),
		new("azure-sas-signature", "{44}", UrlSafe, "https://x.blob.core.windows.net/c?sv=2024&sig={S}"),
		new("presigned-url-signature", "{64}", Hex, "https://b.s3.amazonaws.com/k?X-Amz-Signature={S}"),
		new("memoria-shared-secret", "{43}", UrlSafe, "MEMORIA_QMD_SECRET={S}"),
		new("url-password", "{24}", UrlSafe, "postgres://app:{S}@db.internal/main"),
		new("url-token", "{40}", UrlSafe, "https://{S}@git.example.test/o/r.git"),
		new("userinfo-password", "{24}", UrlSafe, "app:{S}@db.internal"),
		new("connection-string-password", "{24}", UrlSafe, "Server=db;Password={S};"),
		new("authorization-header", "{40}", UrlSafe, "Authorization: Bearer {S}"),
		new("bearer-token", "{40}", UrlSafe, "sent with bearer {S} today"),
		new("query-credential", "{40}", UrlSafe, "https://h.example.test/p?key={S}"),
		new("cu" + "rl-user-password", "{24}", UrlSafe, "cu" + "rl -u admin:{S} https://h.example.test"),
		new("cookie-header", "{40}", UrlSafe, "Cookie: session={S}"),
		new("cu" + "rl-cookie", "{40}", UrlSafe, "cu" + "rl -b session={S} https://h.example.test"),
		new("session-cookie", "{32}", UrlSafe, "PHPSESSID={S}"),
		new("command-password", "{24}", UrlSafe, "sshpass -p {S} ssh me@h"),
		new("mysql-password", "{24}", UrlSafe, "mysql -u root -p{S} db"),
		new("mongo-password", "{24}", Alnum, "mongosh -u root -p {S} --quiet"),
		new("basic-auth-literal", "{24}", UrlSafe, "`Basic ${btoa(\"admin:{S}\")}`"),
		new("securestring-password", "{24}", UrlSafe, "ConvertTo-SecureString \"{S}\" -AsPlainText"),
		new("credential-argument", "{24}", UrlSafe, "login(\"u\", \"{S}\")"),
		new("netrc-password", "{24}", UrlSafe, "machine h login u password {S}"),
		new("xml-secret", "{24}", UrlSafe, "<password>{S}</password>"),
		new("sql-password", "{24}", UrlSafe, "IDENTIFIED BY '{S}'"),
		new("secret-flag", "{40}", UrlSafe, "tool --api-key {S}"),
		new("secret-assignment", "{40}", UrlSafe, "api_key={S}"),
		new("secret-assignment", "{24}", UrlSafe, "password: \"{S}\""),
		new("secret-assignment", "{40}", UrlSafe, "export DEPLOY_TOKEN={S}"),
	];

	public static IEnumerable<TokenSpec> All => Prefixed.Concat(Contextual);

	/// <summary>Whether this format can take this shape at all. A shape a format cannot hold is not a test of it.</summary>
	public static bool Applies(TokenSpec spec, TokenShape shape)
	{
		bool separators = spec.Alphabet.Contains('-') || spec.Alphabet.Contains('_');
		bool upper = spec.Alphabet.Contains('Z');
		bool lower = spec.Alphabet.Contains('z') || spec.Alphabet == Hex;
		return shape switch
		{
			TokenShape.Random or TokenShape.NoDigits => true,
			TokenShape.LowerOnly => lower,
			TokenShape.UpperOnly => upper,
			TokenShape.LowerWithSeparators => separators && lower,
			_ => separators && upper,
		};
	}

	/// <summary>One token. Only this and the seed decide it, so a seed reproduces a miss exactly.</summary>
	public static string Generate(TokenSpec spec, TokenShape shape, Random random)
	{
		return PartPattern().Replace(spec.Template, part => Fill(spec, shape, int.Parse(part.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), random));
	}

	/// <summary>The same input always gives the same number on every platform and run, unlike string.GetHashCode.</summary>
	public static int CellSeed(int seed, TokenSpec spec, TokenShape shape)
	{
		uint hash = 2166136261u ^ (uint)seed;
		foreach (char c in $"{spec.Rule}|{spec.Template}|{spec.Context}|{shape}")
		{
			hash = (hash ^ c) * 16777619u;
		}

		return (int)(hash & 0x7FFFFFFF);
	}

	private static string Fill(TokenSpec spec, TokenShape shape, int length, Random random)
	{
		string letters = spec.Alphabet == Hex ? "abcdef" : "abcdefghijklmnopqrstuvwxyz";
		string mixed = string.Concat(spec.Alphabet.Where(char.IsAsciiLetter));
		char separator = spec.Alphabet.Contains('-') ? '-' : '_';
		return shape switch
		{
			TokenShape.Random => Pick(spec.Alphabet, length, random),
			TokenShape.NoDigits => Pick(string.Concat(spec.Alphabet.Where(static c => !char.IsAsciiDigit(c))), length, random),
			TokenShape.LowerOnly => Pick(letters, length, random),
			TokenShape.UpperOnly => Pick("ABCDEFGHIJKLMNOPQRSTUVWXYZ", length, random),
			TokenShape.SeparatorsInBody => Separated(mixed, separator, length, random),
			TokenShape.LowerWithSeparators => Separated(letters, separator, length, random),
			_ => length < 3 ? Pick(mixed, length, random) : separator + Pick(mixed, length - 2, random) + separator,
		};
	}

	private static string Separated(string alphabet, char separator, int length, Random random)
	{
		StringBuilder builder = new(length);
		while (builder.Length < length)
		{
			int run = Math.Min(random.Next(4, 11), length - builder.Length);
			builder.Append(Pick(alphabet, run, random));
			if (builder.Length < length - 1)
			{
				builder.Append(separator);
			}
		}

		return builder.ToString();
	}

	private static string Pick(string alphabet, int length, Random random)
	{
		StringBuilder builder = new(length);
		for (int i = 0; i < length; i++)
		{
			builder.Append(alphabet[random.Next(alphabet.Length)]);
		}

		return builder.ToString();
	}

	[GeneratedRegex(@"\{(\d+)\}", RegexOptions.CultureInvariant)]
	private static partial Regex PartPattern();
}
