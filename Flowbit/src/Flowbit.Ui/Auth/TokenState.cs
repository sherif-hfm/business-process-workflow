using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Serilog;

namespace Flowbit.Ui.Auth;

/// <summary>
/// Holds the JWT the test UI sends to the API. Registered as a singleton, so a single
/// tester's chosen identity applies to the whole UI process (adequate for a test harness).
/// </summary>
public sealed class TokenState
{
    private readonly Lock gate = new();
    private string? token;
    private string? currentUser;
    private IReadOnlyList<string> currentRoles = [];

    public event Action? Changed;

    public string? Token
    {
        get
        {
            lock (gate)
            {
                return token;
            }
        }
    }

    public string? CurrentUser
    {
        get
        {
            lock (gate)
            {
                return currentUser;
            }
        }
    }

    public IReadOnlyList<string> CurrentRoles
    {
        get
        {
            lock (gate)
            {
                return currentRoles;
            }
        }
    }

    public bool HasToken
    {
        get
        {
            lock (gate)
            {
                return !string.IsNullOrWhiteSpace(token);
            }
        }
    }

    public void Set(string? newToken)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(newToken))
            {
                token = null;
                currentUser = null;
                currentRoles = [];
            }
            else
            {
                token = newToken.Trim();
                (currentUser, currentRoles) = Parse(token);
            }
        }

        Changed?.Invoke();
    }

    public void Clear() => Set(null);

    private static (string? User, IReadOnlyList<string> Roles) Parse(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(token))
            {
                return (null, []);
            }

            var jwt = handler.ReadJwtToken(token);
            var user = jwt.Claims
                .FirstOrDefault(c => c.Type is ClaimTypes.Name or "name" or "unique_name")?.Value
                ?? jwt.Subject;
            var roles = jwt.Claims
                .Where(c => c.Type is ClaimTypes.Role or "role" or "roles")
                .Select(c => c.Value)
                .ToList();
            return (user, roles);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read or parse JWT token.");
            return (null, []);
        }
    }
}
