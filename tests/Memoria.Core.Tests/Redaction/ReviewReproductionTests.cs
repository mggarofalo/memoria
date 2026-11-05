using System.Text;
using System.Text.Json;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;
using Memoria.Core.Sidecar;

namespace Memoria.Core.Tests.Redaction;

/// <summary>Reproductions from an independent adversarial review. See <see cref="ReviewCases"/>.</summary>
public sealed class ReviewReproductionTests
{
	private const string Slot = "{S}";
	private const string KeyPlaceholder = "[REDACTED:private-key]";
	private const string Begin = "-----BEGIN " + Synthetic.PrivateWord + " KEY-----";
	private const string End = "-----END " + Synthetic.PrivateWord + " KEY-----";

	private static readonly Redactor Redactor = new();

	private static string Line1 => Synthetic.Base64(64, 900);

	private static string Line2 => Synthetic.Base64(64, 901);

	public static TheoryData<string, string, string> Positives()
	{
		TheoryData<string, string, string> data = [];
		foreach ((string rule, string template, string secret) in ReviewCases.Positives)
		{
			data.Add(rule, template, secret);
		}

		return data;
	}

	public static TheoryData<string, string> ProviderTokens()
	{
		TheoryData<string, string> data = [];
		foreach ((string rule, string secret) in ReviewCases.ProviderTokens)
		{
			data.Add(rule, secret);
		}

		return data;
	}

	public static TheoryData<string> Negatives() => [.. ReviewCases.Negatives];

	[Theory]
	[MemberData(nameof(Positives))]
	public void Listed_recall_failure_is_closed(string rule, string template, string secret)
	{
		AssertRedacted(rule, template, secret);
	}

	[Theory]
	[MemberData(nameof(ProviderTokens))]
	public void Provider_token_is_redacted_in_every_context(string rule, string secret)
	{
		foreach (string context in RedactionCases.Contexts)
		{
			AssertRedacted(rule, context, secret);
		}
	}

	[Theory]
	[MemberData(nameof(Negatives))]
	public void Over_match_is_closed(string text)
	{
		RedactionResult result = Redactor.Redact(text);

		Assert.Equal(text, result.Text);
		Assert.Empty(result.CountsByRule);
	}

	[Fact]
	public void Real_memoria_key_format_is_redacted()
	{
		string key = "me" + "m_" + Synthetic.Hex(16, 902) + "_" + Synthetic.Hex(64, 903);

		Assert.Equal("Bearer [REDACTED:memoria-api-key]", Redactor.Redact("Bearer " + key).Text);
	}

	[Fact]
	public void Curl_finds_every_password_on_a_line()
	{
		string first = Synthetic.Body(24, 904);
		string second = Synthetic.Body(24, 905);

		string output = Redactor.Redact("cu" + $"rl -u a:{first} https://h; cu" + $"rl --user b:{second} https://i").Text;

		Assert.Equal("cu" + "rl -u a:[REDACTED:cu" + "rl-user-password] https://h; cu" + "rl --user b:[REDACTED:cu" + "rl-user-password] https://i", output);
	}

	[Theory]
	[InlineData("cat -n")]
	[InlineData("reader")]
	[InlineData("grep -n")]
	[InlineData("grep file")]
	[InlineData("grep context")]
	[InlineData("bullet")]
	[InlineData("star")]
	[InlineData("dot")]
	[InlineData("numbered")]
	[InlineData("paren")]
	[InlineData("removed")]
	[InlineData("comment")]
	[InlineData("semicolon")]
	[InlineData("hash")]
	[InlineData("quote")]
	public void Key_with_a_prefix_on_every_line_is_removed_whole(string style)
	{
		string[] lines = [Begin, Line1, Line2, End];
		string input = string.Join('\n', lines.Select((line, i) => Prefix(style, i + 1) + line));

		string output = Redactor.Redact(input).Text;

		// The marker's own run of dashes absorbs a prefix ending in a dash, which holds nothing secret.
		Assert.Equal(Prefix(style, 1).TrimEnd('-') + KeyPlaceholder, output);
	}

	[Theory]
	[InlineData("<br>")]
	[InlineData("<br/>")]
	[InlineData("<br />")]
	[InlineData("&#10;")]
	[InlineData("&#xA;")]
	[InlineData("\\x0a")]
	[InlineData("\\012")]
	[InlineData("\\u000a")]
	[InlineData("`n")]
	[InlineData("`r`n")]
	[InlineData("&#13;&#10;")]
	public void Key_joined_by_an_escaped_line_feed_is_removed_whole(string join)
	{
		string input = string.Join(join, Begin, Line1, Synthetic.Body(20, 906), End);

		Assert.Equal(KeyPlaceholder, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Short_last_line_joined_by_an_escaped_line_feed_is_removed_when_truncated()
	{
		string input = string.Join("<br>", Begin, Line1, Synthetic.Body(20, 907));

		Assert.Equal(KeyPlaceholder, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("-----BEGIN " + Synthetic.PrivateWord + " KEY----- (staging)")]
	[InlineData("-----BEGIN " + Synthetic.PrivateWord + " KEY----- // do not share")]
	[InlineData("-----begin pri" + "vate key-----")]
	[InlineData("-----Begin Rsa Pri" + "vate Key-----")]
	[InlineData("-BEGIN " + Synthetic.PrivateWord + " KEY-")]
	[InlineData("BEGIN " + Synthetic.PrivateWord + " KEY")]
	public void Unusual_begin_marker_is_still_decisive(string marker)
	{
		Assert.Equal(KeyPlaceholder, Redactor.Redact($"{marker}\n{Line1}\n{Line2}").Text);
	}

	[Theory]
	[InlineData("-----end pri" + "vate key-----")]
	[InlineData("-END " + Synthetic.PrivateWord + " KEY-")]
	[InlineData("END " + Synthetic.PrivateWord + " KEY")]
	public void Unusual_end_marker_closes_the_block(string marker)
	{
		Assert.Equal(KeyPlaceholder, Redactor.Redact($"{Begin}\n{Line1}\n{marker}").Text);
	}

	[Fact]
	public void Prose_between_the_marker_and_the_key_does_not_hide_the_key()
	{
		string input = $"{Begin}\nHere is the key you asked for:\n{Line1}\n{Line2}\n{End}\nafter";

		Assert.Equal($"{KeyPlaceholder}\nafter", Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("[...]")]
	[InlineData("...")]
	[InlineData("…")]
	[InlineData("<snip>")]
	[InlineData("[truncated]")]
	[InlineData("... 12 lines omitted ...")]
	[InlineData("(elided)")]
	public void Elision_in_the_middle_does_not_save_the_second_half(string elision)
	{
		string input = $"{Begin}\n{Line1}\n{elision}\n{Line2}\n{End}\nafter";

		Assert.Equal($"{KeyPlaceholder}\nafter", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Elision_with_only_prose_after_it_ends_the_key()
	{
		string input = $"{Begin}\n{Line1}\n[...]\nThat was the key.";

		Assert.Equal($"{KeyPlaceholder}\n[...]\nThat was the key.", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Body_and_end_marker_with_no_begin_is_removed()
	{
		string input = $"before\n{Line1}\n{Line2}\n{Synthetic.Body(20, 908)}\n{End}\nafter";

		Assert.Equal("before\n[REDACTED:private-key-end]\nafter", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Body_with_line_numbers_and_end_marker_with_no_begin_is_removed()
	{
		string input = $"     2\t{Line1}\n     3\t{Line2}\n     4\t{End}\nafter";

		string output = Redactor.Redact(input).Text;

		Assert.DoesNotContain(Line1, output, StringComparison.Ordinal);
		Assert.DoesNotContain(Line2, output, StringComparison.Ordinal);
		Assert.EndsWith("\nafter", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Run_of_base64_lines_with_no_marker_is_removed()
	{
		string tail = Synthetic.Body(20, 909) + "==";
		string input = $"before\n{Line1}\n{Line2}\n{Synthetic.Base64(64, 910)}\n{tail}\nafter words";

		Assert.Equal("before\n[REDACTED:private-key-lines]\nafter words", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Run_of_base64_lines_under_a_certificate_marker_survives()
	{
		string input = $"-----BEGIN CERTIFICATE-----\n{Line1}\n{Line2}\n{Synthetic.Base64(64, 911)}\n-----END CERTIFICATE-----";

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData(new byte[] { 0x30, 0x82, 0x04, 0xBD, 0x02, 0x01, 0x00, 0x30, 0x0D, 0x06, 0x09, 0x2A }, true)]
	[InlineData(new byte[] { 0x30, 0x77, 0x02, 0x01, 0x01, 0x04, 0x20, 0x11, 0x22, 0x33, 0x44, 0x55 }, true)]
	[InlineData(new byte[] { 0x30, 0x82, 0x03, 0xFD, 0x30, 0x82, 0x02, 0xE5, 0xA0, 0x03, 0x02, 0x01 }, false)]
	[InlineData(new byte[] { 0x30, 0x82, 0x01, 0x22, 0x30, 0x0D, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86 }, false)]
	[InlineData(new byte[] { 0x31, 0x82, 0x04, 0xBD, 0x02, 0x01, 0x00, 0x30, 0x0D, 0x06, 0x09, 0x2A }, false)]
	public void Der_head_separates_private_keys_from_certificates(byte[] head, bool isKey)
	{
		string input = Convert.ToBase64String(head) + Synthetic.Body(80, 912);

		Assert.Equal(isKey ? "[REDACTED:private-key-der]" : input, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Openssh_body_with_no_marker_is_removed()
	{
		string input = "b3Blbn" + "NzaC1rZXktdjE" + Synthetic.Body(80, 913);

		Assert.Equal("[REDACTED:private-key-der]", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Base64_encoded_pem_key_is_removed()
	{
		string key = Convert.ToBase64String(Encoding.ASCII.GetBytes("-----BEGIN RSA " + Synthetic.PrivateWord + " KEY-----\n" + Synthetic.PemBody(2, 914)));

		Assert.Equal("client-key-data: [REDACTED:base64-private-key]", Redactor.Redact("client-key-data: " + key).Text);
		Assert.Equal("[REDACTED:base64-private-key]", Redactor.Redact(key).Text);
	}

	[Theory]
	[InlineData("\u001b[32mpassword\u001b[0m=hunter2", "\u001b[32mpassword\u001b[0m=[REDACTED:secret-assignment]")]
	[InlineData("\\u001b[32mpassword\\u001b[0m=hunter2", "\\u001b[32mpassword\\u001b[0m=[REDACTED:secret-assignment]")]
	[InlineData("\\x1b[32mpassword\\x1b[0m=hunter2", "\\x1b[32mpassword\\x1b[0m=[REDACTED:secret-assignment]")]
	[InlineData("\\033[1;32mpassword\\033[0m=hunter2", "\\033[1;32mpassword\\033[0m=[REDACTED:secret-assignment]")]
	[InlineData("^[[32mpassword^[[0m=hunter2", "^[[32mpassword^[[0m=[REDACTED:secret-assignment]")]
	[InlineData("&quot;password&quot;:&quot;hunter2&quot;", "&quot;password&quot;:&quot;[REDACTED:secret-assignment]&quot;")]
	[InlineData("&#34;password&#34;:&#34;hunter2&#34;", "&#34;password&#34;:&#34;[REDACTED:secret-assignment]&#34;")]
	[InlineData("&#x22;password&#x22;:&#x22;hunter2&#x22;", "&#x22;password&#x22;:&#x22;[REDACTED:secret-assignment]&#x22;")]
	[InlineData("&apos;password&apos;:&apos;hunter2&apos;", "&apos;password&apos;:&apos;[REDACTED:secret-assignment]&apos;")]
	[InlineData("“password”: “hunter2”", "“password”: “[REDACTED:secret-assignment]”")]
	[InlineData("pass​word=hunter2", "pass​word=[REDACTED:secret-assignment]")]
	[InlineData("pass­word=hunter2", "pass­word=[REDACTED:secret-assignment]")]
	public void Noise_is_read_through_and_kept_outside_the_span(string input, string expected)
	{
		Assert.Equal(expected, Redactor.Redact(input).Text);
		Assert.Equal(expected, Redactor.Redact(expected).Text);
	}

	[Theory]
	[InlineData('​')]
	[InlineData('‌')]
	[InlineData('‍')]
	[InlineData('⁠')]
	[InlineData('﻿')]
	[InlineData('­')]
	public void Invisible_character_inside_a_token_does_not_hide_it(char invisible)
	{
		string token = Synthetic.GitHubClassic('p');
		string split = token[..10] + invisible + token[10..];

		Assert.Equal("a [REDACTED:github-token] b", Redactor.Redact($"a {split} b").Text);
	}

	[Fact]
	public void Ansi_inside_a_token_does_not_hide_it()
	{
		string token = Synthetic.GitHubClassic('p');
		string split = token[..10] + "\u001b[0m" + token[10..];

		Assert.Equal("a [REDACTED:github-token] b", Redactor.Redact($"a {split} b").Text);
	}

	[Fact]
	public void Text_with_noise_and_no_secret_is_returned_unchanged()
	{
		string input = "\u001b[32mgreen\u001b[0m &quot;quoted&quot; “curly” soft­hyphen \\e[1m";

		RedactionResult result = Redactor.Redact(input);

		Assert.Same(input, result.Text);
		Assert.Empty(result.CountsByRule);
	}

	[Theory]
	[InlineData(64)]
	[InlineData(76)]
	[InlineData(80)]
	public void Token_wrapped_at_a_fixed_column_is_removed_whole(int column)
	{
		string body = Synthetic.Body(120, 915);
		string token = "gh" + "p_" + body;
		string line = new string('a', column - 40) + " " + token;
		string wrapped = line[..column] + "\n" + line[column..];

		string output = Redactor.Redact(wrapped).Text;

		Assert.Equal(new string('a', column - 40) + " [REDACTED:github-token]", output);
	}

	[Fact]
	public void Jwt_wrapped_across_lines_is_removed_whole()
	{
		string jwt = Synthetic.Jwt;
		string wrapped = string.Join('\n', jwt.Chunk(30).Select(static chunk => new string(chunk)));

		Assert.Equal("[REDACTED:jwt]", Redactor.Redact(wrapped).Text);
	}

	[Fact]
	public void Line_after_a_token_is_not_taken_for_its_tail()
	{
		string token = Synthetic.GitHubClassic('p');

		Assert.Equal("[REDACTED:github-token]\nnextword", Redactor.Redact(token + "\nnextword").Text);
	}

	[Fact]
	public void Tokens_listed_on_consecutive_lines_stay_separate()
	{
		string input = Synthetic.GitHubClassic('p') + "\n" + "gh" + "p_" + Synthetic.Body(36, 916);

		RedactionResult result = Redactor.Redact(input);

		Assert.Equal("[REDACTED:github-token]\n[REDACTED:github-token]", result.Text);
		Assert.Equal(2, result.CountsByRule["github-token"]);
	}

	[Fact]
	public void Minified_code_under_a_secret_name_is_not_destroyed()
	{
		StringBuilder builder = new("token=function(a,b){return a+b};");
		while (builder.Length < 8192)
		{
			builder.Append("var c").Append(builder.Length).Append("=function(x){return x*2};");
		}

		string input = builder.ToString();

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Long_quoted_code_under_a_secret_name_is_judged_on_its_prefix()
	{
		string input = "\"token\": \"function run() { return " + new string('x', 5000) + "\"";

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Long_random_value_under_a_secret_name_is_removed_whole()
	{
		string secret = Synthetic.Body(8192, 917);

		Assert.Equal("token=[REDACTED:secret-assignment]", Redactor.Redact("token=" + secret).Text);
	}

	[Fact]
	public void Idempotence_holds_across_fragments_at_random()
	{
		string[] fragments =
		[
			"---\n", "title: ", "token: ", "password=", "\"", "'", "`", "\n", " ", ":", "=", "@", "[REDACTED:x]", "[REDACTED:", "- ", "\\n", "%3D", "&quot;",
			Synthetic.GitHubClassic('p'), Synthetic.Jwt, Synthetic.AwsAccessKey, Synthetic.Body(24, 918), "Hunter2x", "postgres://u:", "https://", "Cookie: ",
			"sid=" + Synthetic.Body(16, 919), Begin, End, Line1, "cu" + "rl -u ", "--password ", "\u001b[0m", "​", new string('a', 76), "$X", "${Y}", "<z>",
		];
		Random random = new(20261104);
		for (int i = 0; i < 4000; i++)
		{
			string input = string.Concat(Enumerable.Range(0, random.Next(1, 9)).Select(_ => fragments[random.Next(fragments.Length)]));

			string once = Redactor.Redact(input).Text;
			RedactionResult twice = Redactor.Redact(once);

			Assert.Equal(once, twice.Text);
			Assert.Empty(twice.CountsByRule);
		}
	}

	[Fact]
	public void Sink_requires_redacted_markdown()
	{
		Assert.Equal(typeof(RedactedMarkdown), typeof(SidecarIngestRequest).GetProperty(nameof(SidecarIngestRequest.Content))!.PropertyType);
		Assert.Empty(typeof(RedactedMarkdown).GetConstructors());
		Assert.Equal(typeof(RedactedMarkdown), typeof(IRedactor).GetMethod(nameof(IRedactor.Redact))!.ReturnType);
		Assert.Equal(typeof(UnredactedMarkdown), Assert.Single(typeof(IRedactor).GetMethod(nameof(IRedactor.Redact))!.GetParameters()).ParameterType);
	}

	[Fact]
	public void Redacted_markdown_writes_as_a_string_and_cannot_be_read_back()
	{
		RedactedMarkdown markdown = Redactor.Redact(new UnredactedMarkdown("password=hunter2"));

		Assert.Equal("password=[REDACTED:secret-assignment]", markdown.Text);
		Assert.Equal(markdown.Text, markdown.ToString());
		Assert.Equal(new Dictionary<string, int> { ["secret-assignment"] = 1 }, markdown.CountsByRule);
		Assert.Equal("\"password=[REDACTED:secret-assignment]\"", JsonSerializer.Serialize(markdown));
		Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<RedactedMarkdown>("\"x\""));
	}

	private static void AssertRedacted(string rule, string template, string secret)
	{
		string input = template.Replace(Slot, secret, StringComparison.Ordinal);
		string expected = template.Replace(Slot, $"[REDACTED:{rule}]", StringComparison.Ordinal);

		RedactionResult result = Redactor.Redact(input);

		Assert.Equal(expected, result.Text);
		Assert.Equal(new Dictionary<string, int> { [rule] = 1 }, result.CountsByRule);
		Assert.Equal(expected, Redactor.Redact(result.Text).Text);
	}

	private static string Prefix(string style, int number)
	{
		return style switch
		{
			"cat -n" => $"{number,6}\t",
			"reader" => $"{number,6}→",
			"grep -n" => $"{number}:",
			"grep file" => $"key.pem:{number}:",
			"grep context" => $"key.pem-",
			"bullet" => "- ",
			"star" => "* ",
			"dot" => "• ",
			"numbered" => $"{number}. ",
			"paren" => $"{number}) ",
			"removed" => "-",
			"comment" => "// ",
			"semicolon" => "; ",
			"hash" => "# ",
			_ => "> ",
		};
	}
}
