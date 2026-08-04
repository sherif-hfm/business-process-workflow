using System.Text.Json;

namespace Flowbit.Service.Models;

public sealed record AdministrativeActionCandidateQuery
{
    public required IReadOnlyList<AdministrativeActionFlowTarget> Targets { get; init; }
    public long? UserTaskId { get; init; }
    public long? InstanceId { get; init; }
    public string? BusinessKey { get; init; }
    public IReadOnlyCollection<long>? UserTaskIds { get; init; }
    public VariableFilterExpression? VariableFilter { get; init; }
    public bool IncludeVariables { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public sealed record AdministrativeActionFlowTarget(
    long WorkflowDefinitionId,
    int FlowId,
    int SourceNodeId);

public sealed record AdministrativeActionCandidateRecord(
    long UserTaskId,
    long InstanceId,
    long TokenId,
    long WorkflowDefinitionId,
    int FlowId,
    string WorkflowKey,
    string? BusinessKey,
    int NodeId,
    string NodeName,
    string? NodeExternalId,
    DateTimeOffset InstanceUpdatedAt,
    DateTimeOffset UserTaskUpdatedAt,
    IReadOnlyDictionary<string, JsonElement>? Variables);
