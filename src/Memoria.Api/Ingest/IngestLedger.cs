using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memoria.Api.Auth;
using Memoria.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Memoria.Api.Ingest;

public enum IngestClaimOutcome
{
	Acquired,

	/// <summary>The recorded version is newer, or this copy is what was deleted. Taking it would replace new text with old.</summary>
	Stale,

	/// <summary>Another upload of the same thing is running.</summary>
	Busy,

	/// <summary>A path this upload would write belongs to something else. Writing it would destroy that text.</summary>
	PathConflict,

	/// <summary>Another session already has this short file-name stem. Claim again with a longer one.</summary>
	StemClash,

	/// <summary>This upload has fewer messages than the recorded one, and the caller did not say that is intended.</summary>
	Shrunk,

	/// <summary>This upload's messages do not start with the ones recorded. Overwriting would replace text it does not have.</summary>
	LostContent,

}

/// <summary>What is recorded about one document or session: what its last write that landed stored. A lease says one is running now.</summary>
public sealed record IngestRecord(
	DateTimeOffset Version,
	string State,
	DateTimeOffset LeaseUntil,
	IReadOnlyList<string> Paths,
	string? LeaseOwner = null,
	long? MessageCount = null,
	string? ContentHash = null,
	string? MessagesDigest = null,
	IReadOnlyList<string>? DeletedPaths = null);

/// <summary>What an upload wants to take. <see cref="Identity"/> must be made from the same inputs as <see cref="Paths"/>, or ordering and ownership stop meaning anything.</summary>
public sealed record IngestClaimRequest(
	string Identity,
	string Collection,
	DateTimeOffset Version,
	DateTimeOffset Now,
	IReadOnlyList<string> Paths,
	string? Lookup = null,
	string? Stem = null,
	long? MessageCount = null,
	bool AllowShrink = false,
	string? ContentHash = null,
	bool Restore = false,
	string? MessagesDigest = null,
	Func<long, string, bool>? StartsWithStored = null,
	Func<string, bool>? IsSameContent = null)
{
	/// <summary>For a session: the lookup its rendered documents in its own folder were recorded under. Those it takes over, as the same text under the session's own rules.</summary>
	public string? RenderedLookup { get; init; }

	/// <summary>For a session that may be another fork: it leaves its rendered documents where they are, and only a path someone deleted still applies to it.</summary>
	public bool LeavesRendered { get; init; }
}

/// <summary>The answer to a claim. <see cref="PreviousPaths"/> is what earlier uploads wrote, which the index may not have seen yet.</summary>
public sealed record IngestClaim(IngestClaimOutcome Outcome, IReadOnlyList<string> PreviousPaths, string? LeaseToken = null, int? RetryAfterSeconds = null)
{
	/// <summary>Parts deleted from this session, which stay deleted.</summary>
	public IReadOnlyList<string> DeletedPaths { get; init; } = [];
}

/// <summary>
/// The durable record of what was ingested. It keeps uploads ordered: a claim is one transaction that compares versions and takes a lease, so an old replay is
/// refused and two uploads of one session cannot interleave. It also remembers which session each path belongs to, and what was deleted. Everything in an
/// ingest collection is Memoria's, so nothing here decides whether a file may be overwritten.
/// </summary>
public interface IIngestLedger
{
	/// <summary>Takes the lease, or says why not. An equal version is a retry, and allowed. Records nothing about what is stored until a write lands.</summary>
	Task<IngestClaim> ClaimAsync(IngestClaimRequest request, CancellationToken cancellationToken);

	/// <summary>
	/// Keeps the lease and notes this path as the upload's, before the write goes out, so a delete can find its session even if the answer never comes back.
	/// Call it holding the path's lock. False when the lease was lost to a delete, and the upload must stop writing.
	/// </summary>
	Task<bool> BeginWriteAsync(string identity, string leaseToken, string collection, string path, DateTimeOffset now, CancellationToken cancellationToken);

	/// <summary>
	/// Records how the upload ended and releases the lease. <paramref name="landed"/> is what to record about it when any of its writes landed; when none did,
	/// what was stored is unchanged, so nothing about it changes either. Does nothing when the lease has passed on, which is what false means.
	/// </summary>
	Task<bool> FinishAsync(string identity, string leaseToken, string collection, string state, IReadOnlyList<string> paths, IngestClaimRequest? landed);

	/// <summary>
	/// Leaves the tombstone, before the file goes, and takes the lease from any upload holding it. A document stays deleted against an older copy; a session's part leaves the
	/// session partial, and stays deleted. Written first so a crash leaves a file a repeated delete can remove, not a removed file an old copy can bring back. Call it holding the path's lock.
	/// </summary>
	Task MarkDeletedAsync(string collection, string path, string documentIdentity, DateTimeOffset now);

	/// <summary>The file is confirmed gone, so the path is no one's.</summary>
	Task ForgetPathAsync(string collection, string path);

	/// <summary>Whether this collection has been checked empty and opened to ingest.</summary>
	Task<bool> IsCollectionOpenAsync(string collection, CancellationToken cancellationToken);

	Task OpenCollectionAsync(string collection, DateTimeOffset now, CancellationToken cancellationToken);

	Task<IngestRecord?> FindAsync(string identity, CancellationToken cancellationToken);

	/// <summary>Every copy recorded under one session name. Copies relocated to another project folder are separate sessions that share it.</summary>
	Task<IReadOnlyList<IngestRecord>> FindByLookupAsync(string lookup, CancellationToken cancellationToken);

	/// <summary>The state to report: an upload holding a live lease is writing, and one whose lease has lapsed or whose process has died is not.</summary>
	string EffectiveState(IngestRecord record, DateTimeOffset now);
}

public static class IngestIdentity
{
	/// <summary>A digest, because session ids and source ids are caller text and may hold a credential that belongs nowhere in a table. Unkeyed on purpose: these must stay the same for good, and cover identifiers, not content.</summary>
	public static string For(params string?[] parts)
	{
		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts))));
	}

	/// <summary>
	/// Links a rendered document to the session it says it belongs to, whichever format that session is later sent in, and only within one folder. Exports may be the only copy of what they
	/// hold, and one in another folder can be another fork of the session, so a session leaves it alone: a double in search is better than a loss.
	/// </summary>
	public static string ForRenderedSession(string collection, string sessionId, string path)
	{
		ArgumentNullException.ThrowIfNull(path);
		return For("rendered-session", collection, path[..(path.LastIndexOf('/') + 1)], sessionId);
	}
}

/// <summary>The ledger in the control database. One API process owns that database, so a lease from any other process is a dead one.</summary>
public sealed class SqliteIngestLedger(ControlDatabase database) : IIngestLedger
{
	/// <summary>Long enough to outlast one part's sidecar call.</summary>
	public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

	private const string Columns = "version_ticks, state, lease_until_ticks, paths, lease_owner, message_count, content_hash, messages_digest, deleted_paths";

	private readonly string _instance = Guid.NewGuid().ToString("N");

	public async Task<IngestClaim> ClaimAsync(IngestClaimRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
		IReadOnlyList<RenderedRow> rendered = await RenderedRowsAsync(connection, transaction, request, cancellationToken);
		IngestRecord? row = TakingOver(await ReadAsync(connection, transaction, request.Identity, cancellationToken), rendered, request);
		IReadOnlyList<string> previous = row?.Paths ?? [];
		IngestClaimOutcome outcome = Decide(row, request);
		if (outcome != IngestClaimOutcome.Acquired)
		{
			return new IngestClaim(outcome, previous, null, outcome == IngestClaimOutcome.Busy ? SecondsLeft(row!, request.Now) : null);
		}

		outcome = await CheckPathsAsync(connection, transaction, request, rendered, cancellationToken);
		if (outcome != IngestClaimOutcome.Acquired)
		{
			return new IngestClaim(outcome, previous);
		}

		await TakeOverAsync(connection, transaction, request.Identity, rendered);
		string token = $"{_instance}:{Guid.NewGuid():N}";
		await UpsertAsync(connection, transaction, request.Identity, request.Lookup, Claimed(request, row, token), cancellationToken);
		await TakeStemAsync(connection, transaction, request, cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		return new IngestClaim(IngestClaimOutcome.Acquired, previous, token) { DeletedPaths = request.Restore ? [] : row?.DeletedPaths ?? [] };
	}

	public async Task<bool> BeginWriteAsync(string identity, string leaseToken, string collection, string path, DateTimeOffset now, CancellationToken cancellationToken)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
		IngestRecord? row = await ReadAsync(connection, transaction, identity, cancellationToken);
		if (row is null || row.LeaseOwner != leaseToken)
		{
			return false;
		}

		await InsertOwnedAsync(connection, transaction, identity, collection, path, cancellationToken);
		IReadOnlyList<string> paths = row.Paths.Contains(path, StringComparer.Ordinal) ? row.Paths : [.. row.Paths, path];
		await UpsertAsync(connection, transaction, identity, null, row with { LeaseUntil = now + Lease, Paths = paths }, cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		return true;
	}

	public async Task<bool> FinishAsync(string identity, string leaseToken, string collection, string state, IReadOnlyList<string> paths, IngestClaimRequest? landed)
	{
		ArgumentNullException.ThrowIfNull(paths);
		await using SqliteConnection connection = await database.OpenAsync();
		await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
		IngestRecord? row = await ReadAsync(connection, transaction, identity, CancellationToken.None);
		if (row is null || row.LeaseOwner != leaseToken)
		{
			return false;
		}

		await UpsertAsync(connection, transaction, identity, null, Finished(row, landed, state, paths), CancellationToken.None);
		await ReplaceOwnedPathsAsync(connection, transaction, identity, collection, paths);
		await transaction.CommitAsync();
		return true;
	}

	public async Task MarkDeletedAsync(string collection, string path, string documentIdentity, DateTimeOffset now)
	{
		await using SqliteConnection connection = await database.OpenAsync();
		await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
		string identity = await OwnerAsync(connection, transaction, collection, path, CancellationToken.None) ?? documentIdentity;
		IngestRecord? row = await ReadAsync(connection, transaction, identity, CancellationToken.None);
		// Deleting what is already deleted changes nothing, so a repeat cannot move a tombstone past what has been stored since.
		if (row is null || row is { State: IngestStateName.Deleted, LeaseOwner: null })
		{
			return;
		}

		await UpsertAsync(connection, transaction, identity, null, AfterDelete(row, identity == documentIdentity, path, now), CancellationToken.None);
		await transaction.CommitAsync();
	}

	public async Task ForgetPathAsync(string collection, string path)
	{
		await using SqliteConnection connection = await database.OpenAsync();
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "DELETE FROM ingest_paths WHERE collection = $collection AND path = $path;";
		command.Parameters.AddWithValue("$collection", collection);
		command.Parameters.AddWithValue("$path", path);
		await command.ExecuteNonQueryAsync();
	}

	public async Task<bool> IsCollectionOpenAsync(string collection, CancellationToken cancellationToken)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT EXISTS (SELECT 1 FROM ingest_collections WHERE collection = $collection);";
		command.Parameters.AddWithValue("$collection", collection);
		return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0;
	}

	public async Task OpenCollectionAsync(string collection, DateTimeOffset now, CancellationToken cancellationToken)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "INSERT OR IGNORE INTO ingest_collections (collection, opened_ticks) VALUES ($collection, $opened);";
		command.Parameters.AddWithValue("$collection", collection);
		command.Parameters.AddWithValue("$opened", now.UtcTicks);
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	public async Task<IngestRecord?> FindAsync(string identity, CancellationToken cancellationToken)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		return await ReadAsync(connection, null, identity, cancellationToken);
	}

	public async Task<IReadOnlyList<IngestRecord>> FindByLookupAsync(string lookup, CancellationToken cancellationToken)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = $"SELECT {Columns} FROM ingest_state WHERE lookup = $lookup;";
		command.Parameters.AddWithValue("$lookup", lookup);
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		List<IngestRecord> records = [];
		while (await reader.ReadAsync(cancellationToken))
		{
			records.Add(Map(reader));
		}

		return records;
	}

	public string EffectiveState(IngestRecord record, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(record);
		return IsLeased(record, now) ? IngestStateName.Writing : record.State;
	}

	private sealed record RenderedRow(string Identity, IngestRecord Record)
	{
		public bool IsLive => Record.State != IngestStateName.Deleted;
	}

	private static async Task<IReadOnlyList<RenderedRow>> RenderedRowsAsync(SqliteConnection connection, SqliteTransaction transaction, IngestClaimRequest request, CancellationToken cancellationToken)
	{
		if (request.RenderedLookup is null)
		{
			return [];
		}

		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = $"SELECT {Columns}, identity FROM ingest_state WHERE lookup = $lookup;";
		command.Parameters.AddWithValue("$lookup", request.RenderedLookup);
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		List<RenderedRow> rows = [];
		while (await reader.ReadAsync(cancellationToken))
		{
			rows.Add(new RenderedRow(reader.GetString(9), Map(reader)));
		}

		return request.LeavesRendered ? [.. rows.Where(static found => !found.IsLive)] : rows;
	}

	/// <summary>
	/// What the session is compared with once its rendered documents count as its own: their paths are its to replace, a copy older or shorter than them is refused as usual, one still
	/// being written holds it up, and a path someone deleted stays deleted. Only a document's own identity says which path a tombstone was for, as a delete clears its paths.
	/// </summary>
	private static IngestRecord? TakingOver(IngestRecord? row, IReadOnlyList<RenderedRow> rendered, IngestClaimRequest request)
	{
		if (rendered.Count == 0)
		{
			return row;
		}

		RenderedRow[] live = [.. rendered.Where(static found => found.IsLive)];
		string[] deleted = [.. request.Paths.Where(path => rendered.Any(found => !found.IsLive && found.Identity == IngestIdentity.For("document", request.Collection, path)))];
		IngestRecord merged = row ?? new IngestRecord(new DateTimeOffset(0, TimeSpan.Zero), IngestStateName.Partial, request.Now, []);
		merged = live.Aggregate(merged, static (soFar, found) => Merged(soFar, found.Record));
		return merged with { DeletedPaths = [.. (merged.DeletedPaths ?? []).Union(deleted, StringComparer.Ordinal)] };
	}

	private static IngestRecord Merged(IngestRecord soFar, IngestRecord record)
	{
		return soFar with
		{
			Version = record.Version > soFar.Version ? record.Version : soFar.Version,
			// A document that does not say how many it holds refuses a takeover rather than guess: allowShrink is how someone says the session really is the whole of it.
			MessageCount = (record.MessageCount ?? long.MaxValue) > (soFar.MessageCount ?? 0) ? record.MessageCount ?? long.MaxValue : soFar.MessageCount,
			Paths = [.. soFar.Paths.Union(record.Paths, StringComparer.Ordinal)],
			LeaseUntil = record.LeaseOwner is null ? soFar.LeaseUntil : record.LeaseUntil,
			LeaseOwner = record.LeaseOwner ?? soFar.LeaseOwner,
		};
	}

	/// <summary>The session owns these paths from here on, so its normal rules apply to them, and a rendered copy sent later is refused for the path it no longer owns.</summary>
	private static async Task TakeOverAsync(SqliteConnection connection, SqliteTransaction transaction, string identity, IReadOnlyList<RenderedRow> rendered)
	{
		foreach (RenderedRow found in rendered.Where(static found => found.IsLive))
		{
			await using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = """
				UPDATE ingest_paths SET owner = $owner WHERE owner = $rendered;
				UPDATE ingest_state SET lookup = NULL, state = $state, paths = '[]', lease_owner = NULL WHERE identity = $rendered;
				""";
			command.Parameters.AddWithValue("$owner", identity);
			command.Parameters.AddWithValue("$rendered", found.Identity);
			command.Parameters.AddWithValue("$state", IngestStateName.TakenOver);
			await command.ExecuteNonQueryAsync();
		}
	}

	/// <summary>A new row starts with no version and nothing stored, so an upload whose writes all fail leaves no mark on what later uploads are compared with.</summary>
	private static IngestRecord Claimed(IngestClaimRequest request, IngestRecord? row, string token)
	{
		IngestRecord before = row ?? new IngestRecord(new DateTimeOffset(0, TimeSpan.Zero), IngestStateName.Partial, request.Now, []);
		return before with { LeaseUntil = request.Now + Lease, LeaseOwner = token, DeletedPaths = request.Restore ? [] : before.DeletedPaths };
	}

	/// <summary>A version is recorded as at most the clock, so a date ahead of it cannot hold honest updates back until that date passes.</summary>
	private static IngestRecord Finished(IngestRecord row, IngestClaimRequest? landed, string state, IReadOnlyList<string> paths)
	{
		IngestRecord released = row with { LeaseUntil = new DateTimeOffset(0, TimeSpan.Zero), LeaseOwner = null, Paths = paths };
		if (landed is null)
		{
			return released;
		}

		return released with
		{
			Version = landed.Version < landed.Now ? landed.Version : landed.Now,
			State = state,
			MessageCount = landed.MessageCount ?? row.MessageCount,
			MessagesDigest = landed.MessageCount is null ? row.MessagesDigest : landed.MessagesDigest,
			ContentHash = landed.ContentHash ?? row.ContentHash,
		};
	}

	/// <summary>The lease comes first, so a replay during an upload is told to wait, and then told it is stale.</summary>
	private IngestClaimOutcome Decide(IngestRecord? row, IngestClaimRequest request)
	{
		if (row is null)
		{
			return IngestClaimOutcome.Acquired;
		}

		if (IsLeased(row, request.Now))
		{
			return IngestClaimOutcome.Busy;
		}

		// A session took this path over, so its text is the session's to write or delete, even once nothing owns the path because that session deleted or stopped writing it.
		if (row.State == IngestStateName.TakenOver && !request.Restore)
		{
			return IngestClaimOutcome.PathConflict;
		}

		return IsStale(row, request) ? IngestClaimOutcome.Stale : WouldLoseStoredText(row, request);
	}

	private static IngestClaimOutcome WouldLoseStoredText(IngestRecord row, IngestClaimRequest request)
	{
		if (request.AllowShrink)
		{
			return IngestClaimOutcome.Acquired;
		}

		if (request.MessageCount < row.MessageCount)
		{
			return IngestClaimOutcome.Shrunk;
		}

		bool sameStart = row.MessagesDigest is not { } stored || row.MessageCount is not { } count || request.StartsWithStored?.Invoke(count, stored) != false;
		return sameStart ? IngestClaimOutcome.Acquired : IngestClaimOutcome.LostContent;
	}

	/// <summary>After a delete only something newer than the delete can return, and never the same content, whatever date it now claims. An operator's restore overrides that.</summary>
	private static bool IsStale(IngestRecord row, IngestClaimRequest request)
	{
		if (row.State != IngestStateName.Deleted)
		{
			return request.Version < row.Version;
		}

		bool sameContent = row.ContentHash is { } stored && request.IsSameContent?.Invoke(stored) == true;
		return !request.Restore && (request.Version <= row.Version || sameContent);
	}

	private static IngestRecord AfterDelete(IngestRecord row, bool isDocument, string path, DateTimeOffset now)
	{
		IngestRecord released = row with { LeaseUntil = new DateTimeOffset(0, TimeSpan.Zero), LeaseOwner = null };
		return isDocument
			? released with { Version = now, State = IngestStateName.Deleted, Paths = [], MessageCount = null }
			: released with
			{
				State = IngestStateName.Partial,
				Paths = [.. row.Paths.Where(known => known != path)],
				DeletedPaths = [.. row.DeletedPaths ?? [], path],
			};
	}

	/// <summary>A lease is live only while it has time left and the process that took it is this one. Anything else died with its process.</summary>
	private bool IsLeased(IngestRecord row, DateTimeOffset now)
	{
		return row.LeaseUntil > now && row.LeaseOwner?.StartsWith(_instance, StringComparison.Ordinal) == true;
	}

	private static int SecondsLeft(IngestRecord row, DateTimeOffset now)
	{
		return Math.Max(1, (int)Math.Ceiling((row.LeaseUntil - now).TotalSeconds));
	}

	private static async Task<IngestClaimOutcome> CheckPathsAsync(SqliteConnection connection, SqliteTransaction transaction, IngestClaimRequest request, IReadOnlyList<RenderedRow> rendered, CancellationToken cancellationToken)
	{
		if (request.Stem is { } stem && await StemHeldByAnotherAsync(connection, transaction, request, stem, cancellationToken))
		{
			return IngestClaimOutcome.StemClash;
		}

		foreach (string path in request.Paths)
		{
			if (await OwnerAsync(connection, transaction, request.Collection, path, cancellationToken) is { } owner && owner != request.Identity && !rendered.Any(found => found.IsLive && found.Identity == owner))
			{
				return IngestClaimOutcome.PathConflict;
			}
		}

		return IngestClaimOutcome.Acquired;
	}

	/// <summary>A stem is only held while its owner still has files. Otherwise deleting a squatter's files would not be enough to free it.</summary>
	private static async Task<bool> StemHeldByAnotherAsync(SqliteConnection connection, SqliteTransaction transaction, IngestClaimRequest request, string stem, CancellationToken cancellationToken)
	{
		string? owner = await StemOwnerAsync(connection, transaction, request.Collection, stem, cancellationToken);
		if (owner is null || owner == request.Identity)
		{
			return false;
		}

		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "SELECT EXISTS (SELECT 1 FROM ingest_paths WHERE owner = $owner);";
		command.Parameters.AddWithValue("$owner", owner);
		return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0;
	}

	private static async Task TakeStemAsync(SqliteConnection connection, SqliteTransaction transaction, IngestClaimRequest request, CancellationToken cancellationToken)
	{
		if (request.Stem is not { } stem)
		{
			return;
		}

		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "INSERT OR REPLACE INTO ingest_stems (collection, stem, owner) VALUES ($collection, $stem, $owner);";
		command.Parameters.AddWithValue("$collection", request.Collection);
		command.Parameters.AddWithValue("$stem", stem);
		command.Parameters.AddWithValue("$owner", request.Identity);
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	private static async Task ReplaceOwnedPathsAsync(SqliteConnection connection, SqliteTransaction transaction, string identity, string collection, IReadOnlyList<string> paths)
	{
		await using (SqliteCommand clear = connection.CreateCommand())
		{
			clear.Transaction = transaction;
			clear.CommandText = "DELETE FROM ingest_paths WHERE owner = $owner;";
			clear.Parameters.AddWithValue("$owner", identity);
			await clear.ExecuteNonQueryAsync();
		}

		foreach (string path in paths)
		{
			await InsertOwnedAsync(connection, transaction, identity, collection, path, CancellationToken.None);
		}
	}

	private static async Task InsertOwnedAsync(SqliteConnection connection, SqliteTransaction transaction, string identity, string collection, string path, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "INSERT OR IGNORE INTO ingest_paths (collection, path, owner) VALUES ($collection, $path, $owner);";
		command.Parameters.AddWithValue("$collection", collection);
		command.Parameters.AddWithValue("$path", path);
		command.Parameters.AddWithValue("$owner", identity);
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	private static async Task<string?> OwnerAsync(SqliteConnection connection, SqliteTransaction? transaction, string collection, string path, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "SELECT owner FROM ingest_paths WHERE collection = $collection AND path = $path;";
		command.Parameters.AddWithValue("$collection", collection);
		command.Parameters.AddWithValue("$path", path);
		return (string?)await command.ExecuteScalarAsync(cancellationToken);
	}

	private static async Task<string?> StemOwnerAsync(SqliteConnection connection, SqliteTransaction transaction, string collection, string stem, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "SELECT owner FROM ingest_stems WHERE collection = $collection AND stem = $stem;";
		command.Parameters.AddWithValue("$collection", collection);
		command.Parameters.AddWithValue("$stem", stem);
		return (string?)await command.ExecuteScalarAsync(cancellationToken);
	}

	private static async Task<IngestRecord?> ReadAsync(SqliteConnection connection, SqliteTransaction? transaction, string identity, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = $"SELECT {Columns} FROM ingest_state WHERE identity = $identity;";
		command.Parameters.AddWithValue("$identity", identity);
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
	}

	private static IngestRecord Map(SqliteDataReader reader)
	{
		return new IngestRecord(
			new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
			reader.GetString(1),
			new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
			JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [],
			reader.IsDBNull(4) ? null : reader.GetString(4),
			reader.IsDBNull(5) ? null : reader.GetInt64(5),
			reader.IsDBNull(6) ? null : reader.GetString(6),
			reader.IsDBNull(7) ? null : reader.GetString(7),
			JsonSerializer.Deserialize<string[]>(reader.GetString(8)) ?? []);
	}

	private static async Task UpsertAsync(SqliteConnection connection, SqliteTransaction transaction, string identity, string? lookup, IngestRecord record, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO ingest_state (identity, lookup, version_ticks, state, lease_until_ticks, lease_owner, content_hash, message_count, paths, messages_digest, deleted_paths)
			VALUES ($identity, $lookup, $version, $state, $lease, $owner, $hash, $messages, $paths, $digest, $deletedPaths)
			ON CONFLICT(identity) DO UPDATE SET
				lookup = COALESCE($lookup, lookup), version_ticks = $version, state = $state, lease_until_ticks = $lease, lease_owner = $owner,
				content_hash = $hash, message_count = $messages, paths = $paths, messages_digest = $digest, deleted_paths = $deletedPaths;
			""";
		command.Parameters.AddWithValue("$identity", identity);
		command.Parameters.AddWithValue("$lookup", (object?)lookup ?? DBNull.Value);
		command.Parameters.AddWithValue("$version", record.Version.UtcTicks);
		command.Parameters.AddWithValue("$state", record.State);
		command.Parameters.AddWithValue("$lease", record.LeaseUntil.UtcTicks);
		command.Parameters.AddWithValue("$owner", (object?)record.LeaseOwner ?? DBNull.Value);
		command.Parameters.AddWithValue("$hash", (object?)record.ContentHash ?? DBNull.Value);
		command.Parameters.AddWithValue("$messages", (object?)record.MessageCount ?? DBNull.Value);
		command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(record.Paths));
		command.Parameters.AddWithValue("$digest", (object?)record.MessagesDigest ?? DBNull.Value);
		command.Parameters.AddWithValue("$deletedPaths", JsonSerializer.Serialize(record.DeletedPaths ?? []));
		await command.ExecuteNonQueryAsync(cancellationToken);
	}
}
