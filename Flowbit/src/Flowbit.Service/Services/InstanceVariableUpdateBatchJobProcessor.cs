using System.Runtime.ExceptionServices;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

public sealed class InstanceVariableUpdateBatchJobProcessor(
    IServiceScopeFactory scopeFactory,
    IWorkflowJobRepository jobs,
    TimeProvider timeProvider,
    ILogger<InstanceVariableUpdateBatchJobProcessor> logger)
    : IInstanceVariableUpdateBatchJobProcessor
{
    private const int ItemPageSize = 100;

    public async Task ProcessAsync(
        WorkflowJobLeaseRecord lease,
        CancellationToken cancellationToken)
    {
        if (lease.Job.Kind is not (
                WorkflowJobKinds.InstanceVariableUpdateBatchPrepare
                or WorkflowJobKinds.InstanceVariableUpdateBatchExecute))
        {
            throw new WorkflowConflictException(
                $"Job #{lease.Job.Id} is not an instance-variable update batch job.");
        }

        var payload = lease.Job.Payload?.Deserialize<InstanceVariableUpdateBatchJobPayload>();
        var expectedPhase = lease.Job.Kind == WorkflowJobKinds.InstanceVariableUpdateBatchPrepare
            ? InstanceVariableUpdateBatchPhases.Prepare
            : InstanceVariableUpdateBatchPhases.Execute;
        if (payload is null
            || payload.BatchId <= 0
            || payload.WorkflowDefinitionId <= 0
            || payload.WorkflowDefinitionId != lease.Job.WorkflowDefinitionId
            || !string.Equals(payload.Phase, expectedPhase, StringComparison.Ordinal)
            || !string.Equals(lease.Job.Phase, expectedPhase, StringComparison.Ordinal))
        {
            await jobs.OpenIncidentAsync(
                Fence(lease),
                "invalid_instance_variable_update_batch_payload",
                $"Instance-variable update batch job #{lease.Job.Id} has an invalid batch, workflow-version, or phase identity.",
                null,
                cancellationToken);
            return;
        }

        try
        {
            if (payload.Phase == InstanceVariableUpdateBatchPhases.Prepare)
            {
                await PrepareAsync(
                    payload.BatchId,
                    payload.WorkflowDefinitionId,
                    cancellationToken);
            }
            else
            {
                await ExecuteAsync(
                    payload.BatchId,
                    payload.WorkflowDefinitionId,
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
                await MarkGroupFailedAsync(
                    payload.BatchId,
                    payload.WorkflowDefinitionId,
                    payload.Phase,
                    exception,
                    cancellationToken);
            }
            throw;
        }

        await jobs.CompleteAsync(Fence(lease), cancellationToken);
    }

    private async Task PrepareAsync(
        long batchId,
        long workflowDefinitionId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var pageScope = scopeFactory.CreateAsyncScope();
            var repository = pageScope.ServiceProvider
                .GetRequiredService<IInstanceVariableUpdateBatchRepository>();
            var batch = await repository.GetAsync(batchId, false, cancellationToken);
            if (batch is null
                || batch.Status == InstanceVariableUpdateBatchStatuses.Cancelled
                || batch.Status != InstanceVariableUpdateBatchStatuses.Preparing)
            {
                return;
            }
            var items = await repository.ListItemsForProcessingAsync(
                batchId,
                workflowDefinitionId,
                [InstanceVariableUpdateBatchItemStatuses.Preparing],
                afterItemId: null,
                ItemPageSize,
                cancellationToken);
            if (items.Count == 0)
            {
                break;
            }
            foreach (var item in items)
            {
                await PrepareItemAsync(item.Id, cancellationToken);
            }
            await RefreshCountsAsync(batchId, cancellationToken);
        }

        await ReconcilePreparationAsync(batchId, cancellationToken);
    }

    private async Task PrepareItemAsync(
        long itemId,
        CancellationToken cancellationToken)
    {
        InstanceVariableUpdateBatchItemRecord observed;
        InstanceVariableUpdateBatchRecord observedBatch;
        IReadOnlyList<InstanceVariableUpdateOutcomePlanDto> plan = [];
        var warnings = new List<InstanceVariableUpdateIssueDto>();
        InstanceVariableUpdateIssueDto? blocker = null;

        await using (var observationScope = scopeFactory.CreateAsyncScope())
        {
            var batchRepository = observationScope.ServiceProvider
                .GetRequiredService<IInstanceVariableUpdateBatchRepository>();
            var runtime = observationScope.ServiceProvider
                .GetRequiredService<IWorkflowRuntimeRepository>();
            var jobRepository = observationScope.ServiceProvider
                .GetRequiredService<IWorkflowJobRepository>();
            observed = await batchRepository.GetItemAsync(itemId, false, cancellationToken)
                ?? null!;
            if (observed is null
                || observed.Status != InstanceVariableUpdateBatchItemStatuses.Preparing)
            {
                return;
            }
            observedBatch = await batchRepository.GetAsync(
                observed.BatchId,
                false,
                cancellationToken) ?? null!;
            if (observedBatch is null
                || observedBatch.Status != InstanceVariableUpdateBatchStatuses.Preparing)
            {
                return;
            }

            var instance = await runtime.GetInstanceAsync(observed.InstanceId, cancellationToken);
            if (instance is null)
            {
                blocker = InstanceVariableUpdateBatchService.Issue(
                    "instance_not_found",
                    "The selected workflow instance no longer exists.");
            }
            else if (!string.Equals(
                         instance.Status,
                         WorkflowInstanceStatuses.Running,
                         StringComparison.OrdinalIgnoreCase))
            {
                blocker = InstanceVariableUpdateBatchService.Issue(
                    "instance_not_running",
                    "The selected workflow instance is no longer running.");
            }
            else if (!string.Equals(
                         instance.WorkflowKey,
                         observedBatch.WorkflowKey,
                         StringComparison.Ordinal))
            {
                blocker = InstanceVariableUpdateBatchService.Issue(
                    "workflow_family_changed",
                    "The selected workflow instance no longer belongs to the selected workflow family.");
            }
            else
            {
                var current = await runtime.LoadLatestVariableVersionsAsync(
                    observed.InstanceId,
                    cancellationToken);
                plan = ClassifyPlan(
                    InstanceVariableUpdateBatchService.DeserializeWrites(
                        observedBatch.Variables),
                    current);
                var openJobs = await jobRepository.ListOpenByInstanceAsync(
                    observed.InstanceId,
                    forUpdate: false,
                    cancellationToken);
                if (openJobs.Count > 0)
                {
                    warnings.Add(InstanceVariableUpdateBatchService.Issue(
                        "active_durable_jobs",
                        $"The instance has {openJobs.Count} active durable job(s). Jobs that already captured variables may continue using their frozen snapshot."));
                }
            }
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider
            .GetRequiredService<IInstanceVariableUpdateBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(observed.BatchId, true, cancellationToken);
        if (batch is null)
        {
            return;
        }
        var item = await batches.GetItemAsync(itemId, true, cancellationToken);
        if (item is null
            || item.Status != InstanceVariableUpdateBatchItemStatuses.Preparing)
        {
            return;
        }
        var now = timeProvider.GetUtcNow();
        if (batch.Status == InstanceVariableUpdateBatchStatuses.Cancelled)
        {
            await batches.UpdateItemAsync(
                InstanceVariableUpdateBatchService.ToItemUpdate(item) with
                {
                    Status = InstanceVariableUpdateBatchItemStatuses.Cancelled,
                    UpdatedAt = now,
                    CompletedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (batch.Status != InstanceVariableUpdateBatchStatuses.Preparing)
        {
            return;
        }

        var eligible = blocker is null;
        await batches.UpdateItemAsync(
            InstanceVariableUpdateBatchService.ToItemUpdate(item) with
            {
                Status = eligible
                    ? InstanceVariableUpdateBatchItemStatuses.Eligible
                    : InstanceVariableUpdateBatchItemStatuses.Ineligible,
                Plan = plan.Count == 0 ? null : JsonSerializer.SerializeToElement(plan),
                Warnings = InstanceVariableUpdateBatchService.SerializeIssues(warnings),
                ErrorCode = blocker?.Code,
                ErrorDescription = blocker?.Message,
                PreparedAt = now,
                CompletedAt = eligible ? null : now,
                UpdatedAt = now
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ReconcilePreparationAsync(
        long batchId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider
            .GetRequiredService<IInstanceVariableUpdateBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(batchId, true, cancellationToken);
        if (batch is null
            || batch.Status != InstanceVariableUpdateBatchStatuses.Preparing)
        {
            return;
        }
        var counts = await batches.CountItemsByStatusAsync(batchId, cancellationToken);
        if (Count(counts, InstanceVariableUpdateBatchItemStatuses.Preparing) > 0)
        {
            return;
        }
        var warningCount = await batches.CountItemsWithWarningsAsync(
            batchId,
            cancellationToken);
        var eligible = Count(counts, InstanceVariableUpdateBatchItemStatuses.Eligible);
        var now = timeProvider.GetUtcNow();
        await batches.UpdateAsync(
            InstanceVariableUpdateBatchService.ApplyCounts(
                InstanceVariableUpdateBatchService.ToUpdate(batch),
                counts) with
            {
                Status = eligible > 0
                    ? InstanceVariableUpdateBatchStatuses.Ready
                    : InstanceVariableUpdateBatchStatuses.CompletedWithIssues,
                WarningItemCount = warningCount,
                PreparedAt = now,
                CompletedAt = eligible == 0 ? now : null,
                UpdatedAt = now
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Instance-variable update batch {BatchId} preparation settled with {EligibleCount} eligible items.",
            batchId,
            eligible);
    }

    private async Task ExecuteAsync(
        long batchId,
        long workflowDefinitionId,
        IReadOnlyDictionary<string, string>? actorClaims,
        bool isFinalAttempt,
        CancellationToken cancellationToken)
    {
        if (!await MarkRunningAsync(batchId, cancellationToken))
        {
            return;
        }

        long? afterItemId = null;
        Exception? firstUnexpectedFailure = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var pageScope = scopeFactory.CreateAsyncScope();
            var repository = pageScope.ServiceProvider
                .GetRequiredService<IInstanceVariableUpdateBatchRepository>();
            var batch = await repository.GetAsync(batchId, false, cancellationToken);
            if (batch is null
                || batch.Status is not (
                    InstanceVariableUpdateBatchStatuses.Running
                    or InstanceVariableUpdateBatchStatuses.Cancelled))
            {
                return;
            }
            var items = await repository.ListItemsForProcessingAsync(
                batchId,
                workflowDefinitionId,
                [InstanceVariableUpdateBatchItemStatuses.Queued],
                afterItemId,
                ItemPageSize,
                cancellationToken);
            if (items.Count == 0)
            {
                break;
            }
            foreach (var item in items)
            {
                var failure = await ExecuteItemAsync(
                    item.Id,
                    actorClaims,
                    isFinalAttempt,
                    cancellationToken);
                firstUnexpectedFailure ??= failure;
            }
            afterItemId = items[^1].Id;
            await RefreshCountsAsync(batchId, cancellationToken);
        }

        if (firstUnexpectedFailure is not null && !isFinalAttempt)
        {
            ExceptionDispatchInfo.Capture(firstUnexpectedFailure).Throw();
        }
        await ReconcileExecutionAsync(batchId, cancellationToken);
    }

    private async Task<bool> MarkRunningAsync(
        long batchId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider
            .GetRequiredService<IInstanceVariableUpdateBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(batchId, true, cancellationToken);
        if (batch is null)
        {
            return false;
        }
        if (batch.Status is InstanceVariableUpdateBatchStatuses.Running
            or InstanceVariableUpdateBatchStatuses.Cancelled)
        {
            return true;
        }
        if (batch.Status != InstanceVariableUpdateBatchStatuses.Queued)
        {
            return false;
        }
        var now = timeProvider.GetUtcNow();
        await batches.UpdateAsync(
            InstanceVariableUpdateBatchService.ToUpdate(batch) with
            {
                Status = InstanceVariableUpdateBatchStatuses.Running,
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
        InstanceVariableUpdateBatchRecord batch;
        InstanceVariableUpdateBatchItemRecord item;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<IInstanceVariableUpdateBatchRepository>();
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
                || item.Status != InstanceVariableUpdateBatchItemStatuses.Queued)
            {
                return null;
            }
            var now = timeProvider.GetUtcNow();
            if (batch.Status == InstanceVariableUpdateBatchStatuses.Cancelled
                && item.StartedAt is null)
            {
                await repository.UpdateItemAsync(
                    InstanceVariableUpdateBatchService.ToItemUpdate(item) with
                    {
                        Status = InstanceVariableUpdateBatchItemStatuses.Cancelled,
                        UpdatedAt = now,
                        CompletedAt = now
                    },
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            if (batch.Status is not (
                    InstanceVariableUpdateBatchStatuses.Running
                    or InstanceVariableUpdateBatchStatuses.Cancelled))
            {
                return null;
            }
            if (item.StartedAt is null)
            {
                item = await repository.UpdateItemAsync(
                    InstanceVariableUpdateBatchService.ToItemUpdate(item) with
                    {
                        StartedAt = now,
                        UpdatedAt = now
                    },
                    cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }

        InstanceVariableUpdateExecutionOutcome? outcome = null;
        Exception? unexpectedFailure = null;
        await using (var executionScope = scopeFactory.CreateAsyncScope())
        {
            var executor = executionScope.ServiceProvider
                .GetRequiredService<IInstanceVariableUpdateExecutor>();
            try
            {
                outcome = await executor.ExecuteAsync(
                    new InstanceVariableUpdateExecutionRequest(
                        item.InstanceId,
                        batch.WorkflowKey,
                        InstanceVariableUpdateBatchService.DeserializeWrites(batch.Variables),
                        batch.Reason,
                        batch.Id,
                        item.Id),
                    ConfirmedActor(batch, actorClaims),
                    cancellationToken);
            }
            catch (Exception exception) when (IsExpectedBusinessException(exception))
            {
                outcome = new InstanceVariableUpdateExecutionOutcome(
                    null,
                    ExceptionCode(exception),
                    exception.Message);
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
            .GetRequiredService<IInstanceVariableUpdateBatchRepository>();
        var current = await resultRepository.GetItemAsync(item.Id, false, cancellationToken);
        if (current is null
            || current.Status != InstanceVariableUpdateBatchItemStatuses.Queued)
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
                InstanceVariableUpdateBatchService.ToItemUpdate(current) with
                {
                    Status = InstanceVariableUpdateBatchItemStatuses.Failed,
                    ErrorCode = "unexpected_processing_error",
                    ErrorDescription = Limit(unexpectedFailure.Message),
                    UpdatedAt = failedAt,
                    CompletedAt = failedAt
                },
                cancellationToken);
            logger.LogError(
                unexpectedFailure,
                "Instance-variable update batch {BatchId} item {BatchItemId} exhausted durable retries.",
                batch.Id,
                item.Id);
            return null;
        }
        if (outcome is null)
        {
            throw new WorkflowConflictException(
                "The variable-update executor returned no outcome.");
        }
        if (outcome.Succeeded)
        {
            throw new WorkflowConflictException(
                "The variable update returned success without atomically completing its batch item.");
        }
        var completedAt = timeProvider.GetUtcNow();
        await resultRepository.UpdateItemAsync(
            InstanceVariableUpdateBatchService.ToItemUpdate(current) with
            {
                Status = InstanceVariableUpdateBatchItemStatuses.Skipped,
                ErrorCode = LimitCode(outcome.SkipCode ?? "skipped"),
                ErrorDescription = Limit(
                    outcome.SkipDescription
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
            .GetRequiredService<IInstanceVariableUpdateBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(batchId, true, cancellationToken);
        if (batch is null)
        {
            return;
        }
        var counts = await batches.CountItemsByStatusAsync(batchId, cancellationToken);
        var warningCount = await batches.CountItemsWithWarningsAsync(batchId, cancellationToken);
        await batches.UpdateAsync(
            InstanceVariableUpdateBatchService.ApplyCounts(
                InstanceVariableUpdateBatchService.ToUpdate(batch),
                counts) with
            {
                WarningItemCount = warningCount,
                UpdatedAt = timeProvider.GetUtcNow()
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ReconcileExecutionAsync(
        long batchId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider
            .GetRequiredService<IInstanceVariableUpdateBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(batchId, true, cancellationToken);
        if (batch is null
            || batch.Status is not (
                InstanceVariableUpdateBatchStatuses.Running
                or InstanceVariableUpdateBatchStatuses.Cancelled))
        {
            return;
        }
        var counts = await batches.CountItemsByStatusAsync(batchId, cancellationToken);
        if (Count(counts, InstanceVariableUpdateBatchItemStatuses.Queued) > 0)
        {
            return;
        }
        var warningCount = await batches.CountItemsWithWarningsAsync(batchId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (batch.Status == InstanceVariableUpdateBatchStatuses.Cancelled)
        {
            await batches.UpdateAsync(
                InstanceVariableUpdateBatchService.ApplyCounts(
                    InstanceVariableUpdateBatchService.ToUpdate(batch),
                    counts) with
                {
                    WarningItemCount = warningCount,
                    CompletedAt = batch.CompletedAt ?? now,
                    UpdatedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        var hasIssues =
            Count(counts, InstanceVariableUpdateBatchItemStatuses.Ineligible)
            + Count(counts, InstanceVariableUpdateBatchItemStatuses.Skipped)
            + Count(counts, InstanceVariableUpdateBatchItemStatuses.Failed)
            + Count(counts, InstanceVariableUpdateBatchItemStatuses.Cancelled) > 0;
        await batches.UpdateAsync(
            InstanceVariableUpdateBatchService.ApplyCounts(
                InstanceVariableUpdateBatchService.ToUpdate(batch),
                counts) with
            {
                Status = hasIssues
                    ? InstanceVariableUpdateBatchStatuses.CompletedWithIssues
                    : InstanceVariableUpdateBatchStatuses.Completed,
                WarningItemCount = warningCount,
                CompletedAt = now,
                UpdatedAt = now
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Instance-variable update batch {BatchId} execution settled with issues={HasIssues}.",
            batchId,
            hasIssues);
    }

    private async Task MarkGroupFailedAsync(
        long batchId,
        long workflowDefinitionId,
        string phase,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batches = scope.ServiceProvider
            .GetRequiredService<IInstanceVariableUpdateBatchRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var batch = await batches.GetAsync(batchId, true, cancellationToken);
        if (batch is null
            || batch.Status is InstanceVariableUpdateBatchStatuses.Completed
                or InstanceVariableUpdateBatchStatuses.CompletedWithIssues
                or InstanceVariableUpdateBatchStatuses.Failed)
        {
            return;
        }
        var now = timeProvider.GetUtcNow();
        var failure = Limit(exception.Message);
        var fromStatuses = phase == InstanceVariableUpdateBatchPhases.Prepare
            ? new[] { InstanceVariableUpdateBatchItemStatuses.Preparing }
            : new[] { InstanceVariableUpdateBatchItemStatuses.Queued };
        await batches.FailItemsAsync(
            batchId,
            workflowDefinitionId,
            fromStatuses,
            "batch_group_processing_failed",
            failure,
            now,
            cancellationToken);
        var counts = await batches.CountItemsByStatusAsync(batchId, cancellationToken);
        var warningCount = await batches.CountItemsWithWarningsAsync(batchId, cancellationToken);
        var pending = phase == InstanceVariableUpdateBatchPhases.Prepare
            ? Count(counts, InstanceVariableUpdateBatchItemStatuses.Preparing)
            : Count(counts, InstanceVariableUpdateBatchItemStatuses.Queued);
        var update = InstanceVariableUpdateBatchService.ApplyCounts(
            InstanceVariableUpdateBatchService.ToUpdate(batch),
            counts) with
        {
            WarningItemCount = warningCount,
            Issues = InstanceVariableUpdateBatchService.SerializeIssues(
                [InstanceVariableUpdateBatchService.Issue(
                    "batch_group_processing_failed",
                    $"Workflow definition #{workflowDefinitionId} {phase} processing failed: {failure}")]),
            UpdatedAt = now
        };
        if (pending == 0)
        {
            if (batch.Status == InstanceVariableUpdateBatchStatuses.Cancelled)
            {
                update = update with { CompletedAt = batch.CompletedAt ?? now };
            }
            else if (phase == InstanceVariableUpdateBatchPhases.Prepare)
            {
                var eligible = Count(counts, InstanceVariableUpdateBatchItemStatuses.Eligible);
                update = update with
                {
                    Status = eligible > 0
                        ? InstanceVariableUpdateBatchStatuses.Ready
                        : InstanceVariableUpdateBatchStatuses.CompletedWithIssues,
                    PreparedAt = now,
                    CompletedAt = eligible == 0 ? now : null
                };
            }
            else
            {
                update = update with
                {
                    Status = InstanceVariableUpdateBatchStatuses.CompletedWithIssues,
                    CompletedAt = now
                };
            }
        }
        await batches.UpdateAsync(update, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogError(
            exception,
            "Instance-variable update batch {BatchId} definition {WorkflowDefinitionId} phase {Phase} exhausted durable retries.",
            batchId,
            workflowDefinitionId,
            phase);
    }

    private static IReadOnlyList<InstanceVariableUpdateOutcomePlanDto> ClassifyPlan(
        IReadOnlyList<InstanceVariableWriteDto> requested,
        IReadOnlyList<InstanceVariableVersionRecord> current)
    {
        var canonical = current
            .GroupBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(variable => variable.Version).First(),
                StringComparer.OrdinalIgnoreCase);
        return requested.Select(write =>
        {
            var exists = canonical.TryGetValue(write.Name, out var stored);
            return new InstanceVariableUpdateOutcomePlanDto(
                exists ? stored!.Name : write.Name,
                exists
                    ? InstanceVariableUpdateOutcomes.Updated
                    : InstanceVariableUpdateOutcomes.Added);
        }).ToArray();
    }

    private static ActorContext ConfirmedActor(
        InstanceVariableUpdateBatchRecord batch,
        IReadOnlyDictionary<string, string>? claims) => new(
        batch.ConfirmedBy
            ?? throw new WorkflowConflictException(
                $"Instance-variable update batch #{batch.Id} has no confirmer."),
        batch.ConfirmedByRoles
            ?? throw new WorkflowConflictException(
                $"Instance-variable update batch #{batch.Id} has no confirmer role snapshot."),
        claims is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(claims, StringComparer.OrdinalIgnoreCase));

    private static bool IsExpectedBusinessException(Exception exception) =>
        exception is WorkflowDomainException
            or WorkflowConflictException
            or WorkflowForbiddenException
            or WorkflowUnauthorizedException;

    private static string ExceptionCode(Exception exception) => exception switch
    {
        WorkflowConflictException => "conflict",
        WorkflowForbiddenException or WorkflowUnauthorizedException =>
            "authentication_changed",
        _ => "ineligible"
    };

    private static WorkflowJobFence Fence(WorkflowJobLeaseRecord lease) => new(
        lease.Job.Id,
        lease.Job.WorkerId
            ?? throw new WorkflowConflictException("A leased workflow job has no worker id."),
        lease.LeaseToken,
        lease.LeaseGeneration);

    private static int Count(
        IReadOnlyDictionary<string, int> counts,
        string status) => counts.TryGetValue(status, out var value) ? value : 0;

    private static string Limit(string value) =>
        value.EnumerateRunes().Count()
            <= InstanceVariableUpdateConstraints.MaxErrorDescriptionLength
            ? value
            : string.Concat(value.EnumerateRunes()
                .Take(InstanceVariableUpdateConstraints.MaxErrorDescriptionLength)
                .Select(rune => rune.ToString()));

    private static string LimitCode(string value) =>
        value.Length <= InstanceVariableUpdateConstraints.MaxErrorCodeLength
            ? value
            : value[..InstanceVariableUpdateConstraints.MaxErrorCodeLength];
}
