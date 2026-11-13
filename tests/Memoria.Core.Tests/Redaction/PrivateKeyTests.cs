using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>PEM blocks in the forms a transcript holds them, asserting the exact bytes left around the placeholder.</summary>
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
			{ "words after the end stay", $"{key} IMPORTANT", $"{Placeholder} IMPORTANT" },
			{ "truncated at end of text", $"{Begin}\n{Line1}\n{Line2}", Placeholder },
			{ "truncated then fence", $"```\n{Begin}\n{Line1}\n```\nnext", $"```\n{Placeholder}\n```\nnext" },
			{ "truncated then prose goes to its punctuation", $"{Begin}\n{Line1}\n\nThe output was cut off here.", $"{Placeholder}." },
			{ "json escaped", $"{{\"key\":\"{Begin}\\n{Line1}\\n{Short}\\n{End}\\n\"}}", $"{{\"key\":\"{Placeholder}\\n\"}}" },
			{ "json escaped truncated", $"{{\"key\":\"{Begin}\\n{Line1}\\n{Short}\"}}", $"{{\"key\":\"{Placeholder}\"}}" },
			{ "json carriage returns", $"\"{Begin}\\r\\n{Line1}\\r\\n{End}\"", $"\"{Placeholder}\"" },
			{ "double escaped", $"\\\"{Begin}\\\\n{Line1}\\\\n{End}\\\\n\\\"", $"\\\"{Placeholder}\\\\n\\\"" },
			{ "yaml block", $"key: |\n  {Begin}\n  {Line1}\n  {Short}\n  {End}\nnext: 1", $"key: |\n  {Placeholder}\nnext: 1" },
			{ "flattened by env dump", $"KEY={Begin} {Line1} {Short} {End} NEXT=1", $"KEY={Placeholder} NEXT=1" },
			{ "two keys", $"{key}\n{key}", $"{Placeholder}\n{Placeholder}" },
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
	[InlineData(31)]
	public void Body_without_a_run_of_key_material_is_not_a_key(int length)
	{
		string input = $"{Begin}\n{Synthetic.Body(length, 509)}\n{End}";

		Assert.Equal(input, Redactor.Redact(input).Text);
	}

	[Fact]
	public void Body_with_a_run_at_the_minimum_is_a_key()
	{
		Assert.Equal(Placeholder, Redactor.Redact($"{Begin}\n{Synthetic.Body(32, 510)}\n{End}").Text);
	}

	[Fact]
	public void Prose_after_the_marker_is_not_a_key()
	{
		string input = $"{Begin} is the marker we search for and it needs no key";

		Assert.Equal(input, Redactor.Redact(input).Text);
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
	public void Marker_at_the_end_of_text_is_not_a_key(string input)
	{
		Assert.Equal(input, Redactor.Redact(input).Text);
	}
}
