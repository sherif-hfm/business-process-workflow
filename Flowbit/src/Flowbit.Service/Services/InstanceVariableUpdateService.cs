using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

public sealed class InstanceVariableUpdateService(
    IWorkflowRuntimeRepository runtime,
    IWorkflowJobRepository jobs,
    IInstanceVariableUpdateRepository updates,
    IInstanceVariableUpdateBatchRepository batches,
    IUnitOfWork unitOfWork,
    IConditionalEventRuntimeCoordinator conditionalEvents)
    : IInstanceVariableUpdateService, IInstanceVariableUpdateExecutor
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<UpdateInstanceVariablesResultDto?> UpdateAsync(
        long instanceId,
        UpdateInstanceVariablesRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        if (instanceId <= 0)
        {
            throw new WorkflowDomainException(
                "Workflow instance id must be greater than zero.");
        }
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var normalized = NormalizeRequest(
            request.Variables,
            request.Reason,
            request.IdempotencyKey);
        var outcome = await ExecuteCoreAsync(
            instanceId,
            expectedWorkflowKey: null,
            normalized,
            actor,
            batchId: null,
            batchItemId: null,
            cancellationToken);

        return outcome.SkipCode switch
        {
            null => outcome.Result,
            InstanceVariableUpdateSkipCodes.InstanceNotFound => null,
            InstanceVariableUpdateSkipCodes.InstanceNotRunning =>
                throw new WorkflowConflictException(outcome.SkipDescription!),
            _ => throw new WorkflowConflictException(outcome.SkipDescription!)
        };
    }

    public async Task<InstanceVariableUpdateExecutionOutcome> ExecuteAsync(
        InstanceVariableUpdateExecutionRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (request.InstanceId <= 0
            || request.BatchId <= 0
            || request.BatchItemId <= 0)
        {
            throw new WorkflowDomainException(
                "The variable-update batch execution request contains an invalid identity.");
        }

        var expectedWorkflowKey =
            InstanceVariableUpdateValidation.NormalizeWorkflowKey(
                request.ExpectedWorkflowKey);
        var normalized = NormalizeRequest(
            request.Variables,
            request.Reason,
            idempotencyKey: null);
        return await ExecuteCoreAsync(
            request.InstanceId,
            expectedWorkflowKey,
            normalized,
            actor,
            request.BatchId,
            request.BatchItemId,
            cancellationToken);
    }

    private async Task<InstanceVariableUpdateExecutionOutcome> ExecuteCoreAsync(
        long instanceId,
        string? expectedWorkflowKey,
        NormalizedRequest request,
        ActorContext actor,
        long? batchId,
        long? batchItemId,
        CancellationToken cancellationToken)
    {
        var performedBy = InstanceVariableUpdateValidation.RequireActor(actor);
        var performedByRoles =
            InstanceVariableUpdateValidation.SnapshotRoles(actor.Roles);

        try
        {
            await using var transaction =
                await unitOfWork.BeginTransactionAsync(cancellationToken);
            var instance = await runtime.GetInstanceForUpdateAsync(
                instanceId,
                lockActiveUserTask: false,
                cancellationToken);
            if (instance is null)
            {
                return Skipped(
                    InstanceVariableUpdateSkipCodes.InstanceNotFound,
                    "The selected workflow instance no longer exists.");
            }

            if (batchId is long concreteBatchId
                && batchItemId is long concreteBatchItemId)
            {
                await ValidateBatchExecutionAsync(
                    concreteBatchId,
                    concreteBatchItemId,
                    instance,
                    expectedWorkflowKey!,
                    request,
                    performedBy,
                    cancellationToken);
            }
            else if (request.IdempotencyKey is not null)
            {
                await updates.LockIdempotencyKeyAsync(
                    instanceId,
                    performedBy,
                    request.IdempotencyKey,
                    cancellationToken);
                var existing = await updates.FindByIdempotencyKeyAsync(
                    instanceId,
                    performedBy,
                    request.IdempotencyKey,
                    cancellationToken);
                if (existing is not null)
                {
                    EnsureReplayMatches(existing, request);
                    var replayWarnings = await BuildActiveJobWarningsAsync(
                        instanceId,
                        cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return Succeeded(ToResult(existing, replayWarnings));
                }
            }

            if (!string.Equals(
                    instance.Status,
                    WorkflowInstanceStatuses.Running,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Skipped(
                    InstanceVariableUpdateSkipCodes.InstanceNotRunning,
                    "Only a running workflow instance can receive administrative variable updates.");
            }
            if (expectedWorkflowKey is not null
                && !string.Equals(
                    instance.WorkflowKey,
                    expectedWorkflowKey,
                    StringComparison.Ordinal))
            {
                return Skipped(
                    InstanceVariableUpdateSkipCodes.WorkflowFamilyChanged,
                    "The workflow instance no longer belongs to the selected workflow family.");
            }

            var warnings = await BuildActiveJobWarningsAsync(
                instanceId,
                cancellationToken);
            var currentVariables = await runtime.LoadLatestVariableVersionsAsync(
                instanceId,
                cancellationToken);
            var writes = ClassifyWrites(request.Variables, currentVariables);

            // Touch first so the audit and response use the exact same timestamp as
            // the instance concurrency marker persisted by this operation.
            var updatedAt = ToPostgresTimestamp(await runtime.TouchInstanceAsync(
                instanceId,
                cancellationToken));
            var audit = await updates.AddAsync(
                new NewInstanceVariableUpdateAuditRecord(
                    instanceId,
                    instance.WorkflowDefinitionId,
                    performedBy,
                    performedByRoles,
                    request.Reason,
                    request.SerializedVariables,
                    request.IdempotencyKey,
                    batchId,
                    batchItemId,
                    updatedAt),
                cancellationToken);

            foreach (var write in writes)
            {
                await runtime.AddVariableAsync(
                    instanceId,
                    write.Name,
                    sourceActionId: null,
                    performedBy,
                    write.Value,
                    cancellationToken,
                    instanceVariableUpdateAuditId: audit.Id);
            }
            await unitOfWork.SaveChangesAsync(cancellationToken);

            var persistedVariables = await updates.ListVariablesAsync(
                audit.Id,
                cancellationToken);
            if (persistedVariables.Count != writes.Count)
            {
                throw new InvalidOperationException(
                    "The administrative variable update did not persist every requested history row.");
            }

            var outcomes = BuildOutcomes(writes, persistedVariables);
            audit = await updates.SetResultAsync(
                audit.Id,
                JsonSerializer.SerializeToElement(outcomes, JsonOptions),
                cancellationToken);
            var result = ToResult(audit, warnings);

            if (batchId is long successfulBatchId
                && batchItemId is long successfulBatchItemId)
            {
                var item = await batches.GetItemAsync(
                    successfulBatchItemId,
                    forUpdate: true,
                    cancellationToken)
                    ?? throw new WorkflowConflictException(
                        "The variable-update batch item no longer exists.");
                if (item.BatchId != successfulBatchId
                    || item.InstanceId != instanceId
                    || item.Status != InstanceVariableUpdateBatchItemStatuses.Queued)
                {
                    throw new WorkflowConflictException(
                        "The variable-update batch item changed while it was executing.");
                }

                await batches.UpdateItemAsync(
                    new InstanceVariableUpdateBatchItemUpdateRecord(
                        item.Id,
                        InstanceVariableUpdateBatchItemStatuses.Succeeded,
                        item.Plan,
                        MergeWarnings(item.Warnings, warnings),
                        JsonSerializer.SerializeToElement(result, JsonOptions),
                        audit.Id,
                        ErrorCode: null,
                        ErrorDescription: null,
                        UpdatedAt: updatedAt,
                        PreparedAt: item.PreparedAt,
                        StartedAt: item.StartedAt,
                        CompletedAt: updatedAt),
                    cancellationToken);
            }

            await conditionalEvents.ResumeForVariableChangesAsync(
                instance,
                actor,
                cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Succeeded(result);
        }
        catch
        {
            unitOfWork.DiscardChanges();
            throw;
        }
    }

    private async Task ValidateBatchExecutionAsync(
        long batchId,
        long batchItemId,
        WorkflowInstanceRecord instance,
        string expectedWorkflowKey,
        NormalizedRequest request,
        string performedBy,
        CancellationToken cancellationToken)
    {
        var item = await batches.GetItemAsync(
            batchItemId,
            forUpdate: true,
            cancellationToken)
            ?? throw new WorkflowConflictException(
                "The variable-update batch item no longer exists.");
        var batch = await batches.GetAsync(
            batchId,
            forUpdate: false,
            cancellationToken)
            ?? throw new WorkflowConflictException(
                "The variable-update batch no longer exists.");

        if (item.BatchId != batchId
            || item.InstanceId != instance.Id
            || item.Status != InstanceVariableUpdateBatchItemStatuses.Queued)
        {
            throw new WorkflowConflictException(
                "The queued variable-update batch item does not match the execution request.");
        }
        if (batch.Status is not (
                InstanceVariableUpdateBatchStatuses.Running
                or InstanceVariableUpdateBatchStatuses.Cancelled))
        {
            throw new WorkflowConflictException(
                $"Variable-update batch #{batch.Id} is not executable while '{batch.Status}'.");
        }
        if (batch.Status == InstanceVariableUpdateBatchStatuses.Cancelled
            && item.StartedAt is null)
        {
            throw new WorkflowConflictException(
                "The unstarted variable-update batch item was cancelled.");
        }
        if (!string.Equals(batch.WorkflowKey, expectedWorkflowKey, StringComparison.Ordinal)
            || !JsonElement.DeepEquals(batch.Variables, request.SerializedVariables)
            || !string.Equals(batch.Reason, request.Reason, StringComparison.Ordinal))
        {
            throw new WorkflowConflictException(
                "The variable-update batch item does not match the frozen request.");
        }
        if (string.IsNullOrWhiteSpace(batch.ConfirmedBy)
            || !string.Equals(
                batch.ConfirmedBy.Trim(),
                performedBy,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowConflictException(
                "The variable-update batch must execute as its confirming actor.");
        }
    }

    private async Task<IReadOnlyList<InstanceVariableUpdateIssueDto>>
        BuildActiveJobWarningsAsync(
            long instanceId,
            CancellationToken cancellationToken)
    {
        var openJobs = await jobs.ListOpenByInstanceAsync(
            instanceId,
            forUpdate: false,
            cancellationToken);
        if (openJobs.Count == 0)
        {
            return [];
        }

        return
        [
            new InstanceVariableUpdateIssueDto(
                "active_durable_jobs",
                $"The instance has {openJobs.Count} active durable job(s). " +
                "Jobs that already captured a variable snapshot may continue using that frozen snapshot.")
        ];
    }

    private static IReadOnlyList<InstanceVariableUpdateWriteRecord> ClassifyWrites(
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
            return new InstanceVariableUpdateWriteRecord(
                exists ? stored!.Name : write.Name,
                exists
                    ? InstanceVariableUpdateOutcomes.Updated
                    : InstanceVariableUpdateOutcomes.Added,
                write.Value.Clone());
        }).ToArray();
    }

    private static IReadOnlyList<InstanceVariableUpdateOutcomeDto> BuildOutcomes(
        IReadOnlyList<InstanceVariableUpdateWriteRecord> writes,
        IReadOnlyList<InstanceVariableUpdateVariableRecord> persisted)
    {
        var byName = persisted.ToDictionary(
            variable => variable.Name,
            StringComparer.OrdinalIgnoreCase);
        return writes.Select(write =>
        {
            if (!byName.TryGetValue(write.Name, out var variable))
            {
                throw new InvalidOperationException(
                    $"The administrative variable update did not persist '{write.Name}'.");
            }
            return new InstanceVariableUpdateOutcomeDto(
                variable.Name,
                write.Outcome,
                variable.Id,
                variable.Value.Clone());
        }).ToArray();
    }

    private static UpdateInstanceVariablesResultDto ToResult(
        InstanceVariableUpdateAuditRecord audit,
        IReadOnlyList<InstanceVariableUpdateIssueDto> warnings)
    {
        var outcomes = audit.Result.Deserialize<
                IReadOnlyList<InstanceVariableUpdateOutcomeDto>>(JsonOptions)
            ?? [];
        return new UpdateInstanceVariablesResultDto(
            audit.Id,
            audit.InstanceId,
            audit.WorkflowDefinitionId,
            audit.PerformedAt,
            outcomes,
            warnings)
        {
            BatchId = audit.BatchId,
            BatchItemId = audit.BatchItemId
        };
    }

    private static JsonElement? MergeWarnings(
        JsonElement? stored,
        IReadOnlyList<InstanceVariableUpdateIssueDto> executionWarnings)
    {
        if (executionWarnings.Count == 0)
        {
            return stored?.Clone();
        }

        var merged = stored?.Deserialize<
                IReadOnlyList<InstanceVariableUpdateIssueDto>>(JsonOptions)
            ?.ToList() ?? [];
        foreach (var warning in executionWarnings)
        {
            if (!merged.Any(existing =>
                    string.Equals(
                        existing.Code,
                        warning.Code,
                        StringComparison.Ordinal)
                    && string.Equals(
                        existing.Message,
                        warning.Message,
                        StringComparison.Ordinal)))
            {
                merged.Add(warning);
            }
        }
        return JsonSerializer.SerializeToElement(merged, JsonOptions);
    }

    private static void EnsureReplayMatches(
        InstanceVariableUpdateAuditRecord existing,
        NormalizedRequest request)
    {
        if (!string.Equals(existing.Reason, request.Reason, StringComparison.Ordinal)
            || !JsonElement.DeepEquals(
                existing.RequestedVariables,
                request.SerializedVariables))
        {
            throw new WorkflowConflictException(
                "IdempotencyKey was already used for a different administrative variable-update request.");
        }
    }

    private static NormalizedRequest NormalizeRequest(
        IReadOnlyList<InstanceVariableWriteDto>? variables,
        string? reason,
        string? idempotencyKey)
    {
        var normalized = InstanceVariableUpdateValidation.NormalizeWrites(variables);
        var normalizedReason =
            InstanceVariableUpdateValidation.NormalizeReason(reason);
        var normalizedIdempotencyKey =
            InstanceVariableUpdateValidation.NormalizeIdempotencyKey(
                idempotencyKey);
        var serialized =
            InstanceVariableUpdateValidation.SerializeWrites(normalized);
        return new NormalizedRequest(
            normalized,
            normalizedReason,
            normalizedIdempotencyKey,
            serialized);
    }

    private static InstanceVariableUpdateExecutionOutcome Succeeded(
        UpdateInstanceVariablesResultDto result) => new(result, null, null);

    // PostgreSQL timestamps have microsecond precision. Normalize before the
    // first response so an idempotent replay returns the exact same timestamp
    // after the audit has been read back from the database.
    private static DateTimeOffset ToPostgresTimestamp(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, value.Offset);

    private static InstanceVariableUpdateExecutionOutcome Skipped(
        string code,
        string description) => new(null, code, description);

    private sealed record NormalizedRequest(
        IReadOnlyList<InstanceVariableWriteDto> Variables,
        string? Reason,
        string? IdempotencyKey,
        JsonElement SerializedVariables);
}
