namespace Memoria.Api.Http;

/// <summary>Names of the access scopes an API key can carry.</summary>
public static class MemoriaScopes
{
	/// <summary>Search, read documents, list collections.</summary>
	public const string Read = "read";

	/// <summary>Submit documents for indexing.</summary>
	public const string Ingest = "ingest";

	/// <summary>Reindex, worker and queue administration.</summary>
	public const string Admin = "admin";
}

/// <summary>
/// Endpoint metadata naming the scope a caller needs. Nothing enforces it
/// yet: the authentication handler reads this metadata and rejects
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
