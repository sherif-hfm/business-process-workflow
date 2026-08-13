using Flowbit.Service.Models;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// Evaluates conditional waits after a complete variable-write batch while the
/// caller still owns the instance transaction and row lock.
/// </summary>
public interface IConditionalEventRuntimeCoordinator
{
    Task ResumeForVariableChangesAsync(
        WorkflowInstanceRecord instance,
        ActorContext actor,
        CancellationToken cancellationToken);
}
