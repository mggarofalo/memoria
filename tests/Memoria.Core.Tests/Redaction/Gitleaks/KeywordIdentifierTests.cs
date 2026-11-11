using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction.Gitleaks;

/// <summary>
/// Upstream's assignment rules know a value by a provider's name before it and its shape alone, and 24 of them accept only digits, hex
/// or a UUID. So the id of a provider's task, app or trace read as its token. A name whose words after the keyword name such a thing,
/// with no credential's word in it, holds an id; a credential's name still holds a credential.
/// </summary>
public sealed class KeywordIdentifierTests
{
	private static string Uuid(int seed) => $"{Synthetic.Hex(8, seed)}-{Synthetic.Hex(4, seed + 1)}-{Synthetic.Hex(4, seed + 2)}-{Synthetic.Hex(4, seed + 3)}-{Synthetic.Hex(12, seed + 4)}";

	public static TheoryData<string> Identifiers() =>
	[
		"asana task: " + Synthetic.Digits(16, 5101),
		"Opened asana task 1203: " + Synthetic.Digits(16, 5102) + " in the board",
		"heroku app id: " + Uuid(5103),
		"HEROKU_APP_ID=" + Uuid(5108),
		"plaid item_id: " + Synthetic.Hex(24, 5113),
		"datadog trace_id: " + Synthetic.Hex(40, 5114),
		"datadog span id = " + Synthetic.Hex(40, 5115),
		"\"asanaProjectId\": \"" + Synthetic.Digits(16, 5116) + "\"",
		"heroku user id: " + Uuid(5117),
		"heroku app guid: " + Uuid(5174),
		"Opened asana task 12 3: " + Synthetic.Digits(16, 5179),
		// The last of several keywords is the one the name follows.
		"jira_confluence_page: " + Synthetic.Body(20, 5180).ToLowerInvariant() + Synthetic.Hex(4, 5181),
		"asana task id: " + Synthetic.Digits(16, 5118),
		"asana workspace: " + Synthetic.Digits(16, 5119),
		"heroku team uuid: " + Uuid(5143),
		"plaid account_ids: " + Synthetic.Hex(24, 5148),
		"datadog org: " + Synthetic.Hex(40, 5149),
	];

	/// <summary>Each line and the value in it, which must not survive, whichever rule takes it.</summary>
	public static TheoryData<string, string> Credentials()
	{
		TheoryData<string, string> data = [];
		foreach ((string name, string value) in new[]
		{
			("heroku app token: ", Uuid(5122)),
			("HEROKU_API_KEY=", Uuid(5127)),
			("heroku: ", Uuid(5132)),
			("asana client_id: ", Synthetic.Digits(16, 5137)),
			("asana_task_secret=", Synthetic.Digits(16, 5138)),
			("plaid client: ", Synthetic.Body(24, 5139).ToLowerInvariant()),
			("datadog app key: ", Synthetic.Hex(40, 5140)),
			("DATADOG_TRACE_TOKEN=", Synthetic.Hex(40, 5141)),
			// Only the words after the keyword say what the value is.
			("project asana => ", Synthetic.Digits(16, 5142)),
			// A name that ends in any word but a thing's or an id's may name a credential the word lists do not know.
			("HEROKU_APP_TOKEN2=", Uuid(5150)),
			("heroku app tokens: ", Uuid(5155)),
			("heroku app bearer: ", Uuid(5160)),
			("datadog app key2: ", Synthetic.Hex(40, 5165)),
			("asana_app_pat=", Synthetic.Digits(16, 5166)),
			("asana task token2: ", Synthetic.Digits(16, 5167)),
			// An id that names no thing, a thing a credential can belong to, and more than a thing.
			("heroku id: ", Uuid(5168)),
			("heroku user: ", Uuid(5182)),
			("asana_service_account=", Synthetic.Digits(16, 5187)),
			("DATADOG_SERVICE_ACCOUNT=", Synthetic.Hex(40, 5188)),
			("datadog application: ", Synthetic.Hex(40, 5189)),
			// A name that ends as a thing's but holds a credential's word before the keyword.
			("secret_asana_task: ", Synthetic.Digits(16, 5190)),
			("asana client_id: ", Synthetic.Digits(16, 5173)),
		})
		{
			data.Add(name + value, value);
		}

		return data;
	}

	[Theory]
	[MemberData(nameof(Identifiers))]
	public void A_provider_name_before_a_things_id_leaves_the_id(string line)
	{
		Assert.Equal(line, Redactor.Shared.RedactFragment(line).Text);
	}

	[Theory]
	[MemberData(nameof(Credentials))]
	public void A_credentials_name_still_has_its_value_redacted(string line, string value)
	{
		string redacted = Redactor.Shared.RedactFragment(line).Text;
		Assert.DoesNotContain(value, redacted, StringComparison.Ordinal);
		Assert.Contains("[REDACTED:", redacted, StringComparison.Ordinal);
	}

	/// <summary>Written out here rather than read from the engine, so a word taken from its list fails a case.</summary>
	[Theory]
	[InlineData("task")]
	[InlineData("tasks")]
	[InlineData("item")]
	[InlineData("items")]
	[InlineData("trace")]
	[InlineData("span")]
	[InlineData("project")]
	[InlineData("projects")]
	[InlineData("org")]
	[InlineData("organization")]
	[InlineData("team")]
	[InlineData("workspace")]
	[InlineData("issue")]
	[InlineData("ticket")]
	[InlineData("order")]
	[InlineData("job")]
	[InlineData("build")]
	[InlineData("run")]
	[InlineData("commit")]
	[InlineData("page")]
	[InlineData("document")]
	[InlineData("folder")]
	[InlineData("channel")]
	[InlineData("thread")]
	[InlineData("customer")]
	[InlineData("invoice")]
	[InlineData("deployment")]
	[InlineData("release")]
	[InlineData("device")]
	[InlineData("node")]
	[InlineData("site")]
	public void Each_things_word_alone_names_an_id(string word)
	{
		string line = $"asana {word}: " + Synthetic.Digits(16, 5200 + word.Length);
		Assert.Equal(line, Redactor.Shared.RedactFragment(line).Text);
	}

	/// <summary>A holder's word names an id only before an id word; alone it may name a credential's holder, as a service account does.</summary>
	[Theory]
	[InlineData("app")]
	[InlineData("apps")]
	[InlineData("application")]
	[InlineData("account")]
	[InlineData("accounts")]
	[InlineData("user")]
	[InlineData("users")]
	public void A_holders_word_names_an_id_only_before_an_id_word(string word)
	{
		string id = Synthetic.Digits(16, 5300 + word.Length);
		string named = $"asana {word}_id: {id}";
		Assert.Equal(named, Redactor.Shared.RedactFragment(named).Text);
		Assert.DoesNotContain(id, Redactor.Shared.RedactFragment($"asana {word}: {id}").Text, StringComparison.Ordinal);
	}

	/// <summary>A keyword written twice: what follows the last one is the name's end, so the second <c>asana</c> is not a word of it.</summary>
	[Fact]
	public void The_last_keyword_ends_the_providers_part_of_the_name()
	{
		string line = "asana_asana_task: " + Synthetic.Digits(16, 5400);
		Assert.Equal(line, Redactor.Shared.RedactFragment(line).Text);
	}
}
