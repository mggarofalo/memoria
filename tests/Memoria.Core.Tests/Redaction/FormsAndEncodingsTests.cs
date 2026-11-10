using System.Text;
using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// Provider forms a review listed as stored, and encodings: what is decoded and replaced, and what is left as it came on purpose.
/// </summary>
public sealed class FormsAndEncodingsTests
{
	private static string Redact(string text) => Redactor.Shared.RedactFragment(text).Text;

	private static string Encoded(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

	private static string GitHub => Synthetic.GitHubClassic('p');

	private static string Eaa(char fourth, int length, int seed) => "EA" + "A" + fourth + Synthetic.Body(length, seed);

	[Theory]
	[InlineData('A', 60, "square-production-token")]
	[InlineData('A', 96, "facebook-token")]
	[InlineData('A', 120, "facebook-token")]
	[InlineData('B', 150, "facebook-token")]
	[InlineData('G', 200, "facebook-token")]
	[InlineData('Z', 96, "facebook-token")]
	[InlineData('C', 40, "facebook-token")]
	[InlineData('C', 60, "facebook-token")]
	public void A_facebook_or_square_token_goes_at_its_lengths(char fourth, int length, string rule)
	{
		string token = Eaa(fourth, length, 7001 + length);

		Assert.Equal($"token [REDACTED:{rule}] here", Redact("token " + token + " here"));
		Assert.Equal($"{{\"t\":\"[REDACTED:{rule}]\"}}", Redact("{\"t\":\"" + token + "\"}"));
		Assert.Equal($"access_token=[REDACTED:{rule}]&a=1", Redact("access_token=" + token + "&a=1"));
	}

	[Theory]
	[InlineData("/{T}")]
	[InlineData("+{T}")]
	[InlineData("{T}+abc")]
	[InlineData("{T}/abc")]
	[InlineData("{T}=")]
	public void Eaa_inside_base64_data_stays(string template)
	{
		string text = "iVBORw0KGgo" + template.Replace("{T}", Eaa('A', 120, 7002), StringComparison.Ordinal);

		Assert.Equal(text, Redact(text));
	}

	[Theory]
	[InlineData('A', 39)]
	[InlineData('A', 61)]
	[InlineData('A', 95)]
	[InlineData('B', 60)]
	[InlineData('C', 39)]
	[InlineData('a', 120)]
	[InlineData('1', 120)]
	public void A_short_or_lower_case_eaa_run_stays(char fourth, int length)
	{
		string text = "id " + Eaa(fourth, length, 7003) + " end";

		Assert.Equal(text, Redact(text));
	}

	[Theory]
	[InlineData("SHA256: EA" + "AB61A6F74D0E5C3B2A19887766554433221100FFEEDDCCBBAA99887766554433221100FFEEDDCCBBAA9988776655443322110A")]
	[InlineData("hash EA" + "AFC4E9D8C7B6A5F4E3D2C1B0A9F8E7D6C5B4A3F2E1D0C9B8A7F6E5D4C3B2A1F0E9D8C7B6A5F4E3D2C1B0A9F8E7D6C5B4A3F2E1D0")]
	[InlineData("{\"blob\":\"EA" + "AAAAAAGw5Mm5oQ2xpZW50SGVsbG9Xb3JsZDEyMzQ1Njc4OTBhYmNkZWZnaGlqa2xtbm9wcXJzdHV2d3h5ejEyMzQ1Njc4OTBBQkNERUZHSElKSw\"}")]
	public void A_hash_or_base64_data_that_starts_like_a_facebook_token_stays(string text)
	{
		Assert.Equal(text, Redact(text));
	}

	/// <summary>An EAAC token keeps the boundaries it had, so one after a slash, as in a path or URL, still goes.</summary>
	[Fact]
	public void An_eaac_token_after_a_slash_goes()
	{
		Assert.Equal("path/[REDACTED:facebook-token]/x", Redact("path/" + Eaa('C', 60, 7009) + "/x"));
	}

	/// <summary>A publishable key is public by design: it goes in the page a browser loads. Its secret key, sk_live_, is replaced.</summary>
	[Fact]
	public void A_stripe_publishable_key_stays()
	{
		string text = "pk" + "_live_" + Synthetic.Body(99, 7004);

		Assert.Equal(text, Redact(text));
		Assert.Equal("[REDACTED:stripe-key]", Redact("sk" + "_live_" + Synthetic.Body(99, 7005)));
	}

	[Theory]
	[InlineData("Authorization: SSWS {S}")]
	[InlineData("-H \"Authorization: SSWS {S}\" https://dev.example.test/api/v1/users")]
	[InlineData("{\"Authorization\":\"SSWS {S}\"}")]
	[InlineData("the api token SSWS {S} works")]
	[InlineData("authorization: ssws {S}")]
	public void An_okta_token_goes(string template)
	{
		string token = "00" + Synthetic.Body(40, 7006);

		Assert.Equal(template.Replace("{S}", "[REDACTED:okta-token]", StringComparison.Ordinal), Redact(template.Replace("{S}", token, StringComparison.Ordinal)));
	}

	[Theory]
	[InlineData("SSWS 00short")]
	[InlineData("SSWS ${OKTA_TOKEN}")]
	[InlineData("set the SSWS header")]
	public void An_okta_scheme_without_a_token_stays(string text)
	{
		Assert.Equal(text, Redact(text));
	}

	[Fact]
	public void An_okta_token_glued_to_more_text_stays()
	{
		string text = "SSWS 00" + Synthetic.Body(41, 7007);

		Assert.Equal(text, Redact(text));
	}

	[Theory]
	[InlineData("https://contoso.webhook.office.com/webhookb2/")]
	[InlineData("https://outlook.office.com/webhook/")]
	[InlineData("https://outlook.office365.com/webhook/")]
	[InlineData("https://OUTLOOK.OFFICE.COM/webhook/")]
	public void A_teams_webhook_goes_on_every_host(string host)
	{
		string path = "01234567-89ab-cdef-0123-456789abcdef@01234567-89ab-cdef-0123-456789abcdef/IncomingWebhook/" + Synthetic.Hex(32, 7008) + "/01234567-89ab-cdef-0123-456789abcdef";

		Assert.Equal(host + "[REDACTED:teams-webhook]", Redact(host + path));
	}

	[Theory]
	[InlineData("https://outlook.office.com/webhook/")]
	[InlineData("https://outlook.office.com/mail/inbox/id/AAQkAGI2TG93AAA")]
	public void An_outlook_link_with_no_webhook_stays(string text)
	{
		Assert.Equal(text, Redact(text));
	}

	[Theory]
	[InlineData("DB_HOST=db\nDB_PASSWORD=Tr0ub4dor-x9Q\n")]
	[InlineData("{\"auths\":{\"registry.example.test\":{\"username\":\"me\",\"password\":\"Tr0ub4dor-x9Q\"}}}")]
	[InlineData("note password=hunter22 in some document")]
	[InlineData("connect as admin:Tr0ub4dor-x9Q@db.internal now")]
	[InlineData("# configuración de producción, contraseña única\nDB_PASSWORD=Tr0ub4dor-x9Q\n")]
	[InlineData("# 本番環境の設定ファイルです。秘密情報を含みます\nDB_PASSWORD=Tr0ub4dor-x9Q\n")]
	[InlineData("KEY=1\r\n\tDB_PASSWORD=Tr0ub4dor-x9Q\r\n")]
	[InlineData("docker login -u me -p Tr0ub4dor-x9Q registry.example.test")]
	[InlineData("mysql -u root -pTr0ub4dor-x9Q mydb")]
	[InlineData("CREATE USER bob IDENTIFIED BY 'Tr0ub4dor-x9Q';")]
	[InlineData("cu" + "rl -su admin:Tr0ub4dor-x9Q https://api.example.test/v1/items")]
	[InlineData("network={\n  ssid=\"home\"\n  psk=\"Tr0ub4dor-x9Q\"\n}\n")]
	[InlineData("DATABASE_URL=postgres%3A%2F%2Fapp%3ATr0ub4dor-x9Q%40db.internal%2Fmain")]
	public void Base64_of_text_holding_a_credential_goes(string text)
	{
		string encoded = Encoded(text);

		Assert.Equal("data: [REDACTED:base64-token] end", Redact("data: " + encoded + " end"));
	}

	[Theory]
	[InlineData("password: adminx and nothing else")]
	[InlineData("the password is required for every login")]
	[InlineData("max_tokens: 4096 and temperature: 0.2")]
	public void Base64_of_text_with_no_credential_stays(string text)
	{
		string encoded = Encoded(text);

		Assert.Equal(encoded, Redact(encoded));
	}

	/// <summary>A URL's query is prose more often than configuration, so percent-encoded text is judged only by tokens.</summary>
	[Theory]
	[InlineData("q=note%20password%20hunter22%20in%20some%20document")]
	[InlineData("%20password%3A%20adminx")]
	public void Percent_encoded_text_is_not_judged_by_what_it_says(string text)
	{
		Assert.Equal(text, Redact(text));
	}

	[Fact]
	public void Base64_of_binary_data_is_not_read_as_text()
	{
		byte[] data = [.. Enumerable.Range(0, 64).Select(static i => (byte)(i * 7 % 32)), .. Encoding.UTF8.GetBytes("password=hunter22")];
		string text = Convert.ToBase64String(data);

		Assert.Equal(text, Redact(text));
	}

	/// <summary>Up to one control character in twenty is still text, as a stray escape in a config is. More is binary data.</summary>
	[Theory]
	[InlineData(1, true)]
	[InlineData(3, false)]
	public void Text_allows_a_few_control_characters(int controls, bool replaced)
	{
		string text = new string('\u0001', controls) + "DB_PASSWORD=Tr0ub4dor-x9Q and some more padding text\n";
		string encoded = Encoded(text);

		Assert.Equal(replaced ? "[REDACTED:base64-token]" : encoded, Redact(encoded));
	}

	[Fact]
	public void Base64_of_text_is_judged_through_four_layers_and_no_more()
	{
		string four = "DB_PASSWORD=Tr0ub4dor-x9Q and the rest of the file\n";
		for (int i = 0; i < 4; i++)
		{
			four = Encoded(four);
		}

		string five = Encoded(four);

		Assert.Equal("[REDACTED:base64-token]", Redact(four));
		Assert.Equal(five, Redact(five));
	}

	[Fact]
	public void Base64_of_utf16_text_with_a_byte_order_mark_is_read()
	{
		string text = Convert.ToBase64String([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("$t = '" + GitHub + "'")]);

		Assert.Equal("[REDACTED:base64-token]", Redact(text));
	}

	[Fact]
	public void Base64_of_utf16_text_is_read()
	{
		string text = Convert.ToBase64String(Encoding.Unicode.GetBytes("$t = '" + GitHub + "'"));

		Assert.Equal("[REDACTED:base64-token]", Redact(text));
	}

	[Fact]
	public void Base64_of_utf16_text_with_a_few_wide_characters_is_read()
	{
		string text = Convert.ToBase64String(Encoding.Unicode.GetBytes("# set token \u2014 do not share\n$t = \u2018" + GitHub + "\u2019"));

		Assert.Equal("[REDACTED:base64-token]", Redact(text));
	}

	[Fact]
	public void An_upper_case_base32_run_that_starts_like_a_facebook_token_stays()
	{
		string text = "EA" + "AB" + Synthetic.Upper(124, 7010);

		Assert.Equal(text, Redact(text));
	}

	[Fact]
	public void Utf16_with_an_odd_byte_count_is_read_as_latin1()
	{
		byte[] bytes = [.. Encoding.Unicode.GetBytes(GitHub), 65];

		Assert.Equal(Convert.ToBase64String(bytes), Redact(Convert.ToBase64String(bytes)));
	}

	/// <summary>Four layers are looked through. A fifth is past the limit that keeps decoding bounded, so it is stored as it came.</summary>
	[Fact]
	public void Base64_is_looked_through_four_layers_and_no_more()
	{
		string four = GitHub;
		for (int i = 0; i < 4; i++)
		{
			four = Encoded(four);
		}

		string five = Encoded(four);

		Assert.Equal("[REDACTED:base64-token]", Redact(four));
		Assert.Equal(five, Redact(five));
	}

	/// <summary>Reversed, a token has no format to know it by, and reading every run backwards would double the work for nothing else.</summary>
	[Fact]
	public void A_reversed_token_is_stored_as_it_came()
	{
		string reversed = new([.. GitHub.Reverse()]);

		Assert.Equal(reversed, Redact(reversed));
	}

	[Fact]
	public void A_token_written_with_doubled_unicode_escapes_goes()
	{
		string token = GitHub;
		string escaped = token[..3] + "\\\\u005f" + token[4..];

		Assert.DoesNotContain(token[4..], Redact("{\"a\":\"" + escaped + "\"}"), StringComparison.Ordinal);
	}
}
