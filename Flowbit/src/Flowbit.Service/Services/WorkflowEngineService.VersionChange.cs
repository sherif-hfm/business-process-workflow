using System.Text;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

public sealed partial class WorkflowEngineService
{
    public async Task<InstanceVersionChangePreviewDto?> PreviewInstanceVersionChangeAsync(
        long id,
        long targetWorkflowId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        EnsureValidVersionChangeInstance(id);
        EnsureValidVersionChangeTarget(targetWorkflowId);
        await LoadSettingsAsync(cancellationToken);

        var instance = await runtime.GetInstanceAsync(id, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        var source = await definitions.GetAsync(
            instance.WorkflowDefinitionId,
            cancellationToken);
        var target = await definitions.GetAsync(targetWorkflowId, cancellationToken);
        if (source is null || target is null)
        {
            return null;
        }

        EnsureSameVersionChangeFamily(source, target);
        if (!string.Equals(
                instance.Status,
                WorkflowInstanceStatuses.Running,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowConflictException(
                "Only a running workflow instance can change workflow version.");
        }
        if (!target.IsPublished)
        {
            throw new WorkflowConflictException(
                "The target workflow version is no longer published.");
        }
        var context = await BuildVersionCompatibilityContextAsync(
            instance,
            source,
            target,
            actor,
            lockDurableState: false,
            cancellationToken);
        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(context);

        return ToVersionChangePreview(instance, source, target, result);
    }

    public async Task<ChangeInstanceVersionResultDto?> ChangeInstanceVersionAsync(
        long id,
        ChangeInstanceVersionRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        EnsureValidVersionChangeInstance(id);
        if (request is null)
        {
            throw new WorkflowDomainException("A version-change request is required.");
        }

        EnsureValidVersionChangeTarget(request.TargetWorkflowId);
        if (request.ExpectedSourceWorkflowId <= 0)
        {
            throw new WorkflowDomainException(
                "ExpectedSourceWorkflowId must be greater than zero.");
        }
        if (request.ExpectedUpdatedAt == default)
        {
            throw new WorkflowDomainException("ExpectedUpdatedAt is required.");
        }
        var reason = NormalizeVersionChangeReason(request.Reason);
        await LoadSettingsAsync(cancellationToken);

        // Resolve the family before opening the transaction. The authoritative
        // source, target publication state, and optimistic-concurrency values
        // are all reloaded after their locks are acquired below.
        var previewInstance = await runtime.GetInstanceAsync(id, cancellationToken);
        if (previewInstance is null)
        {
            return null;
        }

        var previewSource = await definitions.GetAsync(
            previewInstance.WorkflowDefinitionId,
            cancellationToken);
        var previewTarget = await definitions.GetAsync(
            request.TargetWorkflowId,
            cancellationToken);
        if (previewSource is null || previewTarget is null)
        {
            return null;
        }

        EnsureSameVersionChangeFamily(previewSource, previewTarget);
        if (!previewTarget.IsPublished)
        {
            throw new WorkflowConflictException(
                "The target workflow version is no longer published.");
        }

        WorkflowInstanceVersionChangeRecord audit;
        WorkflowDefinitionRecord source;
        WorkflowDefinitionRecord target;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            // A shared family lock serializes this operation with publishing,
            // unpublishing, default changes, and definition deletion.
            await definitions.LockFamilyForStartAsync(
                previewSource.WorkflowKey,
                cancellationToken);

            var instance = await runtime.GetInstanceForUpdateAsync(
                    id,
                    lockActiveUserTask: true,
                    cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The workflow instance no longer exists.");
            if (!string.Equals(
                    instance.Status,
                    WorkflowInstanceStatuses.Running,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkflowConflictException(
                    "Only a running workflow instance can change workflow version.");
            }
            if (instance.WorkflowDefinitionId != request.ExpectedSourceWorkflowId)
            {
                throw new WorkflowConflictException(
                    "The workflow instance source version changed; refresh and preview again.");
            }
            if (instance.UpdatedAt != request.ExpectedUpdatedAt)
            {
                throw new WorkflowConflictException(
                    "The workflow instance changed after preview; refresh and preview again.");
            }

            source = await definitions.GetAsync(
                    instance.WorkflowDefinitionId,
                    cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The source workflow version no longer exists.");
            target = await definitions.GetPublishedAsync(
                    request.TargetWorkflowId,
                    cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The target workflow version is no longer published.");
            EnsureSameVersionChangeFamily(source, target);
            if (!string.Equals(
                    instance.WorkflowKey,
                    source.WorkflowKey,
                    StringComparison.Ordinal)
                || !string.Equals(
                    instance.WorkflowKey,
                    target.WorkflowKey,
                    StringComparison.Ordinal))
            {
                throw new WorkflowConflictException(
                    "The workflow instance family no longer matches the requested versions.");
            }

            var context = await BuildVersionCompatibilityContextAsync(
                instance,
                source,
                target,
                actor,
                lockDurableState: true,
                cancellationToken);
            var compatibility = WorkflowVersionCompatibilityEvaluator.Evaluate(context);
            if (!compatibility.IsCompatible)
            {
                var descriptions = compatibility.Blockers
                    .Take(5)
                    .Select(blocker => $"{blocker.Code}: {blocker.Message}");
                var suffix = compatibility.Blockers.Count > 5
                    ? $" (+{compatibility.Blockers.Count - 5} more)"
                    : string.Empty;
                throw new WorkflowConflictException(
                    "The workflow version is incompatible with the active instance state: "
                    + string.Join("; ", descriptions)
                    + suffix);
            }

            audit = await runtime.ChangeInstanceWorkflowVersionAsync(
                instance.Id,
                request.ExpectedSourceWorkflowId,
                request.ExpectedUpdatedAt,
                target.Id,
                target.Definition,
                ToNodeExecutionActor(actor),
                reason,
                cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Changed workflow instance {InstanceId} from definition {SourceWorkflowId} v{SourceVersion} to definition {TargetWorkflowId} v{TargetVersion} ({Direction}) by {Actor}.",
            id,
            source.Id,
            source.Version,
            target.Id,
            target.Version,
            VersionChangeDirection(source, target),
            actor.User ?? "anonymous");

        var detail = await BuildDetailAsync(id, cancellationToken)
            ?? throw new WorkflowConflictException(
                "The workflow instance no longer exists after its version change.");
        var auditDto = detail.VersionChanges.FirstOrDefault(change => change.Id == audit.Id)
            ?? ToVersionChangeAudit(audit, source, target);
        return new ChangeInstanceVersionResultDto(detail, auditDto);
    }

    private async Task<WorkflowVersionCompatibilityContext>
        BuildVersionCompatibilityContextAsync(
            WorkflowInstanceRecord instance,
            WorkflowDefinitionRecord source,
            WorkflowDefinitionRecord target,
            ActorContext actor,
            bool lockDurableState,
            CancellationToken cancellationToken)
    {
        var activeTokens = (await runtime.ListExecutionTokensAsync(
                instance.Id,
                ExecutionTokenRecordStatuses.Active,
                cancellationToken))
            .OrderBy(token => token.Id)
            .ToList();
        var openUserTasks = (await runtime.ListUserTasksAsync(
                instance.Id,
                null,
                cancellationToken))
            .Where(task => task.Status is
                UserTaskRecordStatuses.Active or UserTaskRecordStatuses.Pending)
            .OrderBy(task => task.Id)
            .ToList();
        var activeMultiInstances = (await runtime.ListMultiInstancesAsync(
                instance.Id,
                MultiInstanceRecordStatuses.Active,
                cancellationToken))
            .OrderBy(execution => execution.Id)
            .ToList();
        var activeGatewayExecutions = (await runtime.ListGatewayExecutionsAsync(
                instance.Id,
                GatewayExecutionRecordStatuses.Active,
                cancellationToken))
            .OrderBy(execution => execution.Id)
            .ToList();
        var activeGatewayBranches = (await runtime.ListGatewayBranchesForInstanceAsync(
                instance.Id,
                activeOnly: true,
                cancellationToken))
            .OrderBy(branch => branch.Id)
            .ToList();
        var complexStates = (await runtime.ListComplexGatewayStatesAsync(
                instance.Id,
                cancellationToken))
            .OrderBy(state => state.Id)
            .ToList();

        var currentVariables = (await runtime.LoadLatestVariableVersionsAsync(
                instance.Id,
                cancellationToken))
            .ToDictionary(
                variable => variable.Name,
                variable => variable.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
        var observedFlows = await runtime.ListObservedSequenceFlowsAsync(
            instance.Id,
            cancellationToken);
        var flowSummaries = (await runtime.ListSequenceFlowSummariesAsync(
                instance.Id,
                cancellationToken))
            .Values
            .OrderBy(summary => summary.SequenceFlowId)
            .ToList();

        // Lock order after the instance-owned runtime rows is jobs, incidents,
        // then timers. Incidents are locked for serialization even though their
        // executable contract is represented by their owning open job.
        var openJobs = await jobs.ListOpenByInstanceAsync(
            instance.Id,
            lockDurableState,
            cancellationToken);
        if (lockDurableState)
        {
            _ = await jobs.ListOpenIncidentsByInstanceAsync(
                instance.Id,
                forUpdate: true,
                cancellationToken);
        }
        var openTimers = await timerSubscriptions.ListActiveOrPausedByInstanceAsync(
            instance.Id,
            lockDurableState,
            cancellationToken);

        var contextNodeId = activeTokens.FirstOrDefault()?.NodeId
            ?? instance.CurrentStepId;
        var contextNode = target.Definition.FlowNodes
                .FirstOrDefault(node => node.Id == contextNodeId)
            ?? source.Definition.FlowNodes.FirstOrDefault(node => node.Id == contextNodeId)
            ?? source.Definition.FlowNodes.First();
        var targetInstance = instance with { WorkflowDefinitionId = target.Id };
        var validationContext = BuildContextMap(
            actor,
            targetInstance,
            target.Definition,
            contextNode);

        return new WorkflowVersionCompatibilityContext
        {
            Instance = instance,
            SourceDefinition = source,
            TargetDefinition = target,
            ActiveTokens = activeTokens,
            OpenUserTasks = openUserTasks,
            ActiveMultiInstanceExecutions = activeMultiInstances,
            ActiveGatewayExecutions = activeGatewayExecutions,
            ActiveGatewayBranches = activeGatewayBranches,
            ActiveComplexGatewayStates = complexStates,
            CurrentVariables = currentVariables,
            VariableValidationContext = validationContext,
            ObservedFlows = observedFlows
                .Select(flow => new ObservedSequenceFlowIdentity(
                    flow.FlowId,
                    flow.SourceNodeId,
                    flow.TargetNodeId))
                .ToList(),
            FlowSummaries = flowSummaries,
            OpenJobs = openJobs,
            OpenTimers = openTimers,
            HasCommittedTraversals = observedFlows.Count > 0
                || flowSummaries.Any(summary => summary.TraversalCount > 0)
        };
    }

    private async Task<IReadOnlyList<InstanceVersionChangeAuditDto>>
        BuildVersionChangeAuditDtosAsync(
            long instanceId,
            CancellationToken cancellationToken)
    {
        var records = await runtime.ListVersionChangesAsync(
            instanceId,
            cancellationToken);
        if (records.Count == 0)
        {
            return [];
        }

        var workflowIds = records
            .SelectMany(record => new[]
            {
                record.SourceWorkflowDefinitionId,
                record.TargetWorkflowDefinitionId
            })
            .Distinct()
            .ToArray();
        var workflows = await definitions.GetManyAsync(workflowIds, cancellationToken);
        var result = new List<InstanceVersionChangeAuditDto>(records.Count);
        foreach (var record in records.OrderByDescending(change => change.ChangedAt)
                     .ThenByDescending(change => change.Id))
        {
            if (!workflows.TryGetValue(record.SourceWorkflowDefinitionId, out var source)
                || !workflows.TryGetValue(record.TargetWorkflowDefinitionId, out var target))
            {
                throw new InvalidOperationException(
                    $"Version-change audit #{record.Id} references a missing workflow definition.");
            }
            result.Add(ToVersionChangeAudit(record, source, target));
        }

        return result;
    }

    private static InstanceVersionChangePreviewDto ToVersionChangePreview(
        WorkflowInstanceRecord instance,
        WorkflowDefinitionRecord source,
        WorkflowDefinitionRecord target,
        WorkflowVersionCompatibilityResult result) =>
        new(
            instance.Id,
            ToVersionSummary(source),
            ToVersionSummary(target),
            VersionChangeDirection(source, target),
            result.IsCompatible,
            result.Blockers.Select(ToVersionChangeIssue).ToList(),
            result.Warnings.Select(ToVersionChangeIssue).ToList(),
            instance.WorkflowDefinitionId,
            instance.UpdatedAt);

    private static InstanceVersionChangeIssueDto ToVersionChangeIssue(
        WorkflowVersionCompatibilityIssue issue) =>
        new(
            Code: issue.Code,
            Message: issue.Message,
            StateType: VersionChangeStateType(issue),
            StateId: issue.RuntimeId,
            NodeId: issue.NodeId,
            FlowId: issue.FlowId,
            VariableName: issue.VariableName);

    private static string? VersionChangeStateType(
        WorkflowVersionCompatibilityIssue issue)
    {
        if (issue.RuntimeId is null)
        {
            return null;
        }

        return issue.Code switch
        {
            WorkflowVersionCompatibilityCodes.InstanceNotRunning
                or WorkflowVersionCompatibilityCodes.SourceDefinitionMismatch => "instance",
            WorkflowVersionCompatibilityCodes.MultiInstanceContractChanged =>
                "multiInstanceExecution",
            WorkflowVersionCompatibilityCodes.OpenJobNodeMissing
                or WorkflowVersionCompatibilityCodes.OpenJobContractChanged => "job",
            WorkflowVersionCompatibilityCodes.OpenTimerNodeMissing
                or WorkflowVersionCompatibilityCodes.OpenTimerContractChanged => "timerSubscription",
            _ => "runtimeState"
        };
    }

    private static InstanceVersionChangeAuditDto ToVersionChangeAudit(
        WorkflowInstanceVersionChangeRecord record,
        WorkflowDefinitionRecord source,
        WorkflowDefinitionRecord target) =>
        new(
            record.Id,
            record.InstanceId,
            ToVersionSummary(source),
            ToVersionSummary(target),
            VersionChangeDirection(source, target),
            record.ChangedBy,
            record.ChangedByRoles,
            record.Reason,
            record.ChangedAt,
            record.BatchId,
            record.BatchItemId);

    private static WorkflowSummaryDto ToVersionSummary(
        WorkflowDefinitionRecord workflow) =>
        new(
            workflow.Id,
            workflow.Name,
            workflow.WorkflowKey,
            workflow.Version,
            workflow.IsPublished,
            workflow.IsDefault,
            workflow.CreatedAt);

    private static string VersionChangeDirection(
        WorkflowDefinitionRecord source,
        WorkflowDefinitionRecord target) =>
        target.Version > source.Version
            ? InstanceVersionChangeDirections.Upgrade
            : InstanceVersionChangeDirections.Downgrade;

    private static void EnsureValidVersionChangeTarget(long targetWorkflowId)
    {
        if (targetWorkflowId <= 0)
        {
            throw new WorkflowDomainException(
                "TargetWorkflowId must be greater than zero.");
        }
    }

    private static void EnsureValidVersionChangeInstance(long instanceId)
    {
        if (instanceId <= 0)
        {
            throw new WorkflowDomainException(
                "Instance id must be greater than zero.");
        }
    }

    private static void EnsureSameVersionChangeFamily(
        WorkflowDefinitionRecord source,
        WorkflowDefinitionRecord target)
    {
        if (source.Id == target.Id)
        {
            throw new WorkflowDomainException(
                "The target workflow version must differ from the instance's current version.");
        }
        if (!string.Equals(
                source.WorkflowKey,
                target.WorkflowKey,
                StringComparison.Ordinal))
        {
            throw new WorkflowDomainException(
                "The target workflow version must belong to the same workflow family.");
        }
    }

    private static string NormalizeVersionChangeReason(string? raw)
    {
        var reason = raw?.Trim() ?? string.Empty;
        var length = reason.EnumerateRunes().Count();
        if (length is < 1 or > 1000)
        {
            throw new WorkflowDomainException(
                "Reason must contain between 1 and 1000 Unicode characters.");
        }
        return reason;
    }
}
