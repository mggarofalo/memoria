using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Memoria.Api.Auth;

/// <summary>
/// The control database: durable API state in one SQLite file, separate from
/// QMD's derived index. Deleting the index loses nothing; deleting this file
/// revokes every key and loses the audit trail, so it lives on a volume.
/// </summary>
public sealed class ControlDatabase(string? path)
{
	private const int SchemaVersion = 3;

	private const string SchemaV3 = """
		ALTER TABLE ingest_state ADD COLUMN content_hash TEXT NULL;
		ALTER TABLE ingest_state ADD COLUMN messages_digest TEXT NULL;
		ALTER TABLE ingest_state ADD COLUMN deleted_paths TEXT NOT NULL DEFAULT '[]';
		ALTER TABLE ingest_state DROP COLUMN deleted_version_ticks;
		CREATE TABLE ingest_collections (
			collection TEXT PRIMARY KEY NOT NULL,
			opened_ticks INTEGER NOT NULL
		);
		""";

	private const string SchemaV2 = """
		CREATE TABLE ingest_state (
			identity TEXT PRIMARY KEY NOT NULL,
			lookup TEXT NULL,
			version_ticks INTEGER NOT NULL,
			state TEXT NOT NULL,
			lease_until_ticks INTEGER NOT NULL,
			lease_owner TEXT NULL,
			deleted_version_ticks INTEGER NULL,
			message_count INTEGER NULL,
			paths TEXT NOT NULL
		);
		CREATE INDEX ingest_state_lookup ON ingest_state (lookup);
		CREATE TABLE ingest_paths (
			collection TEXT NOT NULL,
			path TEXT NOT NULL,
			owner TEXT NOT NULL,
			PRIMARY KEY (collection, path)
		);
		CREATE INDEX ingest_paths_owner ON ingest_paths (owner);
		CREATE TABLE ingest_stems (
			collection TEXT NOT NULL,
			stem TEXT NOT NULL,
			owner TEXT NOT NULL,
			PRIMARY KEY (collection, stem)
		);
		""";

	private const string SchemaV1 = """
		CREATE TABLE api_keys (
			id TEXT PRIMARY KEY NOT NULL,
			sha256_hash BLOB NOT NULL,
			label TEXT NOT NULL,
			scopes TEXT NOT NULL,
			collections TEXT NOT NULL,
			all_collections INTEGER NOT NULL,
			created_at TEXT NOT NULL,
			expires_at TEXT NULL,
			revoked_at TEXT NULL,
			last_used_at TEXT NULL
		);
		CREATE TABLE audit_log (
			id INTEGER PRIMARY KEY AUTOINCREMENT,
			audit_id TEXT NOT NULL UNIQUE,
			timestamp TEXT NOT NULL,
			key_id TEXT NOT NULL,
			operation TEXT NOT NULL,
			collections TEXT NULL,
			attempted_collections TEXT NULL,
			result_count INTEGER NULL,
			status INTEGER NOT NULL,
			trace_id TEXT NOT NULL,
			target TEXT NULL,
			query TEXT NULL
		);
		CREATE INDEX audit_log_timestamp ON audit_log (timestamp);
		CREATE INDEX audit_log_key ON audit_log (key_id, timestamp);
		""";

	private readonly string _connectionString = new SqliteConnectionStringBuilder
	{
		DataSource = path ?? string.Empty,
		Mode = SqliteOpenMode.ReadWriteCreate,
	}.ToString();

	/// <summary>Creates the file and applies the schema. Fails loudly on an unwritable path or a newer schema than this build knows.</summary>
	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		EnsureConfigured();
		string fullPath = Path.GetFullPath(path!);
		if (Path.GetDirectoryName(fullPath) is { Length: > 0 } directory)
		{
			Directory.CreateDirectory(directory);
		}

		await using SqliteConnection connection = await OpenAsync(cancellationToken);
		await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);

		await using SqliteCommand version = connection.CreateCommand();
		version.CommandText = "PRAGMA user_version;";
		int current = Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
		if (current > SchemaVersion)
		{
			throw new InvalidOperationException($"The control database at {fullPath} has schema version {current}; this build understands up to {SchemaVersion}.");
		}

		if (current < SchemaVersion)
		{
			await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
			string migration = string.Concat(current < 1 ? SchemaV1 : "", current < 2 ? SchemaV2 : "", SchemaV3);
			await ExecuteAsync(connection, migration, cancellationToken, transaction);
			await ExecuteAsync(connection, $"PRAGMA user_version = {SchemaVersion};", cancellationToken, transaction);
			await transaction.CommitAsync(cancellationToken);
		}

		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
	}

	/// <summary>Opens a connection. Commits are durable across a process crash; WAL with NORMAL keeps them cheap.</summary>
	public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
	{
		EnsureConfigured();
		SqliteConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken);
		await ExecuteAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken);
		return connection;
	}

	/// <summary>The one timestamp format the database holds; it sorts lexically, which audit pruning relies on.</summary>
	public static string FormatTimestamp(DateTimeOffset value)
	{
		return value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
	}

	public static DateTimeOffset ParseTimestamp(string value)
	{
		return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
	}

	private void EnsureConfigured()
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new InvalidOperationException("Memoria:Auth:DatabasePath is not set.");
		}
	}

	private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;
		command.Transaction = transaction;
		await command.ExecuteNonQueryAsync(cancellationToken);
	}
}
