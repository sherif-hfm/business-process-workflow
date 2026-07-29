using System.Collections.Concurrent;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Caches immutable, per-definition reachability masks used by Inclusive and
/// Complex gateways. A mask answers which incoming flows of a merge can still
/// be reached from a token's current node without first crossing that merge.
/// </summary>
internal static class GatewayTopologyCache
{
    private const int MaximumEntries = 256;
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<long, CacheEntry> Entries = new();

    public static GatewayTopologyIndex Get(long definitionId, WorkflowModel definition)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = Entries.AddOrUpdate(
            definitionId,
            _ => new CacheEntry(new GatewayTopologyIndex(definition), now),
            (_, existing) => now - existing.LastAccess <= EntryLifetime
                ? existing with { LastAccess = now }
                : new CacheEntry(new GatewayTopologyIndex(definition), now));
        if (Entries.Count > MaximumEntries)
        {
            foreach (var stale in Entries
                         .OrderBy(pair => pair.Value.LastAccess)
                         .Take(Entries.Count - MaximumEntries))
            {
                Entries.TryRemove(stale.Key, out _);
            }
        }
        return entry.Index;
    }

    private sealed record CacheEntry(GatewayTopologyIndex Index, DateTimeOffset LastAccess);
}

internal sealed class GatewayTopologyIndex
{
    private readonly IReadOnlyDictionary<int, IReadOnlyList<int>> _predecessors;
    private readonly IReadOnlyDictionary<int, FlowNodeModel> _nodes;
    private readonly IReadOnlyDictionary<int, SequenceFlowModel> _flows;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<SequenceFlowModel>> _incoming;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<SequenceFlowModel>> _outgoing;
    private readonly ConcurrentDictionary<int, MergeReachability> _mergePlans = new();

    public GatewayTopologyIndex(WorkflowModel definition)
    {
        _nodes = definition.FlowNodes.ToDictionary(node => node.Id);
        _flows = definition.SequenceFlows.ToDictionary(flow => flow.Id);
        var incoming = definition.FlowNodes.ToDictionary(
            node => node.Id,
            _ => new List<SequenceFlowModel>());
        var outgoing = definition.FlowNodes.ToDictionary(
            node => node.Id,
            _ => new List<SequenceFlowModel>());
        var predecessors = definition.FlowNodes.ToDictionary(
            node => node.Id,
            _ => new List<int>());
        foreach (var flow in definition.SequenceFlows)
        {
            if (incoming.TryGetValue(flow.TargetRef, out var incomingFlows))
            {
                incomingFlows.Add(flow);
            }
            if (outgoing.TryGetValue(flow.SourceRef, out var outgoingFlows))
            {
                outgoingFlows.Add(flow);
            }
            if (predecessors.TryGetValue(flow.TargetRef, out var values))
            {
                values.Add(flow.SourceRef);
            }
        }
        // Boundary entry is an implicit host -> boundary edge.
        foreach (var boundary in definition.FlowNodes.Where(node =>
                     BpmnFlowNodeTypes.IsErrorBoundary(node.Type)
                     && node.AttachedToRef is not null))
        {
            predecessors[boundary.Id].Add(boundary.AttachedToRef!.Value);
        }
        _predecessors = predecessors.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<int>)pair.Value.Distinct().OrderBy(id => id).ToArray());
        _incoming = incoming.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SequenceFlowModel>)pair.Value
                .OrderBy(flow => flow.Id)
                .ToArray());
        _outgoing = outgoing.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SequenceFlowModel>)pair.Value
                .OrderBy(flow => flow.Id)
                .ToArray());
    }

    public IReadOnlyList<SequenceFlowModel> IncomingFlows(int nodeId) =>
        _incoming.GetValueOrDefault(nodeId) ?? [];

    public IReadOnlyList<SequenceFlowModel> OutgoingFlows(int nodeId) =>
        _outgoing.GetValueOrDefault(nodeId) ?? [];

    public bool CanReachAnyInput(
        int mergeNodeId,
        int fromNodeId,
        IReadOnlyCollection<int> incomingFlowIds)
    {
        if (incomingFlowIds.Count == 0 || fromNodeId == mergeNodeId)
        {
            return false;
        }
        var plan = _mergePlans.GetOrAdd(mergeNodeId, BuildPlan);
        if (!plan.NodeMasks.TryGetValue(fromNodeId, out var nodeMask))
        {
            return false;
        }
        foreach (var flowId in incomingFlowIds)
        {
            if (plan.FlowBits.TryGetValue(flowId, out var bit)
                && (nodeMask[bit / 64] & (1UL << (bit % 64))) != 0)
            {
                return true;
            }
        }
        return false;
    }

    private MergeReachability BuildPlan(int mergeNodeId)
    {
        var incoming = IncomingFlows(mergeNodeId);
        var flowBits = incoming
            .Select((flow, index) => (flow.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index);
        var wordCount = Math.Max(1, (incoming.Count + 63) / 64);
        var masks = _nodes.Keys.ToDictionary(nodeId => nodeId, _ => new ulong[wordCount]);

        for (var bit = 0; bit < incoming.Count; bit++)
        {
            var queue = new Queue<int>();
            var visited = new HashSet<int>();
            queue.Enqueue(incoming[bit].SourceRef);
            while (queue.Count > 0)
            {
                var nodeId = queue.Dequeue();
                if (nodeId == mergeNodeId || !visited.Add(nodeId))
                {
                    continue;
                }
                masks[nodeId][bit / 64] |= 1UL << (bit % 64);
                if (_predecessors.TryGetValue(nodeId, out var predecessors))
                {
                    foreach (var predecessor in predecessors)
                    {
                        queue.Enqueue(predecessor);
                    }
                }
            }
        }

        return new MergeReachability(flowBits, masks);
    }

    private sealed record MergeReachability(
        IReadOnlyDictionary<int, int> FlowBits,
        IReadOnlyDictionary<int, ulong[]> NodeMasks);
}
