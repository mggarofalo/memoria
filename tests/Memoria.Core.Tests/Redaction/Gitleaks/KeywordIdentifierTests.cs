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
		"heroku user: " + Uuid(5117),
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
}
