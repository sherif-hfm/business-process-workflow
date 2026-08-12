using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

/// <summary>
/// Immutable, database-executable projection of one authored user-task inbox
/// visibility condition. Runtime work items snapshot this row by id so a later
/// workflow-version switch cannot silently change an open task's access rule.
/// </summary>
public sealed class WorkflowDefinitionUserTaskConditionEntity
{
    public long Id { get; set; }

    public long WorkflowDefinitionId { get; set; }

    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }

    public int NodeId { get; set; }

    public string NodeName { get; set; } = string.Empty;

    public string? NodeExternalId { get; set; }

    public int ProgramVersion { get; set; }

    public JsonDocument ProgramJson { get; set; } = JsonDocument.Parse("{}");

    public List<string> VariableNames { get; set; } = [];

    public List<string> ExternalReferences { get; set; } = [];

    public string SemanticFingerprint { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<UserTaskEntity> UserTasks { get; set; } = [];
}
