using System.Collections.Immutable;

namespace Flowbit.Service.Models;

/// <summary>
/// Immutable definition-time metadata for one intermediate conditional catch event.
/// </summary>
public sealed record ConditionalEventPlanEntry(
    int NodeId,
    string Condition,
    string DeliveryMode,
    ImmutableArray<string> Dependencies);

/// <summary>
/// Immutable conditional-event plan for one workflow definition. The inverse
/// index lets a variable mutation select candidate event nodes without scanning
/// every conditional expression in the definition.
/// </summary>
public sealed class ConditionalEventDependencyPlan
{
    public static ConditionalEventDependencyPlan Empty { get; } = new(
        ImmutableDictionary<int, ConditionalEventPlanEntry>.Empty,
        ImmutableDictionary.Create<string, ImmutableArray<int>>(
            StringComparer.OrdinalIgnoreCase));

    public ConditionalEventDependencyPlan(
        ImmutableDictionary<int, ConditionalEventPlanEntry> eventsByNodeId,
        ImmutableDictionary<string, ImmutableArray<int>> nodeIdsByVariable)
    {
        EventsByNodeId = eventsByNodeId
            ?? throw new ArgumentNullException(nameof(eventsByNodeId));
        NodeIdsByVariable = nodeIdsByVariable
            ?? throw new ArgumentNullException(nameof(nodeIdsByVariable));
    }

    public ImmutableDictionary<int, ConditionalEventPlanEntry> EventsByNodeId { get; }

    public ImmutableDictionary<string, ImmutableArray<int>> NodeIdsByVariable { get; }
}
