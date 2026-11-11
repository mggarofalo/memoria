using Memoria.Api.Http;

namespace Memoria.Api.Auth;

/// <summary>
/// What an admin key may hand out. Two rules, either of which alone stops a key bound to collections from escaping
/// its binding:
/// <list type="bullet">
/// <item>The <c>admin</c> scope is all-or-nothing. It acts on every collection, so a key cannot hold it beside a
/// collection list. Issuing one is refused, and one already in the database is not given the scope.</item>
/// <item>A key never issues or rotates a key wider than itself: in scopes, in collections, or in lifetime.</item>
/// </list>
/// </summary>
public static class KeyGrant
{
	/// <summary>The furthest ahead any key's <c>expiresAt</c> may be, and the longest <c>DefaultKeyLifetime</c>.</summary>
	public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(730);

	/// <summary>Scopes an admin key may grant beyond its own: managing keys is what the admin scope is for.</summary>
	private static readonly string[] GrantedByAdmin = [MemoriaScopes.Read, MemoriaScopes.Ingest];

	/// <summary>True for a key that holds <c>admin</c> and lists collections.</summary>
	public static bool IsBoundAdmin(IReadOnlyCollection<string> scopes, IReadOnlyCollection<string> collections, bool allCollections)
	{
		return scopes.Contains(MemoriaScopes.Admin, StringComparer.Ordinal) && !allCollections && collections.Count > 0;
	}

	/// <summary>
	/// Null when <paramref name="caller"/> may issue or rotate a key of this shape; otherwise which way it is wider.
	/// A caller that lists no collections reaches every one. A caller that expires cannot make a key that outlives it.
	/// </summary>
	public static string? Exceeds(ApiKeyRecord caller, IReadOnlyCollection<string> scopes, IReadOnlyCollection<string> collections, bool allCollections, DateTimeOffset? expiresAt)
	{
		if (!scopes.All(scope => Grants(caller, scope)))
		{
			return "scopes";
		}

		if (!Covers(caller, collections, allCollections))
		{
			return "collections";
		}

		return caller.ExpiresAt is { } limit && (expiresAt is not { } until || until > limit) ? "lifetime" : null;
	}

	private static bool Grants(ApiKeyRecord caller, string scope)
	{
		bool admin = caller.Scopes.Contains(MemoriaScopes.Admin, StringComparer.Ordinal);
		return caller.Scopes.Contains(scope, StringComparer.Ordinal) || (admin && GrantedByAdmin.Contains(scope, StringComparer.Ordinal));
	}

	private static bool Covers(ApiKeyRecord caller, IReadOnlyCollection<string> collections, bool allCollections)
	{
		if (caller.AllCollections || caller.Collections.Count == 0)
		{
			return true;
		}

		return !allCollections && collections.Count > 0 && collections.All(collection => caller.Collections.Contains(collection, StringComparer.Ordinal));
	}
}
