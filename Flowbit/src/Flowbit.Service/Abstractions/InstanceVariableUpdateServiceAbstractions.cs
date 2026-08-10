using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// Performs a synchronous, administrator-authored variable update for one
/// running workflow instance.
/// </summary>
public interface IInstanceVariableUpdateService
{
    Task<UpdateInstanceVariablesResultDto?> UpdateAsync(
        long instanceId,
        UpdateInstanceVariablesRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reusable atomic executor used by the durable batch processor. A successful
/// call persists the audit, every variable history row, and the batch-item
/// success transition in one transaction.
/// </summary>
public interface IInstanceVariableUpdateExecutor
{
    Task<InstanceVariableUpdateExecutionOutcome> ExecuteAsync(
        InstanceVariableUpdateExecutionRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);
}
