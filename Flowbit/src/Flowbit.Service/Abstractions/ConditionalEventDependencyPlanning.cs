using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// Validates conditional-event expressions and produces their immutable variable
/// dependency plan from a workflow definition.
/// </summary>
public interface IConditionalEventDefinitionAnalyzer
{
    ConditionalEventDependencyPlan Analyze(WorkflowModel definition);
}

/// <summary>
/// Bounded process-local cache for immutable per-definition dependency plans.
/// </summary>
public interface IConditionalEventDependencyPlanCache
{
    ConditionalEventDependencyPlan GetOrAdd(
        long workflowDefinitionId,
        WorkflowModel definition);

    bool TryGet(
        long workflowDefinitionId,
        out ConditionalEventDependencyPlan plan);

    void Remove(long workflowDefinitionId);
}
