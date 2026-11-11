using Memoria.Api.Configuration;
using Memoria.Api.Sidecar;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Tests;

public sealed class ReadinessProbeArgumentTests
{
	/// <summary>Refused at the call, not handed back later as a failed probe that every waiting caller would share.</summary>
	[Fact]
	public void A_probe_is_required()
	{
		ReadinessCache cache = new(TimeProvider.System, Options.Create(new MemoriaOptions()));

		Assert.Throws<ArgumentNullException>(() => { _ = cache.GetAsync(null!); });
	}
}
