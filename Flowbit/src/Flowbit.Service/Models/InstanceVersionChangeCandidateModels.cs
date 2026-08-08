namespace Flowbit.Service.Models;

public sealed record InstanceVersionChangeCandidateQuery
{
    public required long SourceWorkflowDefinitionId { get; init; }
    public long? InstanceId { get; init; }
    public IReadOnlyCollection<long>? InstanceIds { get; init; }
    public string? BusinessKey { get; init; }
    public int? NodeId { get; init; }
    public string? NodeExternalId { get; init; }
    public VariableFilterExpression? VariableFilter { get; init; }
    public bool IncludeVariables { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public sealed record FrozenInstanceVersionChangeCandidate(
    long InstanceId,
    long WorkflowDefinitionId,
    DateTimeOffset UpdatedAt);
