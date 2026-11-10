using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// Lines of this repository's own source and docs that redaction used to change, though none holds a credential. A session that
/// quotes code quotes lines like these, and each loss was permanent. Each stays exactly as written.
/// </summary>
public sealed class CodeFalsePositiveTests
{
	[Theory]
	[InlineData("""    "@inquirer/password": "^5.2.5",""")]
	[InlineData("""      Memoria__Sidecar__Secret: ${MEMORIA_SIDECAR_SECRET:?set MEMORIA_SIDECAR_SECRET to the sidecar's MEMORIA_QMD_SECRET}""")]
	[InlineData("""A key is `mem_<id>_<secret>`, sent as `Authorization: Bearer <key>`. The id is""")]
	[InlineData("""| Secret | `%ProgramData%\<service>\secret\` | Administrators and the service account only, with inheritance off. |""")]
	[InlineData("""export const BAD_KEY = `mem_${"0".repeat(16)}_${"0".repeat(64)}`;""")]
	[InlineData("""const FULL_KEY = /^mem_[0-9a-f]{16}_[0-9a-f]{64}$/;""")]
	[InlineData("""    "-e", `Memoria__Ingest__DigestKey=${randomBytes(32).toString("base64url")}`, image]);""")]
	[InlineData("""$env:MEMORIA_QMD_SECRET = node -e "console.log(require('crypto').randomBytes(32).toString('base64url'))" """)]
	[InlineData("""    if (this.#token !== undefined) headers.authorization = `Bearer ${this.#token}`;""")]
	[InlineData("""    const key = `${includeLexical}:${query}`;""")]
	[InlineData("""/// The wire format of an API key: <c>mem_&lt;id&gt;_&lt;secret&gt;</c>. The id is""")]
	[InlineData("""/// <c>Memoria:Auth:Audit:IncludeQueries</c> is on. Document content is never recorded.""")]
	[InlineData("""/// <summary>Turns <c>Memoria:Auth:TrustedProxies</c> into forwarded-header handling.</summary>""")]
	[InlineData("""/// go by what surrounds a value stay out of it: percent-encoded prose saying <c>password: adminx</c> is not a password.""")]
	[InlineData("""	public static readonly RedactionRule UrlPassword = new(""")]
	[InlineData("""		+ @"(?:(?<secret>[^\s/""`]+)(?:@|%40)|(?<secret>[^\s/""`@]+/[^\s""`@]*)@)"),""")]
	[InlineData("""	private static readonly string[] PasswordWords = ["password", "passwd", "pwd", "passphrase"];""")]
	[InlineData("""Every route needs `Authorization: Bearer <token>` when a token is set. Errors are `{ "error": "<code>", "message": "..." }`.""")]
	[InlineData("""token_entry="    <key>MEMORIA_WORKER_TOKEN_FILE</key><string>$(xml "$token_file")</string>" """)]
	[InlineData("""    const apiKey = this.#routes.apiKeyFor?.(role);""")]
	[InlineData("""/// <summary>Passwords in the conventional places of common tools: <c>sshpass -p</c>, <c>docker login -p</c>.</summary>""")]
	[InlineData("""/// <summary>Tokens with these immediately before them are bookkeeping, not credentials: <c>pageToken</c>, <c>cancellationToken</c>.</summary>""")]
	[InlineData("""/// <summary>A path or URL under a secret-sounding name, such as <c>token: /var/run/secrets/token</c>.</summary>""")]
	public void A_line_of_code_or_docs_stays_as_written(string line)
	{
		Assert.Equal(line, Redactor.Shared.RedactFragment(line).Text);
	}

	/// <summary>The same shapes still give up a real value: an unclosed template is a password, and a literal after a name is one.</summary>
	[Theory]
	[InlineData("""password="${Secret123" """, """password="[REDACTED:secret-assignment]" """)]
	[InlineData("""password="$(Secret123" """, """password="[REDACTED:secret-assignment]" """)]
	[InlineData("<password>Tr0ub4dor-x9Q</password>", "<password>[REDACTED:xml-secret]</password>")]
	[InlineData("""PASSWORD=hunter2hunter2hunter2""", """PASSWORD=[REDACTED:secret-assignment]""")]
	[InlineData("""loadConfig({ MEMORIA_QMD_SECRET: "openapi-generation-secret" })""", """loadConfig({ MEMORIA_QMD_SECRET: "[REDACTED:memoria-shared-secret]" })""")]
	[InlineData("""<c>password: Q7v!x2Lr9Kp</c>""", """<c>password: [REDACTED:secret-assignment]</c>""")]
	public void A_credential_in_the_same_shapes_still_goes(string text, string expected)
	{
		Assert.Equal(expected, Redactor.Shared.RedactFragment(text).Text);
	}

	[Theory]
	[InlineData("^5.2.5", true)]
	[InlineData("~1.2.x", true)]
	[InlineData(">=2.0.1-beta.1", true)]
	[InlineData("5.2", true)]
	[InlineData("hunter2.5", false)]
	[InlineData("^a.b", false)]
	public void A_version_range_is_known(string value, bool range)
	{
		Assert.Equal(range, CodeValues.IsVersionRange(value));
	}

	[Theory]
	[InlineData("${name}", true)]
	[InlineData("${NAME:-default", true)]
	[InlineData("${randomBytes(32).toString(", true)]
	[InlineData("$(cat file)", true)]
	[InlineData("${Secret123", false)]
	[InlineData("$(Secret123", false)]
	[InlineData("pa$$word", false)]
	public void An_interpolation_is_known(string value, bool code)
	{
		Assert.Equal(code, CodeValues.HasInterpolation(value));
	}

	[Theory]
	[InlineData("<c>admin</c>", "admin")]
	[InlineData("adminx</c>", "adminx")]
	[InlineData("<code>value", "value")]
	[InlineData("a<b", "a<b")]
	[InlineData("x</", "x</")]
	public void Markup_glued_to_a_value_is_set_aside(string value, string expected)
	{
		Assert.Equal(expected, CodeValues.WithoutMarkup(value));
	}
}
