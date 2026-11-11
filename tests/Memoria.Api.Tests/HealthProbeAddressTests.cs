using Memoria.Api.Hosting;

namespace Memoria.Api.Tests;

/// <summary>Which address the container's health check probes, for listen lists written the ways ASPNETCORE_URLS allows.</summary>
public sealed class HealthProbeAddressTests
{
	[Theory]
	[InlineData("http://*:7070", "http://127.0.0.1:7070/health")]
	[InlineData(" http://192.168.1.5:7071 ", "http://192.168.1.5:7071/health")]
	[InlineData(";http://192.168.1.5:7072", "http://192.168.1.5:7072/health")]
	[InlineData(" ; http://192.168.1.5:7073;http://192.168.1.5:7074", "http://192.168.1.5:7073/health")]
	public void The_first_real_address_is_probed_on_loopback_where_it_is_a_wildcard(string urls, string expected)
	{
		Assert.Equal(new Uri(expected), HealthProbe.ResolveUrl(urls));
	}
}
