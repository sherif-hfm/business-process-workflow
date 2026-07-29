namespace Flowbit.Service.Models;

/// <summary>
/// Immutable input supplied while evaluating Complex gateway activation and
/// outgoing-flow expressions. Counts are scoped to the current gateway cycle.
/// </summary>
public sealed record GatewayConditionContext(
    IReadOnlyDictionary<int, int> IncomingCounts,
    bool WaitingForStart)
{
    public int TotalIncomingCount => IncomingCounts.Values.Sum();
}
