using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>PEM blocks in every encoding a transcript produces, asserting the exact bytes left around the placeholder.</summary>
public sealed class PrivateKeyTests
{
	private const string Placeholder = "[REDACTED:private-key]";
	private const string Begin = "-----BEGIN " + Synthetic.PrivateWord + " KEY-----";
	private const string End = "-----END " + Synthetic.PrivateWord + " KEY-----";

	private static readonly Redactor Redactor = new();

	private static string Line1 => Synthetic.Base64(64, 500);

	private static string Line2 => Synthetic.Base64(64, 501);

	private static string Short => Synthetic.Body(20, 502) + "==";

	public static TheoryData<string, string, string> Blocks()
	{
		string key = $"{Begin}\n{Line1}\n{Line2}\n{Short}\n{End}";
		return new TheoryData<string, string, string>
		{
			{ "complete", $"before\n{key}\nafter", $"before\n{Placeholder}\nafter" },
			{ "crlf", $"before\r\n{Begin}\r\n{Line1}\r\n{Short}\r\n{End}\r\nafter", $"before\r\n{Placeholder}\r\nafter" },
			{ "rsa", $"-----BEGIN RSA {Synthetic.PrivateWord} KEY-----\n{Line1}\n-----END RSA {Synthetic.PrivateWord} KEY-----", Placeholder },
			{ "ec", $"-----BEGIN EC {Synthetic.PrivateWord} KEY-----\n{Line1}\n-----END EC {Synthetic.PrivateWord} KEY-----", Placeholder },
			{ "openssh", $"-----BEGIN OPENSSH {Synthetic.PrivateWord} KEY-----\n{Line1}\n-----END OPENSSH {Synthetic.PrivateWord} KEY-----", Placeholder },
			{ "encrypted", $"-----BEGIN ENCRYPTED {Synthetic.PrivateWord} KEY-----\n{Line1}\n-----END ENCRYPTED {Synthetic.PrivateWord} KEY-----", Placeholder },
			{ "headers", $"-----BEGIN RSA {Synthetic.PrivateWord} KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,ABCDEF\n\n{Line1}\n-----END RSA {Synthetic.PrivateWord} KEY-----", Placeholder },
			{ "pgp", $"-----BEGIN PGP {Synthetic.PrivateWord} KEY BLOCK-----\nVersion: GnuPG v2\n\n{Line1}\n={Synthetic.Body(4, 503)}\n-----END PGP {Synthetic.PrivateWord} KEY BLOCK-----", Placeholder },
			{ "uppercase after end survives", $"{key} IMPORTANT", $"{Placeholder} IMPORTANT" },
			{ "end marker with no closing dashes", $"{Begin}\n{Line1}\n-----END {Synthetic.PrivateWord} KEY", Placeholder },

			{ "truncated at end of text", $"{Begin}\n{Line1}\n{Line2}", Placeholder },
			{ "truncated mid line", $"{Begin}\n{Line1}\n{Synthetic.Body(10, 504)}", Placeholder },
			{ "truncated then prose", $"{Begin}\n{Line1}\n{Line2}\n\nThe output was cut off here.", $"{Placeholder}\n\nThe output was cut off here." },
			{ "truncated then fence", $"```\n{Begin}\n{Line1}\n```\nnext", $"```\n{Placeholder}\n```\nnext" },
			{ "truncated before another key", $"{Begin}\n{Line1}\n{Begin}\n{Line2}\n{End}", $"{Placeholder}\n{Placeholder}" },

			{ "json escaped", $"{{\"key\":\"{Begin}\\n{Line1}\\n{Short}\\n{End}\\n\"}}", $"{{\"key\":\"{Placeholder}\\n\"}}" },
			{ "json escaped truncated", $"{{\"key\":\"{Begin}\\n{Line1}\\n{Short}\"}}", $"{{\"key\":\"{Placeholder}\"}}" },
			{ "json carriage returns", $"\"{Begin}\\r\\n{Line1}\\r\\n{End}\"", $"\"{Placeholder}\"" },
			{ "double escaped", $"\\\"{Begin}\\\\n{Line1}\\\\n{End}\\\\n\\\"", $"\\\"{Placeholder}\\\\n\\\"" },
			{ "json escaped symbols", $"\"{Begin}\\n{Synthetic.Body(40, 505)}\\u002B\\/{Synthetic.Body(20, 506)}\\u003D\\n{End}\"", $"\"{Placeholder}\"" },

			{ "url encoded", $"k=-----BEGIN%20{Synthetic.PrivateWord}%20KEY-----%0A{Synthetic.Body(40, 507)}%2B%2F{Synthetic.Body(20, 508)}%3D%0A-----END%20{Synthetic.PrivateWord}%20KEY-----&n=1", $"k={Placeholder}&n=1" },
			{ "form encoded", $"k=-----BEGIN+PRIVATE+KEY-----%0D%0A{Line1.Replace('+', 'A').Replace('/', 'B')}%0D%0A-----END+PRIVATE+KEY-----", $"k={Placeholder}" },

			{ "yaml block", $"key: |\n  {Begin}\n  {Line1}\n  {Short}\n  {End}\nnext: 1", $"key: |\n  {Placeholder}\nnext: 1" },
			{ "blockquote", $"> {Begin}\n> {Line1}\n> {End}\n\nnext", $"> {Placeholder}\n\nnext" },
			{ "flattened by env dump", $"KEY={Begin} {Line1} {Short} {End} NEXT=1", $"KEY={Placeholder} NEXT=1" },
			{ "flattened truncated", $"KEY={Begin} {Line1} {Line2} and then words", $"KEY={Placeholder} and then words" },
			{ "string concatenation", $"key = \"{Begin}\\n\" +\n  \"{Line1}\\n\" +\n  \"{End}\\n\";", $"key = \"{Placeholder}\\n\";" },
			{ "json array of lines", $"[\"{Begin}\", \"{Line1}\", \"{End}\"]", $"[\"{Placeholder}\"]" },
			{ "dangling backslash", $"{Begin}\n{Line1}\\", Placeholder + "\\" },
			{ "single word after a blank line is prose", $"{Begin}\n{Line1}\n\nDone\n", $"{Placeholder}\n\nDone\n" },
			{ "single word after crlf blank line is prose", $"{Begin}\r\n{Line1}\r\n\r\nDone\r\n", $"{Placeholder}\r\n\r\nDone\r\n" },
			{ "escaped blank line ends the key", $"\"{Begin}\\n{Line1}\\n\\nDone\\n\"", $"\"{Placeholder}\\n\\nDone\\n\"" },
		};
	}

	[Theory]
	[MemberData(nameof(Blocks))]
	public void Block_is_replaced_exactly(string label, string input, string expected)
	{
		Assert.False(string.IsNullOrEmpty(label));

		RedactionResult result = Redactor.Redact(input);

		Assert.Equal(expected, result.Text);
		Assert.True(result.CountsByRule["private-key"] >= 1);
		Assert.Equal(expected, Redactor.Redact(expected).Text);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(15)]
	public void Body_below_the_minimum_is_not_a_key(int length)
	{
		string input = $"{Begin}\n{Synthetic.Body(length, 509)}\n{End}";

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Body_at_the_minimum_is_a_key()
	{
		Assert.Equal(Placeholder, Redactor.Redact($"{Begin}\n{Synthetic.Body(16, 510)}\n{End}").Text);
	}

	[Theory]
	[InlineData("%")]
	[InlineData("%2")]
	[InlineData("%ZZ")]
	[InlineData("\\q")]
	public void Malformed_escape_ends_the_body_and_survives(string tail)
	{
		string input = $"{Begin}\n{Line1}{tail}";

		Assert.Equal(Placeholder + tail, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("%2Q")]
	[InlineData("%3Q")]
	[InlineData("%0Q")]
	[InlineData("%1A")]
	[InlineData("\\u00")]
	[InlineData("\\u0041Z")]
	public void Near_miss_encodings_are_not_base64(string tail)
	{
		string input = $"{Begin}\n{Line1}{tail}";

		string output = Redactor.Redact(input).Text;

		Assert.StartsWith(Placeholder, output, StringComparison.Ordinal);
		Assert.DoesNotContain(Line1, output, StringComparison.Ordinal);
	}

	[Fact]
	public void Prose_with_short_words_after_the_marker_is_not_body()
	{
		string input = $"{Begin} is the marker we search for and it needs no key";

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Header_look_alike_after_body_stops_the_walk()
	{
		string input = $"{Begin}\n{Line1}\nNote: this was cut off";

		Assert.Equal($"{Placeholder}\nNote: this was cut off", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Header_with_nothing_after_it_is_not_a_key()
	{
		string input = $"{Begin}\nProc-Type: 4,ENCRYPTED";

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("-----BEGIN " + Synthetic.PrivateWord + " KEY-----\n-")]
	[InlineData("-----BEGIN " + Synthetic.PrivateWord + " KEY-----\n:")]
	[InlineData("-----BEGIN " + Synthetic.PrivateWord + " KEY-----\nabc")]
	[InlineData("-----BEGIN " + Synthetic.PrivateWord + " KEY-----\n+")]
	public void Marker_at_the_end_of_text_is_not_a_key(string input)
	{
		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Lone_plus_at_the_end_of_text_is_not_key_material()
	{
		string input = $"{Begin}\n{Line1}\n+";

		Assert.Equal(Placeholder + "\n+", Redactor.Redact(input).Text);
	}

	[Fact]
	public void Plus_before_other_text_starts_a_token()
	{
		string input = $"{Begin}\n{Line1}\n+abc words";

		Assert.Equal($"{Placeholder}\n+abc words", Redactor.Redact(input).Text);
	}

	[Theory]
	[InlineData("xxxxxxxxxxxxxxxxxxxxxxxx", true)]
	[InlineData("XXXXXXXXXXXXXXXXXXXXXXXX", true)]
	public void Mask_of_xs_with_end_marker_survives(string body, bool expectedUnchanged)
	{
		string input = $"{Begin}\n{body}\n{End}";

		Assert.Equal(expectedUnchanged, Redactor.Redact(input).Text == input);
	}

	[Fact]
	public void Mask_of_xs_without_end_marker_survives()
	{
		string input = $"{Begin}\n{new string('x', 64)}";

		Assert.Equal(input, Redactor.Redact(input).Text);
	}
}
