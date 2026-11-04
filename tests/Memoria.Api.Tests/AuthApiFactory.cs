using System.Net.Http.Headers;
using Memoria.Api.Auth;
using Memoria.Api.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Memoria.Api.Tests;

/// <summary>Boots the API with API keys enforced, on a throwaway control database.</summary>
public sealed class AuthApiFactory : MemoriaApiFactory
{
	public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), "memoria-tests", Guid.NewGuid().ToString("N"), "control.db");

	protected override void ConfigureAuth(IWebHostBuilder builder)
	{
		builder.UseSetting("Memoria:Auth:Mode", "ApiKey");
		builder.UseSetting("Memoria:Auth:DatabasePath", DatabasePath);
		builder.UseSetting("Memoria:Auth:RateLimit:PermitLimit", "100000");
		builder.UseSetting("Memoria:Auth:FailedAuth:PermitLimit", "100000");
	}

	public IApiKeyStore Store => Services.GetRequiredService<IApiKeyStore>();

	/// <summary>Issues a key directly through the store, bypassing the admin endpoint's validation.</summary>
	public async Task<(ApiKeyRecord Record, string Token)> IssueAsync(
		string[] scopes,
		string[]? collections = null,
		bool allCollections = false,
		DateTimeOffset? expiresAt = null,
		string label = "test key")
	{
		(ApiKeyRecord record, string token) = ApiKeyRecord.Issue(label, scopes, collections ?? [], allCollections, expiresAt, DateTimeOffset.UtcNow);
		await Store.InsertAsync(record);
		return (record, token);
	}

	public async Task<HttpClient> ClientWithScopesAsync(string[] scopes, string[]? collections = null, bool allCollections = false)
	{
		(_, string token) = await IssueAsync(scopes, collections, allCollections);
		return Authorized(CreateClient(), token);
	}

	public Task<HttpClient> ReaderOfAsync(params string[] collections)
	{
		return ClientWithScopesAsync([MemoriaScopes.Read], collections);
	}

	public Task<HttpClient> AdminAsync()
	{
		return ClientWithScopesAsync([MemoriaScopes.Admin]);
	}

	public static HttpClient Authorized(HttpClient client, string token)
	{
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
		return client;
	}

	/// <summary>
	/// Waits for rows to appear. MCP streams its response, so a client can hold the
	/// result before the middleware's audit write has finished.
	/// </summary>
	public async Task<List<AuditRow>> WaitForAuditAsync(Func<List<AuditRow>, bool> ready)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(10);
		List<AuditRow> rows = await ReadAuditAsync();
		while (!ready(rows) && DateTime.UtcNow < deadline)
		{
			await Task.Delay(10);
			rows = await ReadAuditAsync();
		}

		return rows;
	}

	public async Task<List<AuditRow>> ReadAuditAsync()
	{
		await using SqliteConnection connection = await Services.GetRequiredService<ControlDatabase>().OpenAsync();
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT timestamp, key_id, operation, collections, result_count, status, trace_id, query, audit_id, attempted_collections, target FROM audit_log ORDER BY id;";
		await using SqliteDataReader reader = await command.ExecuteReaderAsync();
		List<AuditRow> rows = [];
		while (await reader.ReadAsync())
		{
			rows.Add(new AuditRow(
				reader.GetString(0),
				reader.GetString(1),
				reader.GetString(2),
				reader.IsDBNull(3) ? null : reader.GetString(3),
				reader.IsDBNull(4) ? null : reader.GetInt32(4),
				reader.GetInt32(5),
				reader.GetString(6),
				reader.IsDBNull(7) ? null : reader.GetString(7),
				reader.GetString(8),
				reader.IsDBNull(9) ? null : reader.GetString(9),
				reader.IsDBNull(10) ? null : reader.GetString(10)));
		}

		return rows;
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
		SqliteConnection.ClearAllPools();
		string? directory = Path.GetDirectoryName(DatabasePath);
		if (directory is not null && Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	public sealed record AuditRow(string Timestamp, string KeyId, string Operation, string? Collections, int? ResultCount, int Status, string TraceId, string? Query, string AuditId, string? AttemptedCollections, string? Target);
}
