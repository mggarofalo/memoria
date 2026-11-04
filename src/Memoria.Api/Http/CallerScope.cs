using Memoria.Api.Auth;

namespace Memoria.Api.Http;

/// <summary>What the current caller is allowed to see.</summary>
public interface ICallerScope
{
	/// <summary>
	/// Collections the caller may read. Null means every collection. Empty means
	/// none: it must never be passed to the sidecar as an empty list, which the
	/// sidecar reads as "the default collections".
	/// </summary>
	IReadOnlyCollection<string>? Collections { get; }

	/// <summary>
	/// True when the caller may see how the backend failed: sidecar addresses,
	/// internal error text, the readiness report. Admin keys and disabled mode.
	/// </summary>
	bool SeesBackendDetails { get; }
}

/// <summary>
/// Reads the caller from the current request's principal. It goes through
/// <see cref="IHttpContextAccessor"/>, not constructor state, so the answer is
/// per request and per caller from whichever DI scope a component was activated
/// in, including MCP tools. No authenticated principal means no access.
/// </summary>
public sealed class HttpCallerScope(IHttpContextAccessor accessor) : ICallerScope
{
	public IReadOnlyCollection<string>? Collections
	{
		get
		{
			System.Security.Claims.ClaimsPrincipal? user = accessor.HttpContext?.User;
			if (user?.Identity?.IsAuthenticated != true)
			{
				return [];
			}

			return user.HasClaim(c => c.Type == MemoriaClaims.AllCollections)
				? null
				: user.FindAll(MemoriaClaims.Collection).Select(static c => c.Value).ToArray();
		}
	}

	public bool SeesBackendDetails
	{
		get
		{
			System.Security.Claims.ClaimsPrincipal? user = accessor.HttpContext?.User;
			return user?.Identity?.IsAuthenticated == true
				&& (user.HasClaim(c => c.Type == MemoriaClaims.AuthDisabled) || user.HasClaim(MemoriaClaims.Scope, MemoriaScopes.Admin));
		}
	}
}

/// <summary>Thrown when a caller names a collection outside their scope. Mapped to 403 by <see cref="MemoriaExceptionHandler"/>.</summary>
public sealed class CollectionForbiddenException(string collection, IReadOnlyList<string> requested)
	: Exception($"Collection '{collection}' is outside this caller's scope.")
{
	public string Collection { get; } = collection;

	/// <summary>Everything the caller asked for, for the audit record.</summary>
	public IReadOnlyList<string> Requested { get; } = requested;
}

public static class CallerScopeExtensions
{
	/// <summary>
	/// True when the caller may read <paramref name="collection"/>. The comparison
	/// is ordinal because QMD's collection names are case-sensitive: <c>Work</c>
	/// and <c>work</c> are two collections, and folding case would let a key for
	/// one read the other.
	/// </summary>
	public static bool Allows(this ICallerScope scope, string? collection)
	{
		if (collection is null)
		{
			return false;
		}

		return scope.Collections is not { } allowed || allowed.Contains(collection, StringComparer.Ordinal);
	}

	/// <summary>
	/// True when the caller may see a display path, <c>collection/path.md</c>.
	/// Used where the sidecar names a document without saying its collection. A
	/// path with a dot or empty segment or a backslash is refused for every
	/// caller: its first segment is not reliably the collection it ends up in.
	/// </summary>
	public static bool AllowsFile(this ICallerScope scope, string? file)
	{
		if (string.IsNullOrEmpty(file) || file.Contains('\\', StringComparison.Ordinal))
		{
			return false;
		}

		string[] segments = file.Split('/');
		if (segments.Length < 2 || segments.Any(static s => s is "" or "." or ".."))
		{
			return false;
		}

		return scope.Allows(segments[0]);
	}

	/// <summary>
	/// The collections a search should run against: the caller's request
	/// narrowed to their scope. Null means "defer to the default set", and only
	/// an unrestricted caller gets it. An empty result means the caller may read
	/// nothing, and the search must not reach the sidecar. A request for a
	/// collection outside the scope is refused rather than silently dropped, so a
	/// misconfigured key is visible, and is refused the same way whether or not
	/// that collection exists.
	/// </summary>
	public static IReadOnlyList<string>? Resolve(this ICallerScope scope, IReadOnlyList<string>? requested)
	{
		if (requested is null || requested.Count == 0)
		{
			return scope.Collections?.ToArray();
		}

		foreach (string collection in requested)
		{
			if (!scope.Allows(collection))
			{
				throw new CollectionForbiddenException(collection, requested);
			}
		}

		return requested;
	}
}
