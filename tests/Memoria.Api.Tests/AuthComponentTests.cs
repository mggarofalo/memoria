using System.Globalization;
using System.Net;
using Memoria.Api.Auth;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Core.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Tests;

/// <summary>
/// The auth building blocks on their own: their argument checks, their edges, and the behaviour that runs once per
/// host, which a test through the pipeline only sees on the first request.
/// </summary>
public sealed class AuthComponentTests
{
	private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void PublicEntryPoints_RefuseANullArgument_AsSuch()
	{
		Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddMemoriaAuth(null!));
		Assert.Throws<ArgumentNullException>(() => new FailedAuthLimiter(null!));
		Assert.Throws<ArgumentNullException>(() => new IngestRedactionPolicy(null!, new ConfigurationBuilder().Build()));
		Assert.Throws<ArgumentNullException>(() => Search.MultiGetPolicy.GlobViolation(null!));
	}

	[Fact]
	public async Task AsyncEntryPoints_RefuseANullArgument_AsSuch()
	{
		await Assert.ThrowsAsync<ArgumentNullException>(() => new KnownGoodKeys().LoadAsync(null!, Now));
		using TempDatabase temp = new();
		await Assert.ThrowsAsync<ArgumentNullException>(() => new SqliteApiKeyStore(temp.Database).InsertAsync(null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() => temp.AuditLog().WriteAsync(null!));
	}

	// --- keys ---------------------------------------------------------------------------------------------------

	[Fact]
	public void AKey_IsExpired_AtTheInstantItExpires()
	{
		(ApiKeyRecord record, _) = ApiKeyRecord.Issue("k", [MemoriaScopes.Read], [], true, Now, Now.AddDays(-1));

		Assert.False(record.IsExpired(Now.AddTicks(-1)));
		Assert.True(record.IsExpired(Now));
		Assert.False(record.IsActive(Now));
	}

	[Fact]
	public async Task AStoredCollectionListOfNull_ReadsAsNoCollections()
	{
		using TempDatabase temp = new();
		SqliteApiKeyStore store = new(temp.Database);
		await store.InitializeAsync();
		(ApiKeyRecord record, _) = ApiKeyRecord.Issue("k", [MemoriaScopes.Read], ["c"], false, null, Now);
		await store.InsertAsync(record);
		await temp.ExecuteAsync("UPDATE api_keys SET collections = 'null';");

		ApiKeyRecord read = (await store.FindAsync(record.Id))!;

		Assert.NotNull(read.Collections);
		Assert.Empty(read.Collections);
	}

	// --- scopes -------------------------------------------------------------------------------------------------

	[Fact]
	public void TheScopeAttributes_EachDemandTheirOwnRequirement()
	{
		Assert.IsType<MemoriaUnboundKeyRequirement>(Assert.Single(new MemoriaUnboundKeyAttribute().GetRequirements()));
		Assert.Equal(new MemoriaScopeRequirement(MemoriaScopes.Ingest), Assert.Single(new MemoriaScopeAttribute(MemoriaScopes.Ingest).GetRequirements()));
	}

	[Fact]
	public void ADocumentWithNoCollection_IsRefused_EvenToAnUnrestrictedCaller()
	{
		Assert.False(Unrestricted.Instance.Allows(null));
		Assert.True(Unrestricted.Instance.Allows("c"));
	}

	private sealed class Unrestricted : ICallerScope
	{
		public static readonly Unrestricted Instance = new();

		public IReadOnlyCollection<string>? Collections => null;

		public bool SeesBackendDetails => false;
	}

	// --- the failed-authentication budget -----------------------------------------------------------------------

	private static FailedAuthLimiter Limiter(int permits = 3, int maxTracked = 10_000, bool enabled = true)
	{
		return new FailedAuthLimiter(Options.Create(new MemoriaOptions
		{
			Auth = new AuthOptions { FailedAuth = new FailedAuthOptions { PermitLimit = permits, MaxTrackedAddresses = maxTracked, Enabled = enabled, Window = TimeSpan.FromMinutes(1) } },
		}));
	}

	[Fact]
	public void ADisabledBudget_NeverReportsACrossing()
	{
		FailedAuthLimiter limiter = Limiter(permits: 1, enabled: false);

		for (int i = 0; i < 3; i++)
		{
			FailedAuthDecision decision = limiter.TryReserve("a", Now);
			Assert.True(decision.Admitted);
			Assert.False(decision.Crossed);
		}

		Assert.Equal(0, limiter.TrackedAddresses);
	}

	[Fact]
	public void TrackedAddresses_CountsEachAddressOnce_AcrossARotation()
	{
		FailedAuthLimiter limiter = Limiter(permits: 100, maxTracked: 4);
		limiter.TryReserve("a", Now);
		limiter.TryReserve("b", Now);
		Assert.Equal(2, limiter.TrackedAddresses);

		limiter.TryReserve("c", Now);
		Assert.Equal(3, limiter.TrackedAddresses);

		limiter.TryReserve("a", Now);
		Assert.Equal(3, limiter.TrackedAddresses);
	}

	[Fact]
	public void NoTrustedProxies_RegistersNothing_AndABadEntryStopsStartupNamingIt()
	{
		ApplicationBuilder app = new(new ServiceCollection().BuildServiceProvider());

		Assert.Same(app, app.UseMemoriaForwardedHeaders(null));
		Assert.Same(app, app.UseMemoriaForwardedHeaders(["", "  "]));
		InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => app.UseMemoriaForwardedHeaders(["10.0.0.1", "not-an-address"]));
		Assert.Contains("'not-an-address'", refused.Message, StringComparison.Ordinal);
		Assert.Contains("Memoria:Auth:TrustedProxies", refused.Message, StringComparison.Ordinal);
	}

	// --- hosting ------------------------------------------------------------------------------------------------

	[Fact]
	public void AddMemoriaAuth_UsesTheSystemClock_UnlessOneIsAlreadyRegistered()
	{
		ServiceCollection bare = new();
		bare.AddMemoriaAuth(new AuthOptions { DatabasePath = "unused.db" });
		Assert.Same(TimeProvider.System, bare.BuildServiceProvider().GetRequiredService<TimeProvider>());

		ManualClock clock = new(Now);
		ServiceCollection withClock = new();
		withClock.AddSingleton<TimeProvider>(clock);
		withClock.AddMemoriaAuth(new AuthOptions { DatabasePath = "unused.db" });
		Assert.Same(clock, withClock.BuildServiceProvider().GetRequiredService<TimeProvider>());
		Assert.Single(withClock, static s => s.ServiceType == typeof(TimeProvider));
	}

	[Fact]
	public async Task DisabledAuth_WarnsUnderTheAuthCategory()
	{
		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
		builder.Configuration["Memoria:Auth:Mode"] = AuthModes.Disabled;
		builder.Services.Configure<MemoriaOptions>(builder.Configuration.GetSection("Memoria"));
		ListLogger logger = new();
		builder.Logging.ClearProviders();
		builder.Logging.AddProvider(logger);
		await using WebApplication app = builder.Build();

		await app.InitializeMemoriaAuthAsync();

		ListLogger.Entry warning = Assert.Single(logger.Entries, static e => e.EventId == 3200);
		Assert.Equal("Memoria.Api.Auth", warning.Category);
		Assert.Equal(LogLevel.Warning, warning.Level);
	}

	// --- the control database -----------------------------------------------------------------------------------

	[Fact]
	public async Task AControlDatabaseWithNoPath_IsRefusedAsMisconfiguration()
	{
		InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => new ControlDatabase(null).InitializeAsync());

		Assert.Contains("Memoria:Auth:DatabasePath", refused.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TheControlDatabase_IsWriteAheadLogged_SyncedNormally_AndPrivateToItsOwner()
	{
		using TempDatabase temp = new();
		await temp.Database.InitializeAsync();

		Assert.Equal("wal", await temp.ScalarAsync("PRAGMA journal_mode;"));
		Assert.Equal("1", await temp.ScalarAsync("PRAGMA synchronous;"));
		if (!OperatingSystem.IsWindows())
		{
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(temp.Path));
		}
	}

	[Fact]
	public async Task ANewerSchema_IsRefused_SayingWhichVersions()
	{
		using TempDatabase temp = new();
		await temp.Database.InitializeAsync();
		await temp.ExecuteAsync("PRAGMA user_version = 99;");

		InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => temp.Database.InitializeAsync());

		Assert.Contains("schema version 99", refused.Message, StringComparison.Ordinal);
		Assert.Contains("understands up to 3", refused.Message, StringComparison.Ordinal);
	}

	// --- redaction settings -------------------------------------------------------------------------------------

	[Fact]
	public void RedactionSettings_AreReportedInOrdinalOrder()
	{
		MemoriaOptions options = new();
		foreach (string name in new[] { "zeta", "alpha" })
		{
			options.Ingest.Collections[name] = new IngestCollectionOptions { Redaction = Memoria.Core.Redaction.RedactionDisabledCollection.SettingValue };
		}

		foreach (string name in new[] { "omega", "beta" })
		{
			options.Ingest.Collections[name] = new IngestCollectionOptions { Redaction = "off" };
		}

		IConfigurationRoot configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["Memoria:Ingest:Collections:Notes:Enabled"] = "true", ["Memoria:Ingest:Collections:Work:Enabled"] = "true" })
			.AddInMemoryCollection(new Dictionary<string, string?> { ["Memoria:Ingest:Collections:notes:Enabled"] = "true", ["Memoria:Ingest:Collections:work:Enabled"] = "true" })
			.Build();

		IngestRedactionPolicy policy = new(Options.Create(options), configuration);

		Assert.Equal(["alpha", "zeta"], policy.DisabledCollections);
		Assert.Equal(["beta", "omega"], policy.UnrecognizedSettings);
		Assert.Equal(["Notes", "Work"], policy.AmbiguousSettings);
	}

	// --- the audit log ------------------------------------------------------------------------------------------

	private static AuditRecord Record(string key = "k1", DateTimeOffset? at = null, IReadOnlyList<string>? collections = null, IReadOnlyList<string>? attempted = null, object? target = null)
	{
		return new AuditRecord(Guid.NewGuid().ToString("N"), at ?? Now, key, "search", collections, attempted, 1, 200, "trace", target, null);
	}

	[Fact]
	public async Task NullAuditLog_AcceptsARecord()
	{
		await new NullAuditLog().WriteAsync(Record());
	}

	[Fact]
	public async Task AnAuditRow_StoresAbsentListsAsNull_AndPresentOnesAsJson()
	{
		using TempDatabase temp = new();
		await temp.Database.InitializeAsync();
		SqliteAuditLog log = temp.AuditLog();

		await log.WriteAsync(Record("absent"));
		await log.WriteAsync(Record("present", collections: ["c"], attempted: ["x"]));

		Assert.Equal("NULL|NULL", await temp.ScalarAsync("SELECT COALESCE(collections, 'NULL') || '|' || COALESCE(attempted_collections, 'NULL') FROM audit_log WHERE key_id = 'absent';"));
		Assert.Equal("[\"c\"]|[\"x\"]", await temp.ScalarAsync("SELECT collections || '|' || attempted_collections FROM audit_log WHERE key_id = 'present';"));
	}

	[Fact]
	public async Task EachRecord_IsLogged_WithAnAdminTargetButNeverWhichFileWasRead()
	{
		using TempDatabase temp = new();
		await temp.Database.InitializeAsync();
		SqliteAuditLog log = temp.AuditLog();

		await log.WriteAsync(Record("admin", target: new AuditTarget("targetkey123", [MemoriaScopes.Read], ["c"], false)));
		await log.WriteAsync(Record("reader", target: new AuditRead("c/private-session.md", true)));

		ListLogger.Entry admin = Assert.Single(temp.Logger.Entries, static e => e.EventId == 3100 && e.Message.Contains("key admin", StringComparison.Ordinal));
		Assert.Contains("targetkey123", admin.Message, StringComparison.Ordinal);
		ListLogger.Entry reader = Assert.Single(temp.Logger.Entries, static e => e.EventId == 3100 && e.Message.Contains("key reader", StringComparison.Ordinal));
		Assert.DoesNotContain("private-session", reader.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnAuditWriteThatFails_IsLoggedAsAnError_AndDoesNotThrow()
	{
		using TempDatabase temp = new();
		SqliteAuditLog log = temp.AuditLog();

		await log.WriteAsync(Record());

		Assert.Equal(LogLevel.Error, Assert.Single(temp.Logger.Entries, static e => e.EventId == 3101).Level);
	}

	[Fact]
	public async Task AKeysRows_AreTrimmedEveryTenthOfTheCap_SoTheyNeverPassTheCapByMore()
	{
		using TempDatabase temp = new();
		await temp.Database.InitializeAsync();
		SqliteAuditLog log = temp.AuditLog(maxRowsPerKey: 20);
		List<int> counts = [];

		for (int i = 0; i < 30; i++)
		{
			await log.WriteAsync(Record());
			counts.Add(int.Parse(await temp.ScalarAsync("SELECT COUNT(*) FROM audit_log;"), CultureInfo.InvariantCulture));
		}

		// A trim every second write: the cap is passed by one row at most, and only between trims.
		Assert.Equal(21, counts.Max());
		Assert.Equal(20, counts[^1]);
		Assert.Equal(21, counts[^2]);
	}

	[Fact]
	public async Task OldRows_ArePrunedAtMostOncePerHour()
	{
		using TempDatabase temp = new();
		await temp.Database.InitializeAsync();
		ManualClock clock = new(Now);
		SqliteAuditLog log = temp.AuditLog(retentionDays: 1, clock: clock);
		DateTimeOffset old = Now.AddDays(-2);
		Task<string> OldRows() => temp.ScalarAsync($"SELECT COUNT(*) FROM audit_log WHERE timestamp < '{ControlDatabase.FormatTimestamp(Now.AddDays(-1))}';");

		await log.WriteAsync(Record(at: old));
		Assert.Equal("0", await OldRows());

		clock.Advance(TimeSpan.FromMinutes(59));
		await log.WriteAsync(Record(at: old));
		Assert.Equal("1", await OldRows());

		clock.Advance(TimeSpan.FromMinutes(1));
		await log.WriteAsync(Record());
		Assert.Equal("0", await OldRows());
	}

	[Fact]
	public async Task ARetentionOfZero_KeepsEveryRow()
	{
		using TempDatabase temp = new();
		await temp.Database.InitializeAsync();
		SqliteAuditLog log = temp.AuditLog(retentionDays: 0);

		await log.WriteAsync(Record(at: Now.AddYears(-5)));
		await log.WriteAsync(Record());

		Assert.Equal("2", await temp.ScalarAsync("SELECT COUNT(*) FROM audit_log;"));
	}

	// --- helpers ------------------------------------------------------------------------------------------------

	private sealed class TempDatabase : IDisposable
	{
		private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "memoria-tests", Guid.NewGuid().ToString("N"));

		public TempDatabase()
		{
			Path = System.IO.Path.Combine(_directory, "control.db");
			Database = new ControlDatabase(Path);
		}

		public string Path { get; }

		public ControlDatabase Database { get; }

		public ListLogger Logger { get; } = new();

		public SqliteAuditLog AuditLog(int retentionDays = 90, int maxRowsPerKey = 100_000, TimeProvider? clock = null)
		{
			MemoriaOptions options = new() { Auth = new AuthOptions { Audit = new AuditOptions { RetentionDays = retentionDays, MaxRowsPerKey = maxRowsPerKey } } };
			return new SqliteAuditLog(Database, Options.Create(options), clock ?? new ManualClock(Now), Logger.For<SqliteAuditLog>());
		}

		public async Task ExecuteAsync(string sql)
		{
			await using SqliteConnection connection = await Database.OpenAsync();
			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = sql;
			await command.ExecuteNonQueryAsync();
		}

		public async Task<string> ScalarAsync(string sql)
		{
			await using SqliteConnection connection = await Database.OpenAsync();
			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = sql;
			return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
		}

		public void Dispose()
		{
			SqliteConnection.ClearAllPools();
			try
			{
				Directory.Delete(_directory, recursive: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Best effort, as in AuthApiFactory.
			}
		}
	}

	private sealed class ManualClock(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;

		public override DateTimeOffset GetUtcNow()
		{
			return _now;
		}

		public void Advance(TimeSpan by)
		{
			_now += by;
		}
	}

	private sealed class ListLogger : ILoggerProvider
	{
		public sealed record Entry(string Category, LogLevel Level, int EventId, string Message);

		public List<Entry> Entries { get; } = [];

		public ILogger<T> For<T>()
		{
			return new Logger<T>(new Factory(this));
		}

		public ILogger CreateLogger(string categoryName)
		{
			return new Sink(this, categoryName);
		}

		public void Dispose()
		{
		}

		private sealed class Factory(ListLogger provider) : ILoggerFactory
		{
			public void AddProvider(ILoggerProvider provider)
			{
			}

			public ILogger CreateLogger(string categoryName)
			{
				return provider.CreateLogger(categoryName);
			}

			public void Dispose()
			{
			}
		}

		private sealed class Sink(ListLogger provider, string category) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull
			{
				return null;
			}

			public bool IsEnabled(LogLevel logLevel)
			{
				return true;
			}

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
			{
				lock (provider.Entries)
				{
					provider.Entries.Add(new Entry(category, logLevel, eventId.Id, formatter(state, exception)));
				}
			}
		}
	}
}
