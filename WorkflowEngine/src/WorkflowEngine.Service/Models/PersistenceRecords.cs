using System.Text.Json;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Service.Models;

public sealed record WorkflowDefinitionRecord(
    long Id,
    string Name,
    int Version,
    WorkflowModel Definition,
    bool IsPublished,
    DateTimeOffset CreatedAt);

public sealed record WorkflowInstanceRecord(
    long Id,
    long WorkflowDefinitionId,
    int CurrentStepId,
    string Status,
    string? ClaimedBy,
    string? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// Snapshot of the flow node an instance currently rests on, denormalized onto
// the instance row so the list/inbox read path never parses the definition JSON.
public sealed record CurrentNodeSnapshot(
    int Id,
    string Name,
    string Type,
    IReadOnlyList<string> Roles,
    bool RequiresClaim);

// Flattened read row for the paged list/inbox queries: instance columns plus the
// joined workflow name/version and the denormalized current-node fields.
public sealed record InstanceListItem(
    long Id,
    long WorkflowId,
    string WorkflowName,
    int WorkflowVersion,
    int CurrentNodeId,
    string CurrentNodeName,
    string CurrentNodeType,
    IReadOnlyList<string> CurrentNodeRoles,
    bool CurrentRequiresClaim,
    string Status,
    string? ClaimedBy,
    string? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record InstanceVariableRecord(
    long Id,
    long InstanceId,
    string VariableName,
    int? SourceActionId,
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

public static class WorkflowInstanceStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}
