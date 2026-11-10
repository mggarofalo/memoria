using System.Text;
using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>Shapes a review found stored as they came, and the near misses that must stay readable after they were fixed.</summary>
public sealed class RedactionGapTests
{
	private static readonly Redactor Redactor = new();

	/// <summary>Split, as in the other fixtures, so a scanner does not read these lines as a real command.</summary>
	private const string Curl = "cu" + "rl ";

	private static string GitHub => Synthetic.GitHubClassic('p');

	private static string Encoded(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

	public static TheoryData<string, string> Stored()
	{
		string aws = Synthetic.AwsSecret;
		string hex32 = Synthetic.Hex(32, 700);
		string password = Synthetic.Body(16, 701);
		return new TheoryData<string, string>
		{
			{ "AWS secret by a variable's name", "AWS_SECRET=" + aws },
			{ "Twilio with no operator", "twilio auth token " + hex32 },
			{ "Twilio with no operator", "Twilio token is " + hex32 + "." },
			{ "Twilio in upper-case hex", "TWILIO_AUTH_TOKEN=" + hex32.ToUpperInvariant() },
			{ "mysql, quoted", "mysql -u root -p'" + password + "' db" },
			{ "mysql, in a container", "docker exec db mysql -uroot -p" + password + " app" },
			{ "mysqldump", "mysqldump -uroot -p" + password + " app > a.sql" },
			{ "mysql, a short weak password", "mysql -uroot -pa1b2c3 db" },
			{ Curl + "-u, no user", Curl + "-u :" + password + " https://h.example.test" },
			{ Curl + "-u, attached", Curl + "-uadmin:" + password + " https://h.example.test" },
			{ Curl + "-u, in a container", "docker exec app " + Curl + "-u admin:" + password + " https://h.example.test" },
			{ "AWS secret by a variable's name", "aws_secret_access_key = " + aws },
			{ "AWS secret by a JSON name", "\"SecretAccessKey\": \"" + aws + "\"" },
			{ "AWS secret on the line after its id", "aws_access_key_id=" + Synthetic.AwsAccessKey + "\nsomething_else=" + aws },
			{ "AWS secret after its id, in prose", "the id " + Synthetic.AwsAccessKey + " has the secret " + aws },
			{ "Twilio auth token by name", "TWILIO_AUTH_TOKEN=" + hex32 },
			{ "Twilio auth token by name", "twilio auth token: " + hex32 },
			{ "Twilio auth token by name", "\"twilioAuthToken\": \"" + hex32 + "\"" },
			{ "Twilio auth token by name", "twilio_token = '" + hex32 + "'" },
			{ "Twilio SID and token together", "AC" + Synthetic.Hex(32, 702) + ":" + hex32 },
			{ "mysql with an attached password", "mysql -uroot -p" + password + " db" },
			{ "mysql with an attached password", "mysql -h db.internal -u app -p" + password },
			{ "mysql with an attached password", "$ mysql -p" + password },
			{ "mysql with an attached password", "ran `mysql -uroot -p" + password + " db` on the box" },
			{ "mysql with an attached password", "\"command\": \"mysql -p" + password + " -e 'select 1'\"" },
			{ "Doppler, personal", "dp" + ".pt." + Synthetic.Body(43, 703) },
			{ "Doppler, CLI", "dp" + ".ct." + Synthetic.Body(43, 704) },
			{ "Doppler, service account", "dp" + ".sa." + Synthetic.Body(43, 705) },
			{ "Doppler, SCIM", "dp" + ".scim." + Synthetic.Body(43, 706) },
			{ "Doppler, audit", "dp" + ".audit." + Synthetic.Body(43, 707) },
			{ "Square, OAuth secret", "sq" + "0csp-" + Synthetic.Body(43, 708) },
			{ "Square, production token", "EA" + "AA" + Synthetic.Body(60, 709) },
			{ Curl + "-u", Curl + "-u admin:" + password + " https://h.example.test" },
			{ Curl + "-u", Curl + "-X POST https://h.example.test -u admin:" + password },
			{ Curl + "-u", Curl + "-sSu admin:" + password + " https://h.example.test" },
			{ Curl + "-u", Curl + "--user admin:" + password + " https://h.example.test" },
			{ Curl + "-u", Curl + "--user=admin:" + password + " https://h.example.test" },
			{ Curl + "-u", Curl + "-u 'admin:" + password + "' https://h.example.test" },
			{ Curl + "-u", "$ " + Curl + "-u admin:" + password + " https://h.example.test" },
			{ Curl + "-u", "ran `" + Curl + "-u admin:" + password + " https://h.example.test` to check" },
			{ Curl + "-u", "\"command\": \"" + Curl + "-u admin:" + password + " https://h.example.test\"" },
			{ "percent-encoded GitHub token", "token=gh" + "p%5F" + Synthetic.Body(36, 710) },
			{ "percent-encoded GitHub token", "https://x.test/?t=g%68" + "p%5f" + Synthetic.Body(36, 711) + "&a=1" },
			{ "percent-encoded GitLab token", "t=gl" + "pat%2D" + Synthetic.Body(20, 712) },
			{ "base64 GitHub token", "the value is " + Encoded(GitHub) + " here" },
			{ "base64 GitHub token in a basic header", "Authorization: Basic " + Encoded("x-access-token:" + GitHub) },
			{ "base64 of a token with no padding", "data=" + Encoded(GitHub).TrimEnd('=') + "&a=1" },
			{ "URL-safe base64 of a token", "v=" + Encoded(Synthetic.Anthropic).Replace('+', '-').Replace('/', '_') },
		};
	}

	[Theory]
	[MemberData(nameof(Stored))]
	public void A_shape_found_in_review_is_not_stored(string name, string text)
	{
		string secret = SecretIn(text);

		string output = Redactor.Redact(text).Text;

		Assert.False(output.Contains(secret, StringComparison.Ordinal), $"{name}: survived in {output}");
	}

	public static TheoryData<string> StaysReadable()
	{
		return new TheoryData<string>
		{
			// Commit and lockfile hashes run through this corpus, and search needs them.
			"commit " + Synthetic.Hex(40, 720),
			"sha256: " + Synthetic.Hex(64, 721),
			"integrity " + Synthetic.Hex(64, 722) + " in the lockfile",
			"a 40-character value with no name: " + Synthetic.Base64(40, 723),

			// Each is base64 or percent-encoded, and holds nothing.
			"the value is aGVsbG8gd29ybGQsIHRoaXMgaXMgb25seSBwbGFpbiB0ZXh0Lg== here",
			"https://h.example.test/search?q=how%20to%20rotate%20a%20key%20safely",
			"a long_identifier_with_many_parts_that_decodes_to_nothing_useful",
			"ModelContextProtocolServerOptionsBuilderExtensions",
		};
	}

	[Theory]
	[MemberData(nameof(StaysReadable))]
	public void Ordinary_text_near_those_shapes_stays_readable(string text)
	{
		Assert.Equal(text, Redactor.Redact(text).Text);
	}

	[Fact]
	public void A_whole_memoria_key_is_replaced_and_its_secret_alone_is_not()
	{
		string secret = Synthetic.Hex(64, 730);

		Assert.Equal("[REDACTED:memoria-api-key]", Redactor.Redact("me" + "m_" + Synthetic.Hex(16, 731) + "_" + secret).Text);
		Assert.Equal(secret, Redactor.Redact(secret).Text);
	}

	[Fact]
	public void Curl_with_a_short_password_is_replaced()
	{
		Assert.Equal(Curl + "-u me:[REDACTED:curl-user-password] https://h.example.test", Redactor.Redact(Curl + "-u me:a1b2c3 https://h.example.test").Text);
	}

	[Theory]
	[InlineData("%20password%3A%20adminx")]
	[InlineData("cGFzc3dvcmQ6IGFkbWlueCBhbmQgbm90aGluZyBlbHNl")]
	public void Encoded_text_that_only_says_password_stays(string text)
	{
		Assert.Equal(text, Redactor.Redact(text).Text);
	}

	[Fact]
	public void A_base64_run_past_the_limit_is_not_decoded()
	{
		string text = Encoded(GitHub + new string(' ', 16 * 1024));

		Assert.Equal(text, Redactor.Redact(text).Text);
	}

	[Fact]
	public void A_percent_encoded_run_past_the_limit_is_not_decoded()
	{
		string text = "gh" + "p%5F" + Synthetic.Body(36, 732) + string.Concat(Enumerable.Repeat("%20", 6000));

		Assert.Equal(text, Redactor.Redact(text).Text);
	}

	/// <summary>Decoded base64 that reads as text is judged as the text would be: password=hunter22 is replaced written out, so it is replaced encoded.</summary>
	[Fact]
	public void Encoded_text_a_contextual_rule_would_take_is_replaced()
	{
		string text = "note " + Encoded("password=hunter22 in some document") + " here";

		Assert.Equal("note [REDACTED:base64-token] here", Redactor.Redact(text).Text);
	}

	[Fact]
	public void A_token_inside_encoded_binary_data_is_found()
	{
		byte[] data = [0, 255, 1, 254, 2, .. Encoding.UTF8.GetBytes(GitHub), 3, 253, 4, 252];
		string text = Convert.ToBase64String(data);

		Assert.DoesNotContain(text, Redactor.Redact(text).Text);
	}

	[Fact]
	public void A_path_after_an_access_key_id_is_not_taken_for_its_secret()
	{
		// Exactly the 40 characters the rule looks at, and only names.
		string text = "the id " + Synthetic.AwsAccessKey + " is used in src/config/settings/environments/product ok";

		Assert.Equal("the id [REDACTED:aws-access-key] is used in src/config/settings/environments/product ok", Redactor.Redact(text).Text);
	}

	public static TheoryData<string> DecodeForever()
	{
		return new TheoryData<string>
		{
			// Not valid UTF-8: a decoder that leaves these alone would be given the same text again.
			string.Concat(Enumerable.Repeat("%FF", 100)),
			"abcdefgh%C0%80ijklmnop%FE%FFqrstuvwx",

			// One layer per round, at the size limit: deep enough to overflow the stack with no limit on depth.
			"%" + string.Concat(Enumerable.Repeat("25", 8000)) + "41",
		};
	}

	/// <summary>A stack overflow cannot be caught, so a failure here takes the whole test run down with it.</summary>
	[Theory]
	[MemberData(nameof(DecodeForever))]
	public void Encoded_text_that_decodes_forever_ends(string text)
	{
		Assert.Equal(text, Redactor.Redact(text).Text);
	}

	[Fact]
	public void Base64_nested_past_the_limit_ends()
	{
		string text = GitHub;
		for (int i = 0; i < 12; i++)
		{
			text = Encoded(text);
		}

		Assert.NotNull(Redactor.Redact(text).Text);
	}

	[Fact]
	public void A_percent_escape_at_the_very_end_is_read()
	{
		string text = "gh" + "p%5F" + Synthetic.Body(35, 733) + "%41";

		Assert.DoesNotContain("%5F", Redactor.Redact(text).Text, StringComparison.Ordinal);
	}

	[Fact]
	public void Encoding_inside_encoding_is_found()
	{
		string text = Encoded(Encoded(GitHub));

		Assert.DoesNotContain(text, Redactor.Redact(text).Text);
	}

	/// <summary>The longest run with no punctuation: the part of each input that has to go.</summary>
	private static string SecretIn(string text)
	{
		string[] runs = text.Split([' ', '=', ':', '"', '\'', '\n', '`', '&', '?', '/', '.', '%', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
		return runs.MaxBy(static run => run.Length)!;
	}
}
