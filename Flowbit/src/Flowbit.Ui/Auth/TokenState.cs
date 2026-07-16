using Flowbit.Shared.Dtos;

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
    private bool identityResolved;

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

    public bool IdentityResolved
    {
        get
        {
            lock (gate)
            {
                return identityResolved;
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
                identityResolved = false;
            }
            else
            {
                token = newToken.Trim();
                // The API is authoritative because its configured identity claim may
                // differ from the token's conventional name/sub claims.
                currentUser = null;
                currentRoles = [];
                identityResolved = false;
            }
        }

        Changed?.Invoke();
    }

    public void Clear() => Set(null);

    public void ApplyResolvedContext(ActorContextDto context)
    {
        lock (gate)
        {
            currentUser = context.User;
            currentRoles = context.Roles;
            identityResolved = true;
        }

        Changed?.Invoke();
    }

    public void ClearResolvedContext()
    {
        lock (gate)
        {
            currentUser = null;
            currentRoles = [];
            identityResolved = false;
        }

        Changed?.Invoke();
    }
}
