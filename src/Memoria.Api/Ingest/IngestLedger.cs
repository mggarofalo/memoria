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

	/// <summary>The recorded version is newer. Taking this one would replace new text with old.</summary>
	Stale,

	/// <summary>Another upload of the same thing is running.</summary>
	Busy,
}

/// <summary>What is recorded about one document or session.</summary>
public sealed record IngestRecord(DateTimeOffset Version, string State, DateTimeOffset LeaseUntil, IReadOnlyList<string> Paths);

/// <summary>The answer to a claim. <see cref="PreviousPaths"/> is what earlier uploads wrote, which the index may not have seen yet.</summary>
public sealed record IngestClaim(IngestClaimOutcome Outcome, IReadOnlyList<string> PreviousPaths);

/// <summary>
/// The durable record of what was ingested. It is what makes writes ordered and exclusive: a claim is one
/// transaction that compares versions and takes a lease, so an old replay is refused and two uploads of one
/// session cannot interleave. It also tells a finished session from one that stopped halfway. The files
/// themselves are written one at a time, so nothing about them can say that.
/// </summary>
public interface IIngestLedger
{
	/// <summary>Takes the lease unless the recorded version is newer, or another upload holds it. An equal version is a retry, and allowed.</summary>
	Task<IngestClaim> ClaimAsync(string identity, DateTimeOffset version, DateTimeOffset now, CancellationToken cancellationToken);

	/// <summary>Keeps the lease through a long upload.</summary>
	Task RenewAsync(string identity, DateTimeOffset now, CancellationToken cancellationToken);

	/// <summary>Records how the upload ended and releases the lease.</summary>
	Task FinishAsync(string identity, string state, IReadOnlyList<string> paths);

	/// <summary>Marks a thing deleted, so an older copy cannot bring it back. False when an upload holds the lease.</summary>
	Task<bool> TombstoneAsync(string identity, DateTimeOffset now, CancellationToken cancellationToken);

	Task<IngestRecord?> FindAsync(string identity, CancellationToken cancellationToken);
}

public static class IngestIdentity
{
	/// <summary>A digest, because session ids and source ids are caller text and may hold a credential that belongs nowhere in a table.</summary>
	public static string For(params string?[] parts)
	{
		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts))));
	}
}

/// <summary>The ledger in the control database.</summary>
public sealed class SqliteIngestLedger(ControlDatabase database) : IIngestLedger
{
	/// <summary>Long enough to outlast one part's sidecar call. A crash leaves the lease to run out, and a retry then finds the upload <c>partial</c>.</summary>
	public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

	public async Task<IngestClaim> ClaimAsync(string identity, DateTimeOffset version, DateTimeOffset now, CancellationToken cancellationToken)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
		IngestRecord? row = await ReadAsync(connection, transaction, identity, cancellationToken);
		IngestClaimOutcome outcome = Decide(row, version, now);
		if (outcome == IngestClaimOutcome.Acquired)
		{
			await UpsertAsync(connection, transaction, identity, new IngestRecord(version, IngestStateName.Writing, now + Lease, row?.Paths ?? []), cancellationToken);
			await transaction.CommitAsync(cancellationToken);
		}

		return new IngestClaim(outcome, row?.Paths ?? []);
	}

	public async Task RenewAsync(string identity, DateTimeOffset now, CancellationToken cancellationToken)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "UPDATE ingest_state SET lease_until_ticks = $lease WHERE identity = $identity;";
		command.Parameters.AddWithValue("$identity", identity);
		command.Parameters.AddWithValue("$lease", (now + Lease).UtcTicks);
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	public async Task FinishAsync(string identity, string state, IReadOnlyList<string> paths)
	{
		await using SqliteConnection connection = await database.OpenAsync();
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "UPDATE ingest_state SET state = $state, lease_until_ticks = 0, paths = $paths WHERE identity = $identity;";
		command.Parameters.AddWithValue("$identity", identity);
		command.Parameters.AddWithValue("$state", state);
		command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(paths));
		await command.ExecuteNonQueryAsync();
	}

	public async Task<bool> TombstoneAsync(string identity, DateTimeOffset now, CancellationToken cancellationToken)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
		IngestRecord? row = await ReadAsync(connection, transaction, identity, cancellationToken);
		if (row is not null && IsLeased(row, now))
		{
			return false;
		}

		DateTimeOffset version = row is not null && row.Version > now ? row.Version : now;
		await UpsertAsync(connection, transaction, identity, new IngestRecord(version, IngestStateName.Deleted, DateTimeOffset.MinValue, []), cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		return true;
	}

	public async Task<IngestRecord?> FindAsync(string identity, CancellationToken cancellationToken)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		return await ReadAsync(connection, null, identity, cancellationToken);
	}

	/// <summary>The lease comes first, so a replay during an upload is told to wait, and then told it is stale.</summary>
	internal static IngestClaimOutcome Decide(IngestRecord? row, DateTimeOffset version, DateTimeOffset now)
	{
		if (row is null)
		{
			return IngestClaimOutcome.Acquired;
		}

		if (IsLeased(row, now))
		{
			return IngestClaimOutcome.Busy;
		}

		return version < row.Version ? IngestClaimOutcome.Stale : IngestClaimOutcome.Acquired;
	}

	private static bool IsLeased(IngestRecord row, DateTimeOffset now)
	{
		return row.State == IngestStateName.Writing && row.LeaseUntil > now;
	}

	private static async Task<IngestRecord?> ReadAsync(SqliteConnection connection, SqliteTransaction? transaction, string identity, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "SELECT version_ticks, state, lease_until_ticks, paths FROM ingest_state WHERE identity = $identity;";
		command.Parameters.AddWithValue("$identity", identity);
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		if (!await reader.ReadAsync(cancellationToken))
		{
			return null;
		}

		return new IngestRecord(
			new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
			reader.GetString(1),
			new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
			JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? []);
	}

	private static async Task UpsertAsync(SqliteConnection connection, SqliteTransaction transaction, string identity, IngestRecord record, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO ingest_state (identity, version_ticks, state, lease_until_ticks, paths)
			VALUES ($identity, $version, $state, $lease, $paths)
			ON CONFLICT(identity) DO UPDATE SET version_ticks = $version, state = $state, lease_until_ticks = $lease, paths = $paths;
			""";
		command.Parameters.AddWithValue("$identity", identity);
		command.Parameters.AddWithValue("$version", record.Version.UtcTicks);
		command.Parameters.AddWithValue("$state", record.State);
		command.Parameters.AddWithValue("$lease", record.LeaseUntil.UtcTicks);
		command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(record.Paths));
		await command.ExecuteNonQueryAsync(cancellationToken);
	}
}
