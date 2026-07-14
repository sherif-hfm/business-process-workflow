using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace WorkflowEngine.Tests;

internal static class ApiTestAuth
{
    public const string Issuer = "workflow-tests";
    public const string Audience = "workflow-tests-api";
    public const string Key = "workflow-tests-signing-key-at-least-32-characters-long";

    public static HttpRequestMessage Authorize(
        HttpRequestMessage request,
        string user = "test-admin",
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user),
            new(JwtRegisteredClaimNames.Sub, user)
        };
        claims.AddRange(roles.DefaultIfEmpty("admin").Select(role => new Claim(ClaimTypes.Role, role)));
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            claims,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(30),
            credentials);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
        request.Headers.TryAddWithoutValidation("X-Test-User", user);
        request.Headers.TryAddWithoutValidation(
            "X-Test-Roles",
            string.Join(',', roles.DefaultIfEmpty("admin")));
        return request;
    }
}
