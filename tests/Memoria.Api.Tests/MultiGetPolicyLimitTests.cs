using Memoria.Api.Search;

namespace Memoria.Api.Tests;

/// <summary>
/// Each of <see cref="MultiGetPolicy"/>'s limits on both sides of its line, and the field and words each refusal
/// carries: the caller gets them as a 400's validation errors, so they are what a client can act on.
/// </summary>
public sealed class MultiGetPolicyLimitTests
{
	private static string[] Refusal(Dictionary<string, string[]> errors, string field)
	{
		Assert.Equal([field], errors.Keys);
		return errors[field];
	}

	private static Dictionary<string, string[]> Validate(IReadOnlyList<string>? ids = null, string? pattern = null, int? maxBytes = null, int? maxLines = null)
	{
		return MultiGetPolicy.Validate(ids, pattern, maxBytes, maxLines);
	}

	private static string Glob(int wildcards)
	{
		return "c/" + string.Concat(Enumerable.Repeat("a*", wildcards));
	}

	[Fact]
	public void AnEmptyIdListAndNoPattern_AskForOne()
	{
		Assert.Equal(["Provide `ids`, `pattern`, or both."], Refusal(Validate(ids: []), "ids"));
	}

	[Fact]
	public void Ids_UpToTheLimit_Pass_AndOneMore_IsRefused()
	{
		string[] ids = [.. Enumerable.Range(0, MultiGetPolicy.MaxReferencesPerRequest).Select(static i => $"c/{i}.md")];
		Assert.Empty(Validate(ids));

		Assert.Equal(["At most 200 ids per request."], Validate([.. ids, "c/one-more.md"])["ids"]);
	}

	[Fact]
	public void AnyIdWithAComma_IsRefused()
	{
		Assert.Equal(["An id may not contain a comma."], Refusal(Validate(["c/a.md", "c/b,c.md"]), "ids"));
	}

	[Fact]
	public void MaxBytes_OutsideItsRange_IsRefused_NamingTheRange()
	{
		Assert.Empty(Validate(["c/a.md"], maxBytes: 1));
		Assert.Empty(Validate(["c/a.md"], maxBytes: MultiGetPolicy.MaxBytesCeiling));
		Assert.Equal(["maxBytes must be between 1 and 1048576."], Refusal(Validate(["c/a.md"], maxBytes: 0), "maxBytes"));
	}

	[Fact]
	public void MaxLines_OfOne_Passes_AndZero_IsRefused()
	{
		Assert.Empty(Validate(["c/a.md"], maxLines: 1));
		Assert.Equal(["maxLines must be at least 1."], Refusal(Validate(["c/a.md"], maxLines: 0), "maxLines"));
	}

	[Fact]
	public void Globs_UpToTheLimit_Pass_AndOneMore_IsRefused()
	{
		string Globs(int count) => string.Join(',', Enumerable.Range(0, count).Select(static i => $"c/{i}*.md"));

		Assert.Empty(Validate(pattern: Globs(MultiGetPolicy.MaxGlobsPerRequest)));
		Assert.Equal(["At most 10 globs per request."], Refusal(Validate(pattern: Globs(MultiGetPolicy.MaxGlobsPerRequest + 1)), "pattern"));
	}

	[Fact]
	public void PlainReferences_UpToTheLimit_Pass_AndOneMore_IsRefused()
	{
		string References(int count) => string.Join(',', Enumerable.Range(0, count).Select(static i => $"c/{i}.md"));

		Assert.Empty(Validate(pattern: References(MultiGetPolicy.MaxReferencesPerRequest)));
		Assert.Equal(["At most 200 references per request."], Refusal(Validate(pattern: References(MultiGetPolicy.MaxReferencesPerRequest + 1)), "pattern"));
	}

	[Fact]
	public void AReference_AtTheLengthLimit_Passes_AndOneLonger_IsRefused()
	{
		string atLimit = "c/" + new string('a', MultiGetPolicy.MaxReferenceLength - 2);

		Assert.Empty(Validate([atLimit]));
		Assert.Equal(["A reference may be at most 1024 characters."], Refusal(Validate([atLimit + "a"]), "ids"));
	}

	[Fact]
	public void AGlobOutsideTheGrammar_IsRefusedUnderPattern_WithItsReason()
	{
		Assert.Equal([MultiGetPolicy.GlobViolation("c/[ab].md")!], Refusal(Validate(pattern: "c/[ab].md"), "pattern"));
	}

	[Fact]
	public void GlobRefusals_SayWhichRuleWasBroken()
	{
		Assert.Equal("A glob may contain only letters, digits, space, '.', '_', '-', '/', ':', '*' and '?'. Braces, brackets, parentheses and extglobs are refused.", MultiGetPolicy.GlobViolation("c/{a}.md"));
		Assert.Equal("A glob may not put '*' next to '?'.", MultiGetPolicy.GlobViolation("c/a*?.md"));
		Assert.Equal("'**' must be a whole path segment.", MultiGetPolicy.GlobViolation("c/a**/b.md"));
	}

	[Fact]
	public void AGlob_AtTheLengthLimit_Passes_AndOneLonger_IsRefused()
	{
		string atLimit = "c/" + new string('a', MultiGetPolicy.MaxGlobLength - 3) + "*";

		Assert.Null(MultiGetPolicy.GlobViolation(atLimit));
		Assert.Equal("A glob may be at most 256 characters.", MultiGetPolicy.GlobViolation(atLimit + "a"));
	}

	[Fact]
	public void TwoGlobstars_Pass_AndThree_AreRefused()
	{
		Assert.Null(MultiGetPolicy.GlobViolation("**/a/**/b.md"));
		Assert.Equal("A glob may contain at most 2 '**' segments.", MultiGetPolicy.GlobViolation("**/a/**/b/**/c.md"));
	}

	[Fact]
	public void Wildcards_UpToTheLimit_Pass_AndOneMore_IsRefused_CountingAGlobstarAsOne()
	{
		Assert.Null(MultiGetPolicy.GlobViolation(Glob(MultiGetPolicy.MaxGlobWildcards)));
		Assert.Equal("A glob may contain at most 12 wildcards.", MultiGetPolicy.GlobViolation(Glob(MultiGetPolicy.MaxGlobWildcards + 1)));
		Assert.Null(MultiGetPolicy.GlobViolation("**/" + Glob(MultiGetPolicy.MaxGlobWildcards - 1)));
		Assert.Equal("A glob may contain at most 12 wildcards.", MultiGetPolicy.GlobViolation("**/" + Glob(MultiGetPolicy.MaxGlobWildcards)));
	}
}
