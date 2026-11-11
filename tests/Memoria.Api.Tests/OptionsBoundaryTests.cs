using Memoria.Api.Configuration;

namespace Memoria.Api.Tests;

/// <summary>
/// The validator stops the host with these messages, so an operator reads them verbatim: each limit is checked at its edge, and
/// each refusal word for word.
/// </summary>
public sealed class OptionsBoundaryTests
{
	private static MemoriaOptions Valid()
	{
		return new MemoriaOptions { Auth = new AuthOptions { DatabasePath = "control.db" }, Ingest = new IngestOptions { DigestKey = "a-key-of-at-least-thirty-two-characters" } };
	}

	private static string[] Failures(Action<MemoriaOptions> change)
	{
		MemoriaOptions options = Valid();
		change(options);
		return new MemoriaOptionsValidator().Validate(null, options).Failures?.ToArray() ?? [];
	}

	public static TheoryData<string, Action<MemoriaOptions>> AtTheEdge => new()
	{
		{ "readiness cache of exactly a minute", static o => o.Sidecar.ReadinessCacheTime = TimeSpan.FromMinutes(1) },
		{ "default limit equal to the max", static o => { o.Search.MaxLimit = 20; o.Search.DefaultLimit = 20; } },
		{ "digest key of exactly 32 characters", static o => o.Ingest.DigestKey = new string('k', 32) },
		{ "document cap of exactly 64 KiB", static o => o.Ingest.MaxDocumentBytes = 64 * 1024 },
		{ "session cap of exactly 1024 bytes", static o => o.Ingest.MaxSessionBytes = 1024 },
		{ "one render at a time", static o => o.Ingest.MaxConcurrentRenders = 1 },
		{ "an upload speed floor of one byte a second", static o => o.Ingest.MinUploadBytesPerSecond = 1 },
		{ "no reindex interval", static o => o.Ingest.MinReindexInterval = TimeSpan.Zero },
		{ "keys that live exactly an hour", static o => o.Auth.DefaultKeyLifetime = TimeSpan.FromHours(1) },
		{ "keys that live exactly two years", static o => o.Auth.DefaultKeyLifetime = TimeSpan.FromDays(730) },
		{ "two tracked addresses", static o => o.Auth.FailedAuth.MaxTrackedAddresses = 2 },
		{ "a sidecar on localhost with no secret", static o => { o.Sidecar.BaseUrl = "http://localhost:8282"; o.Sidecar.Secret = null; } },
	};

	[Theory]
	[MemberData(nameof(AtTheEdge))]
	public void A_value_on_the_limit_is_accepted(string because, Action<MemoriaOptions> change)
	{
		Assert.True(Failures(change).Length == 0, because);
	}

	public static TheoryData<string, Action<MemoriaOptions>> PastTheEdge => new()
	{
		{ "Memoria:Ingest:MaxDocumentBytes must be at least 65536, as frontmatter alone can take 8 KiB.", static o => o.Ingest.MaxDocumentBytes = (64 * 1024) - 1 },
		{ "Memoria:Ingest:MaxSessionBytes must be at least 1024.", static o => o.Ingest.MaxSessionBytes = 1023 },
		{ "Memoria:Ingest:MaxConcurrentRenders must be at least 1.", static o => o.Ingest.MaxConcurrentRenders = 0 },
		{ "Memoria:Ingest:MinUploadBytesPerSecond must be at least 1.", static o => o.Ingest.MinUploadBytesPerSecond = 0 },
		{ "Memoria:Ingest:SessionUploadDeadline must be positive.", static o => o.Ingest.SessionUploadDeadline = TimeSpan.Zero },
		{ "Memoria:Ingest:DocumentUploadDeadline must be positive.", static o => o.Ingest.DocumentUploadDeadline = TimeSpan.Zero },
		{ "Memoria:Ingest:DeleteWriteWait must be positive.", static o => o.Ingest.DeleteWriteWait = TimeSpan.Zero },
		{ "Memoria:Ingest:MinReindexInterval may not be negative.", static o => o.Ingest.MinReindexInterval = TimeSpan.FromTicks(-1) },
	};

	[Theory]
	[MemberData(nameof(PastTheEdge))]
	public void A_value_past_the_limit_stops_the_host_with_this_message(string message, Action<MemoriaOptions> change)
	{
		Assert.Equal([message], Failures(change));
	}

	/// <summary>An unknown mode says so and nothing else: the rules for either mode would only mislead.</summary>
	[Fact]
	public void An_unknown_auth_mode_is_the_only_complaint_about_auth()
	{
		string[] failures = Failures(static o =>
		{
			o.Auth.Mode = "apikey";
			o.Auth.DatabasePath = null;
			o.Auth.DefaultKeyLifetime = TimeSpan.FromMinutes(1);
		});

		Assert.Equal(["Memoria:Auth:Mode must be exactly ApiKey or Disabled, case-sensitive; numbers, lists, surrounding whitespace and empty values are refused."], failures);
	}
}
