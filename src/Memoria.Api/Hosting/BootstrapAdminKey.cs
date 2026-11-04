using Memoria.Api.Auth;
using Memoria.Api.Configuration;
using Memoria.Api.Http;

namespace Memoria.Api.Hosting;

/// <summary>
/// <c>dotnet Memoria.Api.dll --bootstrap-admin-key &lt;label&gt;</c>: creates an
/// admin key, prints it once to stdout and exits without starting a host. This
/// is how the first key exists without any key material in configuration. It
/// refuses while an active admin key exists unless
/// <c>--allow-additional-admin</c> is also passed.
/// </summary>
public static class BootstrapAdminKey
{
	public const string Switch = "--bootstrap-admin-key";

	public const string AllowAdditionalSwitch = "--allow-additional-admin";

	public static bool IsRequested(string[] args)
	{
		return args.Length > 0 && string.Equals(args[0], Switch, StringComparison.Ordinal);
	}

	/// <summary>Everything except this command's own switches, so the configuration stack still sees the rest.</summary>
	public static string[] HostArguments(string[] args)
	{
		return args.Skip(2).Where(static arg => !string.Equals(arg, AllowAdditionalSwitch, StringComparison.Ordinal)).ToArray();
	}

	public static Task<int> RunAsync(string[] args, IConfiguration configuration, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
	{
		string? label = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1].Trim() : null;
		if (string.IsNullOrEmpty(label))
		{
			error.WriteLine($"Usage: {Switch} <label> [{AllowAdditionalSwitch}]");
			return Task.FromResult(2);
		}

		AuthOptions auth = configuration.GetSection(MemoriaOptions.SectionName).GetSection(nameof(MemoriaOptions.Auth)).Get<AuthOptions>() ?? new AuthOptions();
		if (string.IsNullOrWhiteSpace(auth.DatabasePath))
		{
			error.WriteLine("Memoria:Auth:DatabasePath is not set; the key has nowhere to live.");
			return Task.FromResult(1);
		}

		IApiKeyStore store = new SqliteApiKeyStore(new ControlDatabase(auth.DatabasePath));
		return RunAsync(store, label, args.Contains(AllowAdditionalSwitch, StringComparer.Ordinal), TimeProvider.System, output, error, cancellationToken);
	}

	public static async Task<int> RunAsync(IApiKeyStore store, string label, bool allowAdditional, TimeProvider timeProvider, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(timeProvider);
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(error);

		DateTimeOffset now = timeProvider.GetUtcNow();
		await store.InitializeAsync(cancellationToken);
		if (!allowAdditional && await store.HasActiveAdminAsync(now, cancellationToken))
		{
			error.WriteLine($"An active admin key already exists. Revoke it, or pass {AllowAdditionalSwitch} to create another.");
			return 1;
		}

		// Admin-only and bound to no collection: admin does not imply read.
		(ApiKeyRecord record, string token) = ApiKeyRecord.Issue(label, [MemoriaScopes.Admin], [], false, null, now);
		await store.InsertAsync(record, cancellationToken);

		error.WriteLine($"Created admin key {record.Id} ('{label}'). It is printed once, on stdout, and cannot be recovered.");
		output.WriteLine(token);
		return 0;
	}
}
