namespace Flowbit.Service.Abstractions;

/// <summary>
/// Records instance-variable names written by the current scoped unit of work.
/// The tracker is only a transaction-local optimization: durable variable and
/// execution state remains in PostgreSQL.
/// </summary>
public interface IInstanceVariableMutationTracker
{
    void Record(long instanceId, string variableName);

    IReadOnlyCollection<string> Consume(long instanceId);

    bool HasPending(long instanceId);

    void Clear(long instanceId);
}
