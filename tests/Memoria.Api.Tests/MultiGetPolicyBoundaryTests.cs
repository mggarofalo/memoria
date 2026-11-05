using Memoria.Api.Search;

namespace Memoria.Api.Tests;

/// <summary>Each limit in the glob grammar, one either side of the line, with the message a caller reads.</summary>
public sealed class MultiGetPolicyBoundaryTests
{
	private static string[] Message(Dictionary<string, string[]> errors, string field)
	{
		return errors.GetValueOrDefault(field) ?? [];
	}

	private static string Repeated(int count, string format)
	{
		return string.Join(',', Enumerable.Range(0, count).Select(i => string.Format(System.Globalization.CultureInfo.InvariantCulture, format, i)));
	}

	[Fact]
	public void NoSelector_IsRefused_ByName()
	{
		Assert.Equal(["Provide `ids`, `pattern`, or both."], Message(MultiGetPolicy.Validate(null, null, null, null), "ids"));
		Assert.Equal(["Provide `ids`, `pattern`, or both."], Message(MultiGetPolicy.Validate([], " ", null, null), "ids"));
		Assert.Empty(MultiGetPolicy.Validate([], "*.md", null, null));
	}

	[Fact]
	public void IdCount_IsCapped_AtExactlyTheLimit()
	{
		string[] allowed = Enumerable.Range(0, MultiGetPolicy.MaxReferencesPerRequest).Select(static i => $"r{i}.md").ToArray();
		Assert.Empty(MultiGetPolicy.Validate(allowed, null, null, null));
		Assert.Equal(["At most 200 ids per request."], Message(MultiGetPolicy.Validate([.. allowed, "one-more.md"], null, null, null), "ids"));
	}

	[Fact]
	public void AnIdWithAComma_IsRefused_EvenAmongGoodOnes()
	{
		Assert.Equal(["An id may not contain a comma."], Message(MultiGetPolicy.Validate(["good.md", "a,b.md"], null, null, null), "ids"));
	}

	[Fact]
	public void GlobCount_IsCapped_AtExactlyTheLimit()
	{
		Assert.Empty(MultiGetPolicy.Validate(null, Repeated(MultiGetPolicy.MaxGlobsPerRequest, "c{0}/*.md"), null, null));
		Assert.Equal(["At most 10 globs per request."], Message(MultiGetPolicy.Validate(null, Repeated(MultiGetPolicy.MaxGlobsPerRequest + 1, "c{0}/*.md"), null, null), "pattern"));
	}

	[Fact]
	public void ReferenceCountInAPattern_IsCapped()
	{
		Assert.Equal(["At most 200 references per request."], Message(MultiGetPolicy.Validate(null, Repeated(MultiGetPolicy.MaxReferencesPerRequest + 1, "r{0}.md"), null, null), "pattern"));
	}

	[Fact]
	public void ReferenceLength_IsCapped_AtExactlyTheLimit()
	{
		Assert.Empty(MultiGetPolicy.Validate([new string('x', MultiGetPolicy.MaxReferenceLength)], null, null, null));
		Assert.Equal(["A reference may be at most 1024 characters."], Message(MultiGetPolicy.Validate([new string('x', MultiGetPolicy.MaxReferenceLength + 1)], null, null, null), "ids"));
	}

	[Fact]
	public void TheFirstBadGlob_IsWhatTheCallerHearsAbout()
	{
		Assert.Equal(["A glob may not put '*' next to '?'."], Message(MultiGetPolicy.Validate(null, "good/*.md,bad/*?.md", null, null), "pattern"));
	}

	[Fact]
	public void MaxBytesAndMaxLines_NameTheirLimits()
	{
		Assert.Equal(["maxBytes must be between 1 and 1048576."], Message(MultiGetPolicy.Validate(["a.md"], null, 0, null), "maxBytes"));
		Assert.Equal(["maxLines must be at least 1."], Message(MultiGetPolicy.Validate(["a.md"], null, null, 0), "maxLines"));
		Assert.Empty(MultiGetPolicy.Validate(["a.md"], null, 1, 1));
	}

	[Fact]
	public void GlobLength_IsCapped_AtExactlyTheLimit()
	{
		Assert.Null(MultiGetPolicy.GlobViolation("*" + new string('a', MultiGetPolicy.MaxGlobLength - 1)));
		Assert.Equal("A glob may be at most 256 characters.", MultiGetPolicy.GlobViolation("*" + new string('a', MultiGetPolicy.MaxGlobLength)));
	}

	[Fact]
	public void Globstars_AreCapped_AtExactlyTheLimit()
	{
		Assert.Null(MultiGetPolicy.GlobViolation("**/a/**/b"));
		Assert.Equal("A glob may contain at most 2 '**' segments.", MultiGetPolicy.GlobViolation("**/a/**/b/**"));
	}

	[Fact]
	public void Wildcards_AreCapped_AtExactlyTheLimit_CountingAGlobstarAsOne()
	{
		Assert.Null(MultiGetPolicy.GlobViolation("**/" + string.Concat(Enumerable.Repeat("?a", MultiGetPolicy.MaxGlobWildcards - 1))));
		Assert.Equal("A glob may contain at most 12 wildcards.", MultiGetPolicy.GlobViolation("**/" + string.Concat(Enumerable.Repeat("?a", MultiGetPolicy.MaxGlobWildcards))));
	}

	[Theory]
	[InlineData("a{b,c}", "A glob may contain only letters, digits, space, '.', '_', '-', '/', ':', '*' and '?'. Braces, brackets, parentheses and extglobs are refused.")]
	[InlineData("a/**b", "'**' must be a whole path segment.")]
	public void Grammar_NamesTheProblem(string glob, string message)
	{
		Assert.Equal(message, MultiGetPolicy.GlobViolation(glob));
	}

	[Fact]
	public void ANullGlob_IsAProgrammingError()
	{
		Assert.Throws<ArgumentNullException>(() => MultiGetPolicy.GlobViolation(null!));
	}
}
