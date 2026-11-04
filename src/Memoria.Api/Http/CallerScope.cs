namespace Memoria.Api.Http;

/// <summary>
/// What the current caller is allowed to see. This is the seam API-key auth
/// fills in: an API-key authentication handler will register a scoped
/// implementation that reads the key's collection list. Until then every
/// caller is unrestricted, and endpoints already route every collection
/// decision through this interface so the auth epic adds no endpoint code.
/// </summary>
public interface ICallerScope
{
	/// <summary>Collections the caller may read. Null means every collection.</summary>
	IReadOnlyCollection<string>? Collections { get; }
}

/// <summary>Default scope while no authentication is configured.</summary>
public sealed class UnrestrictedCallerScope : ICallerScope
{
	public IReadOnlyCollection<string>? Collections => null;
}

/// <summary>Thrown when a caller names a collection outside their scope. Mapped to 403 by <see cref="MemoriaExceptionHandler"/>.</summary>
public sealed class CollectionForbiddenException(string collection)
	: Exception($"Collection '{collection}' is outside this caller's scope.")
{
	public string Collection { get; } = collection;
}

public static class CallerScopeExtensions
{
	/// <summary>True when the caller may read <paramref name="collection"/>.</summary>
	public static bool Allows(this ICallerScope scope, string collection)
	{
		return scope.Collections is null || scope.Collections.Contains(collection, StringComparer.Ordinal);
	}

	/// <summary>
	/// The collections a search should run against: the caller's request
	/// narrowed to their scope. Null means "defer to the default set". A
	/// request for a collection outside the scope is refused rather than
	/// silently dropped, so a misconfigured key is visible.
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
				throw new CollectionForbiddenException(collection);
			}
		}

		return requested;
	}
}
