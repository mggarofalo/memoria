using Memoria.Api.Auth;
using Microsoft.AspNetCore.Authorization;

namespace Memoria.Api.Http;

/// <summary>
/// Names of the access scopes an API key can carry. They do not imply one
/// another: <see cref="Admin"/> manages keys and sees backend detail but cannot
/// read the corpus unless the key also has <see cref="Read"/>. Day-to-day
/// clients then never hold key-management power, and reading documents always
/// goes through a collection allowlist.
/// </summary>
public static class MemoriaScopes
{
	/// <summary>Search, read documents, list collections.</summary>
	public const string Read = "read";

	/// <summary>Submit documents for indexing.</summary>
	public const string Ingest = "ingest";

	/// <summary>Manage API keys, and operate the backend: workers, the background queue, and reindexing.</summary>
	public const string Admin = "admin";

	public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Read, Ingest, Admin };
}

/// <summary>
/// Endpoint metadata naming the scope a caller needs. ASP.NET Core's
/// authorization middleware picks the requirement up from the metadata, and a
/// caller without the scope gets 401 (no valid key) or 403 (key lacks the scope).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class MemoriaScopeAttribute(string scope) : Attribute, IAuthorizationRequirementData
{
	public string Scope { get; } = scope;

	public IEnumerable<IAuthorizationRequirement> GetRequirements()
	{
		yield return new MemoriaScopeRequirement(Scope);
	}
}

public sealed record MemoriaScopeRequirement(string Scope) : IAuthorizationRequirement;

/// <summary>Succeeds when the authenticated key carries the required scope, exactly.</summary>
public sealed class MemoriaScopeHandler : AuthorizationHandler<MemoriaScopeRequirement>
{
	protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MemoriaScopeRequirement requirement)
	{
		if (context.User.Identity?.IsAuthenticated == true && context.User.HasClaim(MemoriaClaims.Scope, requirement.Scope))
		{
			context.Succeed(requirement);
		}

		return Task.CompletedTask;
	}
}

public static class MemoriaScopeEndpointExtensions
{
	/// <summary>Declares the scope every endpoint in the group requires.</summary>
	public static TBuilder WithMemoriaScope<TBuilder>(this TBuilder builder, string scope) where TBuilder : IEndpointConventionBuilder
	{
		return builder.WithMetadata(new MemoriaScopeAttribute(scope));
	}
}
