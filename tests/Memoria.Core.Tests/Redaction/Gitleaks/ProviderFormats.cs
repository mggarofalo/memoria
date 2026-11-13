namespace Memoria.Core.Tests.Redaction.Gitleaks;

/// <summary>
/// Tokens of providers known through gitleaks's rules, each written to the provider's documented format by hand rather than drawn from the
/// rule's own pattern, as <see cref="ImportedCases"/> draws its samples. A sample drawn from a pattern always matches it, so those only show
/// the import kept the rules; these show the rules know the tokens. Each value is put together here, so this repository's own scan finds none.
/// A provider whose upstream rule knows its token only beside the provider's name, such as Telegram, Mailgun or New Relic, is not here:
/// such a token is caught under a credential's name, by <c>secret-assignment</c>, and not alone in a sentence.
/// </summary>
public static class ProviderFormats
{
	private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
	private const string Base64Url = Base62 + "-_";
	private const string UpperAlnum = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
	private const string Bech32 = "023456789ACDEFGHJKLMNPQRSTUVWXYZ";

	/// <summary>Each provider's token, by its documented prefix and body.</summary>
	public static IReadOnlyList<(string Provider, string Token)> Tokens { get; } =
	[
		("sendgrid", "S" + "G." + Of(Base64Url, 22, 1) + "." + Of(Base64Url, 43, 2)),
		("twilio api key", "S" + "K" + Synthetic.Hex(32, 3)),
		("digitalocean personal access token", "dop" + "_v1_" + Synthetic.Hex(64, 4)),
		("digitalocean oauth token", "doo" + "_v1_" + Synthetic.Hex(64, 5)),
		("shopify access token", "shp" + "at_" + Synthetic.Hex(32, 6)),
		("shopify shared secret", "shp" + "ss_" + Synthetic.Hex(32, 7)),
		("databricks token", "da" + "pi" + Synthetic.Hex(32, 8)),
		("doppler service token", "dp" + ".pt." + Of(Base62, 43, 9)),
		("linear api key", "lin" + "_api_" + Of(Base62, 40, 10)),
		("postman api key", "PM" + "AK-" + Synthetic.Hex(24, 11) + "-" + Synthetic.Hex(34, 12)),
		("pulumi access token", "pu" + "l-" + Synthetic.Hex(40, 13)),
		("vault service token", "hv" + "s." + Of(Base64Url, 95, 14)),
		("terraform cloud token", Of(Base62, 14, 15) + ".atlas" + "v1." + Of(Base62, 67, 16)),
		("grafana service account token", "gl" + "sa_" + Of(Base62, 32, 17) + "_" + Synthetic.Hex(8, 18)),
		("sentry user token", "sntr" + "yu_" + Synthetic.Hex(64, 19)),
		("square access token", "sq0" + "atp-" + Of(Base64Url, 22, 20)),
		("age secret key", "AGE-" + "SECRET-KEY-1" + Of(Bech32, 58, 23)),
		("pypi upload token", "pyp" + "i-AgEIcHlwaS5vcmc" + Of(Base64Url, 70, 24)),
		("atlassian api token", "ATA" + "TT3" + Of(Base64Url, 186, 25)),
		("dynatrace api token", "dt0" + "c01." + Of(UpperAlnum, 24, 26) + "." + Of(UpperAlnum, 64, 27)),
		("planetscale token", "pscale" + "_tkn_" + Of(Base62, 43, 32)),
		("prefect api key", "pn" + "u_" + Of(Base62, 36, 33)),
		("readme api key", "rd" + "me_" + Synthetic.Hex(70, 34)),
		("rubygems api key", "ruby" + "gems_" + Synthetic.Hex(48, 35)),
		("clojars token", "CLOJ" + "ARS_" + Of(Base62, 60, 36)),
		("azure ad client secret", Of(Base62, 3, 37) + "8Q~" + Of(Base64Url, 34, 38)),
		("1password service account token", "ops" + "_eyJ" + Of(Base62, 600, 39)),
	];

	/// <summary>Where a transcript puts a token: an export, a JSON value, a YAML value, a sentence, and a code span.</summary>
	public static IReadOnlyList<string> Contexts { get; } =
	[
		"export SERVICE_CREDENTIAL={T}",
		"{\"credential\": \"{T}\"}",
		"credential: {T}",
		"I set it to {T} and ran the job again.",
		"Run `deploy --credential {T}` from the repo root.",
	];

	private static string Of(string alphabet, int length, int seed)
	{
		Random random = new(9000 + seed);
		return string.Create(length, (alphabet, random), static (span, state) =>
		{
			for (int i = 0; i < span.Length; i++)
			{
				span[i] = state.alphabet[state.random.Next(state.alphabet.Length)];
			}
		});
	}
}
