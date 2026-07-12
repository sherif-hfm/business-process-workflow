using System.Text.Json;

namespace WorkflowEngine.Infrastructure.Entities;

public sealed class UserTaskEntity
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public WorkflowInstanceEntity? Instance { get; set; }
    public long TokenId { get; set; }
    public ExecutionTokenEntity? Token { get; set; }
    public int NodeId { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public string? NodeExternalId { get; set; }
    public List<string> Roles { get; set; } = [];
    public bool RequiresClaim { get; set; }
    public string Status { get; set; } = UserTaskStatuses.Active;
    public string? ClaimedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public long? MultiInstanceExecutionId { get; set; }
    public MultiInstanceExecutionEntity? MultiInstanceExecution { get; set; }
    public int? ItemIndex { get; set; }
    public JsonDocument? ItemValueJson { get; set; }
    public string? Assignee { get; set; }
    public int? SelectedFlowId { get; set; }
    public JsonDocument? ResultJson { get; set; }
    public string? CompletedBy { get; set; }
}

public static class UserTaskStatuses
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}
