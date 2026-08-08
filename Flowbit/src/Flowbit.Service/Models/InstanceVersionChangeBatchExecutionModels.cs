using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Models;

public sealed record InstanceVersionChangeBatchExecutionRequest(
    long BatchId,
    long BatchItemId,
    long InstanceId,
    long ExpectedSourceWorkflowId,
    DateTimeOffset ExpectedUpdatedAt,
    long TargetWorkflowId,
    string Reason);

public sealed record InstanceVersionChangeBatchExecutionOutcome(
    bool Succeeded,
    long? VersionChangeId,
    string? Code,
    string? Description,
    IReadOnlyList<InstanceVersionChangeIssueDto> Blockers,
    IReadOnlyList<InstanceVersionChangeIssueDto> Warnings);
