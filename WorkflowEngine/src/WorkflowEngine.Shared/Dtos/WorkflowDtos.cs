using System.Text.Json;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Shared.Dtos;

/// <summary>
/// Represents a summary of a workflow definition version.
/// </summary>
/// <param name="Id">The unique database ID of this workflow version.</param>
/// <param name="Name">The human-readable name of the workflow.</param>
/// <param name="WorkflowKey">The stable cross-version identifier of the workflow.</param>
/// <param name="Version">The version number (starts at 1).</param>
/// <param name="IsPublished">Indicates if this version is published and can start new instances.</param>
/// <param name="IsDefault">Indicates if this is the default version for its workflow key (used when starting instances by key).</param>
/// <param name="CreatedAt">The timestamp when this version was created.</param>
public sealed record WorkflowSummaryDto(
    long Id,
    string Name,
    string WorkflowKey,
    int Version,
    bool IsPublished,
    bool IsDefault,
    DateTimeOffset CreatedAt);

/// <summary>
/// Represents detailed metadata and structural definition of a workflow version.
/// </summary>
/// <param name="Id">The unique database ID of this workflow version.</param>
/// <param name="Name">The human-readable name of the workflow.</param>
/// <param name="WorkflowKey">The stable cross-version identifier of the workflow.</param>
/// <param name="Version">The version number (starts at 1).</param>
/// <param name="IsPublished">Indicates if this version is published and can start new instances.</param>
/// <param name="IsDefault">Indicates if this is the default version for its workflow key (used when starting instances by key).</param>
/// <param name="CreatedAt">The timestamp when this version was created.</param>
/// <param name="Definition">The full structural workflow JSON model representation.</param>
public sealed record WorkflowDetailDto(
    long Id,
    string Name,
    string WorkflowKey,
    int Version,
    bool IsPublished,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    WorkflowModel Definition);

/// <summary>
/// Request payload for creating a new workflow definition.
/// </summary>
/// <param name="Definition">The structural JSON model of the workflow to create.</param>
/// <param name="Publish">Whether to publish this workflow immediately after creation.</param>
public sealed record CreateWorkflowRequest(WorkflowModel Definition, bool Publish = false);

/// <summary>
/// Request payload for updating an existing workflow definition to a new version.
/// </summary>
/// <param name="Definition">The structural JSON model of the workflow to update.</param>
/// <param name="Publish">Whether to publish this new version immediately.</param>
public sealed record UpdateWorkflowRequest(WorkflowModel Definition, bool Publish = false);

/// <summary>
/// Request payload for starting a new workflow instance.
/// </summary>
/// <param name="WorkflowId">Optional. The specific database ID of the workflow version to start.</param>
/// <param name="WorkflowKey">Optional. The stable key of the workflow (resolves to the default published version).</param>
/// <param name="StartEventId">Optional. The specific start event node ID to trigger.</param>
/// <param name="Variables">Optional. Initial process variables to set.</param>
public sealed record StartInstanceRequest(
    long? WorkflowId,
    string? WorkflowKey,
    int? StartEventId,
    Dictionary<string, JsonElement>? Variables);

/// <summary>
/// Slim response returned when starting a new workflow instance.
/// </summary>
/// <param name="Id">The database ID of the created instance.</param>
/// <param name="CurrentNodeId">The ID of the flow node where the instance is currently resting.</param>
/// <param name="CurrentNodeName">The name of the current resting flow node.</param>
/// <param name="CurrentNodeExternalId">The user-defined external ID of the current resting flow node.</param>
/// <param name="Status">The current execution status (e.g., "running", "completed", "faulted").</param>
/// <param name="StartedBy">The username of the actor who started the instance.</param>
/// <param name="CreatedAt">The timestamp when the instance was started.</param>
/// <param name="UpdatedAt">The timestamp when the instance was last updated.</param>
public sealed record StartInstanceResultDto(
    long Id,
    int CurrentNodeId,
    string CurrentNodeName,
    string? CurrentNodeExternalId,
    string Status,
    string? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Request payload for advancing a workflow instance down a sequence flow.
/// </summary>
/// <param name="Variables">Variables to merge or set during the transition.</param>
public sealed record TakeFlowRequest(
    Dictionary<string, JsonElement>? Variables);

public sealed record MultiInstanceFlowCountDto(int FlowId, int Count, double Percent);

public sealed record MultiInstanceProgressDto(
    long ExecutionId,
    string Mode,
    string Status,
    int Total,
    int Completed,
    int Active,
    int Pending,
    int Cancelled,
    int? WinningFlowId,
    string? CompletionReason,
    IReadOnlyList<MultiInstanceFlowCountDto> FlowCounts);

public sealed record UserTaskWorkSummaryDto(
    bool IsMultiInstance,
    int ActiveCount,
    int PendingCount,
    int ClaimedCount,
    int AssignedCount,
    string? SoleClaimedBy,
    string? SoleAssignee);

public sealed record UserTaskDto(
    long Id,
    long InstanceId,
    long TokenId,
    int NodeId,
    string NodeName,
    string? NodeExternalId,
    IReadOnlyList<string> Roles,
    bool RequiresClaim,
    string Status,
    string? ClaimedBy,
    string? Assignee,
    int? ItemIndex,
    JsonElement? ItemValue,
    int? SelectedFlowId,
    MultiInstanceProgressDto? MultiInstance,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record UserTaskActionAckDto(
    long UserTaskId,
    long InstanceId,
    string TaskStatus,
    string InstanceStatus,
    int SelectedFlowId,
    int CurrentNodeId,
    string CurrentNodeName,
    string? CurrentNodeExternalId,
    MultiInstanceProgressDto? MultiInstance,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Represents a summary of a workflow instance.
/// </summary>
/// <param name="Id">The database ID of the instance.</param>
/// <param name="WorkflowId">The database ID of the workflow version.</param>
/// <param name="WorkflowName">The name of the workflow.</param>
/// <param name="WorkflowVersion">The version of the workflow definition.</param>
/// <param name="CurrentNodeId">The ID of the current resting flow node.</param>
/// <param name="CurrentNodeName">The name of the current resting flow node.</param>
/// <param name="CurrentNodeExternalId">The user-defined external ID of the current resting flow node.</param>
/// <param name="Status">The current execution status of the instance.</param>
/// <param name="StartedBy">The username of the actor who started the instance.</param>
/// <param name="CreatedAt">The timestamp when the instance was created.</param>
/// <param name="UpdatedAt">The timestamp when the instance was last updated.</param>
/// <param name="UserTasks">Aggregate state for the current open user-task work, when present.</param>
public sealed record InstanceSummaryDto(
    long Id,
    long WorkflowId,
    string WorkflowName,
    int WorkflowVersion,
    int CurrentNodeId,
    string CurrentNodeName,
    string? CurrentNodeExternalId,
    string Status,
    string? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    UserTaskWorkSummaryDto? UserTasks);

/// <summary>
/// Represents full details of a workflow instance, including variable values and history.
/// </summary>
/// <param name="Id">The database ID of the instance.</param>
/// <param name="Workflow">The workflow definition and metadata associated with this instance.</param>
/// <param name="CurrentNodeId">The ID of the current resting flow node.</param>
/// <param name="CurrentNodeName">The name of the current resting flow node.</param>
/// <param name="CurrentNodeExternalId">The user-defined external ID of the current resting flow node.</param>
/// <param name="Status">The current execution status of the instance.</param>
/// <param name="StartedBy">The username of the actor who started the instance.</param>
/// <param name="CreatedAt">The timestamp when the instance was created.</param>
/// <param name="UpdatedAt">The timestamp when the instance was last updated.</param>
/// <param name="Variables">The complete list of instance variables and their values.</param>
/// <param name="History">The complete execution history of sequence flow hops and resting states.</param>
/// <param name="MultiInstance">Progress for the active multi-instance user task, when present.</param>
/// <param name="UserTasks">Aggregate state for the current open user-task work, when present.</param>
public sealed record InstanceDetailDto(
    long Id,
    WorkflowDetailDto Workflow,
    int CurrentNodeId,
    string CurrentNodeName,
    string? CurrentNodeExternalId,
    string Status,
    string? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<InstanceVariableDto> Variables,
    IReadOnlyList<InstanceHistoryDto> History,
    MultiInstanceProgressDto? MultiInstance,
    UserTaskWorkSummaryDto? UserTasks);

/// <summary>
/// Slim acknowledgment returned after successfully delivering a message to an intermediate message catch event.
/// </summary>
/// <param name="Id">The database ID of the workflow instance.</param>
/// <param name="CurrentNodeId">The ID of the current resting flow node after the delivery.</param>
/// <param name="CurrentNodeName">The name of the current resting flow node.</param>
/// <param name="CurrentNodeExternalId">The user-defined external ID of the current resting flow node.</param>
/// <param name="Status">The execution status of the instance after the delivery.</param>
/// <param name="UpdatedAt">The timestamp when the instance was updated.</param>
public sealed record MessageDeliveryAckDto(
    long Id,
    int CurrentNodeId,
    string CurrentNodeName,
    string? CurrentNodeExternalId,
    string Status,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Slim acknowledgment returned when starting a workflow instance via a message start event.
/// </summary>
/// <param name="InstanceId">The database ID of the created workflow instance.</param>
/// <param name="CurrentNodeId">The ID of the current resting flow node after starting.</param>
/// <param name="CurrentNodeName">The name of the current resting flow node.</param>
/// <param name="CurrentNodeExternalId">The user-defined external ID of the current resting flow node.</param>
/// <param name="Status">The execution status of the instance after starting.</param>
/// <param name="CreatedAt">The timestamp when the instance was started.</param>
public sealed record MessageStartAckDto(
    long InstanceId,
    int CurrentNodeId,
    string CurrentNodeName,
    string? CurrentNodeExternalId,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// Represents a variable currently stored in a workflow instance.
/// </summary>
/// <param name="Id">The unique database ID of the variable record.</param>
/// <param name="VariableName">The declared name of the variable.</param>
/// <param name="SourceFlowId">Optional. The ID of the sequence flow that was taken when setting this variable.</param>
/// <param name="SetBy">The username of the actor or background task that set the variable.</param>
/// <param name="Value">The JSON value of the variable.</param>
/// <param name="SetAt">The timestamp when the variable was set.</param>
public sealed record InstanceVariableDto(
    long Id,
    string VariableName,
    int? SourceFlowId,
    string? SetBy,
    JsonElement Value,
    DateTimeOffset SetAt);

/// <summary>
/// Represents a single step in the execution history of a workflow instance.
/// </summary>
/// <param name="Id">The unique database ID of the history record.</param>
/// <param name="TokenId">The execution token correlated to this history row.</param>
/// <param name="UserTaskId">The user-task work item correlated to this history row.</param>
/// <param name="MultiInstanceExecutionId">The multi-instance execution correlated to this row.</param>
/// <param name="ItemIndex">The zero-based multi-instance item index.</param>
/// <param name="SequenceFlowId">Optional. The ID of the sequence flow taken during this step.</param>
/// <param name="FromNodeId">The ID of the source flow node transitioned from.</param>
/// <param name="ToNodeId">The ID of the destination flow node transitioned to.</param>
/// <param name="PerformedBy">The username of the actor who triggered or performed this step.</param>
/// <param name="Payload">Optional. The input payload or variables submitted during the transition.</param>
/// <param name="Note">Optional. Execution notes describing internal hops or transition kinds.</param>
/// <param name="PerformedAt">The timestamp when this step was executed.</param>
public sealed record InstanceHistoryDto(
    long Id,
    long? TokenId,
    long? UserTaskId,
    long? MultiInstanceExecutionId,
    int? ItemIndex,
    int? SequenceFlowId,
    int FromNodeId,
    int ToNodeId,
    string? PerformedBy,
    Dictionary<string, JsonElement>? Payload,
    string? Note,
    DateTimeOffset PerformedAt);

/// <summary>
/// Generic wrapper representing a paged list of items.
/// </summary>
/// <typeparam name="T">The type of items in the page.</typeparam>
/// <param name="Items">The collection of items on the current page.</param>
/// <param name="Page">The 1-based page index.</param>
/// <param name="PageSize">The maximum number of items in the page.</param>
/// <param name="TotalCount">The total number of matching items across all pages.</param>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount);

/// <summary>
/// Represents a user task in the inbox of an actor.
/// </summary>
/// <param name="InstanceId">The database ID of the workflow instance.</param>
/// <param name="UserTaskId">The exact work-item ID represented by this inbox row.</param>
/// <param name="MultiInstanceExecutionId">The owning multi-instance execution, when applicable.</param>
/// <param name="ItemIndex">The zero-based multi-instance item index.</param>
/// <param name="ItemValue">The snapshotted collection item.</param>
/// <param name="Assignee">The snapshotted direct username assignment, when present.</param>
/// <param name="MultiInstance">Aggregate progress for the owning multi-instance execution.</param>
/// <param name="WorkflowId">The database ID of the workflow version.</param>
/// <param name="WorkflowName">The name of the workflow.</param>
/// <param name="CurrentNodeId">The ID of the current userTask flow node.</param>
/// <param name="CurrentNodeName">The name of the current userTask flow node.</param>
/// <param name="CurrentNodeExternalId">The user-defined external ID of the current flow node.</param>
/// <param name="NodeRoles">The roles allowed to claim/act on this userTask.</param>
/// <param name="RequiresClaim">Indicates whether the task must be claimed before taking any actions.</param>
/// <param name="ClaimedBy">The username of the actor who has currently claimed the task.</param>
/// <param name="ClaimedByMe">True if the current caller is the one who claimed this task.</param>
/// <param name="CanClaim">True if the current caller is authorized to claim this task based on role constraints.</param>
/// <param name="CanAct">True if the current caller is authorized to act on this task.</param>
/// <param name="CreatedAt">The timestamp when the instance was started.</param>
/// <param name="UpdatedAt">The timestamp when the instance was last updated.</param>
public sealed record InboxItemDto(
    long InstanceId,
    long UserTaskId,
    long? MultiInstanceExecutionId,
    int? ItemIndex,
    JsonElement? ItemValue,
    string? Assignee,
    MultiInstanceProgressDto? MultiInstance,
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
