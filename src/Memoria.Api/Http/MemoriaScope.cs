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

/// <summary>
/// Endpoint metadata for routes that act on every collection at once: the key must not be
/// bound to particular collections. An admin-only key is bound to none and an
/// all-collections key to every one; both pass. It is an authorization requirement, not an
/// endpoint filter, so it is decided before the request body is read and a refused key
/// gets the same 403 whatever it sends.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class MemoriaUnboundKeyAttribute : Attribute, IAuthorizationRequirementData
{
	public IEnumerable<IAuthorizationRequirement> GetRequirements()
	{
		yield return new MemoriaUnboundKeyRequirement();
	}
}

public sealed record MemoriaUnboundKeyRequirement : IAuthorizationRequirement;

/// <summary>Succeeds when the authenticated key carries no collection claim.</summary>
public sealed class MemoriaUnboundKeyHandler : AuthorizationHandler<MemoriaUnboundKeyRequirement>
{
	protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MemoriaUnboundKeyRequirement requirement)
	{
		if (context.User.Identity?.IsAuthenticated == true && !context.User.HasClaim(static c => c.Type == MemoriaClaims.Collection))
		{
			context.Succeed(requirement);
		}

		return Task.CompletedTask;
	}
}

public static class MemoriaPrincipalExtensions
{
	/// <summary>
	/// An authenticated key holding <c>admin</c> and bound to no collection: the only caller that sees backend detail or
	/// probes readiness on its own. The handler already withholds admin from a bound key; this checks again, so the
	/// binding holds even if that ever changes.
	/// </summary>
	public static bool IsUnboundAdmin(this System.Security.Claims.ClaimsPrincipal user)
	{
		return user.Identity?.IsAuthenticated == true
			&& user.HasClaim(MemoriaClaims.Scope, MemoriaScopes.Admin)
			&& !user.HasClaim(static c => c.Type == MemoriaClaims.Collection);
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
