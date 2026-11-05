using System.Globalization;
using System.Text;
using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// Every shape-judged rule, in its real format, against shapes that look as little like a token as a real one can.
/// Each cell is seeded from <see cref="DefaultSeed"/>, so a miss reproduces, and a failure names the seed. Set
/// MEMORIA_TOKEN_SEED to try another, MEMORIA_TOKEN_ROUNDS to run more, and MEMORIA_TOKEN_REPORT to a file to get the whole table.
/// <para>
/// These are heavy, so they run alone: alongside them, the wall-clock budgets in <see cref="RedactorSafetyTests"/> measure the contention rather than the redactor.
/// </para>
/// </summary>
[Collection(nameof(GeneratedTokenCollection))]
public sealed class GeneratedTokenTests
{
	private const int DefaultSeed = 20261105;
	private const int DefaultRounds = 25;

	private static readonly Redactor Redactor = new();

	private static int Seed => int.TryParse(Environment.GetEnvironmentVariable("MEMORIA_TOKEN_SEED"), out int seed) ? seed : DefaultSeed;

	private static int Rounds => int.TryParse(Environment.GetEnvironmentVariable("MEMORIA_TOKEN_ROUNDS"), out int rounds) ? rounds : DefaultRounds;

	public static TheoryData<TokenSpec> Specs()
	{
		TheoryData<TokenSpec> data = [];
		foreach (TokenSpec spec in TokenShapes.All)
		{
			data.Add(spec);
		}

		return data;
	}

	[Theory]
	[MemberData(nameof(Specs))]
	public void Generated_token_of_every_shape_is_redacted(TokenSpec spec)
	{
		List<string> failures = [];
		foreach (TokenShape shape in Enum.GetValues<TokenShape>().Where(shape => TokenShapes.Applies(spec, shape)))
		{
			int misses = CountMisses(spec, shape, Seed, Rounds, out string? firstMiss);
			Report(spec, shape, misses);
			if (misses > 0 && !ExpectedToMiss(spec, shape))
			{
				failures.Add($"{shape}: {misses} of {Rounds} missed, first {firstMiss}");
			}
		}

		Assert.True(failures.Count == 0, $"seed {Seed}, {spec}: {string.Join("; ", failures)}");
	}

	public static TheoryData<TokenSpec> PrefixedSpecs()
	{
		TheoryData<TokenSpec> data = [];
		foreach (TokenSpec spec in TokenShapes.Prefixed)
		{
			data.Add(spec);
		}

		return data;
	}

	/// <summary>The same formats, encoded: what a token's own rule cannot see has to be seen through the decoding. Encoding also hides a value's context, so only those that stand alone.</summary>
	[Theory]
	[MemberData(nameof(PrefixedSpecs))]
	public void Generated_token_encoded_is_redacted(TokenSpec spec)
	{
		if (spec.Context.Contains("hooks.", StringComparison.Ordinal) || spec.Context.Contains("webhook", StringComparison.Ordinal))
		{
			return;
		}

		Random random = new(TokenShapes.CellSeed(Seed, spec, TokenShape.Random));
		for (int round = 0; round < Rounds; round++)
		{
			string token = TokenShapes.Generate(spec, TokenShape.Random, random);
			foreach ((string form, string encoded) in new[]
			{
				("base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(token))),
				("URL-safe base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(token)).TrimEnd('=').Replace('+', '-').Replace('/', '_')),
				("percent", Percent(token)),
			})
			{
				string output = Redactor.Redact("the value is " + encoded + " here").Text;
				Assert.False(output.Contains(encoded, StringComparison.Ordinal), $"seed {Seed}, {spec}, {form}, round {round}: {encoded}");
			}
		}
	}

	/// <summary>Every separator encoded, and every first letter, so no rule can read the token as it stands.</summary>
	private static string Percent(string token)
	{
		StringBuilder builder = new();
		for (int i = 0; i < token.Length; i++)
		{
			char c = token[i];
			builder.Append(i == 0 || !char.IsAsciiLetterOrDigit(c) ? string.Create(CultureInfo.InvariantCulture, $"%{(int)c:X2}") : c.ToString());
		}

		return builder.ToString();
	}

	/// <summary>
	/// What is expected to get through, named here so that nothing new can join it unnoticed. With no prefix of its own, lower-case
	/// letters split by separators cannot be told from a name, and these rules must leave <c>my-word-only-name</c> alone.
	/// </summary>
	private static bool ExpectedToMiss(TokenSpec spec, TokenShape shape)
	{
		return shape == TokenShape.LowerWithSeparators
			&& spec.Rule is "azure-sas-signature" or "bearer-token" or "discord-token" or "query-credential" or "secret-flag" or "url-token"
			|| (shape == TokenShape.LowerWithSeparators && spec.Rule == "secret-assignment" && spec.Context.StartsWith("api_key", StringComparison.Ordinal));
	}

	private static int CountMisses(TokenSpec spec, TokenShape shape, int seed, int rounds, out string? firstMiss)
	{
		Random random = new(TokenShapes.CellSeed(seed, spec, shape));
		int misses = 0;
		firstMiss = null;
		for (int round = 0; round < rounds; round++)
		{
			string token = TokenShapes.Generate(spec, shape, random);
			string output = Redactor.Redact(spec.Context.Replace("{S}", token, StringComparison.Ordinal)).Text;
			if (Leaks(output, token))
			{
				misses++;
				firstMiss ??= $"at round {round}: {token}";
			}
		}

		return misses;
	}

	/// <summary>The whole token, or a piece long enough to matter: replacing half of one is not redacting it.</summary>
	private static bool Leaks(string output, string token)
	{
		return output.Contains(token, StringComparison.Ordinal)
			|| token.Split('-', '_', '.', '/', '@', ':', '~').Any(piece => piece.Length >= 12 && output.Contains(piece, StringComparison.Ordinal));
	}

	private static void Report(TokenSpec spec, TokenShape shape, int misses)
	{
		string? path = Environment.GetEnvironmentVariable("MEMORIA_TOKEN_REPORT");
		if (path is not null)
		{
			string line = string.Create(CultureInfo.InvariantCulture, $"{spec.Rule}\t{spec.Template}\t{spec.Context}\t{shape}\t{misses}\t{Rounds}\n");
			lock (Redactor)
			{
				File.AppendAllText(path, line, new UTF8Encoding(false));
			}
		}
	}
}

/// <summary>Runs <see cref="GeneratedTokenTests"/> after the parallel tests rather than among them.</summary>
[CollectionDefinition(nameof(GeneratedTokenCollection), DisableParallelization = true)]
public sealed class GeneratedTokenCollection;
