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
    public string Create(string? user, IEnumerable<string> roles, int expiresInMinutes)
    {
        var issuer = configuration["Jwt:Issuer"] ?? "workflow-engine-dev";
        var audience = configuration["Jwt:Audience"] ?? "workflow-engine-api";
        var key = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key is not configured.");

        var name = string.IsNullOrWhiteSpace(user) ? "anonymous" : user.Trim();
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, name),
            new(JwtRegisteredClaimNames.Sub, name)
        };
        foreach (var role in roles
                     .Where(r => !string.IsNullOrWhiteSpace(r))
                     .Select(r => r.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
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
        logger.LogInformation("Minted dev JWT for user '{User}' with roles [{Roles}] valid for {Minutes}m.",
            name, string.Join(",", roles), expiresInMinutes);
        return jwt;
    }
}
