using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;

namespace Flowbit.Api.Auth;

public interface IActorContextResolver
{
    ActorContext Resolve(ClaimsPrincipal principal);
}

/// <summary>
/// Converts a validated JWT principal into the single actor identity used throughout
/// workflow authorization, assignment, claiming, runtime context, and audit fields.
/// </summary>
public sealed class ActorContextResolver(ActorIdentityConfiguration configuration)
    : IActorContextResolver
{
    public ActorContext Resolve(ClaimsPrincipal principal)
    {
        var configuredClaimType = configuration.ClaimType;
        var user = configuredClaimType is null
            ? principal.Identity?.Name ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            : ResolveConfiguredIdentity(principal, configuredClaimType);

        var roles = principal.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .ToArray();

        // Preserve the first value for each raw claim type. WorkflowContext.AllowedClaims
        // independently controls which entries may be exposed as sys.claim.*.
        var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in principal.Claims)
        {
            claims.TryAdd(claim.Type, claim.Value);
        }

        return new ActorContext(user, roles, claims);
    }

    private static string ResolveConfiguredIdentity(ClaimsPrincipal principal, string configuredClaimType)
    {
        var candidateTypes = ClaimTypeCandidates(configuredClaimType);
        var matchingClaims = principal.Claims
            .Where(claim => candidateTypes.Contains(claim.Type))
            .ToList();

        if (matchingClaims.Count == 0 || matchingClaims.Any(claim => string.IsNullOrWhiteSpace(claim.Value)))
        {
            throw InvalidIdentityClaim(configuredClaimType);
        }

        var values = matchingClaims
            .Select(claim => claim.Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (values.Count != 1 || values[0].Length > UserTaskConstraints.MaxActorNameLength)
        {
            throw InvalidIdentityClaim(configuredClaimType);
        }

        return values[0];
    }

    private static HashSet<string> ClaimTypeCandidates(string configuredClaimType)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            configuredClaimType
        };

        // JwtBearer uses the framework's inbound claim map by default. Accept both the
        // JWT short name (for example "sub") and its mapped .NET URI so administrators
        // do not have to depend on that implementation detail.
        if (JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.TryGetValue(
                configuredClaimType, out var mappedType))
        {
            candidates.Add(mappedType);
        }

        foreach (var pair in JwtSecurityTokenHandler.DefaultInboundClaimTypeMap)
        {
            if (string.Equals(pair.Value, configuredClaimType, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(pair.Key);
            }
        }

        return candidates;
    }

    private static WorkflowUnauthorizedException InvalidIdentityClaim(string claimType) =>
        new($"The authenticated token does not contain one valid '{claimType}' actor identity claim.");
}
