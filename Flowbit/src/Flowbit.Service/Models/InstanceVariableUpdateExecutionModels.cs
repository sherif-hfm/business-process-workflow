using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Models;

public sealed record InstanceVariableUpdateExecutionRequest(
    long InstanceId,
    string ExpectedWorkflowKey,
    IReadOnlyList<InstanceVariableWriteDto> Variables,
    string? Reason,
    long BatchId,
    long BatchItemId);

public sealed record InstanceVariableUpdateExecutionOutcome(
    UpdateInstanceVariablesResultDto? Result,
    string? SkipCode,
    string? SkipDescription)
{
    public bool Succeeded => Result is not null;

    public bool Skipped => Result is null && SkipCode is not null;
}

public static class InstanceVariableUpdateSkipCodes
{
    public const string InstanceNotFound = "instance_not_found";
    public const string InstanceNotRunning = "instance_not_running";
    public const string WorkflowFamilyChanged = "workflow_family_changed";
}
