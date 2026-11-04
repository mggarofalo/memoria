using Memoria.Api.Auth;
using Memoria.Api.Http;
using Microsoft.Data.Sqlite;

namespace Memoria.Api.Tests;

public sealed class ApiKeyTokenTests
{
	[Fact]
	public void NewToken_RoundTripsItsId_AndHasTheDocumentedShape()
	{
		string id = ApiKeyToken.NewId();
		string token = ApiKeyToken.NewToken(id);

		Assert.Matches("^mem_[0-9a-f]{16}_[0-9a-f]{64}$", token);
		Assert.True(ApiKeyToken.TryParseId(token, out string? parsed));
		Assert.Equal(id, parsed);
	}

	[Fact]
	public void NewToken_NeverRepeatsASecret()
	{
		string id = ApiKeyToken.NewId();

		Assert.NotEqual(ApiKeyToken.NewToken(id), ApiKeyToken.NewToken(id));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("mem")]
	[InlineData("mem__")]
	[InlineData("key_0123456789abcdef_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
	[InlineData("mem_0123456789ABCDEF_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
	[InlineData("mem_0123456789abcdef_0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
	[InlineData("mem_0123456789abcde_00123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
	[InlineData("mem_0123456789abcdef_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
	[InlineData("mem_0123456789abcdef_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
	[InlineData("mem_012345678\n9abcdef_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
	[InlineData("mem_0123456789abcdef_0123456789abcdef_123456789abcdef0123456789abcdef0123456789abcdef")]
	public void TryParseId_RefusesAnythingElse(string? token)
	{
		Assert.False(ApiKeyToken.TryParseId(token, out _));
	}

	[Fact]
	public void Hash_IsSha256OfTheWholeToken_AndComparesByValue()
	{
		byte[] hash = ApiKeyToken.Hash("mem_a_b");

		Assert.Equal(32, hash.Length);
		Assert.True(ApiKeyToken.HashesMatch(hash, ApiKeyToken.Hash("mem_a_b")));
		Assert.False(ApiKeyToken.HashesMatch(hash, ApiKeyToken.Hash("mem_a_c")));
	}
}

public sealed class LastUsedThrottleTests
{
	[Fact]
	public void WritesOncePerInterval_PerKey()
	{
		LastUsedThrottle throttle = new();
		DateTimeOffset now = DateTimeOffset.UtcNow;

		Assert.True(throttle.ShouldWrite("a", now));
		Assert.False(throttle.ShouldWrite("a", now.AddSeconds(1)));
		Assert.True(throttle.ShouldWrite("b", now.AddSeconds(1)));
		Assert.True(throttle.ShouldWrite("a", now + LastUsedThrottle.Interval));
	}
}

public sealed class SqliteApiKeyStoreTests : IAsyncLifetime
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "memoria-tests", Guid.NewGuid().ToString("N"));

	private ControlDatabase _database = null!;

	private SqliteApiKeyStore _store = null!;

	public async Task InitializeAsync()
	{
		_database = new ControlDatabase(Path.Combine(_directory, "nested", "control.db"));
		_store = new SqliteApiKeyStore(_database);
		await _store.InitializeAsync();
	}

	public Task DisposeAsync()
	{
		SqliteConnection.ClearAllPools();
		Directory.Delete(_directory, recursive: true);
		return Task.CompletedTask;
	}

	[Fact]
	public async Task Initialize_CreatesParentDirectory_AndIsRepeatable()
	{
		await _store.InitializeAsync();

		Assert.True(File.Exists(Path.Combine(_directory, "nested", "control.db")));
	}

	[Fact]
	public async Task Initialize_RefusesASchemaNewerThanThisBuild()
	{
		await using (SqliteConnection connection = await _database.OpenAsync())
		await using (SqliteCommand command = connection.CreateCommand())
		{
			command.CommandText = "PRAGMA user_version = 999;";
			await command.ExecuteNonQueryAsync();
		}

		await Assert.ThrowsAsync<InvalidOperationException>(() => _store.InitializeAsync());
	}

	[Fact]
	public async Task InsertAndFind_RoundTripEveryField()
	{
		DateTimeOffset created = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
		DateTimeOffset expires = created.AddDays(30);
		(ApiKeyRecord record, _) = ApiKeyRecord.Issue("laptop", [MemoriaScopes.Read, MemoriaScopes.Ingest], ["a", "B"], false, expires, created);

		await _store.InsertAsync(record);
		ApiKeyRecord found = (await _store.FindAsync(record.Id))!;

		Assert.Equal(record.Id, found.Id);
		Assert.Equal(record.Sha256Hash, found.Sha256Hash);
		Assert.Equal("laptop", found.Label);
		Assert.Equal(["read", "ingest"], found.Scopes);
		Assert.Equal(["a", "B"], found.Collections);
		Assert.False(found.AllCollections);
		Assert.Equal(created, found.CreatedAt);
		Assert.Equal(expires, found.ExpiresAt);
		Assert.Null(found.RevokedAt);
		Assert.Null(found.LastUsedAt);
		Assert.Null(await _store.FindAsync("no-such-key"));
	}

	[Fact]
	public async Task Revoke_KeepsTheFirstTimestamp_AndReportsUnknownIds()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		(ApiKeyRecord record, _) = ApiKeyRecord.Issue("x", [MemoriaScopes.Read], ["a"], false, null, now);
		await _store.InsertAsync(record);

		DateTimeOffset first = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		Assert.True(await _store.RevokeAsync(record.Id, first));
		Assert.True(await _store.RevokeAsync(record.Id, first.AddDays(1)));
		Assert.False(await _store.RevokeAsync("no-such-key", first));

		Assert.Equal(first, (await _store.FindAsync(record.Id))!.RevokedAt);
	}

	[Fact]
	public async Task ReplaceHash_RefusesARevokedKey()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		(ApiKeyRecord record, _) = ApiKeyRecord.Issue("x", [MemoriaScopes.Read], ["a"], false, null, now);
		await _store.InsertAsync(record);
		byte[] replacement = ApiKeyToken.Hash("replacement");

		Assert.True(await _store.ReplaceHashAsync(record.Id, replacement));
		Assert.Equal(replacement, (await _store.FindAsync(record.Id))!.Sha256Hash);

		await _store.RevokeAsync(record.Id, now);
		Assert.False(await _store.ReplaceHashAsync(record.Id, ApiKeyToken.Hash("again")));
	}

	[Fact]
	public async Task HasActiveAdmin_IgnoresRevokedExpiredAndNonAdminKeys()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		Assert.False(await _store.HasActiveAdminAsync(now));

		await _store.InsertAsync(ApiKeyRecord.Issue("reader", [MemoriaScopes.Read], ["a"], false, null, now).Record);
		await _store.InsertAsync(ApiKeyRecord.Issue("expired", [MemoriaScopes.Admin], [], false, now.AddSeconds(-1), now.AddDays(-1)).Record);
		ApiKeyRecord revoked = ApiKeyRecord.Issue("revoked", [MemoriaScopes.Admin], [], false, null, now).Record;
		await _store.InsertAsync(revoked);
		await _store.RevokeAsync(revoked.Id, now);
		Assert.False(await _store.HasActiveAdminAsync(now));

		await _store.InsertAsync(ApiKeyRecord.Issue("live", [MemoriaScopes.Admin], [], false, null, now).Record);
		Assert.True(await _store.HasActiveAdminAsync(now));
	}

	[Fact]
	public async Task TouchLastUsed_PersistsAcrossConnections()
	{
		DateTimeOffset now = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
		(ApiKeyRecord record, _) = ApiKeyRecord.Issue("x", [MemoriaScopes.Read], ["a"], false, null, now);
		await _store.InsertAsync(record);

		await _store.TouchLastUsedAsync(record.Id, now);

		Assert.Equal(now, (await new SqliteApiKeyStore(_database).FindAsync(record.Id))!.LastUsedAt);
	}
}
