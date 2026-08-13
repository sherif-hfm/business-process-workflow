using System.Collections.Concurrent;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// A bounded FIFO cache. Workflow definitions are immutable, so their analyzed
/// plans never require refresh while a definition id remains cached.
/// </summary>
public sealed class ConditionalEventDependencyPlanCache(
    IConditionalEventDefinitionAnalyzer analyzer)
    : IConditionalEventDependencyPlanCache
{
    public const int MaximumEntries = 512;

    private readonly ConcurrentDictionary<long, CacheEntry> entries = new();
    private readonly ConcurrentQueue<(long Id, CacheEntry Entry)> insertionOrder = new();

    public ConditionalEventDependencyPlan GetOrAdd(
        long workflowDefinitionId,
        WorkflowModel definition)
    {
        if (workflowDefinitionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workflowDefinitionId),
                "Workflow definition id must be positive.");
        }
        ArgumentNullException.ThrowIfNull(definition);

        while (true)
        {
            if (entries.TryGetValue(workflowDefinitionId, out var existing))
            {
                return existing.Plan.Value;
            }

            var added = new CacheEntry(new Lazy<ConditionalEventDependencyPlan>(
                () => analyzer.Analyze(definition),
                LazyThreadSafetyMode.ExecutionAndPublication));
            if (!entries.TryAdd(workflowDefinitionId, added))
            {
                continue;
            }

            insertionOrder.Enqueue((workflowDefinitionId, added));
            Trim();
            try
            {
                return added.Plan.Value;
            }
            catch
            {
                ((ICollection<KeyValuePair<long, CacheEntry>>)entries).Remove(
                    new KeyValuePair<long, CacheEntry>(workflowDefinitionId, added));
                throw;
            }
        }
    }

    public bool TryGet(
        long workflowDefinitionId,
        out ConditionalEventDependencyPlan plan)
    {
        if (entries.TryGetValue(workflowDefinitionId, out var cached))
        {
            plan = cached.Plan.Value;
            return true;
        }

        plan = ConditionalEventDependencyPlan.Empty;
        return false;
    }

    public void Remove(long workflowDefinitionId) =>
        entries.TryRemove(workflowDefinitionId, out _);

    private void Trim()
    {
        while (entries.Count > MaximumEntries
            && insertionOrder.TryDequeue(out var oldest))
        {
            ((ICollection<KeyValuePair<long, CacheEntry>>)entries).Remove(
                new KeyValuePair<long, CacheEntry>(oldest.Id, oldest.Entry));
        }
    }

    private sealed record CacheEntry(Lazy<ConditionalEventDependencyPlan> Plan);
}
