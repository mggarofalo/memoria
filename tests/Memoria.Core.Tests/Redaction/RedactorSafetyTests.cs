using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// Fail-closed behavior, idempotence, frontmatter, the public surface, and time budgets.
/// <para>
/// The two budget tests time only the redaction call, in the CPU time of the thread that makes it, which is the redactor's own: other tests,
/// other processes and the garbage collector's background thread do not count. They are in the <see cref="TimingCategory"/> category, which
/// the coverage run and the mutation runs leave out, as instrumented code is slower by a factor that differs from case to case. They run
/// on their own, uninstrumented, in Build &amp; Test and the pre-commit hook, so each budget is about 4 times the cost measured there on a
/// Debug build, and a real regression fails on a fast machine too.
/// </para>
/// </summary>
[Collection(nameof(RedactorBudgetCollection))]
public sealed class RedactorSafetyTests
{
	private const string Secret = "THE-SECRET-VALUE-" + "12345";

	private static readonly Redactor Redactor = new();

	private static readonly TimeSpan Generous = TimeSpan.FromMinutes(1);

	/// <summary>The trait the coverage and mutation runs filter out, and Build &amp; Test runs on its own.</summary>
	public const string TimingCategory = "Timing";

	/// <summary>The slowest case, a run of <c>a=b;password=</c>, took 5.8 s for both passes. The others take 0.2 to 2.6 s.</summary>
	private static readonly TimeSpan AdversarialBudget = TimeSpan.FromSeconds(24);

	/// <summary>It took 24.7 s.</summary>
	private static readonly TimeSpan LargeTranscriptBudget = TimeSpan.FromSeconds(100);

	public static TheoryData<string> Adversarial()
	{
		const int Size = 2_000_000;
		return
		[
			new string('a', Size),
			new string('\\', Size),
			new string('%', Size),
			new string('-', Size),
			new string('=', Size),
			new string(':', Size),
			new string('"', Size),
			new string('\n', Size),
			Repeat("password=", Size),
			Repeat("token=", Size),
			Repeat("password=\"", Size),
			Repeat("a=b;password=", Size),
			Repeat("s" + "k-", Size),
			Repeat("s" + "k-a", Size),
			Repeat("-----BEGIN " + Synthetic.PrivateWord + " KEY-----", Size),
			Repeat("-----BEGIN " + Synthetic.PrivateWord + " KEY----- AAAAAAAAAAAAAAAAAAAAAAAA ", Size),
			Repeat("-----BEGIN " + Synthetic.PrivateWord + " KEY-----\n" + new string('x', 64) + "\n", Size),
			Repeat("postgres://", Size),
			Repeat("postgres://a:", Size),
			"postgres://" + Repeat("a:", Size),
			"postgres://a:" + Repeat("@", Size),
			Repeat("Authorization: Bearer ", Size),
			Repeat("Bearer ", Size),
			Repeat("cu" + "rl ", Size),
			Repeat("cu" + "rl -u ", Size),
			Repeat("Cookie: a=b;", Size),
			Repeat("--password ", Size),
			Repeat("ey" + "J.", Size),
			Repeat("ey" + "Jaaaaaaa.", Size),
			Repeat("mem_a_", Size),
			Repeat("\\n", Size),
			Repeat("\\u0041", Size),
			Repeat("%41", Size),
			new string('a', Size) + "token=" + Synthetic.Body(32, 600),
			"token=\"" + new string('a', Size),
		];
	}

	[Theory]
	[MemberData(nameof(Adversarial))]
	[Trait("Category", TimingCategory)]
	public void Adversarial_input_finishes_within_budget_and_stays_stable(string input)
	{
		string? once = null;
		string? twice = null;
		RedactionException? refused = null;
		TimeSpan started = ThreadCpuTime.Now();

		// A crafted run may use up the rescan credit, which refuses the document rather than skipping anything.
		try
		{
			once = Redactor.Redact(input).Text;
			twice = Redactor.Redact(once).Text;
		}
		catch (RedactionException exception)
		{
			refused = exception;
		}

		TimeSpan spent = ThreadCpuTime.Now() - started;
		Assert.True(spent < AdversarialBudget, $"Two passes over 2 MB took {spent.TotalSeconds:F1} s of CPU, over the budget of {AdversarialBudget.TotalSeconds:F0} s.");
		if (refused is null)
		{
			Assert.Equal(once, twice);
		}
		else
		{
			Assert.Equal(RedactionFailure.ScanBudgetExhausted, refused.Failure);
		}
	}

	/// <summary>A transcript of about 50 MB of ordinary session text, with the token once in each block, and how many blocks it has.</summary>
	internal static (string Text, int Blocks) LargeTranscript(string token)
	{
		const int TargetLength = 50 * 1024 * 1024;
		StringBuilder builder = new(TargetLength + 8192);
		int blocks = 0;
		while (builder.Length < TargetLength)
		{
			builder.Append("## Assistant\n\nRan the build at commit ").Append(Synthetic.Hex(40, blocks % 97))
				.Append(" and read src/Memoria.Core/Redaction/Redactor.cs.\n\n```json\n{\"input_tokens\": 1234, \"max_tokens\": 4096, \"file\": \"a/b.cs\"}\n```\n\n")
				.Append("| name | value |\n|---|---|\n| id | 2e39c784-1a2b-4c3d-8e9f-0a1b2c3d4e5f |\n\n")
				.Append("export GH=").Append(token).Append('\n')
				.Append("The tokens were counted and the password policy is unchanged. ").Append(Synthetic.Base64(512, 7)).Append("\n\n");
			blocks++;
		}

		return (builder.ToString(), blocks);
	}

	[Fact]
	[Trait("Category", TimingCategory)]
	public void Large_transcript_meets_the_time_budget_and_redacts_everything()
	{
		string token = Synthetic.GitHubClassic('p');
		(string input, int blocks) = LargeTranscript(token);
		TimeSpan started = ThreadCpuTime.Now();

		RedactionResult result = Redactor.Redact(input);

		TimeSpan spent = ThreadCpuTime.Now() - started;
		Assert.True(spent < LargeTranscriptBudget, $"A 50 MB transcript took {spent.TotalSeconds:F1} s of CPU, over the budget of {LargeTranscriptBudget.TotalSeconds:F0} s.");
		Assert.Equal(blocks, result.CountsByRule["github-token"]);
		Assert.DoesNotContain(token, result.Text, StringComparison.Ordinal);
		Assert.Equal(new[] { "github-token" }, result.CountsByRule.Keys);
	}

	[Fact]
	public void Regex_timeout_refuses_the_document_and_says_nothing_about_it()
	{
		Regex catastrophic = new("(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1));
		Redactor redactor = new([new RedactionRule("slow", catastrophic)], Generous);
		string input = Secret + new string('a', 64) + "!";

		RedactionException exception = Assert.Throws<RedactionException>(() => redactor.Redact(input));

		Assert.Equal("slow", exception.RuleName);
		Assert.Equal(RedactionFailure.TimedOut, exception.Failure);
		AssertSilent(exception);
	}

	[Fact]
	public void Throwing_judge_refuses_the_document_and_says_nothing_about_it()
	{
		RedactionRule rule = new("broken", Compile("SECRET"), _ => throw new InvalidOperationException(Secret));
		Redactor redactor = new([rule], Generous);

		RedactionException exception = Assert.Throws<RedactionException>(() => redactor.Redact(Secret));

		Assert.Equal("broken", exception.RuleName);
		Assert.Equal(RedactionFailure.RuleFailed, exception.Failure);
		AssertSilent(exception);
	}

	[Fact]
	public void Failure_in_a_later_rule_returns_no_partial_text()
	{
		RedactionRule good = new("good", Compile("SECRET"));
		RedactionRule bad = new("bad", Compile("VALUE"), _ => throw new InvalidOperationException());
		Redactor redactor = new([good, bad], Generous);

		RedactionException exception = Assert.Throws<RedactionException>(() => redactor.Redact(Secret));

		Assert.Equal("bad", exception.RuleName);
	}

	[Fact]
	public void Empty_span_is_a_rule_failure_rather_than_an_endless_loop()
	{
		Redactor redactor = new([new RedactionRule("empty", Compile("x*"))], Generous);

		RedactionException exception = Assert.Throws<RedactionException>(() => redactor.Redact(Secret));

		Assert.Equal(RedactionFailure.RuleFailed, exception.Failure);
	}

	[Fact]
	public void Exhausted_budget_refuses_the_document()
	{
		Redactor redactor = new([new RedactionRule("any", Compile("SECRET"))], TimeSpan.FromTicks(-1));

		RedactionException exception = Assert.Throws<RedactionException>(() => redactor.Redact(Secret));

		Assert.Equal("any", exception.RuleName);
		Assert.Equal(RedactionFailure.TimedOut, exception.Failure);
	}

	[Fact]
	public void Rules_that_never_settle_refuse_the_document()
	{
		Redactor redactor = new([new RedactionRule("loop", Compile("o"))], Generous);

		RedactionException exception = Assert.Throws<RedactionException>(() => redactor.Redact("o"));

		Assert.Equal(RedactionException.WholeDocument, exception.RuleName);
		Assert.Equal(RedactionFailure.DidNotConverge, exception.Failure);
	}

	[Fact]
	public void Null_input_is_rejected_rather_than_passed_through()
	{
		Assert.Throws<ArgumentNullException>(() => Redactor.Redact((string)null!));
		Assert.Throws<ArgumentNullException>(() => Redactor.Redact((Memoria.Core.Sessions.UnredactedMarkdown)null!));
	}

	[Fact]
	public void Exception_message_names_only_rule_and_failure()
	{
		RedactionException exception = new("some-rule", RedactionFailure.TimedOut);

		Assert.Equal("Redaction failed in rule 'some-rule' (TimedOut). The document must not be indexed.", exception.Message);
	}

	[Fact]
	public void Public_surface_offers_no_way_to_change_or_disable_rules()
	{
		Assert.True(typeof(Redactor).IsSealed);
		Assert.Empty(Assert.Single(typeof(Redactor).GetConstructors()).GetParameters());
		Assert.False(typeof(RedactionRule).IsPublic);
		Assert.False(typeof(RedactionRules).IsPublic);
		Assert.All(typeof(Redactor).GetProperties(), property => Assert.Null(property.SetMethod));
	}

	[Fact]
	public void Rule_names_are_unique_stable_and_their_placeholders_are_inert()
	{
		Assert.Equal(Redactor.RuleNames.Count, Redactor.RuleNames.Distinct().Count());
		Assert.All(Redactor.RuleNames, name =>
		{
			Assert.Matches("^[a-z0-9-]+$", name);
			string placeholder = $"[REDACTED:{name}]";
			Assert.Equal(placeholder, Redactor.Redact(placeholder).Text);
			Assert.Equal($"token={placeholder}", Redactor.Redact($"token={placeholder}").Text);
			Assert.Equal($"\"password\": \"{placeholder}\"", Redactor.Redact($"\"password\": \"{placeholder}\"").Text);
		});
	}

	[Fact]
	public void Every_built_in_rule_uses_the_non_backtracking_engine()
	{
		Assert.All(RedactionRules.All, rule =>
		{
			Assert.True(rule.Pattern.Options.HasFlag(RegexOptions.NonBacktracking), rule.Name);
			Assert.NotEqual(Regex.InfiniteMatchTimeout, rule.Pattern.MatchTimeout);
		});
	}

	[Fact]
	public void Unchanged_text_returns_the_same_instance_with_no_counts()
	{
		const string Input = "nothing to see";

		RedactionResult result = Redactor.Redact(Input);

		Assert.Same(Input, result.Text);
		Assert.False(result.WasRedacted);
	}

	[Fact]
	public void Secret_in_a_frontmatter_title_is_removed_and_the_yaml_stays_parseable()
	{
		string token = Synthetic.GitHubClassic('p');
		string input = $"---\ntitle: {token} rotated, it's done\nproject: Memoria\n---\n\n{token}\n";

		RedactionResult result = Redactor.Redact(input);

		Assert.Equal("---\ntitle: '[REDACTED:github-token] rotated, it''s done'\nproject: Memoria\n---\n\n[REDACTED:github-token]\n", result.Text);
		Assert.Equal(2, result.CountsByRule["github-token"]);
		Assert.Equal(result.Text, Redactor.Redact(result.Text).Text);
	}

	[Theory]
	[InlineData("---\ntitle: fix {S} now\n---\n", "---\ntitle: fix [REDACTED:github-token] now\n---\n")]
	[InlineData("---\ntags:\n  - {S}\n---\n", "---\ntags:\n  - '[REDACTED:github-token]'\n---\n")]
	[InlineData("---\r\ntitle: {S}  \r\n---\r\n", "---\r\ntitle: '[REDACTED:github-token]'  \r\n---\r\n")]
	[InlineData("---\ntitle: {S}", "---\ntitle: '[REDACTED:github-token]'")]
	[InlineData("---\ntitle: ok\n---\ntitle: {S}\n", "---\ntitle: ok\n---\ntitle: [REDACTED:github-token]\n")]
	[InlineData("title: {S}\n", "title: [REDACTED:github-token]\n")]
	[InlineData("---{S}", "---[REDACTED:github-token]")]
	[InlineData("---\n{S}\n---\n", "---\n[REDACTED:github-token]\n---\n")]
	[InlineData("---\ntitle:{S}\n---\n", "---\ntitle:[REDACTED:github-token]\n---\n")]
	[InlineData("---\ntitle = {S}\n---\n", "---\ntitle = [REDACTED:github-token]\n---\n")]
	public void Frontmatter_values_are_quoted_only_where_yaml_needs_it(string template, string expected)
	{
		string input = template.Replace("{S}", Synthetic.GitHubClassic('p'), StringComparison.Ordinal);

		RedactionResult result = Redactor.Redact(input);

		Assert.Equal(expected, result.Text);
		Assert.Equal(expected, Redactor.Redact(result.Text).Text);
	}

	[Fact]
	public void Concurrent_use_gives_the_same_answer_on_every_thread()
	{
		string input = $"{Synthetic.Anthropic} password=hunter2 {Synthetic.Jwt}";
		string expected = Redactor.Redact(input).Text;

		string[] results = new string[32];
		Parallel.For(0, results.Length, i => results[i] = Redactor.Redact(input).Text);

		Assert.All(results, text => Assert.Equal(expected, text));
	}

	private static Regex Compile(string pattern) => new(pattern, RegexOptions.None, TimeSpan.FromSeconds(5));

	private static string Repeat(string unit, int size)
	{
		return new StringBuilder(size + unit.Length).Insert(0, unit, (size / unit.Length) + 1).ToString();
	}

	private static void AssertSilent(RedactionException exception)
	{
		Assert.Null(exception.InnerException);
		Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
		Assert.Empty(exception.Data);
	}
}

/// <summary>Runs <see cref="RedactorSafetyTests"/> after the parallel tests, so its budgets are not measured on a core other tests are sharing.</summary>
[CollectionDefinition(nameof(RedactorBudgetCollection), DisableParallelization = true)]
public sealed class RedactorBudgetCollection;
