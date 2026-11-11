using Memoria.Api.Configuration;
using Memoria.Api.Ingest;
using Memoria.Core.Sidecar;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Tests;

/// <summary>What an operator is told when a collection will not open to ingest, and a collection another request opened first.</summary>
public sealed class IngestCollectionOpeningTests
{
	private static IngestCollections Collections(IIngestLedger ledger, string? enabled = null)
	{
		MemoriaOptions options = new();
		if (enabled is not null)
		{
			options.Ingest.Collections[enabled] = new IngestCollectionOptions { Enabled = true };
		}

		return new IngestCollections(ledger, Options.Create(options), TimeProvider.System);
	}

	private static (IMemoriaQmdClient Client, ScriptedSidecarClient Script) NotEmptySidecar()
	{
		(IMemoriaQmdClient client, ScriptedSidecarClient script) = ScriptedSidecarClient.Create();
		script.On(nameof(IMemoriaQmdClient.IsCollectionEmptyAsync), static args => Task.FromResult(new SidecarCollectionEmptyResponse((string)args[0]!, false)));
		return (client, script);
	}

	[Fact]
	public async Task A_collection_not_enabled_names_the_setting_that_opens_it()
	{
		(IMemoriaQmdClient client, ScriptedSidecarClient script) = NotEmptySidecar();

		IngestRefusedException refused = await Assert.ThrowsAsync<IngestRefusedException>(() => Collections(new OpenAfter(0)).RequireOpenAsync("notes", client, default));

		Assert.Equal("ingest_not_enabled", refused.Code);
		Assert.Equal("Collection notes is not open to ingest. An operator enables it with Memoria:Ingest:Collections:notes:Enabled, and its directory must be empty when ingest first uses it.", refused.Message);
		Assert.Empty(script.Calls);
	}

	[Fact]
	public async Task A_collection_with_files_says_why_it_must_start_empty()
	{
		(IMemoriaQmdClient client, _) = NotEmptySidecar();

		IngestRefusedException refused = await Assert.ThrowsAsync<IngestRefusedException>(() => Collections(new OpenAfter(int.MaxValue), "notes").RequireOpenAsync("notes", client, default));

		Assert.Equal("collection_not_empty", refused.Code);
		Assert.Equal("Collection notes has files in its directory, or its directory does not exist, so it cannot be opened to ingest. Ingest owns everything in a collection it writes to, so it must start empty, and its volume must be mounted.", refused.Message);
	}

	[Fact]
	public async Task A_collection_opened_while_this_request_waited_is_not_checked_again()
	{
		(IMemoriaQmdClient client, ScriptedSidecarClient script) = NotEmptySidecar();
		OpenAfter ledger = new(1);

		await Collections(ledger, "notes").RequireOpenAsync("notes", client, default);

		Assert.Empty(script.Calls);
		Assert.Equal(2, ledger.Asked);
	}

	/// <summary>A ledger that says a collection is closed for the first few questions and open after, as when another request opens it in between.</summary>
	private sealed class OpenAfter(int closedAnswers) : IIngestLedger
	{
		public int Asked { get; private set; }

		public Task<bool> IsCollectionOpenAsync(string collection, CancellationToken cancellationToken)
		{
			return Task.FromResult(Asked++ >= closedAnswers);
		}

		public Task OpenCollectionAsync(string collection, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<IngestClaim> ClaimAsync(IngestClaimRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<bool> BeginWriteAsync(string identity, string leaseToken, string collection, string path, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<bool> FinishAsync(string identity, string leaseToken, string collection, string state, IReadOnlyList<string> paths, IngestClaimRequest? landed) => throw new NotSupportedException();

		public Task MarkDeletedAsync(string collection, string path, string documentIdentity, DateTimeOffset now) => throw new NotSupportedException();

		public Task ForgetPathAsync(string collection, string path) => throw new NotSupportedException();

		public Task<IngestRecord?> FindAsync(string identity, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<IReadOnlyList<IngestRecord>> FindByLookupAsync(string lookup, CancellationToken cancellationToken) => throw new NotSupportedException();

		public string EffectiveState(IngestRecord record, DateTimeOffset now) => throw new NotSupportedException();
	}
}

/// <summary>The keyed digests, at the edges of their arguments and configuration.</summary>
public sealed class IngestDigestEdgeTests
{
	[Fact]
	public void A_missing_stored_digest_is_refused_as_missing()
	{
		IngestDigests digests = new(Options.Create(new MemoriaOptions { Ingest = new IngestOptions { DigestKey = "a-test-key-of-at-least-thirty-two-characters" } }));

		Assert.Throws<ArgumentNullException>(() => digests.Matches(null!, "text"));
	}

	[Fact]
	public void An_unset_key_names_the_setting()
	{
		IngestDigests digests = new(Options.Create(new MemoriaOptions()));

		Assert.Equal("Memoria:Ingest:DigestKey is not set.", Assert.Throws<InvalidOperationException>(() => digests.Of("text")).Message);
	}
}
