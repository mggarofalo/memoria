using System.Net;
using Memoria.Core.Sidecar;

namespace Memoria.Core.Tests;

/// <summary>Which sidecar failures are worth retrying, and that a bounded body lets go of the connection's stream.</summary>
public sealed class SidecarFailureTests
{
	[Theory]
	[InlineData(null, true)]
	[InlineData(HttpStatusCode.InternalServerError, true)]
	[InlineData(HttpStatusCode.ServiceUnavailable, true)]
	[InlineData((HttpStatusCode)499, false)]
	[InlineData(HttpStatusCode.TooManyRequests, false)]
	[InlineData(HttpStatusCode.NotFound, false)]
	public void Only_no_answer_or_a_server_error_is_transient(HttpStatusCode? status, bool transient)
	{
		SidecarException error = new(status is null ? SidecarException.Unreachable : "error", "failed", status);

		Assert.Equal(transient, error.IsTransient);
	}

	[Fact]
	public void Disposing_a_bounded_stream_disposes_the_stream_it_reads()
	{
		MemoryStream inner = new(new byte[4]);
		BoundedReadStream stream = new(inner, 8, static () => new InvalidOperationException("over"));

		stream.Dispose();

		Assert.False(inner.CanRead);
	}
}
