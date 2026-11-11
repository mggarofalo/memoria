using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// Under a credential's name, the text beside a template's spans is judged as a value however short its chunks are: a secret can be
/// joined to code in pieces. Only a template made of spans, or with nothing but code beside them, stays.
/// </summary>
public sealed class TemplateChunkTests
{
	public static TheoryData<string, string> ShortChunksBesideSpans() => new()
	{
		{ "api_key = `${base}/qz7/mv3/hx9`", "qz7/mv3/hx9" },
		{ "token = \"${a}-x9k2-${b}\"", "x9k2" },
		{ "password = `${p}a1b2c3`", "a1b2c3" },
		{ "API_KEY=\"${HOST}:k8s7q2\"", "k8s7q2" },
		{ "secret: `${a}${b}q7z9`", "q7z9" },
		// Chunks too short for a run of letters and digits to read as random, split by separators.
		{ "token = \"${a}-x9-k2-${b}-q7\"", "x9-k2" },
		{ "password: ${p}.a1.b2.c3", "a1.b2.c3" },
		{ "secret = \"${h}:z9/y8/w7\"", "z9/y8/w7" },
		// An upper-case name whose parts mix letters and digits, short letters-only chunks past four letters, and a template in a list.
		{ "password = \"${base}/QZ7_MV3_HX9\"", "QZ7_MV3_HX9" },
		{ "token = \"${a}-qzm-vhx-kbw\"", "qzm-vhx-kbw" },
		{ "password = `${a}/abcd`", "abcd" },
		{ "password = `${a}/abc/def`", "abc/def" },
		{ "password = [\"${base}/qz7/mv3/hx9\"]", "qz7/mv3/hx9" },
		{ "token = `${a};k9q\\x7`", "k9q" },
	};

	[Theory]
	[MemberData(nameof(ShortChunksBesideSpans))]
	public void Short_chunks_beside_a_span_under_a_credentials_name_go(string line, string chunk)
	{
		Assert.DoesNotContain(chunk, Redactor.Shared.RedactFragment(line).Text, StringComparison.Ordinal);
	}

	/// <summary>
	/// A deliberate choice: under a credential's name a templated path of words is redacted too, though it is only words. Nothing in the
	/// text tells <c>/secrets/key</c> from a passphrase written between slashes, and losing a path from a transcript costs little where
	/// leaking a credential cannot be undone. Under a name that describes a secret, such as <c>password_file</c>, the same path stays.
	/// </summary>
	[Theory]
	[InlineData("api_key = \"${HOME}/secrets/key\"", "secrets/key")]
	[InlineData("token = \"${API}/v2/oauth\"", "v2/oauth")]
	[InlineData("api_key: `${BASE_URL}/v1/keys`", "v1/keys")]
	public void A_templated_path_of_words_under_a_credentials_name_is_redacted(string line, string path)
	{
		Assert.DoesNotContain(path, Redactor.Shared.RedactFragment(line).Text, StringComparison.Ordinal);
	}

	[Theory]
	// Only spans, with separators between them.
	[InlineData("password = `${A}${B}`")]
	[InlineData("token = \"${a}-${b}\"")]
	[InlineData("api_key=${PREFIX}_${SUFFIX}")]
	[InlineData("secret: `${scheme}://${host}`")]
	// Code beside the spans: markup, an upper-case name, a short word.
	[InlineData("token = \"<value>${TOKEN}</value>\"")]
	[InlineData("password=${X}PROD_ADMIN_PASSWORD_X")]
	[InlineData("api_key = `key_${id}`")]
	[InlineData("token = \"<abc>${TOKEN}</abc>\"")]
	[InlineData("api_key=${PREFIX}_V2_${SUFFIX}")]
	[InlineData("token = \"Bearer ${TOKEN}\"")]
	[InlineData("api_key=${PREFIX}_S3_${SUFFIX}")]
	[InlineData("token = `${a}${b}`, password = `${x}:${y}`")]
	// A name that describes a secret holds no secret, whatever its template.
	[InlineData("token_name = `${base}/qz7/mv3`")]
	[InlineData("password_file = \"${HOME}/secrets/db\"")]
	public void A_template_of_spans_and_code_stays(string line)
	{
		Assert.Equal(line, Redactor.Shared.RedactFragment(line).Text);
	}
}
