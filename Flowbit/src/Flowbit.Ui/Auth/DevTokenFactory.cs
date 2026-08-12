using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Flowbit.Ui.Auth;

/// <summary>
/// Mints a JWT signed with the shared symmetric key (Option A). This is a development
/// test convenience only; production should obtain tokens from a real identity provider.
/// </summary>
public sealed class DevTokenFactory(IConfiguration configuration, ILogger<DevTokenFactory> logger)
{
    public string Create(
        string? user,
        IEnumerable<string> roles,
        int expiresInMinutes,
        IEnumerable<DevTokenClaim>? customClaims = null)
    {
        var issuer = configuration["Jwt:Issuer"] ?? "workflow-engine-dev";
        var audience = configuration["Jwt:Audience"] ?? "workflow-engine-api";
        var key = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key is not configured.");

        var name = string.IsNullOrWhiteSpace(user) ? "anonymous" : user.Trim();
        var normalizedRoles = roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedCustomClaims = DevTokenClaimRules.Normalize(customClaims);
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, name),
            new(JwtRegisteredClaimNames.Sub, name)
        };
        foreach (var role in normalizedRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        foreach (var customClaim in normalizedCustomClaims)
        {
            claims.Add(new Claim(customClaim.Name, customClaim.Value, ClaimValueTypes.String));
        }

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(Math.Max(1, expiresInMinutes)),
            signingCredentials: credentials);

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);
        logger.LogInformation(
            "Minted dev JWT for user '{User}' with roles [{Roles}] and custom claim names [{ClaimNames}] valid for {Minutes}m.",
            name,
            string.Join(",", normalizedRoles),
            string.Join(",", normalizedCustomClaims.Select(claim => claim.Name)),
            expiresInMinutes);
        return jwt;
    }
}

public sealed record DevTokenClaim(string Name, string Value);

internal static class DevTokenClaimRules
{
    public const int MaxClaimCount = 20;
    public const int MaxClaimNameLength = 256;
    public const int MaxClaimValueLength = 1_000;

    private static readonly HashSet<string> ReservedClaimNames = new(StringComparer.OrdinalIgnoreCase)
    {
        JwtRegisteredClaimNames.Iss,
        JwtRegisteredClaimNames.Aud,
        JwtRegisteredClaimNames.Exp,
        JwtRegisteredClaimNames.Nbf,
        JwtRegisteredClaimNames.Iat,
        JwtRegisteredClaimNames.Sub,
        JwtRegisteredClaimNames.UniqueName,
        "nameid",
        "role",
        "roles",
        ClaimTypes.Name,
        ClaimTypes.NameIdentifier,
        ClaimTypes.Role
    };

    public static IReadOnlyList<DevTokenClaim> Normalize(IEnumerable<DevTokenClaim>? customClaims)
    {
        if (customClaims is null)
        {
            return [];
        }

        var normalized = new List<DevTokenClaim>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var customClaim in customClaims)
        {
            if (normalized.Count >= MaxClaimCount)
            {
                throw new ArgumentException($"No more than {MaxClaimCount} custom claims may be added.");
            }

            var claimName = customClaim.Name?.Trim() ?? string.Empty;
            var claimValue = customClaim.Value?.Trim() ?? string.Empty;
            if (claimName.Length == 0)
            {
                throw new ArgumentException("Every custom claim must have a name.");
            }
            if (claimName.Length > MaxClaimNameLength)
            {
                throw new ArgumentException($"Custom claim names may not exceed {MaxClaimNameLength} characters.");
            }
            if (claimName.Any(char.IsControl))
            {
                throw new ArgumentException($"Custom claim name '{claimName}' contains an unsupported control character.");
            }
            if (claimValue.Length > MaxClaimValueLength)
            {
                throw new ArgumentException(
                    $"The value for custom claim '{claimName}' may not exceed {MaxClaimValueLength} characters.");
            }
            if (ReservedClaimNames.Contains(claimName))
            {
                throw new ArgumentException(
                    $"Custom claim '{claimName}' is managed by the user, roles, or token settings and cannot be added here.");
            }
            if (!names.Add(claimName))
            {
                throw new ArgumentException($"Custom claim '{claimName}' is duplicated. Claim names are case-insensitive.");
            }

            normalized.Add(new DevTokenClaim(claimName, claimValue));
        }

        return normalized;
    }
}
