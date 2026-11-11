using Memoria.Api.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Memoria.Api.Tests;

/// <summary>
/// The convention that leaves a body's content type to be judged after authorization. It runs once per
/// host, so it is tested here on its own rather than through a shared host.
/// </summary>
public sealed class ContentTypeConventionTests
{
	private sealed class Conventions : IEndpointConventionBuilder
	{
		public List<Action<EndpointBuilder>> Finals { get; } = [];

		public void Add(Action<EndpointBuilder> convention)
		{
		}

		public void Finally(Action<EndpointBuilder> finallyConvention)
		{
			Finals.Add(finallyConvention);
		}
	}

	private sealed class Accepts(string contentType) : IAcceptsMetadata
	{
		public IReadOnlyList<string> ContentTypes { get; } = [contentType];

		public Type? RequestType => null;

		public bool IsOptional => false;
	}

	[Fact]
	public void NoBuilder_IsRefused()
	{
		Assert.Throws<ArgumentNullException>(() => ContentTypeAfterAuthorization.DecideContentTypeAfterAuthorization<Conventions>(null!));
	}

	[Fact]
	public void EveryAcceptsEntry_IsRemoved_WhereverItSits_AndNothingElse()
	{
		Conventions conventions = new();
		RouteEndpointBuilder endpoint = new(static _ => Task.CompletedTask, RoutePatternFactory.Parse("/v1/admin/reindex"), 0);
		Accepts first = new("application/json");
		Accepts last = new("text/json");
		endpoint.Metadata.Add(first);
		endpoint.Metadata.Add("kept");
		endpoint.Metadata.Add(new Accepts("application/xml"));
		endpoint.Metadata.Add(42);
		endpoint.Metadata.Add(last);

		Conventions returned = conventions.DecideContentTypeAfterAuthorization();
		foreach (Action<EndpointBuilder> convention in conventions.Finals)
		{
			convention(endpoint);
		}

		Assert.Same(conventions, returned);
		Assert.Equal(["kept", 42], endpoint.Metadata);
	}
}
