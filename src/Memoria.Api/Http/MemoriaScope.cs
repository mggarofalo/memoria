namespace Memoria.Api.Http;

/// <summary>Names of the access scopes an API key can carry (MEMORIA-3).</summary>
public static class MemoriaScopes
{
	/// <summary>Search, read documents, list collections.</summary>
	public const string Read = "read";

	/// <summary>Submit documents for indexing (MEMORIA-4).</summary>
	public const string Ingest = "ingest";

	/// <summary>Reindex, worker and queue administration (MEMORIA-18).</summary>
	public const string Admin = "admin";
}

/// <summary>
/// Endpoint metadata naming the scope a caller needs. Nothing enforces it
/// yet: MEMORIA-3's authentication handler reads this metadata and rejects
/// callers whose key lacks the scope. Attaching it now means the auth epic
/// touches no endpoint definitions.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class MemoriaScopeAttribute(string scope) : Attribute
{
	public string Scope { get; } = scope;
}

public static class MemoriaScopeEndpointExtensions
{
	/// <summary>Declares the scope every endpoint in the group requires.</summary>
	public static TBuilder WithMemoriaScope<TBuilder>(this TBuilder builder, string scope) where TBuilder : IEndpointConventionBuilder
	{
		return builder.WithMetadata(new MemoriaScopeAttribute(scope));
	}
}
