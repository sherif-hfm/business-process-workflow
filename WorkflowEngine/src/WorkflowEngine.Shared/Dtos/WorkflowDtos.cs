using System.Text.Json;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Shared.Dtos;

public sealed record WorkflowSummaryDto(
    long Id,
    string Name,
    int Version,
    bool IsPublished,
    DateTimeOffset CreatedAt);

public sealed record WorkflowDetailDto(
    long Id,
    string Name,
    int Version,
    bool IsPublished,
    DateTimeOffset CreatedAt,
    WorkflowModel Definition);

public sealed record CreateWorkflowRequest(WorkflowModel Definition, bool Publish = false);

public sealed record UpdateWorkflowRequest(WorkflowModel Definition, bool Publish = false);

public sealed record StartInstanceRequest(
    long WorkflowId,
    string? StartedBy,
    Dictionary<string, JsonElement>? Variables);

public sealed record ClaimRequest(string? User);

public sealed record TakeActionRequest(
    string? PerformedBy,
    Dictionary<string, JsonElement>? Variables);

public sealed record InstanceSummaryDto(
    long Id,
    long WorkflowId,
    string WorkflowName,
    int WorkflowVersion,
    int CurrentStepId,
    string CurrentStepName,
    string Status,
    string? ClaimedBy,
    string? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record InstanceDetailDto(
    long Id,
    WorkflowDetailDto Workflow,
    int CurrentStepId,
    string CurrentStepName,
    string Status,
    string? ClaimedBy,
    string? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<InstanceVariableDto> Variables,
    IReadOnlyList<InstanceHistoryDto> History);

public sealed record InstanceVariableDto(
    long Id,
    string VariableName,
    int? SourceActionId,
    JsonElement Value,
    DateTimeOffset SetAt);

public sealed record InstanceHistoryDto(
    long Id,
    int? ActionId,
    int FromStepId,
    int ToStepId,
    string? PerformedBy,
    Dictionary<string, JsonElement>? Payload,
    string? Note,
    DateTimeOffset PerformedAt);

public sealed record InboxItemDto(
    long InstanceId,
    long WorkflowId,
    string WorkflowName,
    int CurrentStepId,
    string CurrentStepName,
    IReadOnlyList<string> StepRoles,
    bool RequiresClaim,
    string? ClaimedBy,
    bool ClaimedByMe,
    bool CanClaim,
    bool CanAct,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
