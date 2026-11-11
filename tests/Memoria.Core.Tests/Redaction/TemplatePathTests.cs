using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// A template under a secret's name that builds a path from words, as a vault key or a file's place, is not a secret: its words name a
/// place. A template that puts a random literal, a password-like word or a shell default beside its span still is.
/// </summary>
public sealed class TemplatePathTests
{
	[Theory]
	[InlineData("const secretName = `memoria/${env}/api-key`;")]
	[InlineData("const tokenPath = `${home}/.config/memoria/token`;")]
	[InlineData("password_file=\"${SECRETS_DIR}/db/password.txt\"")]
	[InlineData("export TOKEN_FILE=\"$(pwd)/run/worker-token\"")]
	public void A_template_that_builds_a_path_of_words_stays(string line)
	{
		Assert.Equal(line, Redactor.Shared.RedactFragment(line).Text);
	}

	public static TheoryData<string, string> SecretsBesideASpan()
	{
		string random = Synthetic.Body(24, 9301).ToLowerInvariant();
		return new TheoryData<string, string>
		{
			// A random segment, a password with digits and capitals, a long run of letters, a shell default, and one word after a slash.
			{ "const secretName = `memoria/${env}/" + random + "`;", random },
			{ "password = `${user}/Pr0d" + "P4ss/db`", "Pr0d" + "P4ss" },
			{ "token = `${base}/vault/" + "xqzvbtrkplmnvbcxwqyt" + "zz`", "xqzvbtrkplmnvbcxwqyt" + "zz" },
			{ "DB_PASSWORD=\"${DB_PASSWORD:-Pr0d" + "P4ss}/data/db\"", "Pr0d" + "P4ss" },
			{ "password = `${PREFIX}/Summer" + "password`", "Summer" + "password" },
			// A password's or a token's name keeps words between slashes: they can be the passphrase itself.
			{ "password: `${SALT}/hunter/two`", "hunter/two" },
			{ "secret = `${x}/correct/horse/battery`", "correct/horse/battery" },
			// A name that may carry a secret, though it describes one, keeps them too.
			{ "secret_header = `${scheme}/hunter/two`", "hunter/two" },
			{ "token_ref = `${vault}/correct/horse`", "correct/horse" },
			// A span's own data can be the secret, under any name.
			{ "secretName = `${a}/x/${b:-hunterxyz}/y`", "hunterxyz" },
		};
	}

	[Theory]
	[MemberData(nameof(SecretsBesideASpan))]
	public void A_secret_beside_a_span_still_goes(string line, string secret)
	{
		Assert.DoesNotContain(secret, Redactor.Shared.RedactFragment(line).Text, StringComparison.Ordinal);
	}
}
