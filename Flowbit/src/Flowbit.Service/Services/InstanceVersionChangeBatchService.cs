using System.Text;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

public sealed class InstanceVersionChangeBatchService(
    IWorkflowDefinitionRepository definitions,
    IInstanceVersionChangeCandidateRepository candidates,
    IInstanceVersionChangeBatchRepository batches,
    IWorkflowJobRepository jobs,
    IEngineSettingsRepository engineSettings,
    IUnitOfWork unitOfWork,
    WorkflowContextOptions contextOptions,
    TimeProvider timeProvider) : IInstanceVersionChangeBatchService
{
    private const int DefaultMaxBatchInstances =
        InstanceVersionChangeBatchConstraints.MaxBatchInstances;
    private static readonly TimeSpan[] BatchRetryDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5)
    ];

    public async Task<PagedResult<InstanceVersionChangeCandidateDto>> SearchCandidatesAsync(
        InstanceVersionChangeCandidateSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireActor(actor);
        var filter = request.Filter
            ?? throw new WorkflowDomainException("A candidate filter is required.");
        var source = await GetWorkflowAsync(filter.SourceWorkflowId, cancellationToken);
        var query = BuildCandidateQuery(
            filter,
            request.IncludeVariables ?? false,
            request.Page ?? 1,
            request.PageSize ?? 50);
        var result = await candidates.SearchAsync(query, cancellationToken);
        return new PagedResult<InstanceVersionChangeCandidateDto>(
            result.Items.Select(item => ToCandidateDto(item, source)).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount);
    }

    public async Task<InstanceVersionChangeBatchDetailDto> CreateAsync(
        CreateInstanceVersionChangeBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = RequireActor(actor);
        var reason = NormalizeRequiredReason(request.Reason);
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);
        var selection = request.Selection
            ?? throw new WorkflowDomainException("A batch selection is required.");

        if (idempotencyKey is not null)
        {
            var existing = await batches.FindByIdempotencyKeyAsync(
                user,
                idempotencyKey,
                cancellationToken);
            if (existing is not null)
            {
                EnsureIdempotentReplayMatches(existing, request, reason, selection);
                return await ToDetailAsync(existing, cancellationToken);
            }
        }

        var source = await GetWorkflowAsync(request.SourceWorkflowId, cancellationToken);
        var target = await GetPublishedWorkflowAsync(request.TargetWorkflowId, cancellationToken);
        EnsureVersionPair(source, target);
        var maxInstances = await ResolveMaxBatchInstancesAsync(cancellationToken);
        var (query, excluded, expectedExplicit) = BuildSelectionQuery(selection, request);
        if (expectedExplicit > maxInstances)
        {
            throw new WorkflowDomainException(
                $"The explicit selection exceeds the configured maximum of {maxInstances:N0} instances.");
        }
        var frozen = await candidates.MaterializeAsync(
            query,
            excluded,
            maxInstances,
            cancellationToken);

        if (frozen.Count > maxInstances)
        {
            throw new WorkflowDomainException(
                $"The frozen selection exceeds the configured maximum of {maxInstances:N0} instances.");
        }
        if (expectedExplicit is not null && frozen.Count != expectedExplicit.Value)
        {
            throw new WorkflowDomainException(
                "One or more explicitly selected workflow instances no longer exist.");
        }
        if (frozen.Count == 0)
        {
            throw new WorkflowDomainException(
                "The frozen selection contains no workflow instances.");
        }

        var now = timeProvider.GetUtcNow();
        var selectionSnapshot = JsonSerializer.SerializeToElement(selection);
        InstanceVersionChangeBatchRecord batch;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            if (idempotencyKey is not null)
            {
                await batches.LockIdempotencyKeyAsync(user, idempotencyKey, cancellationToken);
                var winner = await batches.FindByIdempotencyKeyAsync(
                    user,
                    idempotencyKey,
                    cancellationToken);
                if (winner is not null)
                {
                    EnsureIdempotentReplayMatches(winner, request, reason, selection);
                    await transaction.CommitAsync(cancellationToken);
                    return await ToDetailAsync(winner, cancellationToken);
                }
            }

            batch = await batches.AddAsync(
                new NewInstanceVersionChangeBatchRecord(
                    source.WorkflowKey,
                    source.Id,
                    target.Id,
                    reason,
                    selectionSnapshot,
                    user,
                    SnapshotRoles(actor.Roles),
                    idempotencyKey,
                    now),
                cancellationToken);
            await batches.AddItemsAsync(
                batch.Id,
                frozen.OrderBy(item => item.InstanceId)
                    .Select(item => new NewInstanceVersionChangeBatchItemRecord(
                        item.InstanceId,
                        item.WorkflowDefinitionId,
                        item.UpdatedAt,
                        now))
                    .ToArray(),
                cancellationToken);
            var job = await EnqueueBatchJobAsync(
                batch.Id,
                source,
                WorkflowJobKinds.InstanceVersionChangeBatchPrepare,
                "prepare",
                SnapshotAllowedClaims(actor.Claims, contextOptions.AllowedClaims),
                now,
                cancellationToken);
            batch = await batches.UpdateAsync(
                ToUpdate(batch) with
                {
                    TotalItemCount = frozen.Count,
                    PreparationJobId = job.Id,
                    UpdatedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await ToDetailAsync(batch, cancellationToken);
    }

    public async Task<PagedResult<InstanceVersionChangeBatchSummaryDto>> ListAsync(
        InstanceVersionChangeBatchSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireActor(actor);
        var status = NormalizeOptional(request.Status);
        if (status is not null && !InstanceVersionChangeBatchStatuses.IsKnown(status))
        {
            throw new WorkflowDomainException(
                $"Unknown instance version-change batch status '{status}'.");
        }
        var result = await batches.ListAsync(
            new InstanceVersionChangeBatchSearch(
                NormalizeOptional(request.WorkflowKey),
                PositiveOrNull(request.SourceWorkflowId, "Source workflow id"),
                PositiveOrNull(request.TargetWorkflowId, "Target workflow id"),
                status,
                NormalizeOptional(request.PreparedBy),
                Math.Max(1, request.Page ?? 1),
                Math.Clamp(request.PageSize ?? 50, 1, 200)),
            cancellationToken);
        var workflows = await LoadBatchWorkflowsAsync(result.Items, cancellationToken);
        return new PagedResult<InstanceVersionChangeBatchSummaryDto>(
            result.Items.Select(batch => ToSummary(
                batch,
                RequireWorkflow(workflows, batch.SourceWorkflowDefinitionId),
                RequireWorkflow(workflows, batch.TargetWorkflowDefinitionId))).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount);
    }

    public async Task<InstanceVersionChangeBatchDetailDto?> GetAsync(
        long batchId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        RequireActor(actor);
        EnsurePositive(batchId, "Batch id");
        var batch = await batches.GetAsync(batchId, false, cancellationToken);
        return batch is null ? null : await ToDetailAsync(batch, cancellationToken);
    }

    public async Task<PagedResult<InstanceVersionChangeBatchItemDto>?> ListItemsAsync(
        long batchId,
        string? status,
        int page,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        RequireActor(actor);
        EnsurePositive(batchId, "Batch id");
        var normalizedStatus = NormalizeOptional(status);
        if (normalizedStatus is not null
            && !InstanceVersionChangeBatchItemStatuses.IsKnown(normalizedStatus))
        {
            throw new WorkflowDomainException(
                $"Unknown instance version-change batch item status '{normalizedStatus}'.");
        }
        if (await batches.GetAsync(batchId, false, cancellationToken) is null)
        {
            return null;
        }
        var result = await batches.ListItemsAsync(
            batchId,
            normalizedStatus,
            Math.Max(1, page),
            Math.Clamp(pageSize, 1, 200),
            cancellationToken);
        return new PagedResult<InstanceVersionChangeBatchItemDto>(
            result.Items.Select(ToItem).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount);
    }

    public async Task<InstanceVersionChangeBatchDetailDto?> ConfirmAsync(
        long batchId,
        ConfirmInstanceVersionChangeBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePositive(batchId, "Batch id");
        InstanceVersionChangeBatchRecord batch;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            batch = await batches.GetAsync(batchId, true, cancellationToken) ?? null!;
            if (batch is null)
            {
                return null;
            }
            var user = RequireActor(actor);
            if (batch.Status is InstanceVersionChangeBatchStatuses.Queued
                or InstanceVersionChangeBatchStatuses.Running
                or InstanceVersionChangeBatchStatuses.Completed
                or InstanceVersionChangeBatchStatuses.CompletedWithIssues)
            {
                return await ToDetailAsync(batch, cancellationToken);
            }
            if (batch.Status != InstanceVersionChangeBatchStatuses.Ready)
            {
                throw new WorkflowConflictException(
                    $"Only a ready batch can be confirmed; batch #{batch.Id} is '{batch.Status}'.");
            }
            if (request.ExpectedEligibleItemCount != batch.EligibleItemCount
                || request.ExpectedIneligibleItemCount != batch.IneligibleItemCount
                || request.ExpectedWarningItemCount != batch.WarningItemCount
                || request.ExpectedBatchUpdatedAt != batch.UpdatedAt)
            {
                throw new WorkflowConflictException(
                    "The prepared batch changed; refresh its compatibility summary before confirming.");
            }

            // Publication is mutable even though definition snapshots are
            // immutable. Revalidate the exact target while holding the batch
            // lock so confirmation never queues work against an unpublished or
            // reclassified version.
            var source = await definitions.GetAsync(
                batch.SourceWorkflowDefinitionId,
                cancellationToken);
            var target = await definitions.GetPublishedAsync(
                batch.TargetWorkflowDefinitionId,
                cancellationToken);
            if (source is null || target is null)
            {
                throw new WorkflowConflictException(
                    "The prepared source or published target workflow version is no longer available.");
            }
            if (source.Id == target.Id
                || !string.Equals(
                    source.WorkflowKey,
                    target.WorkflowKey,
                    StringComparison.Ordinal)
                || !string.Equals(
                    batch.WorkflowKey,
                    source.WorkflowKey,
                    StringComparison.Ordinal))
            {
                throw new WorkflowConflictException(
                    "The prepared workflow versions no longer form a valid version-change pair.");
            }

            var now = timeProvider.GetUtcNow();
            var queued = await batches.TransitionItemsAsync(
                batch.Id,
                [InstanceVersionChangeBatchItemStatuses.Eligible],
                InstanceVersionChangeBatchItemStatuses.Queued,
                now,
                cancellationToken);
            if (queued != batch.EligibleItemCount)
            {
                throw new WorkflowConflictException(
                    "The prepared eligible population changed; refresh the batch before confirming.");
            }
            WorkflowJobRecord? job = null;
            if (queued > 0)
            {
                job = await EnqueueBatchJobAsync(
                    batch.Id,
                    source,
                    WorkflowJobKinds.InstanceVersionChangeBatchExecute,
                    "execute",
                    SnapshotAllowedClaims(actor.Claims, contextOptions.AllowedClaims),
                    now,
                    cancellationToken);
            }
            batch = await batches.UpdateAsync(
                ToUpdate(batch) with
                {
                    Status = queued > 0
                        ? InstanceVersionChangeBatchStatuses.Queued
                        : InstanceVersionChangeBatchStatuses.CompletedWithIssues,
                    ConfirmedBy = user,
                    ConfirmedByRoles = SnapshotRoles(actor.Roles),
                    EligibleItemCount = 0,
                    QueuedItemCount = queued,
                    ExecutionJobId = job?.Id,
                    ConfirmedAt = now,
                    CompletedAt = queued == 0 ? now : null,
                    UpdatedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await ToDetailAsync(batch, cancellationToken);
    }

    public async Task<InstanceVersionChangeBatchDetailDto?> CancelAsync(
        long batchId,
        CancelInstanceVersionChangeBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePositive(batchId, "Batch id");
        var cancellationReason = NormalizeOptionalReason(
            request.Reason,
            "Cancellation reason");
        InstanceVersionChangeBatchRecord batch;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            batch = await batches.GetAsync(batchId, true, cancellationToken) ?? null!;
            if (batch is null)
            {
                return null;
            }
            if (batch.Status == InstanceVersionChangeBatchStatuses.Cancelled)
            {
                return await ToDetailAsync(batch, cancellationToken);
            }
            if (batch.Status is InstanceVersionChangeBatchStatuses.Completed
                or InstanceVersionChangeBatchStatuses.CompletedWithIssues
                or InstanceVersionChangeBatchStatuses.Failed)
            {
                throw new WorkflowConflictException(
                    $"Terminal batch #{batch.Id} cannot be cancelled.");
            }
            var now = timeProvider.GetUtcNow();
            await batches.CancelUnstartedItemsAsync(batch.Id, now, cancellationToken);
            var counts = await batches.CountItemsByStatusAsync(batch.Id, cancellationToken);
            var warningCount = await batches.CountItemsWithWarningsAsync(
                batch.Id,
                cancellationToken);
            var staleCount = await batches.CountStaleItemsAsync(
                batch.Id,
                cancellationToken);
            batch = await batches.UpdateAsync(
                ApplyCounts(ToUpdate(batch), counts) with
                {
                    Status = InstanceVersionChangeBatchStatuses.Cancelled,
                    WarningItemCount = warningCount,
                    StaleItemCount = staleCount,
                    CancelledBy = RequireActor(actor),
                    CancellationReason = cancellationReason,
                    CancelledAt = now,
                    CompletedAt = Count(counts, InstanceVersionChangeBatchItemStatuses.Queued) == 0
                        ? now
                        : null,
                    UpdatedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await ToDetailAsync(batch, cancellationToken);
    }

    private static InstanceVersionChangeCandidateQuery BuildCandidateQuery(
        InstanceVersionChangeCandidateFilterDto filter,
        bool includeVariables,
        int page,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(filter);
        EnsurePositive(filter.SourceWorkflowId, "Source workflow id");
        return new InstanceVersionChangeCandidateQuery
        {
            SourceWorkflowDefinitionId = filter.SourceWorkflowId,
            InstanceId = PositiveOrNull(filter.InstanceId, "Instance id"),
            BusinessKey = NormalizeOptional(filter.BusinessKey),
            NodeId = PositiveOrNull(filter.NodeId, "Node id"),
            NodeExternalId = NormalizeOptional(filter.NodeExternalId),
            VariableFilter = VariableFilterParser.Parse(filter.VariableFilter),
            IncludeVariables = includeVariables,
            Page = Math.Max(1, page),
            PageSize = Math.Clamp(pageSize, 1, 200)
        };
    }

    private static (
        InstanceVersionChangeCandidateQuery Query,
        IReadOnlyList<long> Excluded,
        int? ExpectedExplicit) BuildSelectionQuery(
            InstanceVersionChangeBatchSelectionDto selection,
            CreateInstanceVersionChangeBatchRequest request)
    {
        var excluded = NormalizeInstanceIds(
            selection.ExcludedInstanceIds,
            allowEmpty: true,
            "excluded instance");
        var mode = selection.Mode?.Trim();
        if (string.Equals(
                mode,
                InstanceVersionChangeBatchSelectionModes.Explicit,
                StringComparison.OrdinalIgnoreCase))
        {
            if (selection.Filter is not null)
            {
                throw new WorkflowDomainException(
                    "An explicit selection cannot also carry an all-matching filter.");
            }
            var ids = NormalizeInstanceIds(
                selection.InstanceIds,
                allowEmpty: false,
                "selected instance");
            var excludedSet = excluded.ToHashSet();
            var included = ids.Where(id => !excludedSet.Contains(id)).ToArray();
            if (included.Length == 0)
            {
                throw new WorkflowDomainException(
                    "The explicit selection contains no non-excluded instances.");
            }
            return (new InstanceVersionChangeCandidateQuery
            {
                SourceWorkflowDefinitionId = request.SourceWorkflowId,
                InstanceIds = included,
                Page = 1,
                PageSize = Math.Min(
                    included.Length,
                    InstanceVersionChangeBatchConstraints.MaxBatchInstances + 1)
            }, excluded, included.Length);
        }

        if (!string.Equals(
                mode,
                InstanceVersionChangeBatchSelectionModes.AllMatching,
                StringComparison.OrdinalIgnoreCase)
            || selection.Filter is null)
        {
            throw new WorkflowDomainException(
                "Selection mode must be 'explicit' or 'allMatching'.");
        }
        if (selection.InstanceIds is { Count: > 0 })
        {
            throw new WorkflowDomainException(
                "An all-matching selection cannot also carry explicit instance ids.");
        }
        if (selection.Filter.SourceWorkflowId != request.SourceWorkflowId)
        {
            throw new WorkflowDomainException(
                "The all-matching filter must use the batch's exact source workflow version.");
        }
        var query = BuildCandidateQuery(selection.Filter, false, 1,
            InstanceVersionChangeBatchConstraints.MaxBatchInstances + 1);
        return (query, excluded, null);
    }

    private async Task<WorkflowJobRecord> EnqueueBatchJobAsync(
        long batchId,
        WorkflowDefinitionRecord source,
        string kind,
        string phase,
        IReadOnlyDictionary<string, string> actorClaims,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var anchor = source.Definition.FlowNodes.FirstOrDefault(node =>
                         node.Id == source.Definition.InitialEventId)
                     ?? source.Definition.FlowNodes.First();
        return await jobs.EnqueueAsync(
            new WorkflowJobCreateRecord
            {
                WorkflowDefinitionId = source.Id,
                WorkflowKey = source.WorkflowKey,
                ActivationId = Guid.NewGuid(),
                NodeId = anchor.Id,
                NodeName = anchor.Name,
                NodeType = anchor.Type,
                Kind = kind,
                QueueClass = WorkflowJobClasses.Activity,
                Phase = phase,
                DueAt = now,
                MaxAttempts = BatchRetryDelays.Length + 1,
                RetryDelays = BatchRetryDelays,
                FailureHandling = WorkflowJobFailureHandling.RetryFirst,
                Payload = JsonSerializer.SerializeToElement(
                    new InstanceVersionChangeBatchJobPayload(batchId)
                    {
                        ActorClaims = actorClaims
                    })
            },
            cancellationToken);
    }

    private async Task<InstanceVersionChangeBatchDetailDto> ToDetailAsync(
        InstanceVersionChangeBatchRecord batch,
        CancellationToken cancellationToken)
    {
        var workflows = await definitions.GetManyAsync(
            [batch.SourceWorkflowDefinitionId, batch.TargetWorkflowDefinitionId],
            cancellationToken);
        var source = RequireWorkflow(workflows, batch.SourceWorkflowDefinitionId);
        var target = RequireWorkflow(workflows, batch.TargetWorkflowDefinitionId);
        return new InstanceVersionChangeBatchDetailDto(
            ToSummary(batch, source, target),
            batch.Selection.Clone(),
            batch.PreparedByRoles,
            batch.ConfirmedByRoles,
            batch.Issues?.Clone(),
            batch.PreparationJobId,
            batch.ExecutionJobId,
            batch.CancelledBy,
            batch.CancellationReason,
            batch.PreparedAt,
            batch.ConfirmedAt,
            batch.StartedAt,
            batch.CancelledAt);
    }

    private static InstanceVersionChangeBatchSummaryDto ToSummary(
        InstanceVersionChangeBatchRecord batch,
        WorkflowDefinitionRecord source,
        WorkflowDefinitionRecord target) =>
        new(
            batch.Id,
            WorkflowDefinitionService.ToSummary(source),
            WorkflowDefinitionService.ToSummary(target),
            target.Version > source.Version
                ? InstanceVersionChangeDirections.Upgrade
                : InstanceVersionChangeDirections.Downgrade,
            batch.Reason,
            batch.Status,
            batch.PreparedBy,
            batch.ConfirmedBy,
            batch.TotalItemCount,
            batch.EligibleItemCount,
            batch.WarningItemCount,
            batch.StaleItemCount,
            batch.BlockedItemCount,
            batch.IneligibleItemCount,
            batch.QueuedItemCount,
            batch.SucceededItemCount,
            batch.SkippedItemCount,
            batch.FailedItemCount,
            batch.CancelledItemCount,
            batch.CreatedAt,
            batch.UpdatedAt,
            batch.CompletedAt);

    private static InstanceVersionChangeBatchItemDto ToItem(
        InstanceVersionChangeBatchItemRecord item) =>
        new(
            item.Id,
            item.BatchId,
            item.InstanceId,
            item.BusinessKey,
            item.CapturedSourceWorkflowDefinitionId,
            item.CapturedInstanceUpdatedAt,
            item.Status,
            DeserializeIssues(item.Blockers),
            DeserializeIssues(item.Warnings),
            item.Result?.Clone(),
            item.VersionChangeAuditId,
            item.ErrorCode,
            item.ErrorDescription,
            item.CreatedAt,
            item.UpdatedAt,
            item.PreparedAt,
            item.StartedAt,
            item.CompletedAt);

    private static InstanceVersionChangeCandidateDto ToCandidateDto(
        InstanceListItem item,
        WorkflowDefinitionRecord source) =>
        new(
            item.Id,
            item.WorkflowDefinitionId,
            source.WorkflowKey,
            item.WorkflowName,
            item.WorkflowVersion,
            item.Status,
            item.BusinessKey,
            item.CreatedAt,
            item.UpdatedAt,
            (item.ExecutionPositions ?? [])
            .Select(position => new ExecutionPositionDto(
                position.TokenId,
                position.NodeId,
                position.NodeName,
                position.NodeExternalId,
                position.NodeType,
                position.Status,
                position.ArrivedViaFlowId,
                position.TerminationReason,
                position.UserTaskId,
                position.MultiInstanceExecutionId,
                position.ActivationId,
                position.WaitState,
                position.WaitingJobId,
                position.WaitingTimerSubscriptionId))
            .ToArray())
        {
            Variables = item.Variables
        };

    internal static InstanceVersionChangeBatchUpdateRecord ToUpdate(
        InstanceVersionChangeBatchRecord batch) =>
        new(
            batch.Id,
            batch.Status,
            batch.ConfirmedBy,
            batch.ConfirmedByRoles,
            batch.TotalItemCount,
            batch.EligibleItemCount,
            batch.IneligibleItemCount,
            batch.WarningItemCount,
            batch.StaleItemCount,
            batch.QueuedItemCount,
            batch.SucceededItemCount,
            batch.SkippedItemCount,
            batch.FailedItemCount,
            batch.CancelledItemCount,
            batch.Issues,
            batch.PreparationJobId,
            batch.ExecutionJobId,
            batch.CancelledBy,
            batch.CancellationReason,
            batch.UpdatedAt,
            batch.PreparedAt,
            batch.ConfirmedAt,
            batch.StartedAt,
            batch.CompletedAt,
            batch.CancelledAt);

    internal static InstanceVersionChangeBatchItemUpdateRecord ToItemUpdate(
        InstanceVersionChangeBatchItemRecord item) =>
        new(
            item.Id,
            item.Status,
            item.Blockers,
            item.Warnings,
            item.Result,
            item.ErrorCode,
            item.ErrorDescription,
            item.UpdatedAt,
            item.PreparedAt,
            item.StartedAt,
            item.CompletedAt);

    internal static InstanceVersionChangeBatchUpdateRecord ApplyCounts(
        InstanceVersionChangeBatchUpdateRecord update,
        IReadOnlyDictionary<string, int> counts) =>
        update with
        {
            EligibleItemCount = Count(counts, InstanceVersionChangeBatchItemStatuses.Eligible),
            IneligibleItemCount = Count(counts, InstanceVersionChangeBatchItemStatuses.Ineligible),
            QueuedItemCount = Count(counts, InstanceVersionChangeBatchItemStatuses.Queued),
            SucceededItemCount = Count(counts, InstanceVersionChangeBatchItemStatuses.Succeeded),
            SkippedItemCount = Count(counts, InstanceVersionChangeBatchItemStatuses.Skipped),
            FailedItemCount = Count(counts, InstanceVersionChangeBatchItemStatuses.Failed),
            CancelledItemCount = Count(counts, InstanceVersionChangeBatchItemStatuses.Cancelled)
        };

    internal static JsonElement? SerializeIssues(
        IReadOnlyCollection<InstanceVersionChangeIssueDto> issues) =>
        issues.Count == 0 ? null : JsonSerializer.SerializeToElement(issues);

    internal static InstanceVersionChangeIssueDto Issue(
        string code,
        string message,
        string? stateType = null,
        long? stateId = null) =>
        new(code, Limit(message), stateType, stateId);

    internal static int Count(
        IReadOnlyDictionary<string, int> counts,
        string status) => counts.TryGetValue(status, out var value) ? value : 0;

    private async Task<IReadOnlyDictionary<long, WorkflowDefinitionRecord>>
        LoadBatchWorkflowsAsync(
            IReadOnlyCollection<InstanceVersionChangeBatchRecord> values,
            CancellationToken cancellationToken) =>
        await definitions.GetManyAsync(
            values.SelectMany(batch => new[]
                {
                    batch.SourceWorkflowDefinitionId,
                    batch.TargetWorkflowDefinitionId
                })
                .Distinct()
                .ToArray(),
            cancellationToken);

    private static WorkflowDefinitionRecord RequireWorkflow(
        IReadOnlyDictionary<long, WorkflowDefinitionRecord> workflows,
        long id) => workflows.TryGetValue(id, out var workflow)
        ? workflow
        : throw new InvalidOperationException(
            $"Instance version-change batch references missing workflow definition #{id}.");

    private async Task<WorkflowDefinitionRecord> GetWorkflowAsync(
        long id,
        CancellationToken cancellationToken)
    {
        EnsurePositive(id, "Workflow definition id");
        return await definitions.GetAsync(id, cancellationToken)
            ?? throw new WorkflowDomainException(
                $"Workflow definition #{id} was not found.");
    }

    private async Task<WorkflowDefinitionRecord> GetPublishedWorkflowAsync(
        long id,
        CancellationToken cancellationToken)
    {
        EnsurePositive(id, "Target workflow id");
        return await definitions.GetPublishedAsync(id, cancellationToken)
            ?? throw new WorkflowDomainException(
                $"Published target workflow definition #{id} was not found.");
    }

    private static void EnsureVersionPair(
        WorkflowDefinitionRecord source,
        WorkflowDefinitionRecord target)
    {
        if (source.Id == target.Id)
        {
            throw new WorkflowDomainException(
                "The target workflow version must differ from the source version.");
        }
        if (!string.Equals(source.WorkflowKey, target.WorkflowKey, StringComparison.Ordinal))
        {
            throw new WorkflowDomainException(
                "Source and target workflow versions must belong to the same workflow family.");
        }
    }

    private async Task<int> ResolveMaxBatchInstancesAsync(
        CancellationToken cancellationToken)
    {
        var setting = await engineSettings.GetByKeyAsync(
            InstanceVersionChangeBatchConstraints.MaxBatchInstancesSetting,
            cancellationToken);
        return int.TryParse(setting?.Value, out var value) && value > 0
            ? Math.Min(value, InstanceVersionChangeBatchConstraints.MaxBatchInstances)
            : DefaultMaxBatchInstances;
    }

    private static void EnsureIdempotentReplayMatches(
        InstanceVersionChangeBatchRecord existing,
        CreateInstanceVersionChangeBatchRequest request,
        string reason,
        InstanceVersionChangeBatchSelectionDto selection)
    {
        if (existing.SourceWorkflowDefinitionId != request.SourceWorkflowId
            || existing.TargetWorkflowDefinitionId != request.TargetWorkflowId
            || !string.Equals(existing.Reason, reason, StringComparison.Ordinal)
            || !JsonElement.DeepEquals(
                existing.Selection,
                JsonSerializer.SerializeToElement(selection)))
        {
            throw new WorkflowConflictException(
                "IdempotencyKey was already used for a different instance version-change batch request.");
        }
    }

    private static IReadOnlyList<long> NormalizeInstanceIds(
        IReadOnlyList<long>? values,
        bool allowEmpty,
        string label)
    {
        var result = (values ?? [])
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        if (!allowEmpty && result.Length == 0)
        {
            throw new WorkflowDomainException(
                "Explicit selection requires at least one instance id.");
        }
        if (result.Any(id => id <= 0))
        {
            throw new WorkflowDomainException(
                $"Every {label} id must be greater than zero.");
        }
        return result;
    }

    private static IReadOnlyList<InstanceVersionChangeIssueDto> DeserializeIssues(
        JsonElement? value) =>
        value?.Deserialize<List<InstanceVersionChangeIssueDto>>() ?? [];

    private static IReadOnlyDictionary<string, string> SnapshotAllowedClaims(
        IReadOnlyDictionary<string, string> actorClaims,
        IEnumerable<string> allowedClaims)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configured in allowedClaims)
        {
            var allowed = configured?.Trim();
            if (string.IsNullOrEmpty(allowed))
            {
                continue;
            }
            if (actorClaims.TryGetValue(allowed, out var direct))
            {
                result.TryAdd(allowed, direct);
                continue;
            }
            var pair = actorClaims.FirstOrDefault(candidate =>
                candidate.Key.EndsWith('/' + allowed, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(pair.Key))
            {
                result.TryAdd(allowed, pair.Value);
            }
        }
        return result;
    }

    private static IReadOnlyList<string> SnapshotRoles(
        IReadOnlyCollection<string> roles) =>
        roles.Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string RequireActor(ActorContext actor)
    {
        var user = actor.User?.Trim();
        if (string.IsNullOrWhiteSpace(user))
        {
            throw new WorkflowUnauthorizedException(
                "An authenticated workflow administrator is required.");
        }
        if (user.Length > InstanceVersionChangeBatchConstraints.MaxActorNameLength)
        {
            throw new WorkflowDomainException("The workflow administrator name is too long.");
        }
        return user;
    }

    private static string NormalizeRequiredReason(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        var length = normalized.EnumerateRunes().Count();
        if (length is < 1 or > InstanceVersionChangeBatchConstraints.MaxReasonLength)
        {
            throw new WorkflowDomainException(
                $"Reason must contain between 1 and {InstanceVersionChangeBatchConstraints.MaxReasonLength} Unicode characters.");
        }
        return normalized;
    }

    private static string? NormalizeOptionalReason(string? value, string label)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is not null
            && normalized.EnumerateRunes().Count()
            > InstanceVersionChangeBatchConstraints.MaxReasonLength)
        {
            throw new WorkflowDomainException(
                $"{label} cannot exceed {InstanceVersionChangeBatchConstraints.MaxReasonLength} Unicode characters.");
        }
        return normalized;
    }

    private static string? NormalizeIdempotencyKey(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized?.Length > InstanceVersionChangeBatchConstraints.MaxIdempotencyKeyLength)
        {
            throw new WorkflowDomainException(
                $"IdempotencyKey cannot exceed {InstanceVersionChangeBatchConstraints.MaxIdempotencyKeyLength} characters.");
        }
        return normalized;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long? PositiveOrNull(long? value, string name)
    {
        if (value is <= 0)
        {
            throw new WorkflowDomainException($"{name} must be greater than zero.");
        }
        return value;
    }

    private static int? PositiveOrNull(int? value, string name)
    {
        if (value is <= 0)
        {
            throw new WorkflowDomainException($"{name} must be greater than zero.");
        }
        return value;
    }

    private static void EnsurePositive(long value, string name)
    {
        if (value <= 0)
        {
            throw new WorkflowDomainException($"{name} must be greater than zero.");
        }
    }

    private static string Limit(string value) =>
        value.EnumerateRunes().Count()
            <= InstanceVersionChangeBatchConstraints.MaxErrorDescriptionLength
            ? value
            : string.Concat(value.EnumerateRunes()
                .Take(InstanceVersionChangeBatchConstraints.MaxErrorDescriptionLength)
                .Select(rune => rune.ToString()));
}
