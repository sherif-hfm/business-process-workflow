using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

public sealed class AdministrativeActionBatchJobProcessor(
    IServiceScopeFactory scopeFactory,
    IWorkflowJobRepository jobs,
    IEngineSettingsRepository engineSettings,
    TimeProvider timeProvider,
    ILogger<AdministrativeActionBatchJobProcessor> logger)
    : IAdministrativeActionBatchJobProcessor
{
    private const int ItemPageSize = 100;

    public async Task ProcessAsync(
        WorkflowJobLeaseRecord lease,
        CancellationToken cancellationToken)
    {
        if (lease.Job.Kind is not (
                WorkflowJobKinds.AdministrativeBatchPrepare
                or WorkflowJobKinds.AdministrativeBatchExecute))
        {
            throw new WorkflowConflictException(
                $"Job #{lease.Job.Id} is not an administrative batch job.");
        }
        var payload = lease.Job.Payload?.Deserialize<AdministrativeActionBatchJobPayload>();
        if (payload is null || payload.BatchId <= 0)
        {
            await jobs.OpenIncidentAsync(
                Fence(lease),
                "invalid_administrative_batch_payload",
                $"Administrative batch job #{lease.Job.Id} has no valid batch identity.",
                null,
                cancellationToken);
            return;
        }

        try
        {
            if (lease.Job.Kind == WorkflowJobKinds.AdministrativeBatchPrepare)
            {
                await PrepareAsync(
                    payload.BatchId,
                    payload.ActorClaims,
                    cancellationToken);
            }
            else
            {
                await ExecuteAsync(
                    payload.BatchId,
                    payload.ActorClaims,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (lease.AttemptNumber >= lease.Job.MaxAttempts)
            {
                await MarkFailedAsync(payload.BatchId, exception, cancellationToken);
            }
            throw;
        }

        // Completing the durable job is deliberately outside the phase-failure
        // handler. The phase may already have committed a ready or completed
        // batch; losing the lease while acknowledging the job must not rewrite
        // that committed business outcome to failed.
        await jobs.CompleteAsync(Fence(lease), cancellationToken);
    }

    private async Task PrepareAsync(
        long batchId,
        IReadOnlyDictionary<string, string>? actorClaims,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var pageScope = scopeFactory.CreateAsyncScope();
            var repository = pageScope.ServiceProvider
                .GetRequiredService<IAdministrativeActionBatchRepository>();
            var batch = await repository.GetAsync(batchId, false, cancellationToken);
            if (batch is null || batch.Status == AdministrativeActionBatchStatuses.Cancelled)
            {
                return;
            }
            if (batch.Status != AdministrativeActionBatchStatuses.Preparing)
            {
                return;
            }
            var items = await repository.ListItemsForProcessingAsync(
                batchId,
                [AdministrativeActionBatchItemStatuses.Preparing],
                ItemPageSize,
                cancellationToken);
            if (items.Count == 0)
            {
                break;
            }
            foreach (var item in items)
            {
                await PrepareItemAsync(item.Id, actorClaims, cancellationToken);
            }
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider.GetRequiredService<IAdministrativeActionBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var current = await batches.GetAsync(batchId, true, cancellationToken);
        if (current is null || current.Status != AdministrativeActionBatchStatuses.Preparing)
        {
            return;
        }
        var counts = await batches.CountItemsByStatusAsync(batchId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var affectedTaskCount = await batches.SumAffectedTaskCountAsync(
            batchId,
            null,
            cancellationToken);
        var maxAffectedTasks = await ResolveMaxAffectedTasksAsync(cancellationToken);
        if (affectedTaskCount > maxAffectedTasks)
        {
            await batches.TransitionItemsAsync(
                batchId,
                [AdministrativeActionBatchItemStatuses.Eligible],
                AdministrativeActionBatchItemStatuses.Failed,
                now,
                cancellationToken);
            counts = await batches.CountItemsByStatusAsync(batchId, cancellationToken);
            await batches.UpdateAsync(
                AdministrativeActionBatchService.ApplyCounts(
                    AdministrativeActionBatchService.ToUpdate(current),
                    counts) with
                {
                    Status = AdministrativeActionBatchStatuses.Failed,
                    TotalAffectedTaskCount = affectedTaskCount,
                    Issues = AdministrativeActionBatchService.SerializeIssues(
                        [AdministrativeActionBatchService.Issue(
                            "affected_task_limit_exceeded",
                            $"Preparation found {affectedTaskCount:N0} affected tasks, exceeding the configured maximum of {maxAffectedTasks:N0}.")]),
                    PreparedAt = now,
                    CompletedAt = now,
                    UpdatedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        await batches.UpdateAsync(
            AdministrativeActionBatchService.ApplyCounts(
                AdministrativeActionBatchService.ToUpdate(current),
                counts) with
            {
                Status = AdministrativeActionBatchStatuses.Ready,
                TotalAffectedTaskCount = affectedTaskCount,
                PreparedAt = now,
                UpdatedAt = now
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Administrative batch {BatchId} preparation completed.",
            batchId);
    }

    private async Task PrepareItemAsync(
        long itemId,
        IReadOnlyDictionary<string, string>? actorClaims,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider.GetRequiredService<IAdministrativeActionBatchRepository>();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngineService>();
        var batchService = scope.ServiceProvider.GetRequiredService<IAdministrativeActionBatchService>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var observed = await batches.GetItemAsync(itemId, false, cancellationToken);
        if (observed is null)
        {
            return;
        }
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // Administrative lock order is batch -> item. Cancellation follows the
        // same order, preventing a prepare/cancel deadlock.
        var batch = await batches.GetAsync(observed.BatchId, true, cancellationToken);
        if (batch is null)
        {
            return;
        }
        var item = await batches.GetItemAsync(itemId, true, cancellationToken);
        if (item is null || item.Status != AdministrativeActionBatchItemStatuses.Preparing)
        {
            return;
        }
        var now = timeProvider.GetUtcNow();
        if (batch.Status == AdministrativeActionBatchStatuses.Cancelled)
        {
            await batches.UpdateItemAsync(
                AdministrativeActionBatchService.ToItemUpdate(item) with
                {
                    Status = AdministrativeActionBatchItemStatuses.Cancelled,
                    UpdatedAt = now,
                    CompletedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (batch.Status != AdministrativeActionBatchStatuses.Preparing)
        {
            return;
        }

        var actor = PreparedActor(batch, actorClaims);
        AdministrativeActionEligibilityDto eligibility;
        try
        {
            eligibility = await engine.PreviewAdministrativeBatchActionAsync(
                BuildRequest(batch, item),
                actor,
                cancellationToken);
        }
        catch (Exception exception) when (exception is
            WorkflowDomainException or WorkflowConflictException
            or WorkflowForbiddenException or WorkflowUnauthorizedException)
        {
            eligibility = new AdministrativeActionEligibilityDto(
                false,
                item.AffectedTaskCount,
                [AdministrativeActionBatchService.Issue(
                    "administrative_action_unavailable",
                    exception.Message)]);
        }

        await batches.UpdateItemAsync(
            AdministrativeActionBatchService.ToItemUpdate(item) with
            {
                Status = eligibility.Eligible
                    ? AdministrativeActionBatchItemStatuses.Eligible
                    : AdministrativeActionBatchItemStatuses.Ineligible,
                Issues = eligibility.Issues.Count == 0
                    ? null
                    : AdministrativeActionBatchService.SerializeIssues(eligibility.Issues),
                AffectedTaskCount = eligibility.AffectedTaskCount,
                UpdatedAt = now,
                PreparedAt = now,
                CompletedAt = eligibility.Eligible ? null : now
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ExecuteAsync(
        long batchId,
        IReadOnlyDictionary<string, string>? actorClaims,
        CancellationToken cancellationToken)
    {
        if (!await MarkRunningAsync(batchId, cancellationToken))
        {
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var pageScope = scopeFactory.CreateAsyncScope();
            var repository = pageScope.ServiceProvider
                .GetRequiredService<IAdministrativeActionBatchRepository>();
            var batch = await repository.GetAsync(batchId, false, cancellationToken);
            if (batch is null)
            {
                return;
            }
            if (batch.Status is not (
                    AdministrativeActionBatchStatuses.Running
                    or AdministrativeActionBatchStatuses.Cancelled))
            {
                return;
            }
            var items = await repository.ListItemsForProcessingAsync(
                batchId,
                [AdministrativeActionBatchItemStatuses.Queued],
                ItemPageSize,
                cancellationToken);
            if (items.Count == 0)
            {
                break;
            }
            foreach (var item in items)
            {
                await ExecuteItemAsync(item.Id, actorClaims, cancellationToken);
            }
            await RefreshExecutionProgressAsync(batchId, cancellationToken);
        }

        await FinalizeExecutionOrCancellationAsync(batchId, cancellationToken);
    }

    private async Task RefreshExecutionProgressAsync(
        long batchId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider.GetRequiredService<IAdministrativeActionBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(batchId, true, cancellationToken);
        if (batch is null || batch.Status is not (
                AdministrativeActionBatchStatuses.Running
                or AdministrativeActionBatchStatuses.Cancelled))
        {
            return;
        }
        var counts = await batches.CountItemsByStatusAsync(batchId, cancellationToken);
        await batches.UpdateAsync(
            AdministrativeActionBatchService.ApplyCounts(
                AdministrativeActionBatchService.ToUpdate(batch),
                counts) with
            {
                UpdatedAt = timeProvider.GetUtcNow()
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<bool> MarkRunningAsync(long batchId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider.GetRequiredService<IAdministrativeActionBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(batchId, true, cancellationToken);
        if (batch is null)
        {
            return false;
        }
        // Cancellation stops items that have not begun, but an item whose
        // StartedAt was already committed remains resumable. This lets a
        // durable retry finish or classify the in-flight item and prevents a
        // cancelled batch from retaining a permanent queued count.
        if (batch.Status == AdministrativeActionBatchStatuses.Cancelled)
        {
            return true;
        }
        if (batch.Status == AdministrativeActionBatchStatuses.Running)
        {
            return true;
        }
        if (batch.Status != AdministrativeActionBatchStatuses.Queued)
        {
            return false;
        }
        var now = timeProvider.GetUtcNow();
        await batches.UpdateAsync(
            AdministrativeActionBatchService.ToUpdate(batch) with
            {
                Status = AdministrativeActionBatchStatuses.Running,
                StartedAt = batch.StartedAt ?? now,
                UpdatedAt = now
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task ExecuteItemAsync(
        long itemId,
        IReadOnlyDictionary<string, string>? actorClaims,
        CancellationToken cancellationToken)
    {
        AdministrativeActionBatchRecord batch;
        AdministrativeActionBatchItemRecord item;
        var now = timeProvider.GetUtcNow();
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IAdministrativeActionBatchRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var observed = await repository.GetItemAsync(itemId, false, cancellationToken);
            if (observed is null)
            {
                return;
            }
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            batch = await repository.GetAsync(observed.BatchId, true, cancellationToken)
                ?? null!;
            if (batch is null)
            {
                return;
            }
            item = await repository.GetItemAsync(itemId, true, cancellationToken)
                ?? null!;
            if (item is null || item.Status != AdministrativeActionBatchItemStatuses.Queued)
            {
                return;
            }
            if (batch.Status == AdministrativeActionBatchStatuses.Cancelled
                && item.StartedAt is null)
            {
                await repository.UpdateItemAsync(
                    AdministrativeActionBatchService.ToItemUpdate(item) with
                    {
                        Status = AdministrativeActionBatchItemStatuses.Cancelled,
                        UpdatedAt = now,
                        CompletedAt = now
                    },
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            if (batch.Status is not (
                    AdministrativeActionBatchStatuses.Running
                    or AdministrativeActionBatchStatuses.Cancelled))
            {
                return;
            }
            if (item.StartedAt is null)
            {
                item = await repository.UpdateItemAsync(
                    AdministrativeActionBatchService.ToItemUpdate(item) with
                    {
                        StartedAt = now,
                        UpdatedAt = now
                    },
                    cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }

        AdministrativeActionResultDto? result = null;
        var terminalStatus = AdministrativeActionBatchItemStatuses.Skipped;
        string? terminalCode = null;
        string? terminalDescription = null;
        await using (var actionScope = scopeFactory.CreateAsyncScope())
        {
            var engine = actionScope.ServiceProvider.GetRequiredService<IWorkflowEngineService>();
            var actor = ConfirmedActor(batch, actorClaims);
            try
            {
                result = await engine.ExecuteAdministrativeBatchActionAsync(
                    BuildRequest(batch, item),
                    actor,
                    cancellationToken);
                if (result is null)
                {
                    terminalCode = "position_not_found";
                    terminalDescription = "The selected execution position no longer exists.";
                }
            }
            catch (AdministrativeActionExecutionException exception)
            {
                terminalStatus = AdministrativeActionBatchItemStatuses.Failed;
                terminalCode = "downstream_execution_failed";
                terminalDescription = exception.Message;
            }
            catch (Exception exception) when (exception is
                WorkflowConflictException or WorkflowDomainException
                or WorkflowForbiddenException or WorkflowUnauthorizedException)
            {
                terminalCode = exception switch
                {
                    WorkflowConflictException => "stale",
                    WorkflowForbiddenException or WorkflowUnauthorizedException => "authentication_changed",
                    _ => "ineligible"
                };
                terminalDescription = exception.Message;
            }
        }

        await using var resultScope = scopeFactory.CreateAsyncScope();
        var resultRepository = resultScope.ServiceProvider
            .GetRequiredService<IAdministrativeActionBatchRepository>();
        var current = await resultRepository.GetItemAsync(item.Id, false, cancellationToken);
        if (current is null || current.Status != AdministrativeActionBatchItemStatuses.Queued)
        {
            return;
        }
        var completedAt = timeProvider.GetUtcNow();
        if (result is not null)
        {
            // The engine commits the transition and succeeded item state in the
            // same transaction. A returned result with a still-queued item
            // indicates an invalid implementation and must be retried rather
            // than recorded non-atomically by the worker.
            throw new WorkflowConflictException(
                "The administrative transition returned without atomically completing its batch item.");
        }
        else
        {
            await resultRepository.UpdateItemAsync(
                AdministrativeActionBatchService.ToItemUpdate(current) with
                {
                    Status = terminalStatus,
                    ErrorCode = terminalCode,
                    ErrorDescription = Limit(terminalDescription),
                    Issues = AdministrativeActionBatchService.SerializeIssues(
                        [AdministrativeActionBatchService.Issue(
                            terminalCode ?? "skipped",
                            terminalDescription ?? "The item was skipped during revalidation.")]),
                    UpdatedAt = completedAt,
                    CompletedAt = completedAt
                },
                cancellationToken);
        }
    }

    private async Task FinalizeExecutionOrCancellationAsync(
        long batchId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider.GetRequiredService<IAdministrativeActionBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(batchId, true, cancellationToken);
        if (batch is null || batch.Status is not (
                AdministrativeActionBatchStatuses.Running
                or AdministrativeActionBatchStatuses.Cancelled))
        {
            return;
        }
        var counts = await batches.CountItemsByStatusAsync(batchId, cancellationToken);
        if (Count(counts, AdministrativeActionBatchItemStatuses.Queued) > 0)
        {
            return;
        }
        if (batch.Status == AdministrativeActionBatchStatuses.Cancelled)
        {
            var cancelledAt = batch.CancelledAt ?? timeProvider.GetUtcNow();
            await batches.UpdateAsync(
                AdministrativeActionBatchService.ApplyCounts(
                    AdministrativeActionBatchService.ToUpdate(batch),
                    counts) with
                {
                    CompletedAt = batch.CompletedAt ?? cancelledAt,
                    UpdatedAt = timeProvider.GetUtcNow()
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Administrative batch {BatchId} cancellation finalized after in-flight items settled.",
                batchId);
            return;
        }

        var hasIssues = Count(counts, AdministrativeActionBatchItemStatuses.Ineligible)
                        + Count(counts, AdministrativeActionBatchItemStatuses.Skipped)
                        + Count(counts, AdministrativeActionBatchItemStatuses.Failed)
                        + Count(counts, AdministrativeActionBatchItemStatuses.Cancelled) > 0;
        var now = timeProvider.GetUtcNow();
        await batches.UpdateAsync(
            AdministrativeActionBatchService.ApplyCounts(
                AdministrativeActionBatchService.ToUpdate(batch),
                counts) with
            {
                Status = hasIssues
                    ? AdministrativeActionBatchStatuses.CompletedWithIssues
                    : AdministrativeActionBatchStatuses.Completed,
                CompletedAt = now,
                UpdatedAt = now
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Administrative batch {BatchId} execution completed with issues={HasIssues}.",
            batchId,
            hasIssues);
    }

    private async Task MarkFailedAsync(
        long batchId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider.GetRequiredService<IAdministrativeActionBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(batchId, true, cancellationToken);
        if (batch is null
            || batch.Status is AdministrativeActionBatchStatuses.Ready
                or AdministrativeActionBatchStatuses.Completed
                or AdministrativeActionBatchStatuses.CompletedWithIssues
                or AdministrativeActionBatchStatuses.Failed)
        {
            return;
        }
        var now = timeProvider.GetUtcNow();
        await batches.TransitionItemsAsync(
            batchId,
            [
                AdministrativeActionBatchItemStatuses.Preparing,
                AdministrativeActionBatchItemStatuses.Eligible,
                AdministrativeActionBatchItemStatuses.Queued
            ],
            AdministrativeActionBatchItemStatuses.Failed,
            now,
            cancellationToken);
        var counts = await batches.CountItemsByStatusAsync(batchId, cancellationToken);
        var issues = AdministrativeActionBatchService.SerializeIssues(
            [AdministrativeActionBatchService.Issue(
                "batch_processing_failed",
                exception.Message)]);
        if (batch.Status == AdministrativeActionBatchStatuses.Cancelled)
        {
            await batches.UpdateAsync(
                AdministrativeActionBatchService.ApplyCounts(
                    AdministrativeActionBatchService.ToUpdate(batch),
                    counts) with
                {
                    Issues = issues,
                    CompletedAt = batch.CompletedAt ?? now,
                    UpdatedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogError(
                exception,
                "Cancelled administrative batch {BatchId} exhausted retries while settling in-flight items.",
                batchId);
            return;
        }
        await batches.UpdateAsync(
            AdministrativeActionBatchService.ApplyCounts(
                AdministrativeActionBatchService.ToUpdate(batch),
                counts) with
            {
                Status = AdministrativeActionBatchStatuses.Failed,
                Issues = issues,
                CompletedAt = now,
                UpdatedAt = now
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogError(
            exception,
            "Administrative batch {BatchId} exhausted durable processing retries.",
            batchId);
    }

    private static AdministrativeActionRequest BuildRequest(
        AdministrativeActionBatchRecord batch,
        AdministrativeActionBatchItemRecord item) =>
        new()
        {
            BatchId = batch.Id,
            BatchItemId = item.Id,
            ExpectedWorkflowDefinitionId = item.WorkflowDefinitionId,
            SourceNodeId = item.SourceNodeId,
            ActionKind = batch.ActionKind,
            FlowId = item.FlowId,
            BoundaryNodeId = batch.BoundaryNodeId,
            MultiInstanceMode = batch.MultiInstanceMode,
            PositionKind = item.PositionKind,
            PositionId = item.PositionId,
            UserTaskId = item.UserTaskId,
            MultiInstanceExecutionId = item.MultiInstanceExecutionId,
            ExpectedTokenId = item.TokenId,
            ExpectedTokenActivationId = item.TokenActivationId,
            ExpectedPositionUpdatedAt = item.CapturedPositionUpdatedAt,
            ExpectedTimerSubscriptionId = item.TimerSubscriptionId,
            ExpectedTimerJobId = item.TimerJobId,
            ExpectedTimerOccurrence = item.CapturedTimerOccurrence,
            ExpectedTimerStatus = item.CapturedTimerStatus,
            ExpectedTimerSubscriptionUpdatedAt = item.CapturedTimerSubscriptionUpdatedAt,
            Reason = batch.Reason,
            Variables = batch.CommonVariables.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.OrdinalIgnoreCase)
        };

    private static ActorContext PreparedActor(
        AdministrativeActionBatchRecord batch,
        IReadOnlyDictionary<string, string>? actorClaims) =>
        new(
            batch.PreparedBy,
            batch.PreparedByRoles,
            SnapshotClaims(actorClaims));

    private static ActorContext ConfirmedActor(
        AdministrativeActionBatchRecord batch,
        IReadOnlyDictionary<string, string>? actorClaims) =>
        new(
            batch.ConfirmedBy
                ?? throw new WorkflowConflictException(
                    $"Administrative batch #{batch.Id} has no confirmer."),
            batch.ConfirmedByRoles
                ?? throw new WorkflowConflictException(
                    $"Administrative batch #{batch.Id} has no confirmer role snapshot."),
            SnapshotClaims(actorClaims));

    private static IReadOnlyDictionary<string, string> SnapshotClaims(
        IReadOnlyDictionary<string, string>? claims) =>
        claims is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(claims, StringComparer.OrdinalIgnoreCase);

    private static WorkflowJobFence Fence(WorkflowJobLeaseRecord lease) =>
        new(
            lease.Job.Id,
            lease.Job.WorkerId
                ?? throw new WorkflowConflictException("A leased workflow job has no worker id."),
            lease.LeaseToken,
            lease.LeaseGeneration);

    private static int Count(IReadOnlyDictionary<string, int> counts, string status) =>
        counts.TryGetValue(status, out var value) ? value : 0;

    private async Task<int> ResolveMaxAffectedTasksAsync(CancellationToken cancellationToken)
    {
        var setting = await engineSettings.GetByKeyAsync(
            AdministrativeActionConstraints.BatchMaxAffectedTasksSetting,
            cancellationToken);
        return int.TryParse(setting?.Value, out var value) && value > 0
            ? Math.Min(value, AdministrativeActionConstraints.MaxAffectedTasks)
            : AdministrativeActionConstraints.MaxAffectedTasks;
    }

    private static string? Limit(string? value) =>
        value is null || value.Length <= AdministrativeActionConstraints.MaxErrorDescriptionLength
            ? value
            : value[..AdministrativeActionConstraints.MaxErrorDescriptionLength];
}

public sealed class WorkflowJobProcessorRouter(
    WorkflowEngineService engine,
    IAdministrativeActionBatchJobProcessor administrativeBatches,
    IInstanceVersionChangeBatchJobProcessor instanceVersionChangeBatches,
    IInstanceVariableUpdateBatchJobProcessor instanceVariableUpdateBatches)
    : IWorkflowJobProcessor
{
    public Task ProcessAsync(
        WorkflowJobLeaseRecord lease,
        CancellationToken cancellationToken) =>
        lease.Job.Kind switch
        {
            WorkflowJobKinds.AdministrativeBatchPrepare
                or WorkflowJobKinds.AdministrativeBatchExecute =>
                administrativeBatches.ProcessAsync(lease, cancellationToken),
            WorkflowJobKinds.InstanceVersionChangeBatchPrepare
                or WorkflowJobKinds.InstanceVersionChangeBatchExecute =>
                instanceVersionChangeBatches.ProcessAsync(lease, cancellationToken),
            WorkflowJobKinds.InstanceVariableUpdateBatchPrepare
                or WorkflowJobKinds.InstanceVariableUpdateBatchExecute =>
                instanceVariableUpdateBatches.ProcessAsync(lease, cancellationToken),
            _ => engine.ProcessAsync(lease, cancellationToken)
        };
}
