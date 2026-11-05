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
}

/// <summary>What is recorded about one document or session.</summary>
public sealed record IngestRecord(
	DateTimeOffset Version,
	string State,
	DateTimeOffset LeaseUntil,
	IReadOnlyList<string> Paths,
	string? LeaseOwner = null,
	DateTimeOffset? DeletedVersion = null,
	long? MessageCount = null);

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
	bool AllowShrink = false);

/// <summary>The answer to a claim. <see cref="PreviousPaths"/> is what earlier uploads wrote, which the index may not have seen yet.</summary>
public sealed record IngestClaim(IngestClaimOutcome Outcome, IReadOnlyList<string> PreviousPaths, string? LeaseToken = null, int? RetryAfterSeconds = null);

public enum IngestDeleteOutcome
{
	/// <summary>The ledger knew the path, and now records it gone.</summary>
	Recorded,

	/// <summary>The ledger has never heard of the path. Nothing was written, so a delete of nothing leaves nothing behind.</summary>
	NoRecord,

	/// <summary>An upload holds the path right now.</summary>
	Busy,
}

public sealed record IngestDeleteGate(IngestDeleteOutcome Outcome, int? RetryAfterSeconds = null);

/// <summary>
/// The durable record of what was ingested. It makes writes ordered and exclusive: a claim is one transaction
/// that compares versions, takes a lease and takes ownership of paths, so an old replay is refused, two uploads of one
/// session cannot interleave, and no upload can write a file that belongs to another. It also tells a finished session
/// from one that stopped halfway. The files are written one at a time, so nothing about them can say that.
/// </summary>
public interface IIngestLedger
{
	/// <summary>Takes the lease and the paths, or says why not. An equal version is a retry, and allowed.</summary>
	Task<IngestClaim> ClaimAsync(IngestClaimRequest request, CancellationToken cancellationToken);

	/// <summary>Keeps the lease through a long upload. False when it was lost, and the upload must stop writing.</summary>
	Task<bool> RenewAsync(string identity, string leaseToken, DateTimeOffset now, CancellationToken cancellationToken);

	/// <summary>Records how the upload ended and releases the lease. Does nothing when the lease has passed to someone else, which is what false means.</summary>
	Task<bool> FinishAsync(string identity, string leaseToken, string collection, string state, IReadOnlyList<string> paths);

	/// <summary>
	/// Records that a path is being deleted, before the file goes. A document becomes a tombstone, so an older copy cannot bring it back.
	/// A session's part leaves the session partial, as the session no longer has all of itself.
	/// </summary>
	Task<IngestDeleteGate> PrepareDeleteAsync(string collection, string path, string documentIdentity, DateTimeOffset now, CancellationToken cancellationToken);

	/// <summary>Tombstones a document the ledger did not know but a delete did remove, such as one from before there was a ledger.</summary>
	Task RecordDeletedAsync(string documentIdentity, DateTimeOffset now, CancellationToken cancellationToken);

	/// <summary>Who owns each of these paths. A path nobody has claimed is absent.</summary>
	Task<IReadOnlyDictionary<string, string>> OwnersAsync(string collection, IReadOnlyList<string> paths, CancellationToken cancellationToken);

	Task<IngestRecord?> FindAsync(string identity, CancellationToken cancellationToken);

	/// <summary>Every copy recorded under one session name. Copies relocated to another project folder are separate sessions that share it.</summary>
	Task<IReadOnlyList<IngestRecord>> FindByLookupAsync(string lookup, CancellationToken cancellationToken);

	/// <summary>The state to report: an upload whose lease has lapsed or whose process has died is not still running.</summary>
	string EffectiveState(IngestRecord record, DateTimeOffset now);
}

public static class IngestIdentity
{
	/// <summary>A digest, because session ids and source ids are caller text and may hold a credential that belongs nowhere in a table.</summary>
	public static string For(params string?[] parts)
	{
		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts))));
	}
}

/// <summary>The ledger in the control database. One API process owns that database, so a lease from any other process is a dead one.</summary>
public sealed class SqliteIngestLedger(ControlDatabase database) : IIngestLedger
{
	/// <summary>Long enough to outlast one part's sidecar call.</summary>
	public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

	private readonly string _instance = Guid.NewGuid().ToString("N");

	public async Task<IngestClaim> ClaimAsync(IngestClaimRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
		IngestRecord? row = await ReadAsync(connection, transaction, request.Identity, cancellationToken);
		IReadOnlyList<string> previous = row?.Paths ?? [];
		IngestClaimOutcome outcome = Decide(row, request);
		if (outcome != IngestClaimOutcome.Acquired)
		{
			return new IngestClaim(outcome, previous, null, outcome == IngestClaimOutcome.Busy ? SecondsLeft(row!, request.Now) : null);
		}

		outcome = await CheckOwnershipAsync(connection, transaction, request, cancellationToken);
		if (outcome != IngestClaimOutcome.Acquired)
		{
			return new IngestClaim(outcome, previous);
		}

		string token = $"{_instance}:{Guid.NewGuid():N}";
		IngestRecord claimed = new(request.Version, IngestStateName.Writing, request.Now + Lease, previous, token, row?.DeletedVersion, request.MessageCount ?? row?.MessageCount);
		await UpsertAsync(connection, transaction, request.Identity, request.Lookup, claimed, cancellationToken);
		await TakeOwnershipAsync(connection, transaction, request, cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		return new IngestClaim(IngestClaimOutcome.Acquired, previous, token);
	}

	public async Task<bool> RenewAsync(string identity, string leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "UPDATE ingest_state SET lease_until_ticks = $lease WHERE identity = $identity AND lease_owner = $owner AND state = $writing;";
		command.Parameters.AddWithValue("$identity", identity);
		command.Parameters.AddWithValue("$owner", leaseToken);
		command.Parameters.AddWithValue("$writing", IngestStateName.Writing);
		command.Parameters.AddWithValue("$lease", (now + Lease).UtcTicks);
		return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
	}

	public async Task<bool> FinishAsync(string identity, string leaseToken, string collection, string state, IReadOnlyList<string> paths)
	{
		await using SqliteConnection connection = await database.OpenAsync();
		await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "UPDATE ingest_state SET state = $state, lease_until_ticks = 0, lease_owner = NULL, paths = $paths WHERE identity = $identity AND lease_owner = $owner;";
		command.Parameters.AddWithValue("$identity", identity);
		command.Parameters.AddWithValue("$owner", leaseToken);
		command.Parameters.AddWithValue("$state", state);
		command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(paths));
		if (await command.ExecuteNonQueryAsync() == 0)
		{
			return false;
		}

		await ReplaceOwnedPathsAsync(connection, transaction, identity, collection, paths);
		await transaction.CommitAsync();
		return true;
	}

	public async Task<IngestDeleteGate> PrepareDeleteAsync(string collection, string path, string documentIdentity, DateTimeOffset now, CancellationToken cancellationToken)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
		string identity = await OwnerAsync(connection, transaction, collection, path, cancellationToken) ?? documentIdentity;
		IngestRecord? row = await ReadAsync(connection, transaction, identity, cancellationToken);
		if (row is null)
		{
			return new IngestDeleteGate(IngestDeleteOutcome.NoRecord);
		}

		if (IsLeased(row, now))
		{
			return new IngestDeleteGate(IngestDeleteOutcome.Busy, SecondsLeft(row, now));
		}

		await UpsertAsync(connection, transaction, identity, null, AfterDelete(row, identity == documentIdentity, path, now), cancellationToken);
		await ReleasePathAsync(connection, transaction, collection, path, cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		return new IngestDeleteGate(IngestDeleteOutcome.Recorded);
	}

	public async Task RecordDeletedAsync(string documentIdentity, DateTimeOffset now, CancellationToken cancellationToken)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
		await UpsertAsync(connection, transaction, documentIdentity, null, new IngestRecord(now, IngestStateName.Deleted, DateTimeOffset.MinValue, []), cancellationToken);
		await transaction.CommitAsync(cancellationToken);
	}

	public async Task<IReadOnlyDictionary<string, string>> OwnersAsync(string collection, IReadOnlyList<string> paths, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(paths);
		Dictionary<string, string> owners = new(StringComparer.Ordinal);
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		foreach (string path in paths)
		{
			if (await OwnerAsync(connection, null, collection, path, cancellationToken) is { } owner)
			{
				owners[path] = owner;
			}
		}

		return owners;
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
		return record.State == IngestStateName.Writing && !IsLeased(record, now) ? IngestStateName.Partial : record.State;
	}

	private const string Columns = "version_ticks, state, lease_until_ticks, paths, lease_owner, deleted_version_ticks, message_count";

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

		if (IsStale(row, request.Version))
		{
			return IngestClaimOutcome.Stale;
		}

		return !request.AllowShrink && request.MessageCount < row.MessageCount ? IngestClaimOutcome.Shrunk : IngestClaimOutcome.Acquired;
	}

	/// <summary>After a delete only something newer than the delete can return, and never the very copy that was deleted, whatever date it claims.</summary>
	private static bool IsStale(IngestRecord row, DateTimeOffset version)
	{
		return row.State == IngestStateName.Deleted
			? version <= row.Version || version == row.DeletedVersion
			: version < row.Version;
	}

	private static IngestRecord AfterDelete(IngestRecord row, bool isDocument, string path, DateTimeOffset now)
	{
		return isDocument
			? new IngestRecord(now, IngestStateName.Deleted, DateTimeOffset.MinValue, [], null, row.State == IngestStateName.Deleted ? row.DeletedVersion : row.Version)
			: row with { State = IngestStateName.Partial, LeaseUntil = DateTimeOffset.MinValue, LeaseOwner = null, Paths = [.. row.Paths.Where(known => known != path)] };
	}

	/// <summary>A lease is live only while it has time left and the process that took it is this one. Anything else died with its process.</summary>
	private bool IsLeased(IngestRecord row, DateTimeOffset now)
	{
		return row.State == IngestStateName.Writing && row.LeaseUntil > now && row.LeaseOwner?.StartsWith(_instance, StringComparison.Ordinal) == true;
	}

	private static int SecondsLeft(IngestRecord row, DateTimeOffset now)
	{
		return Math.Max(1, (int)Math.Ceiling((row.LeaseUntil - now).TotalSeconds));
	}

	private static async Task<IngestClaimOutcome> CheckOwnershipAsync(SqliteConnection connection, SqliteTransaction transaction, IngestClaimRequest request, CancellationToken cancellationToken)
	{
		if (request.Stem is { } stem && await StemOwnerAsync(connection, transaction, request.Collection, stem, cancellationToken) is { } stemOwner && stemOwner != request.Identity)
		{
			return IngestClaimOutcome.StemClash;
		}

		foreach (string path in request.Paths)
		{
			if (await OwnerAsync(connection, transaction, request.Collection, path, cancellationToken) is { } owner && owner != request.Identity)
			{
				return IngestClaimOutcome.PathConflict;
			}
		}

		return IngestClaimOutcome.Acquired;
	}

	private static async Task TakeOwnershipAsync(SqliteConnection connection, SqliteTransaction transaction, IngestClaimRequest request, CancellationToken cancellationToken)
	{
		foreach (string path in request.Paths)
		{
			await InsertOwnedAsync(connection, transaction, request.Identity, request.Collection, path, cancellationToken);
		}

		if (request.Stem is { } stem)
		{
			await using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "INSERT OR IGNORE INTO ingest_stems (collection, stem, owner) VALUES ($collection, $stem, $owner);";
			command.Parameters.AddWithValue("$collection", request.Collection);
			command.Parameters.AddWithValue("$stem", stem);
			command.Parameters.AddWithValue("$owner", request.Identity);
			await command.ExecuteNonQueryAsync(cancellationToken);
		}
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

	private static async Task ReleasePathAsync(SqliteConnection connection, SqliteTransaction transaction, string collection, string path, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "DELETE FROM ingest_paths WHERE collection = $collection AND path = $path;";
		command.Parameters.AddWithValue("$collection", collection);
		command.Parameters.AddWithValue("$path", path);
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
			reader.IsDBNull(5) ? null : new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
			reader.IsDBNull(6) ? null : reader.GetInt64(6));
	}

	private static async Task UpsertAsync(SqliteConnection connection, SqliteTransaction transaction, string identity, string? lookup, IngestRecord record, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO ingest_state (identity, lookup, version_ticks, state, lease_until_ticks, lease_owner, deleted_version_ticks, message_count, paths)
			VALUES ($identity, $lookup, $version, $state, $lease, $owner, $deleted, $messages, $paths)
			ON CONFLICT(identity) DO UPDATE SET
				lookup = COALESCE($lookup, lookup), version_ticks = $version, state = $state, lease_until_ticks = $lease, lease_owner = $owner,
				deleted_version_ticks = $deleted, message_count = $messages, paths = $paths;
			""";
		command.Parameters.AddWithValue("$identity", identity);
		command.Parameters.AddWithValue("$lookup", (object?)lookup ?? DBNull.Value);
		command.Parameters.AddWithValue("$version", record.Version.UtcTicks);
		command.Parameters.AddWithValue("$state", record.State);
		command.Parameters.AddWithValue("$lease", record.LeaseUntil.UtcTicks);
		command.Parameters.AddWithValue("$owner", (object?)record.LeaseOwner ?? DBNull.Value);
		command.Parameters.AddWithValue("$deleted", (object?)record.DeletedVersion?.UtcTicks ?? DBNull.Value);
		command.Parameters.AddWithValue("$messages", (object?)record.MessageCount ?? DBNull.Value);
		command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(record.Paths));
		await command.ExecuteNonQueryAsync(cancellationToken);
	}
}
