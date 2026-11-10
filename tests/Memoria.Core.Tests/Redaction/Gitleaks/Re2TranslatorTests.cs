namespace Memoria.Core.Tests.Redaction.Gitleaks;

public sealed class Re2TranslatorTests
{
	[Theory]
	[InlineData(@"\d\w\s", "[0-9][0-9A-Za-z_][\\t\\n\\f\\r ]")]
	[InlineData(@"\D\W\S", "[^0-9][^0-9A-Za-z_][^\\t\\n\\f\\r ]")]
	[InlineData(@"[\w.-]", "[0-9A-Za-z_.-]")]
	[InlineData(@"[^\d]", "[^0-9]")]
	[InlineData(@"[\S]", "[^\\t\\n\\f\\r ]")]
	[InlineData(@"a$", @"a\z")]
	[InlineData(@"[$]", "[$]")]
	[InlineData(@"\$", @"\$")]
	[InlineData(@"[[:alnum:]]", "[0-9A-Za-z]")]
	[InlineData(@"[\s\S-]", @"[\s\S]")]
	[InlineData(@"[\s\S]", @"[\s\S]")]
	[InlineData(@"[a-[]", @"[a-\[]")]
	[InlineData(@"\x60", @"\x60")]
	[InlineData(@"\x{60}", @"\x60")]
	[InlineData(@"\x{2028}", "\\u2028")]
	[InlineData(@"(?i)a(?-i:b)(?s:.)", "(?i)a(?-i:b)(?s:.)")]
	[InlineData(@"\bA\b\z", @"\bA\b\z")]
	[InlineData(@"[]a]", "[]a]")]
	public void Translates_to_the_same_meaning_on_ascii_text(string re2, string dotnet)
	{
		Assert.Equal(dotnet, Re2Translator.TranslatePlain(re2));
	}

	[Theory]
	[InlineData(@"key=(\w+)", @"key=(?<secret>[0-9A-Za-z_]+)", 0)]
	[InlineData(@"(a)|(b)", "(?<secret>a)|(?<secret>b)", 0)]
	[InlineData(@"x(?:(a)|(b))", "x(?:(?<secret>a)|(?<secret>b))", 0)]
	[InlineData(@"(a(b))", "(?<secret>a(?:b))", 0)]
	[InlineData(@"([a-z]{4}-){3}x", "(?:[a-z]{4}-){3}x", 0)]
	[InlineData(@"(a)(b)", "(?:a)(?:b)", 0)]
	[InlineData(@"(a)?b", "(?:a)?b", 0)]
	[InlineData(@"x(a){2}|(b)", "x(?:a){2}|(?:b)", 0)]
	[InlineData(@"(login|token)=(\w+)", @"(?:login|token)=(?<secret>[0-9A-Za-z_]+)", 2)]
	[InlineData(@"(?P<alg>ab)c", "(?:ab)c", 0)]
	[InlineData(@"Z(?:(?P<a>x)|(?P<b>y))[a-z]{40}", "Z(?:(?:x)|(?:y))[a-z]{40}", 0)]
	[InlineData(@"ghp_[0-9a-z]{36}", "ghp_[0-9a-z]{36}", 0)]
	public void The_secret_is_the_group_upstream_reports(string re2, string dotnet, int secretGroup)
	{
		Assert.Equal(dotnet, Re2Translator.TranslateRule(re2, secretGroup));
	}

	[Theory]
	[InlineData(@"a(?=b)")]
	[InlineData(@"a(?!b)")]
	[InlineData(@"(?<=a)b")]
	[InlineData(@"(?<!a)b")]
	[InlineData(@"\Qa\E")]
	[InlineData(@"\pL")]
	[InlineData(@"\C")]
	[InlineData(@"(a)\1")]
	[InlineData(@"(?U)a+")]
	[InlineData(@"[\Da]")]
	[InlineData(@"[^\D]")]
	[InlineData(@"[[:nonsense:]]")]
	[InlineData(@"(a")]
	[InlineData(@"a)")]
	[InlineData(@"\")]
	[InlineData(@"[ab")]
	[InlineData(@"\x{1F600}")]
	public void Refuses_what_it_cannot_say_the_same_way(string re2)
	{
		Assert.Throws<NotSupportedException>(() => Re2Translator.TranslatePlain(re2));
	}

	[Fact]
	public void A_secret_group_number_out_of_range_is_refused()
	{
		Assert.Throws<NotSupportedException>(static () => Re2Translator.TranslateRule("(a)", 2));
	}
}
