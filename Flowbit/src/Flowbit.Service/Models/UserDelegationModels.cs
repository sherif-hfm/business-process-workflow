namespace Flowbit.Service.Models;

public static class UserDelegationAcceptanceStates
{
    public const string NotRequired = "notRequired";
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";

    public static bool IsKnown(string value) =>
        value is NotRequired or Pending or Accepted or Rejected;

    public static bool IsEffective(string value) =>
        value is NotRequired or Accepted;
}

public static class UserDelegationConstraints
{
    public const int MaxActorNameLength = 300;
    public const int MaxWorkflowKeyLength = 300;
    public const int MaxReasonLength = 1000;
    public const int MaxWorkflowKeysPerBatch = 100;
    public const string AdminRolesSettingKey = "Delegation.AdminRoles";
    public const string DefaultAdminRole = "admin";
}

public sealed record UserDelegationRecord(
    long Id,
    string Delegator,
    string Delegate,
    string WorkflowKey,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    bool RequiresAcceptance,
    string AcceptanceState,
    string CreatedBy,
    string? CreationReason,
    DateTimeOffset CreatedAt,
    string? DecisionBy,
    DateTimeOffset? DecisionAt,
    string? DecisionReason,
    string? RevokedBy,
    DateTimeOffset? RevokedAt,
    string? RevocationReason,
    DateTimeOffset UpdatedAt);

public sealed record NewUserDelegationRecord(
    string Delegator,
    string Delegate,
    string WorkflowKey,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    bool RequiresAcceptance,
    string AcceptanceState,
    string CreatedBy,
    string? CreationReason,
    DateTimeOffset CreatedAt);

public sealed record UserDelegationSearch(
    string? Delegator,
    string? Delegate,
    string? WorkflowKey,
    string? AcceptanceState,
    int Page,
    int PageSize);

public sealed record WorkflowDelegationPolicyRecord(
    string WorkflowKey,
    bool RequiresAcceptance,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);
