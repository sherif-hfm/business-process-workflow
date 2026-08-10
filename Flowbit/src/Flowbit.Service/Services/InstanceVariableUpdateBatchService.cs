using System.Text;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

public sealed class InstanceVariableUpdateBatchService(
    IWorkflowDefinitionRepository definitions,
    IInstanceVariableUpdateCandidateRepository candidates,
    IInstanceVariableUpdateBatchRepository batches,
    IWorkflowJobRepository jobs,
    IEngineSettingsRepository engineSettings,
    IUnitOfWork unitOfWork,
    WorkflowContextOptions contextOptions,
    TimeProvider timeProvider) : IInstanceVariableUpdateBatchService
{
    private static readonly JsonSerializerOptions WebJsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan[] BatchRetryDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5)
    ];

    public async Task<PagedResult<InstanceVariableUpdateCandidateDto>> SearchCandidatesAsync(
        InstanceVariableUpdateCandidateSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        InstanceVariableUpdateValidation.RequireActor(actor);
        var filter = request.Filter
            ?? throw new WorkflowDomainException("A candidate filter is required.");
        var query = await BuildCandidateQueryAsync(
            filter,
            request.Sort,
            request.Cursor,
            request.IncludeVariables ?? false,
            Math.Max(1, request.Page ?? 1),
            Math.Clamp(request.PageSize ?? 50, 1, 200),
            cancellationToken);
        var result = await candidates.SearchAsync(query, cancellationToken);
        var workflowIds = result.Items.Select(item => item.WorkflowDefinitionId)
            .Distinct()
            .ToArray();
        var workflows = await definitions.GetManyAsync(workflowIds, cancellationToken);
        var jobSummaries = await jobs.GetInstanceJobSummariesAsync(
            result.Items.Select(item => item.Id).ToArray(),
            cancellationToken);
        return new PagedResult<InstanceVariableUpdateCandidateDto>(
            result.Items.Select(item => ToCandidateDto(item, workflows, jobSummaries)).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount)
        {
            NextCursor = result.NextCursor
        };
    }

    public async Task<InstanceVariableUpdateBatchDetailDto> CreateAsync(
        CreateInstanceVariableUpdateBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = InstanceVariableUpdateValidation.RequireActor(actor);
        var workflowKey = InstanceVariableUpdateValidation.NormalizeWorkflowKey(
            request.WorkflowKey);
        var writes = InstanceVariableUpdateValidation.NormalizeWrites(request.Variables);
        var reason = InstanceVariableUpdateValidation.NormalizeReason(request.Reason);
        var idempotencyKey = InstanceVariableUpdateValidation.NormalizeIdempotencyKey(
            request.IdempotencyKey);
        var selection = request.Selection
            ?? throw new WorkflowDomainException("A batch selection is required.");
        var canonicalSelection = CanonicalizeSelection(workflowKey, selection);
        var serializedWrites = InstanceVariableUpdateValidation.SerializeWrites(writes);
        var selectionSnapshot = JsonSerializer.SerializeToElement(
            canonicalSelection,
            WebJsonOptions);

        if (idempotencyKey is not null)
        {
            var existing = await batches.FindByIdempotencyKeyAsync(
                user,
                idempotencyKey,
                cancellationToken);
            if (existing is not null)
            {
                EnsureIdempotentReplayMatches(
                    existing,
                    workflowKey,
                    serializedWrites,
                    reason,
                    selectionSnapshot);
                return await ToDetailAsync(existing, cancellationToken);
            }
        }

        var family = await RequireWorkflowFamilyAsync(workflowKey, cancellationToken);
        var maxInstances = await ResolveMaxBatchInstancesAsync(cancellationToken);
        var (query, excluded, expectedExplicit) = await BuildSelectionQueryAsync(
            workflowKey,
            canonicalSelection,
            maxInstances,
            cancellationToken);
        if (expectedExplicit is > 0 && expectedExplicit > maxInstances)
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
                "One or more explicitly selected running instances are no longer in the selected workflow family.");
        }
        if (frozen.Count == 0)
        {
            throw new WorkflowDomainException("The frozen selection contains no running instances.");
        }

        var expandedWrites = checked((long)frozen.Count * writes.Count);
        if (expandedWrites > InstanceVariableUpdateConstraints.MaxExpandedWrites)
        {
            throw new WorkflowDomainException(
                $"The batch expands to {expandedWrites:N0} variable writes; the maximum is {InstanceVariableUpdateConstraints.MaxExpandedWrites:N0}.");
        }
        var valueBytes = Encoding.UTF8.GetByteCount(serializedWrites.GetRawText());
        var expandedBytes = checked((long)valueBytes * frozen.Count);
        if (expandedBytes > InstanceVariableUpdateConstraints.MaxExpandedPayloadBytes)
        {
            throw new WorkflowDomainException(
                "The expanded serialized variable values exceed the 100 MiB batch limit.");
        }

        var representedDefinitionIds = frozen
            .Select(item => item.WorkflowDefinitionId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var represented = await definitions.GetManyAsync(
            representedDefinitionIds,
            cancellationToken);
        if (represented.Count != representedDefinitionIds.Length
            || represented.Values.Any(definition => !string.Equals(
                definition.WorkflowKey,
                workflowKey,
                StringComparison.Ordinal)))
        {
            throw new WorkflowConflictException(
                "The selected workflow family changed while the batch was being frozen.");
        }

        var now = timeProvider.GetUtcNow();
        InstanceVariableUpdateBatchRecord batch;
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
                    EnsureIdempotentReplayMatches(
                        winner,
                        workflowKey,
                        serializedWrites,
                        reason,
                        selectionSnapshot);
                    await transaction.CommitAsync(cancellationToken);
                    return await ToDetailAsync(winner, cancellationToken);
                }
            }

            batch = await batches.AddAsync(
                new NewInstanceVariableUpdateBatchRecord(
                    workflowKey,
                    serializedWrites,
                    selectionSnapshot,
                    reason,
                    user,
                    InstanceVariableUpdateValidation.SnapshotRoles(actor.Roles),
                    idempotencyKey,
                    now),
                cancellationToken);
            await batches.AddItemsAsync(
                batch.Id,
                frozen.OrderBy(item => item.InstanceId)
                    .Select(item => new NewInstanceVariableUpdateBatchItemRecord(
                        item.InstanceId,
                        item.WorkflowDefinitionId,
                        item.UpdatedAt,
                        now))
                    .ToArray(),
                cancellationToken);

            var claims = SnapshotAllowedClaims(actor.Claims, contextOptions.AllowedClaims);
            foreach (var definitionId in representedDefinitionIds)
            {
                var definition = represented[definitionId];
                var job = await EnqueueBatchJobAsync(
                    batch.Id,
                    definition,
                    WorkflowJobKinds.InstanceVariableUpdateBatchPrepare,
                    InstanceVariableUpdateBatchPhases.Prepare,
                    claims,
                    now,
                    cancellationToken);
                await batches.AddJobLinkAsync(
                    new NewInstanceVariableUpdateBatchJobLinkRecord(
                        batch.Id,
                        definition.Id,
                        InstanceVariableUpdateBatchPhases.Prepare,
                        job.Id,
                        job.Id),
                    cancellationToken);
            }

            batch = await batches.UpdateAsync(
                ToUpdate(batch) with
                {
                    TotalItemCount = frozen.Count,
                    UpdatedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await ToDetailAsync(batch, cancellationToken);
    }

    public async Task<PagedResult<InstanceVariableUpdateBatchSummaryDto>> ListAsync(
        InstanceVariableUpdateBatchSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        InstanceVariableUpdateValidation.RequireActor(actor);
        var status = NormalizeOptional(request.Status);
        if (status is not null && !InstanceVariableUpdateBatchStatuses.IsKnown(status))
        {
            throw new WorkflowDomainException(
                $"Unknown instance-variable update batch status '{status}'.");
        }
        var result = await batches.ListAsync(
            new InstanceVariableUpdateBatchSearch(
                NormalizeOptional(request.WorkflowKey),
                status,
                NormalizeOptional(request.PreparedBy),
                Math.Max(1, request.Page ?? 1),
                Math.Clamp(request.PageSize ?? 50, 1, 200)),
            cancellationToken);
        var summaries = new List<InstanceVariableUpdateBatchSummaryDto>(result.Items.Count);
        foreach (var batch in result.Items)
        {
            var links = await batches.ListJobLinksAsync(batch.Id, cancellationToken);
            summaries.Add(ToSummary(batch, links.Select(link => link.WorkflowDefinitionId)
                .Distinct().Count()));
        }
        return new PagedResult<InstanceVariableUpdateBatchSummaryDto>(
            summaries,
            result.Page,
            result.PageSize,
            result.TotalCount);
    }

    public async Task<InstanceVariableUpdateBatchDetailDto?> GetAsync(
        long batchId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        InstanceVariableUpdateValidation.RequireActor(actor);
        EnsurePositive(batchId, "Batch id");
        var batch = await batches.GetAsync(batchId, false, cancellationToken);
        return batch is null ? null : await ToDetailAsync(batch, cancellationToken);
    }

    public async Task<PagedResult<InstanceVariableUpdateBatchItemDto>?> ListItemsAsync(
        long batchId,
        string? status,
        int page,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        InstanceVariableUpdateValidation.RequireActor(actor);
        EnsurePositive(batchId, "Batch id");
        var normalizedStatus = NormalizeOptional(status);
        if (normalizedStatus is not null
            && !InstanceVariableUpdateBatchItemStatuses.IsKnown(normalizedStatus))
        {
            throw new WorkflowDomainException(
                $"Unknown instance-variable update item status '{normalizedStatus}'.");
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
        return new PagedResult<InstanceVariableUpdateBatchItemDto>(
            result.Items.Select(ToItem).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount);
    }

    public async Task<InstanceVariableUpdateBatchDetailDto?> ConfirmAsync(
        long batchId,
        ConfirmInstanceVariableUpdateBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePositive(batchId, "Batch id");
        var user = InstanceVariableUpdateValidation.RequireActor(actor);
        InstanceVariableUpdateBatchRecord batch;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            batch = await batches.GetAsync(batchId, true, cancellationToken) ?? null!;
            if (batch is null)
            {
                return null;
            }
            if (batch.Status is InstanceVariableUpdateBatchStatuses.Queued
                or InstanceVariableUpdateBatchStatuses.Running
                or InstanceVariableUpdateBatchStatuses.Completed
                or InstanceVariableUpdateBatchStatuses.CompletedWithIssues)
            {
                return await ToDetailAsync(batch, cancellationToken);
            }
            if (batch.Status != InstanceVariableUpdateBatchStatuses.Ready)
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
                    "The prepared batch changed; refresh its counts and warnings before confirming.");
            }

            var now = timeProvider.GetUtcNow();
            var queued = await batches.TransitionItemsAsync(
                batch.Id,
                [InstanceVariableUpdateBatchItemStatuses.Eligible],
                InstanceVariableUpdateBatchItemStatuses.Queued,
                now,
                cancellationToken);
            if (queued != batch.EligibleItemCount)
            {
                throw new WorkflowConflictException(
                    "The prepared eligible population changed; refresh the batch before confirming.");
            }

            var prepareLinks = await batches.ListJobLinksAsync(batch.Id, cancellationToken);
            var definitionIds = prepareLinks
                .Where(link => link.Phase == InstanceVariableUpdateBatchPhases.Prepare)
                .Select(link => link.WorkflowDefinitionId)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            var workflows = await definitions.GetManyAsync(definitionIds, cancellationToken);
            if (workflows.Count != definitionIds.Length)
            {
                throw new WorkflowConflictException(
                    "A workflow version represented by the prepared batch no longer exists.");
            }
            var claims = SnapshotAllowedClaims(actor.Claims, contextOptions.AllowedClaims);
            if (queued > 0)
            {
                foreach (var definitionId in definitionIds)
                {
                    var definition = workflows[definitionId];
                    var job = await EnqueueBatchJobAsync(
                        batch.Id,
                        definition,
                        WorkflowJobKinds.InstanceVariableUpdateBatchExecute,
                        InstanceVariableUpdateBatchPhases.Execute,
                        claims,
                        now,
                        cancellationToken);
                    await batches.AddJobLinkAsync(
                        new NewInstanceVariableUpdateBatchJobLinkRecord(
                            batch.Id,
                            definition.Id,
                            InstanceVariableUpdateBatchPhases.Execute,
                            job.Id,
                            job.Id),
                        cancellationToken);
                }
            }
            batch = await batches.UpdateAsync(
                ToUpdate(batch) with
                {
                    Status = queued > 0
                        ? InstanceVariableUpdateBatchStatuses.Queued
                        : InstanceVariableUpdateBatchStatuses.CompletedWithIssues,
                    ConfirmedBy = user,
                    ConfirmedByRoles = InstanceVariableUpdateValidation.SnapshotRoles(actor.Roles),
                    EligibleItemCount = 0,
                    QueuedItemCount = queued,
                    ConfirmedAt = now,
                    CompletedAt = queued == 0 ? now : null,
                    UpdatedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return await ToDetailAsync(batch, cancellationToken);
    }

    public async Task<InstanceVariableUpdateBatchDetailDto?> CancelAsync(
        long batchId,
        CancelInstanceVariableUpdateBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePositive(batchId, "Batch id");
        var user = InstanceVariableUpdateValidation.RequireActor(actor);
        var cancellationReason = InstanceVariableUpdateValidation.NormalizeReason(
            request.Reason,
            "Cancellation reason");
        InstanceVariableUpdateBatchRecord batch;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            batch = await batches.GetAsync(batchId, true, cancellationToken) ?? null!;
            if (batch is null)
            {
                return null;
            }
            if (batch.Status == InstanceVariableUpdateBatchStatuses.Cancelled)
            {
                return await ToDetailAsync(batch, cancellationToken);
            }
            if (batch.Status is InstanceVariableUpdateBatchStatuses.Completed
                or InstanceVariableUpdateBatchStatuses.CompletedWithIssues
                or InstanceVariableUpdateBatchStatuses.Failed)
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
            batch = await batches.UpdateAsync(
                ApplyCounts(ToUpdate(batch), counts) with
                {
                    Status = InstanceVariableUpdateBatchStatuses.Cancelled,
                    WarningItemCount = warningCount,
                    CancelledBy = user,
                    CancellationReason = cancellationReason,
                    CancelledAt = now,
                    CompletedAt = Count(counts, InstanceVariableUpdateBatchItemStatuses.Queued) == 0
                        ? now
                        : null,
                    UpdatedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return await ToDetailAsync(batch, cancellationToken);
    }

    private async Task<InstanceVariableUpdateCandidateQuery> BuildCandidateQueryAsync(
        InstanceVariableUpdateCandidateFilterDto filter,
        IReadOnlyList<SearchSortDto>? sort,
        string? cursor,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var workflowKey = InstanceVariableUpdateValidation.NormalizeWorkflowKey(
            filter.WorkflowKey);
        await RequireWorkflowFamilyAsync(workflowKey, cancellationToken);
        var workflowId = PositiveOrNull(filter.WorkflowId, "Workflow definition id");
        if (workflowId is not null)
        {
            var workflow = await definitions.GetAsync(workflowId.Value, cancellationToken);
            if (workflow is null
                || !string.Equals(workflow.WorkflowKey, workflowKey, StringComparison.Ordinal))
            {
                throw new WorkflowDomainException(
                    $"Workflow definition #{workflowId.Value} does not belong to workflow family '{workflowKey}'.");
            }
        }
        return new InstanceVariableUpdateCandidateQuery(
            workflowKey,
            workflowId,
            PositiveOrNull(filter.InstanceId, "Instance id"),
            NormalizeOptional(filter.BusinessKey),
            PositiveOrNull(filter.NodeId, "Node id"),
            NormalizeOptional(filter.NodeExternalId),
            VariableFilterParser.Parse(filter.VariableFilter),
            ParseSort(sort),
            NormalizeOptional(cursor),
            includeVariables,
            page,
            pageSize);
    }

    private async Task<(InstanceVariableUpdateCandidateQuery Query,
        IReadOnlyList<long> Excluded, int? ExpectedExplicit)> BuildSelectionQueryAsync(
        string workflowKey,
        InstanceVariableUpdateBatchSelectionDto selection,
        int maxInstances,
        CancellationToken cancellationToken)
    {
        var excluded = NormalizeInstanceIds(
            selection.ExcludedInstanceIds,
            allowEmpty: true,
            "excluded instance");
        var mode = selection.Mode?.Trim();
        if (string.Equals(mode, InstanceVariableUpdateBatchSelectionModes.Explicit,
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
            return (new InstanceVariableUpdateCandidateQuery(
                workflowKey,
                WorkflowDefinitionId: null,
                InstanceId: null,
                BusinessKey: null,
                NodeId: null,
                NodeExternalId: null,
                VariableFilter: null,
                Sort: [],
                Cursor: null,
                IncludeVariables: false,
                Page: 1,
                PageSize: Math.Min(included.Length, maxInstances),
                InstanceIds: included), excluded, included.Length);
        }

        if (!string.Equals(mode, InstanceVariableUpdateBatchSelectionModes.AllMatching,
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
        if (!string.Equals(
                selection.Filter.WorkflowKey?.Trim(),
                workflowKey,
                StringComparison.Ordinal))
        {
            throw new WorkflowDomainException(
                "The all-matching filter must use the batch workflow family.");
        }
        var query = await BuildCandidateQueryAsync(
            selection.Filter,
            sort: null,
            cursor: null,
            includeVariables: false,
            page: 1,
            pageSize: maxInstances,
            cancellationToken);
        return (query, excluded, null);
    }

    private async Task<WorkflowJobRecord> EnqueueBatchJobAsync(
        long batchId,
        WorkflowDefinitionRecord definition,
        string kind,
        string phase,
        IReadOnlyDictionary<string, string> actorClaims,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var anchor = definition.Definition.FlowNodes.FirstOrDefault(node =>
                         node.Id == definition.Definition.InitialEventId)
                     ?? definition.Definition.FlowNodes.First();
        return await jobs.EnqueueAsync(
            new WorkflowJobCreateRecord
            {
                WorkflowDefinitionId = definition.Id,
                WorkflowKey = definition.WorkflowKey,
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
                    new InstanceVariableUpdateBatchJobPayload(
                        batchId,
                        definition.Id,
                        phase)
                    {
                        ActorClaims = actorClaims
                    })
            },
            cancellationToken);
    }

    private async Task<InstanceVariableUpdateBatchDetailDto> ToDetailAsync(
        InstanceVariableUpdateBatchRecord batch,
        CancellationToken cancellationToken)
    {
        var links = await batches.ListJobLinksAsync(batch.Id, cancellationToken);
        var workflowIds = links.Select(link => link.WorkflowDefinitionId).Distinct().ToArray();
        var workflows = await definitions.GetManyAsync(workflowIds, cancellationToken);
        var jobDtos = new List<InstanceVariableUpdateBatchJobLinkDto>(links.Count);
        foreach (var link in links.OrderBy(link => link.Phase).ThenBy(link => link.WorkflowDefinitionId))
        {
            if (!workflows.TryGetValue(link.WorkflowDefinitionId, out var workflow))
            {
                throw new InvalidOperationException(
                    $"Variable-update batch #{batch.Id} references missing workflow definition #{link.WorkflowDefinitionId}.");
            }
            var job = link.JobId is long jobId
                ? await jobs.GetAsync(jobId, cancellationToken)
                : null;
            jobDtos.Add(new InstanceVariableUpdateBatchJobLinkDto(
                link.Id,
                link.OriginalJobId,
                link.JobId,
                link.Phase,
                WorkflowDefinitionService.ToSummary(workflow),
                job?.Status));
        }

        var writes = batch.Variables.Deserialize<List<InstanceVariableWriteDto>>() ?? [];
        var issues = batch.Issues?.Deserialize<List<InstanceVariableUpdateIssueDto>>() ?? [];
        return new InstanceVariableUpdateBatchDetailDto(
            ToSummary(batch, workflowIds.Length),
            batch.Selection.Clone(),
            writes,
            batch.PreparedByRoles,
            batch.ConfirmedByRoles,
            issues,
            jobDtos,
            batch.CancelledBy,
            batch.CancellationReason,
            batch.PreparedAt,
            batch.ConfirmedAt,
            batch.StartedAt,
            batch.CancelledAt);
    }

    private static InstanceVariableUpdateBatchSummaryDto ToSummary(
        InstanceVariableUpdateBatchRecord batch,
        int workflowDefinitionCount) => new(
        batch.Id,
        batch.WorkflowKey,
        batch.Status,
        batch.PreparedBy,
        batch.ConfirmedBy,
        batch.Reason,
        DeserializeWrites(batch.Variables).Count,
        workflowDefinitionCount,
        batch.TotalItemCount,
        batch.EligibleItemCount,
        batch.IneligibleItemCount,
        batch.WarningItemCount,
        batch.QueuedItemCount,
        batch.SucceededItemCount,
        batch.SkippedItemCount,
        batch.FailedItemCount,
        batch.CancelledItemCount,
        batch.CreatedAt,
        batch.UpdatedAt,
        batch.CompletedAt);

    private static InstanceVariableUpdateBatchItemDto ToItem(
        InstanceVariableUpdateBatchItemRecord item) => new(
        item.Id,
        item.BatchId,
        item.InstanceId,
        item.BusinessKey,
        item.CapturedWorkflowDefinitionId,
        item.CapturedInstanceUpdatedAt,
        item.Status,
        item.Plan?.Deserialize<List<InstanceVariableUpdateOutcomePlanDto>>(
            WebJsonOptions) ?? [],
        item.Warnings?.Deserialize<List<InstanceVariableUpdateIssueDto>>(
            WebJsonOptions) ?? [],
        item.Result?.Clone(),
        item.UpdateOperationId,
        item.ErrorCode,
        item.ErrorDescription,
        item.CreatedAt,
        item.UpdatedAt,
        item.PreparedAt,
        item.StartedAt,
        item.CompletedAt);

    private static InstanceVariableUpdateCandidateDto ToCandidateDto(
        InstanceListItem item,
        IReadOnlyDictionary<long, WorkflowDefinitionRecord> workflows,
        IReadOnlyDictionary<long, WorkflowInstanceJobSummaryRecord> jobSummaries)
    {
        if (!workflows.TryGetValue(item.WorkflowDefinitionId, out var workflow))
        {
            throw new InvalidOperationException(
                $"Candidate instance #{item.Id} references missing workflow definition #{item.WorkflowDefinitionId}.");
        }
        var dto = new InstanceVariableUpdateCandidateDto(
            item.Id,
            item.WorkflowDefinitionId,
            workflow.WorkflowKey,
            item.WorkflowName,
            item.WorkflowVersion,
            item.Status,
            item.BusinessKey,
            item.CreatedAt,
            item.UpdatedAt,
            (item.ExecutionPositions ?? []).Select(position => new ExecutionPositionDto(
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
                position.WaitingTimerSubscriptionId)).ToArray())
        {
            Variables = item.Variables
        };
        if (jobSummaries.TryGetValue(item.Id, out var summary))
        {
            dto = dto with
            {
                Jobs = new InstanceJobSummaryDto(
                    summary.OpenCount,
                    summary.QueuedCount,
                    summary.RunningCount,
                    summary.IncidentCount,
                    summary.NearestDueAt)
            };
        }
        return dto;
    }

    internal static InstanceVariableUpdateBatchUpdateRecord ToUpdate(
        InstanceVariableUpdateBatchRecord batch) => new(
        batch.Id,
        batch.Status,
        batch.ConfirmedBy,
        batch.ConfirmedByRoles,
        batch.TotalItemCount,
        batch.EligibleItemCount,
        batch.IneligibleItemCount,
        batch.WarningItemCount,
        batch.QueuedItemCount,
        batch.SucceededItemCount,
        batch.SkippedItemCount,
        batch.FailedItemCount,
        batch.CancelledItemCount,
        batch.Issues,
        batch.CancelledBy,
        batch.CancellationReason,
        batch.UpdatedAt,
        batch.PreparedAt,
        batch.ConfirmedAt,
        batch.StartedAt,
        batch.CompletedAt,
        batch.CancelledAt);

    internal static InstanceVariableUpdateBatchItemUpdateRecord ToItemUpdate(
        InstanceVariableUpdateBatchItemRecord item) => new(
        item.Id,
        item.Status,
        item.Plan,
        item.Warnings,
        item.Result,
        item.UpdateOperationId,
        item.ErrorCode,
        item.ErrorDescription,
        item.UpdatedAt,
        item.PreparedAt,
        item.StartedAt,
        item.CompletedAt);

    internal static InstanceVariableUpdateBatchUpdateRecord ApplyCounts(
        InstanceVariableUpdateBatchUpdateRecord update,
        IReadOnlyDictionary<string, int> counts) => update with
        {
            EligibleItemCount = Count(counts, InstanceVariableUpdateBatchItemStatuses.Eligible),
            IneligibleItemCount = Count(counts, InstanceVariableUpdateBatchItemStatuses.Ineligible),
            QueuedItemCount = Count(counts, InstanceVariableUpdateBatchItemStatuses.Queued),
            SucceededItemCount = Count(counts, InstanceVariableUpdateBatchItemStatuses.Succeeded),
            SkippedItemCount = Count(counts, InstanceVariableUpdateBatchItemStatuses.Skipped),
            FailedItemCount = Count(counts, InstanceVariableUpdateBatchItemStatuses.Failed),
            CancelledItemCount = Count(counts, InstanceVariableUpdateBatchItemStatuses.Cancelled)
        };

    internal static int Count(
        IReadOnlyDictionary<string, int> counts,
        string status) => counts.TryGetValue(status, out var value) ? value : 0;

    internal static JsonElement? SerializeIssues(
        IReadOnlyCollection<InstanceVariableUpdateIssueDto> issues) =>
        issues.Count == 0 ? null : JsonSerializer.SerializeToElement(issues);

    internal static InstanceVariableUpdateIssueDto Issue(string code, string message) =>
        new(Limit(code, InstanceVariableUpdateConstraints.MaxErrorCodeLength),
            Limit(message, InstanceVariableUpdateConstraints.MaxErrorDescriptionLength));

    internal static IReadOnlyList<InstanceVariableWriteDto> DeserializeWrites(
        JsonElement value) => value.Deserialize<List<InstanceVariableWriteDto>>() ?? [];

    private async Task<IReadOnlyList<WorkflowDefinitionRecord>> RequireWorkflowFamilyAsync(
        string workflowKey,
        CancellationToken cancellationToken)
    {
        var versions = await definitions.ListVersionsByKeyAsync(workflowKey, cancellationToken);
        if (versions.Count == 0)
        {
            throw new WorkflowDomainException(
                $"Workflow family '{workflowKey}' was not found.");
        }
        return versions;
    }

    private async Task<int> ResolveMaxBatchInstancesAsync(
        CancellationToken cancellationToken)
    {
        var setting = await engineSettings.GetByKeyAsync(
            InstanceVariableUpdateConstraints.MaxBatchInstancesSetting,
            cancellationToken);
        return int.TryParse(setting?.Value, out var value) && value > 0
            ? Math.Min(value, InstanceVariableUpdateConstraints.MaxBatchInstances)
            : InstanceVariableUpdateConstraints.MaxBatchInstances;
    }

    private static IReadOnlyList<InstanceSortCriterion> ParseSort(
        IReadOnlyList<SearchSortDto>? values)
    {
        if (values is null || values.Count == 0)
        {
            return [new InstanceSortCriterion(InstanceSortField.UpdatedAt, SortDirection.Descending)];
        }
        if (values.Count > 3)
        {
            throw new WorkflowDomainException("At most 3 sort clauses are allowed.");
        }
        var result = new List<InstanceSortCriterion>(values.Count);
        var fields = new HashSet<InstanceSortField>();
        foreach (var value in values)
        {
            if (value is null || string.IsNullOrWhiteSpace(value.Field))
            {
                throw new WorkflowDomainException("Sort fields must not be blank.");
            }
            var field = value.Field.Trim().ToLowerInvariant() switch
            {
                "id" => InstanceSortField.Id,
                "createdat" => InstanceSortField.CreatedAt,
                "updatedat" => InstanceSortField.UpdatedAt,
                _ => throw new WorkflowDomainException(
                    $"Unknown instance sort field '{value.Field}'. Allowed fields: id, createdAt, updatedAt.")
            };
            if (!fields.Add(field))
            {
                throw new WorkflowDomainException(
                    $"Sort field '{value.Field}' was specified more than once.");
            }
            var direction = value.Direction?.Trim().ToLowerInvariant() switch
            {
                "asc" => SortDirection.Ascending,
                "desc" => SortDirection.Descending,
                _ => throw new WorkflowDomainException(
                    $"Unknown sort direction '{value.Direction}'. Allowed directions: asc, desc.")
            };
            result.Add(new InstanceSortCriterion(field, direction));
        }
        return result;
    }

    private static IReadOnlyList<long> NormalizeInstanceIds(
        IReadOnlyList<long>? values,
        bool allowEmpty,
        string label)
    {
        var result = (values ?? []).Distinct().OrderBy(id => id).ToArray();
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

    private static InstanceVariableUpdateBatchSelectionDto CanonicalizeSelection(
        string workflowKey,
        InstanceVariableUpdateBatchSelectionDto selection)
    {
        var mode = selection.Mode?.Trim();
        var excluded = NormalizeInstanceIds(
            selection.ExcludedInstanceIds,
            allowEmpty: true,
            "excluded instance");

        if (string.Equals(
                mode,
                InstanceVariableUpdateBatchSelectionModes.Explicit,
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
            var idSet = ids.ToHashSet();
            var effectiveExcluded = excluded.Where(idSet.Contains).ToArray();
            return new InstanceVariableUpdateBatchSelectionDto(
                InstanceVariableUpdateBatchSelectionModes.Explicit,
                ids,
                Filter: null,
                effectiveExcluded);
        }

        if (!string.Equals(
                mode,
                InstanceVariableUpdateBatchSelectionModes.AllMatching,
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
        if (!string.Equals(
                selection.Filter.WorkflowKey?.Trim(),
                workflowKey,
                StringComparison.Ordinal))
        {
            throw new WorkflowDomainException(
                "The all-matching filter must use the batch workflow family.");
        }

        var canonicalFilter = new InstanceVariableUpdateCandidateFilterDto
        {
            WorkflowKey = workflowKey,
            WorkflowId = PositiveOrNull(
                selection.Filter.WorkflowId,
                "Workflow definition id"),
            InstanceId = PositiveOrNull(selection.Filter.InstanceId, "Instance id"),
            BusinessKey = NormalizeOptional(selection.Filter.BusinessKey),
            NodeId = PositiveOrNull(selection.Filter.NodeId, "Node id"),
            NodeExternalId = NormalizeOptional(selection.Filter.NodeExternalId),
            VariableFilter = selection.Filter.VariableFilter?.Clone()
        };
        return new InstanceVariableUpdateBatchSelectionDto(
            InstanceVariableUpdateBatchSelectionModes.AllMatching,
            InstanceIds: null,
            canonicalFilter,
            excluded);
    }

    private static void EnsureIdempotentReplayMatches(
        InstanceVariableUpdateBatchRecord existing,
        string workflowKey,
        JsonElement variables,
        string? reason,
        JsonElement selection)
    {
        if (!string.Equals(existing.WorkflowKey, workflowKey, StringComparison.Ordinal)
            || !JsonElement.DeepEquals(existing.Variables, variables)
            || !string.Equals(existing.Reason, reason, StringComparison.Ordinal)
            || !JsonElement.DeepEquals(existing.Selection, selection))
        {
            throw new WorkflowConflictException(
                "IdempotencyKey was already used for a different instance-variable update batch request.");
        }
    }

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

    private static string Limit(string value, int maximum) =>
        value.EnumerateRunes().Count() <= maximum
            ? value
            : string.Concat(value.EnumerateRunes().Take(maximum).Select(rune => rune.ToString()));
}
