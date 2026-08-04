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

    public long UserTaskId { get; set; }

    public UserTaskEntity? UserTask { get; set; }

    public long TokenId { get; set; }

    public ExecutionTokenEntity? Token { get; set; }

    public long SourceWorkflowDefinitionId { get; set; }

    public WorkflowDefinitionEntity? SourceWorkflowDefinition { get; set; }

    public long TargetWorkflowDefinitionId { get; set; }

    public WorkflowDefinitionEntity? TargetWorkflowDefinition { get; set; }

    public DateTimeOffset CapturedInstanceUpdatedAt { get; set; }

    public DateTimeOffset CapturedUserTaskUpdatedAt { get; set; }

    public string Status { get; set; } = AdministrativeActionBatchItemStatuses.Preparing;

    public JsonDocument? IssuesJson { get; set; }

    public JsonDocument? ResultJson { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorDescription { get; set; }

    public long? NewUserTaskId { get; set; }

    public UserTaskEntity? NewUserTask { get; set; }

    public long? VersionChangeAuditId { get; set; }

    public WorkflowInstanceVersionChangeEntity? VersionChangeAudit { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? PreparedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
