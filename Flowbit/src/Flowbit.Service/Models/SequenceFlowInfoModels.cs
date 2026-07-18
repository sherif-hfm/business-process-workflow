using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flowbit.Service.Models;

/// <summary>
/// The fixed-size runtime projection for one sequence flow. The action view
/// describes explicit actor selections; the traversal view describes token
/// movement along the flow.
/// </summary>
public sealed record SequenceFlowRuntimeSummary(
    [property: JsonPropertyName("flowId")] int FlowId,
    [property: JsonPropertyName("actions")] SequenceFlowRuntimeView Actions,
    [property: JsonPropertyName("traversals")] SequenceFlowRuntimeView Traversals)
{
    public static SequenceFlowRuntimeSummary Empty(int flowId) => new(
        flowId,
        SequenceFlowRuntimeView.Empty,
        SequenceFlowRuntimeView.Empty);

    /// <summary>
    /// Returns the canonical camel-case JSON shape exposed by
    /// <c>FlowInfo(id, 'all')</c> and the JavaScript execution context.
    /// </summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this);
}

public sealed record SequenceFlowRuntimeView(
    [property: JsonPropertyName("count")] long Count,
    [property: JsonPropertyName("last")] SequenceFlowLastOccurrence? Last)
{
    public static SequenceFlowRuntimeView Empty { get; } = new(0, null);
}

public sealed record SequenceFlowLastOccurrence(
    [property: JsonPropertyName("user")] string? User,
    [property: JsonPropertyName("userRoles")] IReadOnlyList<string> UserRoles,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("values")] JsonElement? Values);

/// <summary>
/// A transition-local, mutable snapshot of the persisted summaries. It keeps
/// definition flow ids separately from used-flow summaries so callers can
/// distinguish a known but unused flow (canonical empty summary) from an
/// unknown flow id. The engine may update it after staging an occurrence so
/// downstream expressions in the same transaction observe that occurrence.
/// </summary>
public sealed class SequenceFlowInfoSnapshot
{
    private readonly HashSet<int> _knownFlowIds;
    private readonly Dictionary<int, SequenceFlowRuntimeSummary> _summaries;
    private readonly ReadOnlyDictionary<int, SequenceFlowRuntimeSummary> _readOnlySummaries;

    public SequenceFlowInfoSnapshot(
        IEnumerable<int> knownFlowIds,
        IEnumerable<SequenceFlowRuntimeSummary>? summaries = null)
    {
        ArgumentNullException.ThrowIfNull(knownFlowIds);

        _knownFlowIds = knownFlowIds.ToHashSet();
        _summaries = new Dictionary<int, SequenceFlowRuntimeSummary>();
        _readOnlySummaries = new ReadOnlyDictionary<int, SequenceFlowRuntimeSummary>(_summaries);

        foreach (var summary in summaries ?? [])
        {
            SetSummary(summary);
        }
    }

    public IReadOnlySet<int> KnownFlowIds => _knownFlowIds;

    /// <summary>
    /// Persisted or transactionally updated non-empty summaries. Use
    /// <see cref="TryGetSummary"/> when canonical empty summaries are required.
    /// </summary>
    public IReadOnlyDictionary<int, SequenceFlowRuntimeSummary> Summaries => _readOnlySummaries;

    public bool TryGetSummary(int flowId, out SequenceFlowRuntimeSummary summary)
    {
        if (!_knownFlowIds.Contains(flowId))
        {
            summary = null!;
            return false;
        }

        if (!_summaries.TryGetValue(flowId, out summary!))
        {
            summary = SequenceFlowRuntimeSummary.Empty(flowId);
        }

        return true;
    }

    public SequenceFlowRuntimeSummary GetSummary(int flowId)
    {
        if (TryGetSummary(flowId, out var summary))
        {
            return summary;
        }

        throw new KeyNotFoundException($"Sequence flow #{flowId} is not part of this workflow definition.");
    }

    public void SetSummary(SequenceFlowRuntimeSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!_knownFlowIds.Contains(summary.FlowId))
        {
            throw new ArgumentOutOfRangeException(
                nameof(summary),
                summary.FlowId,
                "The summary flow id is not part of this workflow definition.");
        }

        _summaries[summary.FlowId] = summary;
    }
}
