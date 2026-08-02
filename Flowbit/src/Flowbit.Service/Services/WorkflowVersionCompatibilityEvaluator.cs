using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Performs the deterministic, side-effect-free compatibility checks shared by
/// workflow-version preview and the authoritative in-transaction recheck.
/// </summary>
public static class WorkflowVersionCompatibilityEvaluator
{
    private static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    public static WorkflowVersionCompatibilityResult Evaluate(
        WorkflowVersionCompatibilityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Instance);
        ArgumentNullException.ThrowIfNull(context.SourceDefinition);
        ArgumentNullException.ThrowIfNull(context.TargetDefinition);

        var blockers = new List<WorkflowVersionCompatibilityIssue>();
        var warnings = new List<WorkflowVersionCompatibilityIssue>();
        var source = context.SourceDefinition.Definition;
        var target = context.TargetDefinition.Definition;
        var sourceNodes = IndexNodes(source);
        var targetNodes = IndexNodes(target);

        ValidateEnvelope(context, blockers);
        var activeNodeIds = ValidateActiveNodes(
            context,
            sourceNodes,
            targetNodes,
            blockers);
        ValidateOpenUserTasks(
            context,
            source,
            target,
            sourceNodes,
            targetNodes,
            blockers);
        ValidateActiveMessageCatches(
            activeNodeIds,
            sourceNodes,
            targetNodes,
            warnings);
        ValidateActiveMultiInstance(
            context,
            source,
            target,
            sourceNodes,
            targetNodes,
            blockers);

        if (RequiresExactTopology(context))
        {
            ValidateExactTopology(source, target, sourceNodes, targetNodes, blockers);
        }

        ValidateCurrentVariables(context, target, blockers, warnings);
        ValidateObservedFlows(context, source, target, blockers);
        ValidateFlowInfoHistory(context, source, target, blockers);
        ValidateOpenJobs(context, source, target, sourceNodes, targetNodes, blockers);
        ValidateOpenTimers(context, source, target, sourceNodes, targetNodes, blockers);

        return new WorkflowVersionCompatibilityResult(
            SortIssues(blockers),
            SortIssues(warnings));
    }

    private static void ValidateEnvelope(
        WorkflowVersionCompatibilityContext context,
        ICollection<WorkflowVersionCompatibilityIssue> blockers)
    {
        if (!string.Equals(
                context.Instance.Status,
                WorkflowInstanceStatuses.Running,
                StringComparison.Ordinal))
        {
            blockers.Add(Issue(
                WorkflowVersionCompatibilityCodes.InstanceNotRunning,
                $"Instance #{context.Instance.Id} is '{context.Instance.Status}', not running.",
                runtimeId: context.Instance.Id));
        }

        if (context.Instance.WorkflowDefinitionId != context.SourceDefinition.Id
            || !string.Equals(
                context.Instance.WorkflowKey,
                context.SourceDefinition.WorkflowKey,
                StringComparison.Ordinal))
        {
            blockers.Add(Issue(
                WorkflowVersionCompatibilityCodes.SourceDefinitionMismatch,
                $"Source definition #{context.SourceDefinition.Id} is not the current definition of instance #{context.Instance.Id}.",
                runtimeId: context.Instance.Id));
        }

        if (context.SourceDefinition.Id == context.TargetDefinition.Id)
        {
            blockers.Add(Issue(
                WorkflowVersionCompatibilityCodes.SameDefinition,
                $"Definition #{context.TargetDefinition.Id} is already assigned to the instance."));
        }

        if (!string.Equals(
                context.SourceDefinition.WorkflowKey,
                context.TargetDefinition.WorkflowKey,
                StringComparison.Ordinal)
            || !string.Equals(
                context.Instance.WorkflowKey,
                context.TargetDefinition.WorkflowKey,
                StringComparison.Ordinal))
        {
            blockers.Add(Issue(
                WorkflowVersionCompatibilityCodes.WorkflowKeyMismatch,
                "Source and target definitions do not belong to the instance's exact workflow key."));
        }

        if (!context.TargetDefinition.IsPublished)
        {
            blockers.Add(Issue(
                WorkflowVersionCompatibilityCodes.TargetNotPublished,
                $"Target definition #{context.TargetDefinition.Id} is not published."));
        }
    }

    private static IReadOnlySet<int> ValidateActiveNodes(
        WorkflowVersionCompatibilityContext context,
        IReadOnlyDictionary<int, FlowNodeModel> sourceNodes,
        IReadOnlyDictionary<int, FlowNodeModel> targetNodes,
        ICollection<WorkflowVersionCompatibilityIssue> blockers)
    {
        var activeNodeIds = context.ActiveTokens.Select(token => token.NodeId)
            .Concat(context.OpenUserTasks.Select(task => task.NodeId))
            .Concat(context.ActiveMultiInstanceExecutions.Select(execution => execution.NodeId))
            .Concat(context.ActiveGatewayExecutions.Select(execution => execution.GatewayNodeId))
            .Concat(context.ActiveComplexGatewayStates.Select(state => state.GatewayNodeId))
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        foreach (var nodeId in activeNodeIds)
        {
            if (!sourceNodes.TryGetValue(nodeId, out var sourceNode))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.ActiveNodeMissing,
                    $"Active node #{nodeId} is missing from the source definition.",
                    nodeId));
                continue;
            }

            if (!targetNodes.TryGetValue(nodeId, out var targetNode))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.ActiveNodeMissing,
                    $"Active node #{nodeId} is missing from the target definition.",
                    nodeId));
                continue;
            }

            if (!string.Equals(sourceNode.Type, targetNode.Type, StringComparison.Ordinal))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.ActiveNodeTypeChanged,
                    $"Active node #{nodeId} changes type from '{sourceNode.Type}' to '{targetNode.Type}'.",
                    nodeId));
            }

            if (!string.Equals(sourceNode.ExternalId, targetNode.ExternalId, StringComparison.Ordinal))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.ActiveNodeExternalIdChanged,
                    $"Active node #{nodeId} changes its external id.",
                    nodeId));
            }
        }

        return activeNodeIds.ToHashSet();
    }

    private static void ValidateOpenUserTasks(
        WorkflowVersionCompatibilityContext context,
        WorkflowModel source,
        WorkflowModel target,
        IReadOnlyDictionary<int, FlowNodeModel> sourceNodes,
        IReadOnlyDictionary<int, FlowNodeModel> targetNodes,
        ICollection<WorkflowVersionCompatibilityIssue> blockers)
    {
        foreach (var nodeId in context.OpenUserTasks
                     .Select(task => task.NodeId)
                     .Distinct()
                     .OrderBy(id => id))
        {
            if (!sourceNodes.TryGetValue(nodeId, out var sourceNode)
                || !targetNodes.TryGetValue(nodeId, out var targetNode)
                || !BpmnFlowNodeTypes.IsUserTask(sourceNode.Type)
                || !BpmnFlowNodeTypes.IsUserTask(targetNode.Type))
            {
                continue;
            }

            if (!string.Equals(
                    UserTaskAccessContract(source, sourceNode),
                    UserTaskAccessContract(target, targetNode),
                    StringComparison.Ordinal))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.UserTaskContractChanged,
                    $"Open user task node #{nodeId} changes its role, claim, assignment, or assignee contract.",
                    nodeId));
            }

            if (sourceNode.MultiInstance is null
                && !string.Equals(
                    AttachedTimerContract(source, nodeId),
                    AttachedTimerContract(target, nodeId),
                    StringComparison.Ordinal))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.AttachedTimerContractChanged,
                    $"Open ordinary user task node #{nodeId} changes its attached timer contract.",
                    nodeId));
            }
        }
    }

    private static void ValidateActiveMessageCatches(
        IEnumerable<int> activeNodeIds,
        IReadOnlyDictionary<int, FlowNodeModel> sourceNodes,
        IReadOnlyDictionary<int, FlowNodeModel> targetNodes,
        ICollection<WorkflowVersionCompatibilityIssue> warnings)
    {
        foreach (var nodeId in activeNodeIds.OrderBy(id => id))
        {
            if (!sourceNodes.TryGetValue(nodeId, out var sourceNode)
                || !targetNodes.TryGetValue(nodeId, out var targetNode)
                || !BpmnFlowNodeTypes.IsMessageCatch(sourceNode.Type)
                || !BpmnFlowNodeTypes.IsMessageCatch(targetNode.Type))
            {
                continue;
            }

            if (!JsonContractEquals(sourceNode.Message, targetNode.Message))
            {
                warnings.Add(Issue(
                    WorkflowVersionCompatibilityCodes.MessageCatchContractChanged,
                    $"Active message catch node #{nodeId} changes its delivery contract; the target contract applies immediately after the version change.",
                    nodeId));
            }
        }
    }

    private static void ValidateActiveMultiInstance(
        WorkflowVersionCompatibilityContext context,
        WorkflowModel source,
        WorkflowModel target,
        IReadOnlyDictionary<int, FlowNodeModel> sourceNodes,
        IReadOnlyDictionary<int, FlowNodeModel> targetNodes,
        ICollection<WorkflowVersionCompatibilityIssue> blockers)
    {
        var nodeIds = context.ActiveMultiInstanceExecutions.Select(execution => execution.NodeId)
            .Concat(context.OpenUserTasks
                .Where(task => task.MultiInstanceExecutionId is not null)
                .Select(task => task.NodeId))
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        foreach (var nodeId in nodeIds)
        {
            if (!sourceNodes.TryGetValue(nodeId, out var sourceNode)
                || !targetNodes.TryGetValue(nodeId, out var targetNode))
            {
                continue;
            }

            if (!JsonContractEquals(sourceNode.MultiInstance, targetNode.MultiInstance)
                || !string.Equals(
                    UserTaskAccessContract(source, sourceNode),
                    UserTaskAccessContract(target, targetNode),
                    StringComparison.Ordinal)
                || !string.Equals(
                    AttachedTimerContract(source, nodeId),
                    AttachedTimerContract(target, nodeId),
                    StringComparison.Ordinal))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.MultiInstanceContractChanged,
                    $"Active multi-instance node #{nodeId} changes its execution configuration.",
                    nodeId));
            }

            if (!string.Equals(
                    OutgoingFlowContract(source, nodeId),
                    OutgoingFlowContract(target, nodeId),
                    StringComparison.Ordinal))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.MultiInstanceOutcomeChanged,
                    $"Active multi-instance node #{nodeId} changes its outcome-flow semantics.",
                    nodeId));
            }

            foreach (var execution in context.ActiveMultiInstanceExecutions
                         .Where(item => item.NodeId == nodeId)
                         .OrderBy(item => item.Id))
            {
                var multi = targetNode.MultiInstance;
                if (multi is null
                    || !string.Equals(execution.Mode, multi.Mode, StringComparison.Ordinal)
                    || !string.Equals(execution.Source, multi.Source, StringComparison.Ordinal)
                    || execution.OnePerActor != multi.OnePerActor
                    || !string.Equals(execution.ResultVariable, multi.ResultVariable, StringComparison.Ordinal))
                {
                    blockers.Add(Issue(
                        WorkflowVersionCompatibilityCodes.MultiInstanceContractChanged,
                        $"Active multi-instance execution #{execution.Id} does not match the target contract for node #{nodeId}.",
                        nodeId,
                        runtimeId: execution.Id));
                }
            }
        }
    }

    private static bool RequiresExactTopology(WorkflowVersionCompatibilityContext context) =>
        context.ActiveTokens.Count > 1
        || context.ActiveGatewayExecutions.Count > 0
        || context.ActiveGatewayBranches.Count > 0
        || context.ActiveComplexGatewayStates.Count > 0;

    private static void ValidateExactTopology(
        WorkflowModel source,
        WorkflowModel target,
        IReadOnlyDictionary<int, FlowNodeModel> sourceNodes,
        IReadOnlyDictionary<int, FlowNodeModel> targetNodes,
        ICollection<WorkflowVersionCompatibilityIssue> blockers)
    {
        foreach (var nodeId in sourceNodes.Keys.Except(targetNodes.Keys).OrderBy(id => id))
        {
            blockers.Add(Issue(
                WorkflowVersionCompatibilityCodes.TopologyNodeMissing,
                $"Target topology removes node #{nodeId} while branch or gateway state is active.",
                nodeId));
        }

        foreach (var nodeId in targetNodes.Keys.Except(sourceNodes.Keys).OrderBy(id => id))
        {
            blockers.Add(Issue(
                WorkflowVersionCompatibilityCodes.TopologyNodeAdded,
                $"Target topology adds node #{nodeId} while branch or gateway state is active.",
                nodeId));
        }

        foreach (var nodeId in sourceNodes.Keys.Intersect(targetNodes.Keys).OrderBy(id => id))
        {
            var sourceNode = sourceNodes[nodeId];
            var targetNode = targetNodes[nodeId];
            if (!string.Equals(sourceNode.Type, targetNode.Type, StringComparison.Ordinal))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.TopologyNodeTypeChanged,
                    $"Target topology changes node #{nodeId} from '{sourceNode.Type}' to '{targetNode.Type}'.",
                    nodeId));
                continue;
            }

            if (BpmnFlowNodeTypes.IsGateway(sourceNode.Type)
                && !string.Equals(
                    GatewayContract(source, sourceNode),
                    GatewayContract(target, targetNode),
                    StringComparison.Ordinal))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.GatewayContractChanged,
                    $"Gateway node #{nodeId} changes its runtime routing contract.",
                    nodeId));
            }

            if (BpmnFlowNodeTypes.IsScopedInterrupt(sourceNode.Type)
                && sourceNode.GatewayRef != targetNode.GatewayRef)
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.ScopedInterruptContractChanged,
                    $"Scoped interrupt node #{nodeId} changes gatewayRef.",
                    nodeId));
            }
        }

        var sourceFlows = IndexFlows(source);
        var targetFlows = IndexFlows(target);
        foreach (var flowId in sourceFlows.Keys.Except(targetFlows.Keys).OrderBy(id => id))
        {
            blockers.Add(Issue(
                WorkflowVersionCompatibilityCodes.TopologyFlowMissing,
                $"Target topology removes sequence flow #{flowId} while branch or gateway state is active.",
                flowId: flowId));
        }

        foreach (var flowId in targetFlows.Keys.Except(sourceFlows.Keys).OrderBy(id => id))
        {
            blockers.Add(Issue(
                WorkflowVersionCompatibilityCodes.TopologyFlowAdded,
                $"Target topology adds sequence flow #{flowId} while branch or gateway state is active.",
                flowId: flowId));
        }

        foreach (var flowId in sourceFlows.Keys.Intersect(targetFlows.Keys).OrderBy(id => id))
        {
            var sourceFlow = sourceFlows[flowId];
            var targetFlow = targetFlows[flowId];
            if (sourceFlow.SourceRef != targetFlow.SourceRef
                || sourceFlow.TargetRef != targetFlow.TargetRef)
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.TopologyFlowEndpointsChanged,
                    $"Sequence flow #{flowId} changes endpoints while branch or gateway state is active.",
                    flowId: flowId));
            }
        }
    }

    private static void ValidateCurrentVariables(
        WorkflowVersionCompatibilityContext context,
        WorkflowModel target,
        ICollection<WorkflowVersionCompatibilityIssue> blockers,
        ICollection<WorkflowVersionCompatibilityIssue> warnings)
    {
        var declarations = TargetVariableDeclarations(target)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Owner, StringComparer.Ordinal)
            .ToArray();
        var declarationsByName = declarations
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var overlay = new Dictionary<string, JsonElement>(
            context.VariableValidationContext,
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in context.CurrentVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            overlay[pair.Key] = pair.Value;
        }

        foreach (var pair in context.CurrentVariables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!declarationsByName.TryGetValue(pair.Key, out var matching))
            {
                warnings.Add(Issue(
                    WorkflowVersionCompatibilityCodes.VariableUndeclaredInTarget,
                    $"Current variable '{pair.Key}' is not declared by the target and will be preserved.",
                    variableName: pair.Key));
                continue;
            }

            foreach (var declaration in matching)
            {
                var isNull = pair.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
                if ((!isNull || !declaration.Nullable)
                    && (isNull || !TypedOutputValueValidator.IsValid(
                        pair.Value,
                        declaration.DataType,
                        declaration.IsArray)))
                {
                    blockers.Add(Issue(
                        WorkflowVersionCompatibilityCodes.VariableTypeIncompatible,
                        $"Current variable '{pair.Key}' does not satisfy target {declaration.Owner} type '{TypedOutputValueValidator.DescribeExpected(declaration.DataType, declaration.IsArray)}'.",
                        variableName: pair.Key));
                    continue;
                }

                if (!isNull
                    && !string.IsNullOrWhiteSpace(declaration.Validation)
                    && !SequenceFlowConditionEvaluator.Evaluate(declaration.Validation, overlay))
                {
                    blockers.Add(Issue(
                        WorkflowVersionCompatibilityCodes.VariableValidationFailed,
                        $"Current variable '{pair.Key}' fails target {declaration.Owner} validation '{declaration.Validation}'.",
                        variableName: pair.Key));
                }
            }
        }
    }

    private static void ValidateObservedFlows(
        WorkflowVersionCompatibilityContext context,
        WorkflowModel source,
        WorkflowModel target,
        ICollection<WorkflowVersionCompatibilityIssue> blockers)
    {
        var sourceFlows = IndexFlows(source);
        var targetFlows = IndexFlows(target);
        var explicitIds = new HashSet<int>();

        foreach (var observed in context.ObservedFlows
                     .OrderBy(flow => flow.FlowId)
                     .ThenBy(flow => flow.SourceNodeId)
                     .ThenBy(flow => flow.TargetNodeId))
        {
            explicitIds.Add(observed.FlowId);
            ValidateObservedFlowIdentity(observed, targetFlows, blockers);
        }

        foreach (var summary in context.FlowSummaries
                     .Where(summary => summary.ActionCount > 0 || summary.TraversalCount > 0)
                     .OrderBy(summary => summary.SequenceFlowId))
        {
            if (explicitIds.Contains(summary.SequenceFlowId))
            {
                continue;
            }

            if (!sourceFlows.TryGetValue(summary.SequenceFlowId, out var sourceFlow))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.SourceDefinitionMismatch,
                    $"Observed sequence flow #{summary.SequenceFlowId} is missing from the source definition.",
                    flowId: summary.SequenceFlowId));
                continue;
            }

            ValidateObservedFlowIdentity(
                new ObservedSequenceFlowIdentity(
                    sourceFlow.Id,
                    sourceFlow.SourceRef,
                    sourceFlow.TargetRef),
                targetFlows,
                blockers);
        }
    }

    private static void ValidateObservedFlowIdentity(
        ObservedSequenceFlowIdentity observed,
        IReadOnlyDictionary<int, SequenceFlowModel> targetFlows,
        ICollection<WorkflowVersionCompatibilityIssue> blockers)
    {
        if (!targetFlows.TryGetValue(observed.FlowId, out var targetFlow))
        {
            blockers.Add(Issue(
                WorkflowVersionCompatibilityCodes.ObservedFlowMissing,
                $"Previously observed sequence flow #{observed.FlowId} is missing from the target definition.",
                flowId: observed.FlowId));
            return;
        }

        if (targetFlow.SourceRef != observed.SourceNodeId
            || targetFlow.TargetRef != observed.TargetNodeId)
        {
            blockers.Add(Issue(
                WorkflowVersionCompatibilityCodes.ObservedFlowEndpointsChanged,
                $"Previously observed sequence flow #{observed.FlowId} changes endpoints.",
                flowId: observed.FlowId));
        }
    }

    private static void ValidateFlowInfoHistory(
        WorkflowVersionCompatibilityContext context,
        WorkflowModel source,
        WorkflowModel target,
        ICollection<WorkflowVersionCompatibilityIssue> blockers)
    {
        var hasCommittedTraversal = context.HasCommittedTraversals
            || context.FlowSummaries.Any(summary => summary.TraversalCount > 0);
        if (hasCommittedTraversal
            && !DefinitionUsesFlowInfo(source)
            && DefinitionUsesFlowInfo(target))
        {
            blockers.Add(Issue(
                WorkflowVersionCompatibilityCodes.FlowInfoHistoryIncomplete,
                "The target introduces FlowInfo after a traversal committed without complete flow-evidence collection."));
        }
    }

    private static void ValidateOpenJobs(
        WorkflowVersionCompatibilityContext context,
        WorkflowModel source,
        WorkflowModel target,
        IReadOnlyDictionary<int, FlowNodeModel> sourceNodes,
        IReadOnlyDictionary<int, FlowNodeModel> targetNodes,
        ICollection<WorkflowVersionCompatibilityIssue> blockers)
    {
        foreach (var job in context.OpenJobs.OrderBy(job => job.Id))
        {
            if (!sourceNodes.TryGetValue(job.NodeId, out var sourceNode)
                || !targetNodes.TryGetValue(job.NodeId, out var targetNode))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.OpenJobNodeMissing,
                    $"Open job #{job.Id} references node #{job.NodeId}, which is absent from a source or target definition.",
                    job.NodeId,
                    runtimeId: job.Id));
                continue;
            }

            var sourceContract = DurableNodeContract(source, sourceNode);
            var targetContract = DurableNodeContract(target, targetNode);
            if (!string.Equals(sourceContract, targetContract, StringComparison.Ordinal)
                || !JobDescriptorMatches(job, targetNode)
                || job.WorkflowDefinitionId != context.SourceDefinition.Id
                || !string.Equals(job.WorkflowKey, context.Instance.WorkflowKey, StringComparison.Ordinal))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.OpenJobContractChanged,
                    $"Open job #{job.Id} is not exactly compatible with target node #{job.NodeId}.",
                    job.NodeId,
                    runtimeId: job.Id));
            }
        }
    }

    private static void ValidateOpenTimers(
        WorkflowVersionCompatibilityContext context,
        WorkflowModel source,
        WorkflowModel target,
        IReadOnlyDictionary<int, FlowNodeModel> sourceNodes,
        IReadOnlyDictionary<int, FlowNodeModel> targetNodes,
        ICollection<WorkflowVersionCompatibilityIssue> blockers)
    {
        foreach (var timer in context.OpenTimers.OrderBy(timer => timer.Id))
        {
            if (!sourceNodes.TryGetValue(timer.TimerNodeId, out var sourceNode)
                || !targetNodes.TryGetValue(timer.TimerNodeId, out var targetNode))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.OpenTimerNodeMissing,
                    $"Open timer subscription #{timer.Id} references node #{timer.TimerNodeId}, which is absent from a source or target definition.",
                    timer.TimerNodeId,
                    runtimeId: timer.Id));
                continue;
            }

            if (!string.Equals(
                    TimerNodeContract(source, sourceNode),
                    TimerNodeContract(target, targetNode),
                    StringComparison.Ordinal)
                || !TimerDescriptorMatches(timer, targetNode)
                || timer.WorkflowDefinitionId != context.SourceDefinition.Id
                || !string.Equals(timer.WorkflowKey, context.Instance.WorkflowKey, StringComparison.Ordinal))
            {
                blockers.Add(Issue(
                    WorkflowVersionCompatibilityCodes.OpenTimerContractChanged,
                    $"Open timer subscription #{timer.Id} is not exactly compatible with target timer node #{timer.TimerNodeId}.",
                    timer.TimerNodeId,
                    runtimeId: timer.Id));
            }
        }
    }

    private static bool JobDescriptorMatches(WorkflowJobRecord job, FlowNodeModel targetNode)
    {
        if (!string.Equals(job.NodeType, targetNode.Type, StringComparison.Ordinal))
        {
            return false;
        }

        var expectedQueueClass = BpmnFlowNodeTypes.IsServiceTask(targetNode.Type)
                                 || BpmnFlowNodeTypes.IsScriptTask(targetNode.Type)
            ? WorkflowJobClasses.Activity
            : WorkflowJobClasses.Control;
        if (!string.Equals(job.QueueClass, expectedQueueClass, StringComparison.Ordinal))
        {
            return false;
        }

        var kindMatches = job.Kind switch
        {
            WorkflowJobKinds.AsyncBefore => targetNode.AsyncBefore,
            WorkflowJobKinds.AsyncAfter => targetNode.AsyncAfter,
            WorkflowJobKinds.Timer => BpmnFlowNodeTypes.IsTimerCatch(targetNode.Type),
            WorkflowJobKinds.TimerBoundary => BpmnFlowNodeTypes.IsTimerBoundary(targetNode.Type),
            WorkflowJobKinds.TimerStart => BpmnFlowNodeTypes.IsTimerStart(targetNode.Type),
            _ => false
        };
        if (!kindMatches)
        {
            return false;
        }

        var expectedPhase = job.Kind switch
        {
            WorkflowJobKinds.AsyncBefore => WorkflowJobKinds.AsyncBefore,
            WorkflowJobKinds.AsyncAfter => WorkflowJobKinds.AsyncAfter,
            WorkflowJobKinds.Timer
                or WorkflowJobKinds.TimerBoundary
                or WorkflowJobKinds.TimerStart => WorkflowJobKinds.Timer,
            _ => null
        };
        if (expectedPhase is null
            || !string.Equals(job.Phase, expectedPhase, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryResolveRetryDelays(targetNode, out var retryDelays))
        {
            return false;
        }

        return job.MaxAttempts == retryDelays.Count + 1
            && job.RetryDelays.SequenceEqual(retryDelays)
            && string.Equals(
                job.FailureHandling,
                targetNode.Job?.FailureHandling ?? WorkflowJobFailureHandling.BoundaryFirst,
                StringComparison.Ordinal);
    }

    private static bool TryResolveRetryDelays(
        FlowNodeModel node,
        out IReadOnlyList<TimeSpan> retryDelays)
    {
        if (node.Job?.RetryDelays is null)
        {
            retryDelays = DefaultRetryDelays;
            return true;
        }

        var result = new List<TimeSpan>(node.Job.RetryDelays.Count);
        foreach (var value in node.Job.RetryDelays)
        {
            if (!TimerDefinitionRules.TryParseFixedDuration(value, out var delay))
            {
                retryDelays = [];
                return false;
            }

            result.Add(delay);
        }

        retryDelays = result;
        return true;
    }

    private static bool TimerDescriptorMatches(
        TimerSubscriptionRecord timer,
        FlowNodeModel targetNode)
    {
        if (!TryGetTimerExpression(targetNode.Timer, out var kind, out var expression))
        {
            return false;
        }

        var attachedToNodeId = BpmnFlowNodeTypes.IsTimerBoundary(targetNode.Type)
            ? targetNode.AttachedToRef
            : null;
        var cancelActivity = BpmnFlowNodeTypes.IsTimerBoundary(targetNode.Type)
            ? targetNode.CancelActivity ?? true
            : true;

        return string.Equals(timer.ScheduleKind, kind, StringComparison.Ordinal)
            && string.Equals(timer.ScheduleExpression, expression, StringComparison.Ordinal)
            && timer.AttachedToNodeId == attachedToNodeId
            && timer.CancelActivity == cancelActivity;
    }

    private static bool TryGetTimerExpression(
        TimerDefinitionModel? timer,
        out string kind,
        out string expression)
    {
        if (!string.IsNullOrWhiteSpace(timer?.TimeDate))
        {
            kind = TimerScheduleKinds.Date;
            expression = timer.TimeDate.Trim();
            return true;
        }
        if (!string.IsNullOrWhiteSpace(timer?.TimeDuration))
        {
            kind = TimerScheduleKinds.Duration;
            expression = timer.TimeDuration.Trim();
            return true;
        }
        if (!string.IsNullOrWhiteSpace(timer?.TimeCycle))
        {
            kind = TimerScheduleKinds.Cycle;
            expression = timer.TimeCycle.Trim();
            return true;
        }

        kind = string.Empty;
        expression = string.Empty;
        return false;
    }

    private static IReadOnlyList<TargetVariableDeclaration> TargetVariableDeclarations(
        WorkflowModel target)
    {
        var result = new List<TargetVariableDeclaration>();
        Add(target.Variables, "process variable", allowNullable: true);
        foreach (var node in target.FlowNodes.OrderBy(node => node.Id))
        {
            Add(node.Variables, $"node #{node.Id} variable", allowNullable: false);
            foreach (var mapping in node.Service?.OutputMappings ?? [])
            {
                if (mapping is null || string.IsNullOrWhiteSpace(mapping.Variable))
                {
                    continue;
                }

                result.Add(new TargetVariableDeclaration(
                    $"service task node #{node.Id} output mapping",
                    mapping.Variable,
                    mapping.DataType ?? WorkflowVariableTypes.Json,
                    mapping.IsArray ?? false,
                    false,
                    mapping.Validation));
            }
            foreach (var mapping in node.Message?.OutputMappings ?? [])
            {
                if (mapping is null || string.IsNullOrWhiteSpace(mapping.Variable))
                {
                    continue;
                }

                result.Add(new TargetVariableDeclaration(
                    $"message node #{node.Id} output mapping",
                    mapping.Variable,
                    mapping.DataType ?? WorkflowVariableTypes.Json,
                    mapping.IsArray ?? false,
                    false,
                    mapping.Validation));
            }
        }
        foreach (var flow in target.SequenceFlows.OrderBy(flow => flow.Id))
        {
            Add(flow.Variables, $"sequence flow #{flow.Id} variable", allowNullable: false);
        }

        return result;

        void Add(IEnumerable<VariableModel> variables, string owner, bool allowNullable)
        {
            foreach (var variable in variables)
            {
                if (variable is null || string.IsNullOrWhiteSpace(variable.Name))
                {
                    continue;
                }

                result.Add(new TargetVariableDeclaration(
                    owner,
                    variable.Name,
                    variable.DataType,
                    variable.IsArray,
                    allowNullable && variable.Nullable,
                    variable.Validation));
            }
        }
    }

    private static bool DefinitionUsesFlowInfo(WorkflowModel definition)
    {
        var gatewayIds = definition.FlowNodes
            .Where(node => BpmnFlowNodeTypes.IsGateway(node.Type))
            .Select(node => node.Id)
            .ToHashSet();
        if (definition.SequenceFlows.Any(flow =>
                SequenceFlowConditionEvaluator.ContainsFlowInfoReference(flow.CompletionCondition)
                || (gatewayIds.Contains(flow.SourceRef)
                    && SequenceFlowConditionEvaluator.ContainsFlowInfoReference(flow.Condition))))
        {
            return true;
        }

        return definition.FlowNodes
            .Where(node => BpmnFlowNodeTypes.IsScriptTask(node.Type))
            .Any(node =>
                node.Assignments.Any(assignment => assignment is not null
                    && SequenceFlowConditionEvaluator.ContainsFlowInfoReference(assignment.Expression))
                || (string.Equals(node.ScriptFormat, ScriptFormats.JavaScript, StringComparison.Ordinal)
                    && (node.UsesFlowInfo == true
                        || JavaScriptFlowInfoUsage.ContainsDirectCall(node.Script))));
    }

    private static string UserTaskAccessContract(WorkflowModel definition, FlowNodeModel node) =>
        JsonSerializer.Serialize(new
        {
            Roles = CanonicalRoles(node.Roles),
            node.RequiresClaim,
            node.ClaimMode,
            node.InheritClaimFromNodeId,
            node.AssigneeExpression,
            node.RequiresAssignment,
            node.AssignmentMode,
            node.InheritAssignmentFromNodeId,
            UnclaimRoles = CanonicalRoles(definition.UnclaimRoles),
            TaskAssignmentRoles = CanonicalRoles(definition.TaskAssignmentRoles)
        });

    private static string AttachedTimerContract(WorkflowModel definition, int hostNodeId) =>
        JsonSerializer.Serialize(definition.FlowNodes
            .Where(node => BpmnFlowNodeTypes.IsTimerBoundary(node.Type)
                && node.AttachedToRef == hostNodeId)
            .OrderBy(node => node.Id)
            .Select(node => TimerNodeContract(definition, node))
            .ToArray());

    private static string GatewayContract(WorkflowModel definition, FlowNodeModel node) =>
        JsonSerializer.Serialize(new
        {
            Node = CanonicalNode(node),
            Outgoing = OutgoingFlowContracts(definition, node.Id)
        });

    private static string DurableNodeContract(WorkflowModel definition, FlowNodeModel node) =>
        JsonSerializer.Serialize(new
        {
            Node = CanonicalNode(node),
            Outgoing = OutgoingFlowContracts(definition, node.Id),
            ErrorBoundaries = BoundaryContracts(definition, node.Id, BpmnFlowNodeTypes.IsErrorBoundary),
            TimerBoundaries = BoundaryContracts(definition, node.Id, BpmnFlowNodeTypes.IsTimerBoundary)
        });

    private static string TimerNodeContract(WorkflowModel definition, FlowNodeModel node) =>
        JsonSerializer.Serialize(new
        {
            Node = CanonicalNode(node),
            Outgoing = OutgoingFlowContracts(definition, node.Id)
        });

    private static IReadOnlyList<string> BoundaryContracts(
        WorkflowModel definition,
        int hostNodeId,
        Func<string, bool> typePredicate) =>
        definition.FlowNodes
            .Where(node => typePredicate(node.Type) && node.AttachedToRef == hostNodeId)
            .OrderBy(node => node.Id)
            .Select(node => JsonSerializer.Serialize(new
            {
                Node = CanonicalNode(node),
                Outgoing = OutgoingFlowContracts(definition, node.Id)
            }))
            .ToArray();

    private static string OutgoingFlowContract(WorkflowModel definition, int nodeId) =>
        JsonSerializer.Serialize(OutgoingFlowContracts(definition, nodeId));

    private static IReadOnlyList<string> OutgoingFlowContracts(
        WorkflowModel definition,
        int nodeId) =>
        definition.SequenceFlows
            .Where(flow => flow.SourceRef == nodeId)
            .Select(CanonicalFlow)
            .ToArray();

    private static string CanonicalNode(FlowNodeModel node)
    {
        var json = JsonSerializer.SerializeToNode(node) as JsonObject
            ?? throw new InvalidOperationException("A workflow node did not serialize as an object.");
        json.Remove("name");
        json.Remove("laneId");
        json.Remove("x");
        json.Remove("y");
        CanonicalizeStringSet(json, "roles");
        return json.ToJsonString();
    }

    private static string CanonicalFlow(SequenceFlowModel flow)
    {
        var json = JsonSerializer.SerializeToNode(flow) as JsonObject
            ?? throw new InvalidOperationException("A sequence flow did not serialize as an object.");
        json.Remove("name");
        json.Remove("externalId");
        CanonicalizeStringSet(json, "roles");
        CanonicalizeStringSet(json, "canActWithoutClaimRoles");
        return json.ToJsonString();
    }

    private static void CanonicalizeStringSet(JsonObject json, string propertyName)
    {
        if (json[propertyName] is not JsonArray values)
        {
            return;
        }

        var canonical = values
            .Select(value => value?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var replacement = new JsonArray();
        foreach (var value in canonical)
        {
            replacement.Add(value);
        }
        json[propertyName] = replacement;
    }

    private static IReadOnlyList<string> CanonicalRoles(IEnumerable<string>? roles) =>
        (roles ?? [])
        .Where(role => !string.IsNullOrWhiteSpace(role))
        .Select(role => role.Trim().ToUpperInvariant())
        .Distinct(StringComparer.Ordinal)
        .OrderBy(role => role, StringComparer.Ordinal)
        .ToArray();

    private static bool JsonContractEquals<T>(T source, T target) =>
        string.Equals(
            JsonSerializer.Serialize(source),
            JsonSerializer.Serialize(target),
            StringComparison.Ordinal);

    private static IReadOnlyDictionary<int, FlowNodeModel> IndexNodes(WorkflowModel definition) =>
        definition.FlowNodes
            .GroupBy(node => node.Id)
            .ToDictionary(group => group.Key, group => group.First());

    private static IReadOnlyDictionary<int, SequenceFlowModel> IndexFlows(WorkflowModel definition) =>
        definition.SequenceFlows
            .GroupBy(flow => flow.Id)
            .ToDictionary(group => group.Key, group => group.First());

    private static IReadOnlyList<WorkflowVersionCompatibilityIssue> SortIssues(
        IEnumerable<WorkflowVersionCompatibilityIssue> issues) =>
        issues.OrderBy(issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(issue => issue.NodeId ?? int.MinValue)
            .ThenBy(issue => issue.FlowId ?? int.MinValue)
            .ThenBy(issue => issue.RuntimeId ?? long.MinValue)
            .ThenBy(issue => issue.VariableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(issue => issue.Message, StringComparer.Ordinal)
            .ToArray();

    private static WorkflowVersionCompatibilityIssue Issue(
        string code,
        string message,
        int? nodeId = null,
        int? flowId = null,
        long? runtimeId = null,
        string? variableName = null) =>
        new(code, message, nodeId, flowId, runtimeId, variableName);

    private sealed record TargetVariableDeclaration(
        string Owner,
        string Name,
        string DataType,
        bool IsArray,
        bool Nullable,
        string? Validation);
}
