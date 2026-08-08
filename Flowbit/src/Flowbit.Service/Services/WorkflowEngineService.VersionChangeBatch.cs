using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

public sealed partial class WorkflowEngineService
{
    public async Task<InstanceVersionChangeBatchExecutionOutcome>
        ExecuteInstanceVersionChangeBatchItemAsync(
            InstanceVersionChangeBatchExecutionRequest request,
            ActorContext actor,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (request.BatchId <= 0
            || request.BatchItemId <= 0
            || request.InstanceId <= 0
            || request.ExpectedSourceWorkflowId <= 0
            || request.TargetWorkflowId <= 0
            || request.ExpectedUpdatedAt == default)
        {
            throw new WorkflowDomainException(
                "The version-change batch execution request contains an invalid identity or timestamp fence.");
        }

        var reason = NormalizeVersionChangeReason(request.Reason);
        await LoadSettingsAsync(cancellationToken);

        var observed = await runtime.GetInstanceAsync(
            request.InstanceId,
            cancellationToken);
        if (observed is null)
        {
            return FailedBatchVersionChange(
                "instance_not_found",
                "The selected workflow instance no longer exists.");
        }
        if (!string.Equals(
                observed.Status,
                WorkflowInstanceStatuses.Running,
                StringComparison.OrdinalIgnoreCase))
        {
            return FailedBatchVersionChange(
                WorkflowVersionCompatibilityCodes.InstanceNotRunning,
                "Only a running workflow instance can change workflow version.");
        }
        if (observed.WorkflowDefinitionId != request.ExpectedSourceWorkflowId
            || observed.UpdatedAt != request.ExpectedUpdatedAt)
        {
            return FailedBatchVersionChange(
                "stale_since_preparation",
                "The workflow instance changed after batch preparation.");
        }

        var observedSource = await definitions.GetAsync(
            request.ExpectedSourceWorkflowId,
            cancellationToken);
        var observedTarget = await definitions.GetAsync(
            request.TargetWorkflowId,
            cancellationToken);
        if (observedSource is null)
        {
            return FailedBatchVersionChange(
                "source_definition_missing",
                "The source workflow version no longer exists.");
        }
        if (observedTarget is null || !observedTarget.IsPublished)
        {
            return FailedBatchVersionChange(
                WorkflowVersionCompatibilityCodes.TargetNotPublished,
                "The target workflow version is no longer published.");
        }
        if (observedSource.Id == observedTarget.Id
            || !string.Equals(
                observedSource.WorkflowKey,
                observedTarget.WorkflowKey,
                StringComparison.Ordinal))
        {
            return FailedBatchVersionChange(
                WorkflowVersionCompatibilityCodes.WorkflowKeyMismatch,
                "The source and target workflow versions no longer form a valid version-change pair.");
        }

        WorkflowInstanceVersionChangeRecord audit;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await definitions.LockFamilyForStartAsync(
                observedSource.WorkflowKey,
                cancellationToken);

            var instance = await runtime.GetInstanceForUpdateAsync(
                request.InstanceId,
                lockActiveUserTask: true,
                cancellationToken);
            if (instance is null)
            {
                return FailedBatchVersionChange(
                    "instance_not_found",
                    "The selected workflow instance no longer exists.");
            }
            if (!string.Equals(
                    instance.Status,
                    WorkflowInstanceStatuses.Running,
                    StringComparison.OrdinalIgnoreCase))
            {
                return FailedBatchVersionChange(
                    WorkflowVersionCompatibilityCodes.InstanceNotRunning,
                    "Only a running workflow instance can change workflow version.");
            }
            if (instance.WorkflowDefinitionId != request.ExpectedSourceWorkflowId
                || instance.UpdatedAt != request.ExpectedUpdatedAt)
            {
                return FailedBatchVersionChange(
                    "stale_since_preparation",
                    "The workflow instance changed after batch preparation.");
            }

            var source = await definitions.GetAsync(
                instance.WorkflowDefinitionId,
                cancellationToken);
            var target = await definitions.GetPublishedAsync(
                request.TargetWorkflowId,
                cancellationToken);
            if (source is null)
            {
                return FailedBatchVersionChange(
                    "source_definition_missing",
                    "The source workflow version no longer exists.");
            }
            if (target is null)
            {
                return FailedBatchVersionChange(
                    WorkflowVersionCompatibilityCodes.TargetNotPublished,
                    "The target workflow version is no longer published.");
            }
            if (!string.Equals(
                    instance.WorkflowKey,
                    source.WorkflowKey,
                    StringComparison.Ordinal)
                || !string.Equals(
                    instance.WorkflowKey,
                    target.WorkflowKey,
                    StringComparison.Ordinal)
                || source.Id == target.Id)
            {
                return FailedBatchVersionChange(
                    WorkflowVersionCompatibilityCodes.WorkflowKeyMismatch,
                    "The instance, source version, and target version no longer belong to one valid workflow family.");
            }

            var context = await BuildVersionCompatibilityContextAsync(
                instance,
                source,
                target,
                actor,
                lockDurableState: true,
                cancellationToken);
            var compatibility = WorkflowVersionCompatibilityEvaluator.Evaluate(context);
            var blockers = compatibility.Blockers
                .Select(ToVersionChangeIssue)
                .ToList();
            var warnings = compatibility.Warnings
                .Select(ToVersionChangeIssue)
                .ToList();
            if (blockers.Count > 0)
            {
                return new InstanceVersionChangeBatchExecutionOutcome(
                    false,
                    null,
                    "incompatible",
                    "The workflow version is incompatible with the instance's current runtime state.",
                    blockers,
                    warnings);
            }

            audit = await runtime.ChangeInstanceWorkflowVersionForBatchAsync(
                instance.Id,
                request.ExpectedSourceWorkflowId,
                request.ExpectedUpdatedAt,
                target.Id,
                target.Definition,
                ToNodeExecutionActor(actor),
                reason,
                request.BatchId,
                request.BatchItemId,
                cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return new InstanceVersionChangeBatchExecutionOutcome(
            true,
            audit.Id,
            null,
            null,
            [],
            []);
    }

    private static InstanceVersionChangeBatchExecutionOutcome FailedBatchVersionChange(
        string code,
        string description) =>
        new(
            false,
            null,
            code,
            description,
            [],
            []);
}
