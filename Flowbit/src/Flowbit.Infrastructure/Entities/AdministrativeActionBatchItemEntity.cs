using System.Text.Json;
using Flowbit.Service.Models;

namespace Flowbit.Infrastructure.Entities;

public sealed class AdministrativeActionBatchItemEntity
{
    public long Id { get; set; }

    public long BatchId { get; set; }

    public AdministrativeActionBatchEntity? Batch { get; set; }

    public long InstanceId { get; set; }

    public WorkflowInstanceEntity? Instance { get; set; }

    public string PositionKind { get; set; } = string.Empty;

    public long? UserTaskId { get; set; }

    public UserTaskEntity? UserTask { get; set; }

    public long? MultiInstanceExecutionId { get; set; }

    public MultiInstanceExecutionEntity? MultiInstanceExecution { get; set; }

    public long TokenId { get; set; }

    public ExecutionTokenEntity? Token { get; set; }

    public Guid TokenActivationId { get; set; }

    public long WorkflowDefinitionId { get; set; }

    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }

    public int FlowId { get; set; }

    public int SourceNodeId { get; set; }

    public DateTimeOffset CapturedPositionUpdatedAt { get; set; }

    public long? TimerSubscriptionId { get; set; }

    public TimerSubscriptionEntity? TimerSubscription { get; set; }

    public long? TimerJobId { get; set; }

    public long? CapturedTimerOccurrence { get; set; }

    public string? CapturedTimerStatus { get; set; }

    public DateTimeOffset? CapturedTimerSubscriptionUpdatedAt { get; set; }

    public int AffectedTaskCount { get; set; }

    public string Status { get; set; } = AdministrativeActionBatchItemStatuses.Preparing;

    public JsonDocument? IssuesJson { get; set; }

    public JsonDocument? ResultJson { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorDescription { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? PreparedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
