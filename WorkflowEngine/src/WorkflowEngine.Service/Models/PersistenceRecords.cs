using System.Text.Json;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Service.Models;

public sealed record WorkflowDefinitionRecord(
    long Id,
    string Name,
    string WorkflowKey,
    int Version,
    WorkflowModel Definition,
    bool IsPublished,
    bool IsDefault,
    DateTimeOffset CreatedAt);

public sealed record WorkflowInstanceRecord(
    long Id,
    long WorkflowDefinitionId,
    long ActiveTokenId,
    int CurrentStepId,
    long? ActiveUserTaskId,
    string Status,
    string? ClaimedBy,
    string? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// Snapshot copied onto an execution token and, for userTask nodes, its work item.
public sealed record CurrentNodeSnapshot(
    int Id,
    string Name,
    string? ExternalId,
    string Type,
    IReadOnlyList<string> Roles,
    bool RequiresClaim);

// Compatibility projection for the existing instance-oriented API. TokenId and
// UserTaskId keep the persistence boundary ready for task/token-addressed APIs.
public sealed record InstanceListItem(
    long Id,
    long WorkflowId,
    long WorkflowDefinitionId,
    string WorkflowName,
    int WorkflowVersion,
    long TokenId,
    long? UserTaskId,
    int CurrentNodeId,
    string CurrentNodeName,
    string? CurrentNodeExternalId,
    string CurrentNodeType,
    IReadOnlyList<string> CurrentNodeRoles,
    bool CurrentRequiresClaim,
    string Status,
    string? ClaimedBy,
    string? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// Exact-match filter over an instance variable's scalar value (name = value).
public sealed record VariableFilter(string Name, string Value);

public sealed record InstanceVariableRecord(
    long Id,
    long InstanceId,
    string VariableName,
    int? SourceActionId,
    string? SetBy,
    JsonElement Value,
    DateTimeOffset SetAt);

public sealed record InstanceHistoryRecord(
    long Id,
    long InstanceId,
    int? ActionId,
    int FromStepId,
    int ToStepId,
    string? PerformedBy,
    Dictionary<string, JsonElement>? Payload,
    string? Note,
    DateTimeOffset PerformedAt);

public sealed record EngineSettingRecord(
    long Id,
    string? Namespace,
    string Key,
    string Value,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class WorkflowInstanceStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    // Terminal status set when an instance enters an errorEndEvent (vs the
    // Completed status set by a plain endEvent). Filterable in the list/inbox.
    public const string Faulted = "faulted";
}
