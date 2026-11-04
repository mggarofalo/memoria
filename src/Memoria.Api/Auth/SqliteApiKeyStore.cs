using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Memoria.Api.Auth;

/// <summary>API keys in the control database. Every lookup hits the file, so a revocation takes effect on the next request.</summary>
public sealed class SqliteApiKeyStore(ControlDatabase database) : IApiKeyStore
{
	private const string Columns = "id, sha256_hash, label, scopes, collections, all_collections, created_at, expires_at, revoked_at, last_used_at";

	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		return database.InitializeAsync(cancellationToken);
	}

	public async Task InsertAsync(ApiKeyRecord record, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(record);
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = $"""
			INSERT INTO api_keys ({Columns})
			VALUES ($id, $hash, $label, $scopes, $collections, $all, $created, $expires, $revoked, $used);
			""";
		command.Parameters.AddWithValue("$id", record.Id);
		command.Parameters.AddWithValue("$hash", record.Sha256Hash);
		command.Parameters.AddWithValue("$label", record.Label);
		command.Parameters.AddWithValue("$scopes", JsonSerializer.Serialize(record.Scopes));
		command.Parameters.AddWithValue("$collections", JsonSerializer.Serialize(record.Collections));
		command.Parameters.AddWithValue("$all", record.AllCollections ? 1 : 0);
		command.Parameters.AddWithValue("$created", ControlDatabase.FormatTimestamp(record.CreatedAt));
		command.Parameters.AddWithValue("$expires", ToDbValue(record.ExpiresAt));
		command.Parameters.AddWithValue("$revoked", ToDbValue(record.RevokedAt));
		command.Parameters.AddWithValue("$used", ToDbValue(record.LastUsedAt));
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	public async Task<ApiKeyRecord?> FindAsync(string id, CancellationToken cancellationToken = default)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = $"SELECT {Columns} FROM api_keys WHERE id = $id;";
		command.Parameters.AddWithValue("$id", id);
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
	}

	public async Task<IReadOnlyList<ApiKeyRecord>> ListAsync(CancellationToken cancellationToken = default)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = $"SELECT {Columns} FROM api_keys ORDER BY created_at, id;";
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		List<ApiKeyRecord> records = [];
		while (await reader.ReadAsync(cancellationToken))
		{
			records.Add(Read(reader));
		}

		return records;
	}

	public async Task<bool> RevokeAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "UPDATE api_keys SET revoked_at = COALESCE(revoked_at, $at) WHERE id = $id;";
		command.Parameters.AddWithValue("$id", id);
		command.Parameters.AddWithValue("$at", ControlDatabase.FormatTimestamp(at));
		return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
	}

	public async Task<bool> ReplaceHashAsync(string id, byte[] sha256Hash, CancellationToken cancellationToken = default)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "UPDATE api_keys SET sha256_hash = $hash WHERE id = $id AND revoked_at IS NULL;";
		command.Parameters.AddWithValue("$id", id);
		command.Parameters.AddWithValue("$hash", sha256Hash);
		return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
	}

	public async Task TouchLastUsedAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default)
	{
		await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "UPDATE api_keys SET last_used_at = $at WHERE id = $id;";
		command.Parameters.AddWithValue("$id", id);
		command.Parameters.AddWithValue("$at", ControlDatabase.FormatTimestamp(at));
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	public async Task<bool> HasActiveAdminAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
	{
		IReadOnlyList<ApiKeyRecord> records = await ListAsync(cancellationToken);
		return records.Any(record => record.IsActiveAdmin(now));
	}

	private static object ToDbValue(DateTimeOffset? value)
	{
		return value is { } timestamp ? ControlDatabase.FormatTimestamp(timestamp) : DBNull.Value;
	}

	private static DateTimeOffset? ReadTimestamp(SqliteDataReader reader, int ordinal)
	{
		return reader.IsDBNull(ordinal) ? null : ControlDatabase.ParseTimestamp(reader.GetString(ordinal));
	}

	private static string[] ReadList(SqliteDataReader reader, int ordinal)
	{
		return JsonSerializer.Deserialize<string[]>(reader.GetString(ordinal)) ?? [];
	}

	private static ApiKeyRecord Read(SqliteDataReader reader)
	{
		return new ApiKeyRecord(
			reader.GetString(0),
			(byte[])reader.GetValue(1),
			reader.GetString(2),
			ReadList(reader, 3),
			ReadList(reader, 4),
			reader.GetInt64(5) == 1,
			ControlDatabase.ParseTimestamp(reader.GetString(6)),
			ReadTimestamp(reader, 7),
			ReadTimestamp(reader, 8),
			ReadTimestamp(reader, 9));
	}
}
