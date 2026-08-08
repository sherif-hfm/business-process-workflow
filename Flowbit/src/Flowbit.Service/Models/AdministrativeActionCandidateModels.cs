using System.Text.Json;

namespace Flowbit.Service.Models;

public sealed record AdministrativeActionPositionKey(
    string PositionKind,
    long PositionId);

public sealed record AdministrativeActionCandidateQuery
{
    public required long WorkflowDefinitionId { get; init; }
    public required int SourceNodeId { get; init; }
    public string? PositionKind { get; init; }
    public long? PositionId { get; init; }
    public long? InstanceId { get; init; }
    public string? BusinessKey { get; init; }
    public IReadOnlyCollection<AdministrativeActionPositionKey>? Positions { get; init; }
    public IReadOnlyCollection<AdministrativeActionPositionKey>? ExcludedPositions { get; init; }
    public VariableFilterExpression? VariableFilter { get; init; }
    public bool IncludeVariables { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public sealed record AdministrativeTimerBoundaryStateRecord(
    int BoundaryNodeId,
    long? TimerSubscriptionId,
    long? TimerJobId,
    string? Status,
    DateTimeOffset? NextDueAt,
    long? Occurrence,
    DateTimeOffset? UpdatedAt);

public sealed record AdministrativeActionCandidateRecord(
    string PositionKind,
    long PositionId,
    long? UserTaskId,
    long? MultiInstanceExecutionId,
    long InstanceId,
    long TokenId,
    Guid TokenActivationId,
    long WorkflowDefinitionId,
    string WorkflowKey,
    string? BusinessKey,
    int NodeId,
    string NodeName,
    string? NodeExternalId,
    DateTimeOffset PositionUpdatedAt,
    int AffectedTaskCount,
    IReadOnlyList<AdministrativeTimerBoundaryStateRecord> TimerBoundaries,
    IReadOnlyDictionary<string, JsonElement>? Variables);
