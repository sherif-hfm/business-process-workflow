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
    int? StartEventId,
    Dictionary<string, JsonElement>? Variables);

public sealed record TakeFlowRequest(
    Dictionary<string, JsonElement>? Variables);

public sealed record InstanceSummaryDto(
    long Id,
    long WorkflowId,
    string WorkflowName,
    int WorkflowVersion,
    int CurrentNodeId,
    string CurrentNodeName,
    string? CurrentNodeExternalId,
    string Status,
    string? ClaimedBy,
    string? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record InstanceDetailDto(
    long Id,
    WorkflowDetailDto Workflow,
    int CurrentNodeId,
    string CurrentNodeName,
    string? CurrentNodeExternalId,
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
    int? SourceFlowId,
    JsonElement Value,
    DateTimeOffset SetAt);

public sealed record InstanceHistoryDto(
    long Id,
    int? SequenceFlowId,
    int FromNodeId,
    int ToNodeId,
    string? PerformedBy,
    Dictionary<string, JsonElement>? Payload,
    string? Note,
    DateTimeOffset PerformedAt);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount);

public sealed record InboxItemDto(
    long InstanceId,
    long WorkflowId,
    string WorkflowName,
    int CurrentNodeId,
    string CurrentNodeName,
    string? CurrentNodeExternalId,
    IReadOnlyList<string> NodeRoles,
    bool RequiresClaim,
    string? ClaimedBy,
    bool ClaimedByMe,
    bool CanClaim,
    bool CanAct,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
