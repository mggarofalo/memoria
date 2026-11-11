using Memoria.Api.Auth;
using Memoria.Api.Ingest;
using Memoria.Core.Contracts;
using Microsoft.Data.Sqlite;
using static Memoria.Api.Tests.IngestKit;

namespace Memoria.Api.Tests;

/// <summary>The ledger on its own control database: what it records about a write, a delete and a lease, at the edges the HTTP tests do not reach.</summary>
public sealed class IngestLedgerEdgeTests
{
	private readonly ControlDatabase _database = new(Path.Combine(Path.GetTempPath(), "memoria-tests", Guid.NewGuid().ToString("N"), "control.db"));

	private async Task<SqliteIngestLedger> LedgerAsync()
	{
		await _database.InitializeAsync();
		return new SqliteIngestLedger(_database);
	}

	private static async Task WriteAsync(SqliteIngestLedger ledger, IngestClaimRequest request, params string[] paths)
	{
		IngestClaim claim = await ledger.ClaimAsync(request, default);
		Assert.Equal(IngestClaimOutcome.Acquired, claim.Outcome);
		Assert.True(await ledger.FinishAsync(request.Identity, claim.LeaseToken!, request.Collection, IngestStateName.Complete, paths, request));
	}

	private async Task<string> RawAsync(string column, string identity)
	{
		await using SqliteConnection connection = await _database.OpenAsync();
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = $"SELECT {column} FROM ingest_state WHERE identity = $identity;";
		command.Parameters.AddWithValue("$identity", identity);
		return (string)(await command.ExecuteScalarAsync())!;
	}

	[Fact]
	public void A_rendered_document_is_linked_to_its_session_within_its_own_folder()
	{
		Assert.Equal(IngestIdentity.For("rendered-session", "c", "2024/", "s"), IngestIdentity.ForRenderedSession("c", "s", "2024/a.md"));
		Assert.Equal(IngestIdentity.ForRenderedSession("c", "s", "2024/a.md"), IngestIdentity.ForRenderedSession("c", "s", "2024/b.md"));
		Assert.NotEqual(IngestIdentity.ForRenderedSession("c", "s", "2024/a.md"), IngestIdentity.ForRenderedSession("c", "s", "2025/a.md"));
	}

	[Fact]
	public async Task Missing_arguments_are_refused_as_missing()
	{
		SqliteIngestLedger ledger = await LedgerAsync();

		Assert.Throws<ArgumentNullException>(static () => IngestIdentity.ForRenderedSession("c", "s", null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() => ledger.ClaimAsync(null!, default));
		await Assert.ThrowsAsync<ArgumentNullException>(() => ledger.FinishAsync("nobody", "token", "c", IngestStateName.Complete, null!, null));
		Assert.Throws<ArgumentNullException>(() => ledger.EffectiveState(null!, DateTimeOffset.UtcNow));
	}

	[Fact]
	public async Task A_stale_claim_carries_no_retry_time()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		await WriteAsync(ledger, new IngestClaimRequest("d", "c", Stamp, Stamp.AddDays(1), ["a.md"]), "a.md");

		IngestClaim stale = await ledger.ClaimAsync(new IngestClaimRequest("d", "c", Stamp.AddHours(-1), Stamp.AddDays(1), ["a.md"]), default);

		Assert.Equal(IngestClaimOutcome.Stale, stale.Outcome);
		Assert.Null(stale.RetryAfterSeconds);
	}

	[Fact]
	public async Task A_path_begun_is_recorded_once_as_the_uploads_own()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		IngestClaim claim = await ledger.ClaimAsync(new IngestClaimRequest("d", "c", Stamp, Stamp, []), default);

		Assert.True(await ledger.BeginWriteAsync("d", claim.LeaseToken!, "c", "a.md", Stamp, default));
		Assert.True(await ledger.BeginWriteAsync("d", claim.LeaseToken!, "c", "a.md", Stamp, default));

		Assert.Equal(["a.md"], (await ledger.FindAsync("d", default))!.Paths);
	}

	[Fact]
	public async Task A_lease_ends_exactly_when_its_time_is_up()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		await ledger.ClaimAsync(new IngestClaimRequest("d", "c", Stamp, Stamp, ["a.md"]), default);
		IngestRecord record = (await ledger.FindAsync("d", default))!;

		Assert.Equal(IngestStateName.Writing, ledger.EffectiveState(record, record.LeaseUntil.AddTicks(-1)));
		Assert.Equal(IngestStateName.Partial, ledger.EffectiveState(record, record.LeaseUntil));
		Assert.Equal(IngestClaimOutcome.Acquired, (await ledger.ClaimAsync(new IngestClaimRequest("d", "c", Stamp, Stamp + SqliteIngestLedger.Lease, ["a.md"]), default)).Outcome);
	}

	[Fact]
	public async Task A_path_an_upload_stopped_writing_is_free_for_another()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		await WriteAsync(ledger, new IngestClaimRequest("first", "c", Stamp, Stamp, ["a.md", "b.md"]), "a.md", "b.md");
		await WriteAsync(ledger, new IngestClaimRequest("first", "c", Stamp.AddHours(1), Stamp.AddHours(1), ["a.md"]), "a.md");

		Assert.Equal(IngestClaimOutcome.Acquired, (await ledger.ClaimAsync(new IngestClaimRequest("second", "c", Stamp, Stamp.AddHours(2), ["b.md"]), default)).Outcome);
		Assert.Equal(IngestClaimOutcome.PathConflict, (await ledger.ClaimAsync(new IngestClaimRequest("third", "c", Stamp, Stamp.AddHours(2), ["a.md"]), default)).Outcome);
	}

	[Fact]
	public async Task A_copy_dated_exactly_at_its_delete_stays_deleted()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		string identity = DocumentIngestor.IdentityOf("c", "a.md");
		await WriteAsync(ledger, new IngestClaimRequest(identity, "c", Stamp, Stamp, ["a.md"]), "a.md");
		DateTimeOffset deleted = Stamp.AddHours(1);
		await ledger.MarkDeletedAsync("c", "a.md", identity, deleted);

		IngestClaim same = await ledger.ClaimAsync(new IngestClaimRequest(identity, "c", deleted, deleted.AddMinutes(1), ["a.md"]), default);
		IngestClaim after = await ledger.ClaimAsync(new IngestClaimRequest(identity, "c", deleted.AddTicks(1), deleted.AddMinutes(1), ["a.md"]), default);

		Assert.Equal(IngestClaimOutcome.Stale, same.Outcome);
		Assert.Equal(IngestClaimOutcome.Acquired, after.Outcome);
	}

	[Fact]
	public async Task Each_deleted_part_of_a_session_stays_deleted_until_a_restore()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		await WriteAsync(ledger, new IngestClaimRequest("s", "c", Stamp, Stamp, ["p1.md", "p2.md", "p3.md"]), "p1.md", "p2.md", "p3.md");
		await ledger.MarkDeletedAsync("c", "p1.md", DocumentIngestor.IdentityOf("c", "p1.md"), Stamp.AddMinutes(1));
		await ledger.MarkDeletedAsync("c", "p2.md", DocumentIngestor.IdentityOf("c", "p2.md"), Stamp.AddMinutes(2));

		Assert.Equal(["p1.md", "p2.md"], (await ledger.FindAsync("s", default))!.DeletedPaths);

		IngestClaim update = await ledger.ClaimAsync(new IngestClaimRequest("s", "c", Stamp.AddHours(1), Stamp.AddHours(1), ["p3.md"]), default);
		Assert.Equal(["p1.md", "p2.md"], update.DeletedPaths);
		Assert.Equal(["p1.md", "p2.md"], (await ledger.FindAsync("s", default))!.DeletedPaths);
		await ledger.FinishAsync("s", update.LeaseToken!, "c", IngestStateName.Partial, ["p3.md"], null);

		IngestClaim restore = await ledger.ClaimAsync(new IngestClaimRequest("s", "c", Stamp.AddHours(2), Stamp.AddHours(2), ["p1.md", "p2.md", "p3.md"], Restore: true), default);
		Assert.Empty(restore.DeletedPaths);
		Assert.Empty((await ledger.FindAsync("s", default))!.DeletedPaths!);
	}

	[Fact]
	public async Task A_write_that_does_not_count_its_messages_keeps_the_stored_count_and_digest()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		await WriteAsync(ledger, new IngestClaimRequest("s", "c", Stamp, Stamp, ["a.md"], MessageCount: 3, MessagesDigest: "three"), "a.md");
		await WriteAsync(ledger, new IngestClaimRequest("s", "c", Stamp.AddHours(1), Stamp.AddHours(1), ["a.md"]), "a.md");

		IngestRecord record = (await ledger.FindAsync("s", default))!;
		Assert.Equal(3, record.MessageCount);
		Assert.Equal("three", record.MessagesDigest);
		IngestClaim lost = await ledger.ClaimAsync(new IngestClaimRequest("s", "c", Stamp.AddHours(2), Stamp.AddHours(2), ["a.md"], MessageCount: 3, StartsWithStored: static (_, _) => false), default);
		Assert.Equal(IngestClaimOutcome.LostContent, lost.Outcome);
	}

	[Fact]
	public async Task The_content_hash_is_the_last_one_that_landed_and_survives_a_write_without_one()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		await WriteAsync(ledger, new IngestClaimRequest("d", "c", Stamp, Stamp, ["a.md"], ContentHash: "first"), "a.md");
		await WriteAsync(ledger, new IngestClaimRequest("d", "c", Stamp.AddHours(1), Stamp.AddHours(1), ["a.md"], ContentHash: "second"), "a.md");

		Assert.Equal("second", (await ledger.FindAsync("d", default))!.ContentHash);

		await WriteAsync(ledger, new IngestClaimRequest("d", "c", Stamp.AddHours(2), Stamp.AddHours(2), ["a.md"]), "a.md");

		Assert.Equal("second", (await ledger.FindAsync("d", default))!.ContentHash);
	}

	[Fact]
	public async Task A_new_row_stores_an_empty_list_of_deleted_paths()
	{
		SqliteIngestLedger ledger = await LedgerAsync();

		await ledger.ClaimAsync(new IngestClaimRequest("d", "c", Stamp, Stamp, ["a.md"]), default);

		Assert.Equal("[]", await RawAsync("deleted_paths", "d"));
	}

	[Fact]
	public async Task A_row_whose_lists_read_as_null_is_read_as_empty()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		await WriteAsync(ledger, new IngestClaimRequest("d", "c", Stamp, Stamp, ["a.md"]), "a.md");
		await using (SqliteConnection connection = await _database.OpenAsync())
		{
			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "UPDATE ingest_state SET paths = 'null', deleted_paths = 'null' WHERE identity = 'd';";
			await command.ExecuteNonQueryAsync();
		}

		IngestRecord record = (await ledger.FindAsync("d", default))!;

		Assert.NotNull(record.Paths);
		Assert.Empty(record.Paths);
		Assert.NotNull(record.DeletedPaths);
		Assert.Empty(record.DeletedPaths);
	}
}

/// <summary>A session taking over the rendered documents recorded under its name: what it is compared with, and what it may write.</summary>
public sealed class RenderedTakeoverLedgerTests
{
	private const string Lookup = "rendered-lookup";

	private static readonly DateTimeOffset Later = Stamp.AddDays(1);

	private static async Task<SqliteIngestLedger> LedgerAsync()
	{
		ControlDatabase database = new(Path.Combine(Path.GetTempPath(), "memoria-tests", Guid.NewGuid().ToString("N"), "control.db"));
		await database.InitializeAsync();
		return new SqliteIngestLedger(database);
	}

	private static async Task WriteAsync(SqliteIngestLedger ledger, IngestClaimRequest request, params string[] paths)
	{
		IngestClaim claim = await ledger.ClaimAsync(request, default);
		Assert.Equal(IngestClaimOutcome.Acquired, claim.Outcome);
		Assert.True(await ledger.FinishAsync(request.Identity, claim.LeaseToken!, request.Collection, IngestStateName.Complete, paths, request));
	}

	/// <summary>A migrated document, still live, recorded under the session's rendered lookup.</summary>
	private static Task RenderedAsync(SqliteIngestLedger ledger, string path, DateTimeOffset version, long? messages = null)
	{
		return WriteAsync(ledger, new IngestClaimRequest(DocumentIngestor.IdentityOf("c", path), "c", version, version, [path], Lookup: Lookup, MessageCount: messages), path);
	}

	private static IngestClaimRequest Session(DateTimeOffset version, string[] paths, long? messages = null)
	{
		return new IngestClaimRequest("session", "c", version, Later, paths, MessageCount: messages) { RenderedLookup = Lookup };
	}

	[Fact]
	public async Task The_session_is_compared_with_its_own_paths_as_well_as_the_rendered_ones()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		await WriteAsync(ledger, new IngestClaimRequest("session", "c", Stamp, Stamp, ["own.md"]), "own.md");
		await RenderedAsync(ledger, "rendered.md", Stamp);

		IngestClaim claim = await ledger.ClaimAsync(Session(Stamp.AddHours(1), ["own.md", "rendered.md"]), default);

		Assert.Equal(IngestClaimOutcome.Acquired, claim.Outcome);
		Assert.Equal(["own.md", "rendered.md"], claim.PreviousPaths);
	}

	[Fact]
	public async Task A_part_the_session_had_deleted_stays_deleted_through_a_takeover()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		await WriteAsync(ledger, new IngestClaimRequest("session", "c", Stamp, Stamp, ["p1.md", "p2.md"]), "p1.md", "p2.md");
		await ledger.MarkDeletedAsync("c", "p2.md", DocumentIngestor.IdentityOf("c", "p2.md"), Stamp.AddMinutes(1));
		await RenderedAsync(ledger, "rendered.md", Stamp);

		IngestClaim claim = await ledger.ClaimAsync(Session(Stamp.AddHours(1), ["p1.md", "p2.md", "rendered.md"]), default);

		Assert.Equal(IngestClaimOutcome.Acquired, claim.Outcome);
		Assert.Equal(["p2.md"], claim.DeletedPaths);
	}

	[Fact]
	public async Task The_newest_version_of_the_session_and_its_documents_is_the_one_to_beat()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		await WriteAsync(ledger, new IngestClaimRequest("session", "c", Stamp.AddHours(2), Stamp.AddHours(2), ["own.md"]), "own.md");
		await RenderedAsync(ledger, "rendered.md", Stamp);

		IngestClaim between = await ledger.ClaimAsync(Session(Stamp.AddHours(1), ["own.md"]), default);

		Assert.Equal(IngestClaimOutcome.Stale, between.Outcome);
	}

	[Fact]
	public async Task The_longest_of_the_session_and_its_documents_is_the_count_to_reach()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		await WriteAsync(ledger, new IngestClaimRequest("session", "c", Stamp, Stamp, ["own.md"], MessageCount: 10), "own.md");
		await RenderedAsync(ledger, "rendered.md", Stamp, messages: 5);

		IngestClaim shorter = await ledger.ClaimAsync(Session(Stamp.AddHours(1), ["own.md"], messages: 7), default);

		Assert.Equal(IngestClaimOutcome.Shrunk, shorter.Outcome);
	}

	[Fact]
	public async Task A_session_still_being_written_is_busy_whatever_its_documents_say()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		await ledger.ClaimAsync(new IngestClaimRequest("session", "c", Stamp, Later, ["own.md"]), default);
		await RenderedAsync(ledger, "rendered.md", Stamp);

		IngestClaim again = await ledger.ClaimAsync(Session(Stamp.AddHours(1), ["own.md"]), default);

		Assert.Equal(IngestClaimOutcome.Busy, again.Outcome);
	}

	[Fact]
	public async Task A_path_owned_by_something_other_than_the_documents_still_conflicts()
	{
		SqliteIngestLedger ledger = await LedgerAsync();
		await WriteAsync(ledger, new IngestClaimRequest("stranger", "c", Stamp, Stamp, ["theirs.md"]), "theirs.md");
		await RenderedAsync(ledger, "rendered.md", Stamp);

		IngestClaim claim = await ledger.ClaimAsync(Session(Stamp.AddHours(1), ["rendered.md", "theirs.md"]), default);

		Assert.Equal(IngestClaimOutcome.PathConflict, claim.Outcome);
	}
}
