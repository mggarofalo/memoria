using System.Net;

namespace Memoria.Api.Tests;

/// <summary>
/// The document route turns an encoded slash into a real one, so a reference can
/// spell a path it could not spell before. None may reach a sidecar route other
/// than document lookup. The real-server version is in scripts/e2e.
/// </summary>
public sealed class DocumentTraversalTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private const string Allowed = "claude-sessions";

	private const string StatusBody = """{"totalDocuments":1,"needsEmbedding":0,"hasVectorIndex":false,"collections":[]}""";

	[Theory]
	[InlineData("..%2Fstatus")]
	[InlineData("a%2F..%2F..%2Fstatus")]
	[InlineData("%2E%2E%2Fstatus")]
	[InlineData("..%5Cstatus")]
	[InlineData("..%252Fstatus")]
	[InlineData("%252E%252E%252Fstatus")]
	[InlineData("%2F%2Fstatus")]
	[InlineData("a%5C..%5C..%5Cstatus")]
	[InlineData("..%2F..%2Fadmin%2Fkeys")]
	[InlineData(".%2Fstatus")]
	public async Task GetDocument_CannotReachAnotherSidecarRoute(string reference)
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/status", HttpStatusCode.OK, StatusBody);
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		HttpResponseMessage response = await reader.GetAsync(new Uri($"/v1/docs/{reference}", UriKind.Relative));

		Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest });
		Assert.DoesNotContain("totalDocuments", await response.Content.ReadAsStringAsync());
		Assert.All(factory.Sidecar.Requests, request =>
		{
			Assert.Equal("GET", request.Method);
			Assert.StartsWith("/docs/", request.Uri.AbsolutePath);
		});
	}

	// These segments are what would have normalized away. They must stop before the sidecar at all.
	[Theory]
	[InlineData("..%2Fstatus")]
	[InlineData("a%2F..%2F..%2Fstatus")]
	[InlineData("%2E%2E%2Fstatus")]
	[InlineData("..%5Cstatus")]
	[InlineData("..%252Fstatus")]
	public async Task GetDocument_DotSegments_NeverReachTheSidecar(string reference)
	{
		factory.Sidecar.Requests.Clear();
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		HttpResponseMessage response = await reader.GetAsync(new Uri($"/v1/docs/{reference}", UriKind.Relative));

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Empty(factory.Sidecar.Requests);
	}
}
