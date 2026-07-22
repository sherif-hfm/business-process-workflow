using Flowbit.Service.Models;

namespace Flowbit.Api.Auth;

/// <summary>
/// Process-lifetime configuration for the JWT claim that identifies an authenticated
/// workflow actor. The value is loaded from <c>flowbit.engine_settings</c> during API startup
/// and deliberately remains fixed until the process is restarted.
/// </summary>
public sealed class ActorIdentityConfiguration
{
    public const string SettingKey = "Authentication.UserIdentityClaim";

    private string? claimType;
    private bool initialized;

    /// <summary>
    /// The configured claim type, or <see langword="null"/> when the legacy
    /// <c>Identity.Name</c>/<c>NameIdentifier</c> selection should be used.
    /// </summary>
    public string? ClaimType => initialized
        ? claimType
        : throw new InvalidOperationException("Actor identity configuration has not been initialized.");

    public void Initialize(EngineSettingRecord? setting)
    {
        if (initialized)
        {
            throw new InvalidOperationException("Actor identity configuration has already been initialized.");
        }

        if (setting is not null && string.IsNullOrWhiteSpace(setting.Value))
        {
            throw new InvalidOperationException(
                $"Engine setting '{SettingKey}' must contain a nonblank JWT claim type.");
        }

        claimType = setting?.Value.Trim();
        initialized = true;
    }
}
