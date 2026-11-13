using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>The two judgements the plain rules make: whether a name holds a credential, and whether what follows it is a value.</summary>
public sealed class NameAndValueTests
{
	[Theory]
	[InlineData("password")]
	[InlineData("db_password")]
	[InlineData("PGPASSWORD")]
	[InlineData("new_password")]
	[InlineData("passphrase")]
	[InlineData("SSHPASS")]
	[InlineData("psk")]
	[InlineData("api_key")]
	[InlineData("apiKey")]
	[InlineData("x-api-key")]
	[InlineData("CONSUMER_KEY")]
	[InlineData("client-key-data")]
	[InlineData("access_token")]
	[InlineData("GITHUB_TOKEN")]
	[InlineData("csrfToken")]
	[InlineData("DB_PASS")]
	[InlineData("user_pass")]
	[InlineData("AUTH_HEADER")]
	[InlineData("client_secret")]
	[InlineData("secret_id")]
	[InlineData("auth")]
	[InlineData("Authorization")]
	[InlineData("credentials")]
	[InlineData("API key")]
	public void A_name_that_holds_a_credential_is_one(string name)
	{
		Assert.True(SecretKeyNames.IsSecretName(name));
	}

	[Theory]
	[InlineData("max_tokens")]
	[InlineData("input_tokens")]
	[InlineData("pageToken")]
	[InlineData("cancellationToken")]
	[InlineData("token_count")]
	[InlineData("tokenizer")]
	[InlineData("password_file")]
	[InlineData("passwordStdin")]
	[InlineData("secret_name")]
	[InlineData("token_id")]
	[InlineData("change_password")]
	[InlineData("isPassword")]
	[InlineData("author")]
	[InlineData("bypass")]
	[InlineData("BYPASS")]
	[InlineData("COMPASS")]
	[InlineData("sort_key")]
	[InlineData("public_key")]
	[InlineData("the key")]
	[InlineData("key")]
	[InlineData("pass")]
	[InlineData("keys")]
	[InlineData("___")]
	[InlineData("")]
	public void A_name_that_describes_counts_or_names_something_else_is_not(string name)
	{
		Assert.False(SecretKeyNames.IsSecretName(name));
	}

	[Theory]
	[InlineData("--pass", true)]
	[InlineData("--key", true)]
	[InlineData("-p", false)]
	[InlineData("--token", true)]
	[InlineData("--api-key", true)]
	[InlineData("--token-file", false)]
	[InlineData("--keyboard", false)]
	[InlineData("--author", false)]
	public void A_bare_pass_or_key_flag_takes_a_credential(string flag, bool expected)
	{
		Assert.Equal(expected, SecretKeyNames.IsSecretFlag(flag));
	}

	[Theory]
	[InlineData("hunter2", true)]
	[InlineData("abc", true)]
	[InlineData("correct horse battery staple", true)]
	[InlineData("${PREFIX}abc123", true)]
	[InlineData("<token>abc123", true)]
	[InlineData("ab[REDACTED:jwt]cd", true)]
	[InlineData("ab", false)]
	[InlineData("required", false)]
	[InlineData("None", false)]
	[InlineData("xxxx", false)]
	[InlineData("****", false)]
	[InlineData("$TOKEN", false)]
	[InlineData("${TOKEN}", false)]
	[InlineData("${TOKEN", false)]
	[InlineData("$(cat token.txt)", false)]
	[InlineData("%TOKEN%", false)]
	[InlineData("{{ token }}", false)]
	[InlineData("<token>", false)]
	[InlineData("<token", false)]
	[InlineData("process.env.TOKEN", false)]
	[InlineData("os.environ['TOKEN']", false)]
	[InlineData("getPassword()", false)]
	[InlineData("/run/secrets/token", true)]
	[InlineData("./token", false)]
	[InlineData("~/.token", false)]
	[InlineData("C:\\keys\\token", false)]
	[InlineData("https://example.test/oauth/token", false)]
	[InlineData("[REDACTED:jwt]", false)]
	[InlineData("[REDACTED:jwt]@host", false)]
	[InlineData("---", false)]
	public void A_value_under_a_name_is_replaced_unless_it_stands_in_for_one(string value, bool expected)
	{
		Assert.Equal(expected, PlainValues.IsReplaceable(value));
	}

	[Theory]
	[InlineData("password", true)]
	[InlineData("pw", true)]
	[InlineData("x1", true)]
	[InlineData("$PASSWORD", false)]
	[InlineData("xxxx", false)]
	[InlineData("''", false)]
	[InlineData("[REDACTED:url-password]", false)]
	public void A_value_in_a_password_s_place_is_replaced_whatever_word_it_is(string value, bool expected)
	{
		Assert.Equal(expected, PlainValues.IsPassword(value));
	}

	[Theory]
	[InlineData("8080:80", true)]
	[InlineData("127.0.0.1:5432:5432", true)]
	[InlineData("3306", false)]
	[InlineData("hunter2", false)]
	public void A_port_mapping_is_not_a_password(string value, bool expected)
	{
		Assert.Equal(expected, PlainValues.IsPortMapping(value));
	}

	[Theory]
	[InlineData("[REDACTED:jwt]", true)]
	[InlineData("[REDACTED:gitleaks-github-pat]", true)]
	[InlineData("[REDACTED:]", false)]
	[InlineData("[REDACTED:Jwt]", false)]
	[InlineData("[REDACTED:jwt", false)]
	[InlineData("[REDACTED:jwt] ", false)]
	[InlineData("REDACTED:jwt]", false)]
	public void Only_a_placeholder_this_engine_writes_reads_as_one(string text, bool expected)
	{
		Assert.Equal(expected, RedactionRule.IsPlaceholder(text));
	}

	[Fact]
	public void A_placeholder_name_past_the_longest_a_rule_has_is_not_one()
	{
		Assert.False(RedactionRule.IsPlaceholder("[REDACTED:" + new string('a', 65) + "]"));
		Assert.True(RedactionRule.IsPlaceholder("[REDACTED:" + new string('a', 64) + "]"));
	}
}
