using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

public sealed class InstanceVersionChangeBatchJobProcessor(
    IServiceScopeFactory scopeFactory,
    IWorkflowJobRepository jobs,
    TimeProvider timeProvider,
    ILogger<InstanceVersionChangeBatchJobProcessor> logger)
    : IInstanceVersionChangeBatchJobProcessor
{
    private const int ItemPageSize = 100;

    public async Task ProcessAsync(
        WorkflowJobLeaseRecord lease,
        CancellationToken cancellationToken)
    {
        if (lease.Job.Kind is not (
                WorkflowJobKinds.InstanceVersionChangeBatchPrepare
                or WorkflowJobKinds.InstanceVersionChangeBatchExecute))
        {
            throw new WorkflowConflictException(
                $"Job #{lease.Job.Id} is not an instance version-change batch job.");
        }

        var payload = lease.Job.Payload?.Deserialize<InstanceVersionChangeBatchJobPayload>();
        if (payload is null || payload.BatchId <= 0)
        {
            await jobs.OpenIncidentAsync(
                Fence(lease),
                "invalid_instance_version_change_batch_payload",
                $"Instance version-change batch job #{lease.Job.Id} has no valid batch identity.",
                null,
                cancellationToken);
            return;
        }

        try
        {
            if (lease.Job.Kind == WorkflowJobKinds.InstanceVersionChangeBatchPrepare)
            {
                await PrepareAsync(payload.BatchId, payload.ActorClaims, cancellationToken);
            }
            else
            {
                await ExecuteAsync(
                    payload.BatchId,
                    payload.ActorClaims,
                    lease.AttemptNumber >= lease.Job.MaxAttempts,
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

        // The dispatcher owns heartbeats and retry scheduling. Acknowledgement
        // remains outside the phase-failure handler because the business result
        // may already be committed even if the final job fence was lost.
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
                .GetRequiredService<IInstanceVersionChangeBatchRepository>();
            var batch = await repository.GetAsync(batchId, false, cancellationToken);
            if (batch is null
                || batch.Status == InstanceVersionChangeBatchStatuses.Cancelled
                || batch.Status != InstanceVersionChangeBatchStatuses.Preparing)
            {
                return;
            }

            var items = await repository.ListItemsForProcessingAsync(
                batchId,
                [InstanceVersionChangeBatchItemStatuses.Preparing],
                null,
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
            await RefreshPreparationProgressAsync(batchId, cancellationToken);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider
            .GetRequiredService<IInstanceVersionChangeBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var current = await batches.GetAsync(batchId, true, cancellationToken);
        if (current is null
            || current.Status != InstanceVersionChangeBatchStatuses.Preparing)
        {
            return;
        }

        var counts = await batches.CountItemsByStatusAsync(batchId, cancellationToken);
        var warningCount = await batches.CountItemsWithWarningsAsync(batchId, cancellationToken);
        var staleCount = await batches.CountStaleItemsAsync(batchId, cancellationToken);
        var eligibleCount = Count(counts, InstanceVersionChangeBatchItemStatuses.Eligible);
        var now = timeProvider.GetUtcNow();
        await batches.UpdateAsync(
            InstanceVersionChangeBatchService.ApplyCounts(
                InstanceVersionChangeBatchService.ToUpdate(current),
                counts) with
            {
                Status = eligibleCount == 0
                    ? InstanceVersionChangeBatchStatuses.CompletedWithIssues
                    : InstanceVersionChangeBatchStatuses.Ready,
                WarningItemCount = warningCount,
                StaleItemCount = staleCount,
                PreparedAt = now,
                CompletedAt = eligibleCount == 0 ? now : null,
                UpdatedAt = now
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Instance version-change batch {BatchId} preparation completed with {EligibleCount} eligible items.",
            batchId,
            eligibleCount);
    }

    private async Task PrepareItemAsync(
        long itemId,
        IReadOnlyDictionary<string, string>? actorClaims,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider
            .GetRequiredService<IInstanceVersionChangeBatchRepository>();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngineService>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var observed = await batches.GetItemAsync(itemId, false, cancellationToken);
        if (observed is null)
        {
            return;
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // Every worker mutation follows the aggregate lock order: batch, then item.
        var batch = await batches.GetAsync(observed.BatchId, true, cancellationToken);
        if (batch is null)
        {
            return;
        }
        var item = await batches.GetItemAsync(itemId, true, cancellationToken);
        if (item is null
            || item.Status != InstanceVersionChangeBatchItemStatuses.Preparing)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (batch.Status == InstanceVersionChangeBatchStatuses.Cancelled)
        {
            await batches.UpdateItemAsync(
                InstanceVersionChangeBatchService.ToItemUpdate(item) with
                {
                    Status = InstanceVersionChangeBatchItemStatuses.Cancelled,
                    UpdatedAt = now,
                    CompletedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (batch.Status != InstanceVersionChangeBatchStatuses.Preparing)
        {
            return;
        }

        var blockers = new List<InstanceVersionChangeIssueDto>();
        IReadOnlyList<InstanceVersionChangeIssueDto> warnings = [];
        if (item.CapturedSourceWorkflowDefinitionId
            != batch.SourceWorkflowDefinitionId)
        {
            blockers.Add(InstanceVersionChangeBatchService.Issue(
                "source_definition_mismatch",
                "The frozen item no longer matches the batch source workflow version.",
                "instance",
                item.InstanceId));
        }
        else
        {
            try
            {
                var preview = await engine.PreviewInstanceVersionChangeAsync(
                    item.InstanceId,
                    batch.TargetWorkflowDefinitionId,
                    PreparedActor(batch, actorClaims),
                    cancellationToken);
                if (preview is null)
                {
                    blockers.Add(InstanceVersionChangeBatchService.Issue(
                        "instance_not_found",
                        "The selected workflow instance no longer exists.",
                        "instance",
                        item.InstanceId));
                }
                else
                {
                    warnings = preview.Warnings;
                    if (preview.ExpectedSourceWorkflowId
                            != item.CapturedSourceWorkflowDefinitionId
                        || preview.ExpectedUpdatedAt
                            != item.CapturedInstanceUpdatedAt)
                    {
                        blockers.Add(InstanceVersionChangeBatchService.Issue(
                            "stale_since_selection",
                            "The workflow instance changed after the batch selection was frozen.",
                            "instance",
                            item.InstanceId));
                    }
                    else
                    {
                        blockers.AddRange(preview.Blockers);
                    }
                }
            }
            catch (Exception exception) when (IsExpectedBusinessException(exception))
            {
                blockers.Add(InstanceVersionChangeBatchService.Issue(
                    ExceptionCode(exception),
                    exception.Message,
                    "instance",
                    item.InstanceId));
            }
        }

        var eligible = blockers.Count == 0;
        await batches.UpdateItemAsync(
            InstanceVersionChangeBatchService.ToItemUpdate(item) with
            {
                Status = eligible
                    ? InstanceVersionChangeBatchItemStatuses.Eligible
                    : InstanceVersionChangeBatchItemStatuses.Ineligible,
                Blockers = InstanceVersionChangeBatchService.SerializeIssues(blockers),
                Warnings = InstanceVersionChangeBatchService.SerializeIssues(warnings),
                ErrorCode = eligible ? null : blockers[0].Code,
                ErrorDescription = eligible ? null : Limit(blockers[0].Message),
                PreparedAt = now,
                CompletedAt = eligible ? null : now,
                UpdatedAt = now
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RefreshPreparationProgressAsync(
        long batchId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider
            .GetRequiredService<IInstanceVersionChangeBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(batchId, true, cancellationToken);
        if (batch is null
            || batch.Status != InstanceVersionChangeBatchStatuses.Preparing)
        {
            return;
        }

        var counts = await batches.CountItemsByStatusAsync(batchId, cancellationToken);
        var warningCount = await batches.CountItemsWithWarningsAsync(batchId, cancellationToken);
        var staleCount = await batches.CountStaleItemsAsync(batchId, cancellationToken);
        await batches.UpdateAsync(
            InstanceVersionChangeBatchService.ApplyCounts(
                InstanceVersionChangeBatchService.ToUpdate(batch),
                counts) with
            {
                WarningItemCount = warningCount,
                StaleItemCount = staleCount,
                UpdatedAt = timeProvider.GetUtcNow()
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ExecuteAsync(
        long batchId,
        IReadOnlyDictionary<string, string>? actorClaims,
        bool isFinalAttempt,
        CancellationToken cancellationToken)
    {
        if (!await MarkRunningAsync(batchId, cancellationToken))
        {
            return;
        }

        long? afterItemId = null;
        Exception? firstUnexpectedItemFailure = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var pageScope = scopeFactory.CreateAsyncScope();
            var repository = pageScope.ServiceProvider
                .GetRequiredService<IInstanceVersionChangeBatchRepository>();
            var batch = await repository.GetAsync(batchId, false, cancellationToken);
            if (batch is null
                || batch.Status is not (
                    InstanceVersionChangeBatchStatuses.Running
                    or InstanceVersionChangeBatchStatuses.Cancelled))
            {
                return;
            }
            var items = await repository.ListItemsForProcessingAsync(
                batchId,
                [InstanceVersionChangeBatchItemStatuses.Queued],
                afterItemId,
                ItemPageSize,
                cancellationToken);
            if (items.Count == 0)
            {
                break;
            }
            foreach (var item in items)
            {
                var unexpectedFailure = await ExecuteItemAsync(
                    item.Id,
                    actorClaims,
                    isFinalAttempt,
                    cancellationToken);
                firstUnexpectedItemFailure ??= unexpectedFailure;
            }
            // A queued poison item deliberately remains queued before the final
            // durable attempt. Advance through the frozen membership so it
            // cannot starve later independent items during this attempt.
            afterItemId = items[^1].Id;
            await RefreshCountsAsync(batchId, cancellationToken);
        }

        if (firstUnexpectedItemFailure is not null && !isFinalAttempt)
        {
            ExceptionDispatchInfo.Capture(firstUnexpectedItemFailure).Throw();
        }

        await FinalizeAsync(batchId, cancellationToken);
    }

    private async Task<bool> MarkRunningAsync(
        long batchId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider
            .GetRequiredService<IInstanceVersionChangeBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(batchId, true, cancellationToken);
        if (batch is null)
        {
            return false;
        }
        if (batch.Status is InstanceVersionChangeBatchStatuses.Running
            or InstanceVersionChangeBatchStatuses.Cancelled)
        {
            return true;
        }
        if (batch.Status != InstanceVersionChangeBatchStatuses.Queued)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        await batches.UpdateAsync(
            InstanceVersionChangeBatchService.ToUpdate(batch) with
            {
                Status = InstanceVersionChangeBatchStatuses.Running,
                StartedAt = batch.StartedAt ?? now,
                UpdatedAt = now
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<Exception?> ExecuteItemAsync(
        long itemId,
        IReadOnlyDictionary<string, string>? actorClaims,
        bool isFinalAttempt,
        CancellationToken cancellationToken)
    {
        InstanceVersionChangeBatchRecord batch;
        InstanceVersionChangeBatchItemRecord item;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<IInstanceVersionChangeBatchRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var observed = await repository.GetItemAsync(itemId, false, cancellationToken);
            if (observed is null)
            {
                return null;
            }
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            batch = await repository.GetAsync(observed.BatchId, true, cancellationToken)
                ?? null!;
            if (batch is null)
            {
                return null;
            }
            item = await repository.GetItemAsync(itemId, true, cancellationToken)
                ?? null!;
            if (item is null
                || item.Status != InstanceVersionChangeBatchItemStatuses.Queued)
            {
                return null;
            }

            var now = timeProvider.GetUtcNow();
            if (batch.Status == InstanceVersionChangeBatchStatuses.Cancelled
                && item.StartedAt is null)
            {
                await repository.UpdateItemAsync(
                    InstanceVersionChangeBatchService.ToItemUpdate(item) with
                    {
                        Status = InstanceVersionChangeBatchItemStatuses.Cancelled,
                        UpdatedAt = now,
                        CompletedAt = now
                    },
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            if (batch.Status is not (
                    InstanceVersionChangeBatchStatuses.Running
                    or InstanceVersionChangeBatchStatuses.Cancelled))
            {
                return null;
            }
            if (item.StartedAt is null)
            {
                item = await repository.UpdateItemAsync(
                    InstanceVersionChangeBatchService.ToItemUpdate(item) with
                    {
                        StartedAt = now,
                        UpdatedAt = now
                    },
                    cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }

        InstanceVersionChangeBatchExecutionOutcome? outcome = null;
        Exception? unexpectedFailure = null;
        await using (var executionScope = scopeFactory.CreateAsyncScope())
        {
            var executor = executionScope.ServiceProvider
                .GetRequiredService<IInstanceVersionChangeBatchExecutor>();
            try
            {
                outcome = await executor.ExecuteInstanceVersionChangeBatchItemAsync(
                    new InstanceVersionChangeBatchExecutionRequest(
                        batch.Id,
                        item.Id,
                        item.InstanceId,
                        item.CapturedSourceWorkflowDefinitionId,
                        item.CapturedInstanceUpdatedAt,
                        batch.TargetWorkflowDefinitionId,
                        batch.Reason),
                    ConfirmedActor(batch, actorClaims),
                    cancellationToken);
            }
            catch (Exception exception) when (IsExpectedBusinessException(exception))
            {
                outcome = new InstanceVersionChangeBatchExecutionOutcome(
                    false,
                    null,
                    ExceptionCode(exception),
                    exception.Message,
                    [],
                    []);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                unexpectedFailure = exception;
            }
        }

        await using var resultScope = scopeFactory.CreateAsyncScope();
        var resultRepository = resultScope.ServiceProvider
            .GetRequiredService<IInstanceVersionChangeBatchRepository>();
        var current = await resultRepository.GetItemAsync(item.Id, false, cancellationToken);
        if (current is null
            || current.Status != InstanceVersionChangeBatchItemStatuses.Queued)
        {
            return null;
        }
        if (unexpectedFailure is not null)
        {
            if (!isFinalAttempt)
            {
                return unexpectedFailure;
            }

            var failedAt = timeProvider.GetUtcNow();
            await resultRepository.UpdateItemAsync(
                InstanceVersionChangeBatchService.ToItemUpdate(current) with
                {
                    Status = InstanceVersionChangeBatchItemStatuses.Failed,
                    ErrorCode = "unexpected_processing_error",
                    ErrorDescription = Limit(unexpectedFailure.Message),
                    UpdatedAt = failedAt,
                    CompletedAt = failedAt
                },
                cancellationToken);
            logger.LogError(
                unexpectedFailure,
                "Instance version-change batch {BatchId} item {BatchItemId} exhausted durable processing retries.",
                batch.Id,
                item.Id);
            return null;
        }
        if (outcome is null)
        {
            throw new WorkflowConflictException(
                "The version-change batch executor returned no outcome.");
        }
        if (outcome.Succeeded)
        {
            // Success must update the instance, audit row, and item in one
            // engine transaction. Never manufacture success in the worker.
            throw new WorkflowConflictException(
                "The version change returned success without atomically completing its batch item.");
        }

        var completedAt = timeProvider.GetUtcNow();
        await resultRepository.UpdateItemAsync(
            InstanceVersionChangeBatchService.ToItemUpdate(current) with
            {
                Status = InstanceVersionChangeBatchItemStatuses.Skipped,
                Blockers = InstanceVersionChangeBatchService.SerializeIssues(outcome.Blockers),
                // A stale execution fence may prevent a fresh compatibility
                // evaluation. Keep the immutable preparation warnings unless
                // execution produced a newer warning set.
                Warnings = outcome.Warnings.Count == 0
                    ? current.Warnings
                    : InstanceVersionChangeBatchService.SerializeIssues(outcome.Warnings),
                ErrorCode = LimitCode(outcome.Code ?? "skipped"),
                ErrorDescription = Limit(
                    outcome.Description
                    ?? "The item was skipped during execution revalidation."),
                UpdatedAt = completedAt,
                CompletedAt = completedAt
            },
            cancellationToken);
        return null;
    }

    private async Task RefreshCountsAsync(
        long batchId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider
            .GetRequiredService<IInstanceVersionChangeBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(batchId, true, cancellationToken);
        if (batch is null
            || batch.Status is not (
                InstanceVersionChangeBatchStatuses.Running
                or InstanceVersionChangeBatchStatuses.Cancelled))
        {
            return;
        }
        var counts = await batches.CountItemsByStatusAsync(batchId, cancellationToken);
        var warningCount = await batches.CountItemsWithWarningsAsync(batchId, cancellationToken);
        var staleCount = await batches.CountStaleItemsAsync(batchId, cancellationToken);
        await batches.UpdateAsync(
            InstanceVersionChangeBatchService.ApplyCounts(
                InstanceVersionChangeBatchService.ToUpdate(batch),
                counts) with
            {
                WarningItemCount = warningCount,
                StaleItemCount = staleCount,
                UpdatedAt = timeProvider.GetUtcNow()
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task FinalizeAsync(
        long batchId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider
            .GetRequiredService<IInstanceVersionChangeBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(batchId, true, cancellationToken);
        if (batch is null
            || batch.Status is not (
                InstanceVersionChangeBatchStatuses.Running
                or InstanceVersionChangeBatchStatuses.Cancelled))
        {
            return;
        }

        var counts = await batches.CountItemsByStatusAsync(batchId, cancellationToken);
        if (Count(counts, InstanceVersionChangeBatchItemStatuses.Queued) > 0)
        {
            return;
        }
        var warningCount = await batches.CountItemsWithWarningsAsync(batchId, cancellationToken);
        var staleCount = await batches.CountStaleItemsAsync(batchId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (batch.Status == InstanceVersionChangeBatchStatuses.Cancelled)
        {
            await batches.UpdateAsync(
                InstanceVersionChangeBatchService.ApplyCounts(
                    InstanceVersionChangeBatchService.ToUpdate(batch),
                    counts) with
                {
                    WarningItemCount = warningCount,
                    StaleItemCount = staleCount,
                    CompletedAt = batch.CompletedAt ?? now,
                    UpdatedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var hasIssues =
            Count(counts, InstanceVersionChangeBatchItemStatuses.Ineligible)
            + Count(counts, InstanceVersionChangeBatchItemStatuses.Skipped)
            + Count(counts, InstanceVersionChangeBatchItemStatuses.Failed)
            + Count(counts, InstanceVersionChangeBatchItemStatuses.Cancelled) > 0;
        await batches.UpdateAsync(
            InstanceVersionChangeBatchService.ApplyCounts(
                InstanceVersionChangeBatchService.ToUpdate(batch),
                counts) with
            {
                Status = hasIssues
                    ? InstanceVersionChangeBatchStatuses.CompletedWithIssues
                    : InstanceVersionChangeBatchStatuses.Completed,
                WarningItemCount = warningCount,
                StaleItemCount = staleCount,
                CompletedAt = now,
                UpdatedAt = now
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Instance version-change batch {BatchId} execution completed with issues={HasIssues}.",
            batchId,
            hasIssues);
    }

    private async Task MarkFailedAsync(
        long batchId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider
            .GetRequiredService<IInstanceVersionChangeBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(batchId, true, cancellationToken);
        if (batch is null
            || batch.Status is InstanceVersionChangeBatchStatuses.Ready
                or InstanceVersionChangeBatchStatuses.Completed
                or InstanceVersionChangeBatchStatuses.CompletedWithIssues
                or InstanceVersionChangeBatchStatuses.Failed)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var failureDescription = Limit(exception.Message);
        await batches.FailItemsAsync(
            batchId,
            [
                InstanceVersionChangeBatchItemStatuses.Preparing,
                InstanceVersionChangeBatchItemStatuses.Eligible,
                InstanceVersionChangeBatchItemStatuses.Queued
            ],
            "batch_processing_failed",
            failureDescription,
            now,
            cancellationToken);
        var counts = await batches.CountItemsByStatusAsync(batchId, cancellationToken);
        var warningCount = await batches.CountItemsWithWarningsAsync(batchId, cancellationToken);
        var staleCount = await batches.CountStaleItemsAsync(batchId, cancellationToken);
        var issues = InstanceVersionChangeBatchService.SerializeIssues(
            [InstanceVersionChangeBatchService.Issue(
                "batch_processing_failed",
                failureDescription)]);
        var update = InstanceVersionChangeBatchService.ApplyCounts(
                InstanceVersionChangeBatchService.ToUpdate(batch),
                counts) with
            {
                WarningItemCount = warningCount,
                StaleItemCount = staleCount,
                Issues = issues,
                CompletedAt = batch.CompletedAt ?? now,
                UpdatedAt = now
            };
        if (batch.Status != InstanceVersionChangeBatchStatuses.Cancelled)
        {
            update = update with { Status = InstanceVersionChangeBatchStatuses.Failed };
        }
        await batches.UpdateAsync(update, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogError(
            exception,
            "Instance version-change batch {BatchId} exhausted durable processing retries.",
            batchId);
    }

    private static ActorContext PreparedActor(
        InstanceVersionChangeBatchRecord batch,
        IReadOnlyDictionary<string, string>? claims) =>
        new(batch.PreparedBy, batch.PreparedByRoles, SnapshotClaims(claims));

    private static ActorContext ConfirmedActor(
        InstanceVersionChangeBatchRecord batch,
        IReadOnlyDictionary<string, string>? claims) =>
        new(
            batch.ConfirmedBy
                ?? throw new WorkflowConflictException(
                    $"Instance version-change batch #{batch.Id} has no confirmer."),
            batch.ConfirmedByRoles
                ?? throw new WorkflowConflictException(
                    $"Instance version-change batch #{batch.Id} has no confirmer role snapshot."),
            SnapshotClaims(claims));

    private static IReadOnlyDictionary<string, string> SnapshotClaims(
        IReadOnlyDictionary<string, string>? claims) =>
        claims is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(claims, StringComparer.OrdinalIgnoreCase);

    private static bool IsExpectedBusinessException(Exception exception) =>
        exception is WorkflowDomainException
            or WorkflowConflictException
            or WorkflowForbiddenException
            or WorkflowUnauthorizedException;

    private static string ExceptionCode(Exception exception) => exception switch
    {
        WorkflowConflictException => "stale",
        WorkflowForbiddenException or WorkflowUnauthorizedException =>
            "authentication_changed",
        _ => "ineligible"
    };

    private static WorkflowJobFence Fence(WorkflowJobLeaseRecord lease) =>
        new(
            lease.Job.Id,
            lease.Job.WorkerId
                ?? throw new WorkflowConflictException(
                    "A leased workflow job has no worker id."),
            lease.LeaseToken,
            lease.LeaseGeneration);

    private static int Count(
        IReadOnlyDictionary<string, int> counts,
        string status) => counts.TryGetValue(status, out var value) ? value : 0;

    private static string Limit(string value) =>
        value.EnumerateRunes().Count()
            <= InstanceVersionChangeBatchConstraints.MaxErrorDescriptionLength
            ? value
            : string.Concat(value.EnumerateRunes()
                .Take(InstanceVersionChangeBatchConstraints.MaxErrorDescriptionLength)
                .Select(rune => rune.ToString()));

    private static string LimitCode(string value) =>
        value.Length <= InstanceVersionChangeBatchConstraints.MaxErrorCodeLength
            ? value
            : value[..InstanceVersionChangeBatchConstraints.MaxErrorCodeLength];
}
