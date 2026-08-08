using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public sealed class AdministrativeActionBatchService(
    IWorkflowDefinitionRepository definitions,
    IAdministrativeActionCandidateRepository candidates,
    IAdministrativeActionBatchRepository batches,
    IWorkflowJobRepository jobs,
    IEngineSettingsRepository engineSettings,
    IUnitOfWork unitOfWork,
    WorkflowContextOptions contextOptions,
    TimeProvider timeProvider) : IAdministrativeActionBatchService
{
    private static readonly TimeSpan[] BatchRetryDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5)
    ];

    private sealed record ResolvedAction(
        WorkflowDefinitionRecord Workflow,
        FlowNodeModel Source,
        SequenceFlowModel Flow,
        FlowNodeModel? Boundary,
        AdministrativeActionSummaryDto Summary);

    public async Task<IReadOnlyList<WorkflowSummaryDto>> ListWorkflowCatalogAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        RequireActor(actor);
        var latest = await definitions.ListLatestAsync(cancellationToken);
        var result = new List<WorkflowSummaryDto>();
        foreach (var key in latest.Select(item => item.WorkflowKey).Distinct(StringComparer.Ordinal))
        {
            var versions = await definitions.ListVersionsByKeyAsync(key, cancellationToken);
            result.AddRange(versions
                .Where(version => version.Definition.FlowNodes
                    .Where(node => BpmnFlowNodeTypes.IsUserTask(node.Type))
                    .Any(node => ResolveActions(version, node.Id).Count > 0))
                .Select(WorkflowDefinitionService.ToSummary));
        }
        return result
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.WorkflowKey, StringComparer.Ordinal)
            .ThenByDescending(item => item.Version)
            .ToArray();
    }

    public async Task<IReadOnlyList<AdministrativeActionSourceNodeDto>> ListSourceNodesAsync(
        long workflowDefinitionId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        RequireActor(actor);
        var workflow = await GetWorkflowAsync(workflowDefinitionId, cancellationToken);
        return workflow.Definition.FlowNodes
            .Where(node => BpmnFlowNodeTypes.IsUserTask(node.Type))
            .Where(node => ResolveActions(workflow, node.Id).Count > 0)
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Id)
            .Select(node => new AdministrativeActionSourceNodeDto(
                workflow.Id,
                workflow.Version,
                node.Id,
                node.Name,
                node.ExternalId,
                node.MultiInstance is not null))
            .ToArray();
    }

    public async Task<IReadOnlyList<AdministrativeActionSummaryDto>> ListActionsAsync(
        long workflowDefinitionId,
        int sourceNodeId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        RequireActor(actor);
        var workflow = await GetWorkflowAsync(workflowDefinitionId, cancellationToken);
        RequireSourceNode(workflow, sourceNodeId);
        return ResolveActions(workflow, sourceNodeId);
    }

    public async Task<PagedResult<AdministrativeActionCandidateDto>> SearchCandidatesAsync(
        AdministrativeActionCandidateSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireActor(actor);
        var workflow = await GetWorkflowAsync(request.WorkflowDefinitionId, cancellationToken);
        RequireSourceNode(workflow, request.SourceNodeId);
        var query = BuildCandidateQuery(request);
        var page = await candidates.SearchAsync(query, cancellationToken);
        return new PagedResult<AdministrativeActionCandidateDto>(
            page.Items.Select(item => ToCandidateDto(item, workflow.Version)).ToArray(),
            page.Page,
            page.PageSize,
            page.TotalCount);
    }

    public async Task<AdministrativeActionBatchDetailDto> CreateAsync(
        CreateAdministrativeActionBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = RequireActor(actor);
        var reason = NormalizeOptionalReason(request.Reason, "Reason");
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);
        var selection = request.Selection
            ?? throw new WorkflowDomainException("A batch selection is required.");
        ValidateVariableNameUniqueness(request.Variables);

        if (idempotencyKey is not null)
        {
            var existing = await batches.FindByIdempotencyKeyAsync(user, idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                EnsureIdempotentReplayMatches(existing, request, reason, selection);
                return ToDetail(existing);
            }
        }

        var resolved = await ResolveActionAsync(request, cancellationToken);
        ValidateCommonVariables(resolved.Summary, request.Variables);
        ValidateMultiInstanceMode(resolved, request.MultiInstanceMode);
        var maxAffectedTasks = await ResolveMaxAffectedTasksAsync(cancellationToken);
        var (query, excluded, expectedExplicit) = BuildSelectionQuery(selection, request);
        var frozen = await candidates.MaterializeAsync(
            query,
            excluded,
            maxAffectedTasks,
            cancellationToken);

        if (expectedExplicit is not null && frozen.Count != expectedExplicit.Value)
        {
            throw new WorkflowDomainException(
                "One or more explicitly selected execution positions no longer match the exact workflow version and source node.");
        }
        if (frozen.Count == 0)
        {
            throw new WorkflowDomainException("The frozen selection contains no active execution positions.");
        }
        var affectedTaskCount = frozen.Sum(item => Math.Max(0, item.AffectedTaskCount));
        if (affectedTaskCount > maxAffectedTasks)
        {
            throw new WorkflowDomainException(
                $"The frozen selection affects {affectedTaskCount:N0} tasks and exceeds the configured maximum of {maxAffectedTasks:N0}.");
        }

        var snapshot = ToActionSnapshot(resolved);
        var selectionSnapshot = JsonSerializer.SerializeToElement(selection);
        var commonVariables = CloneVariables(request.Variables);
        var now = timeProvider.GetUtcNow();
        AdministrativeActionBatchRecord batch;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            if (idempotencyKey is not null)
            {
                await batches.LockIdempotencyKeyAsync(user, idempotencyKey, cancellationToken);
                var winner = await batches.FindByIdempotencyKeyAsync(user, idempotencyKey, cancellationToken);
                if (winner is not null)
                {
                    EnsureIdempotentReplayMatches(winner, request, reason, selection);
                    await transaction.CommitAsync(cancellationToken);
                    return ToDetail(winner);
                }
            }

            batch = await batches.AddAsync(
                new NewAdministrativeActionBatchRecord(
                    resolved.Workflow.WorkflowKey,
                    resolved.Workflow.Id,
                    resolved.Source.Id,
                    resolved.Summary.ActionKind,
                    resolved.Flow.Id,
                    resolved.Boundary?.Id,
                    NormalizeOptional(request.MultiInstanceMode),
                    snapshot,
                    reason,
                    commonVariables,
                    selectionSnapshot,
                    user,
                    SnapshotRoles(actor.Roles),
                    idempotencyKey,
                    now),
                cancellationToken);

            await batches.AddItemsAsync(
                batch.Id,
                frozen.Select(candidate => ToNewItem(candidate, resolved, now)).ToArray(),
                cancellationToken);
            var job = await EnqueueBatchJobAsync(
                batch.Id,
                resolved,
                WorkflowJobKinds.AdministrativeBatchPrepare,
                "prepare",
                SnapshotAllowedClaims(actor.Claims, contextOptions.AllowedClaims),
                now,
                cancellationToken);
            batch = await batches.UpdateAsync(
                ToUpdate(batch) with
                {
                    TotalItemCount = frozen.Count,
                    TotalAffectedTaskCount = affectedTaskCount,
                    PreparationJobId = job.Id,
                    UpdatedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return ToDetail(batch);
    }

    public async Task<PagedResult<AdministrativeActionBatchSummaryDto>> ListAsync(
        AdministrativeActionBatchSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireActor(actor);
        var status = NormalizeOptional(request.Status);
        if (status is not null && !AdministrativeActionBatchStatuses.IsKnown(status))
        {
            throw new WorkflowDomainException($"Unknown administrative batch status '{status}'.");
        }
        var page = Math.Max(1, request.Page ?? 1);
        var pageSize = Math.Clamp(request.PageSize ?? 50, 1, 200);
        var result = await batches.ListAsync(
            new AdministrativeActionBatchSearch(
                NormalizeOptional(request.WorkflowKey),
                PositiveOrNull(request.WorkflowDefinitionId, "Workflow definition id"),
                status,
                NormalizeOptional(request.PreparedBy),
                page,
                pageSize),
            cancellationToken);
        return new PagedResult<AdministrativeActionBatchSummaryDto>(
            result.Items.Select(ToSummary).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount);
    }

    public async Task<AdministrativeActionBatchDetailDto?> GetAsync(
        long batchId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        RequireActor(actor);
        EnsurePositive(batchId, "Batch id");
        var batch = await batches.GetAsync(batchId, false, cancellationToken);
        return batch is null ? null : ToDetail(batch);
    }

    public async Task<PagedResult<AdministrativeActionBatchItemDto>?> ListItemsAsync(
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
            && !AdministrativeActionBatchItemStatuses.IsKnown(normalizedStatus))
        {
            throw new WorkflowDomainException($"Unknown administrative batch item status '{normalizedStatus}'.");
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
        return new PagedResult<AdministrativeActionBatchItemDto>(
            result.Items.Select(ToItem).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount);
    }

    public async Task<AdministrativeActionBatchDetailDto?> ConfirmAsync(
        long batchId,
        ConfirmAdministrativeActionBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePositive(batchId, "Batch id");
        AdministrativeActionBatchRecord batch;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            batch = await batches.GetAsync(batchId, true, cancellationToken) ?? null!;
            if (batch is null)
            {
                return null;
            }
            RequireActor(actor);
            if (batch.Status is AdministrativeActionBatchStatuses.Queued
                or AdministrativeActionBatchStatuses.Running
                or AdministrativeActionBatchStatuses.Completed
                or AdministrativeActionBatchStatuses.CompletedWithIssues)
            {
                return ToDetail(batch);
            }
            if (batch.Status != AdministrativeActionBatchStatuses.Ready)
            {
                throw new WorkflowConflictException(
                    $"Only a ready batch can be confirmed; batch #{batch.Id} is '{batch.Status}'.");
            }
            if (request.ExpectedEligibleItemCount != batch.EligibleItemCount
                || request.ExpectedAffectedTaskCount != batch.TotalAffectedTaskCount
                || request.ExpectedBatchUpdatedAt != batch.UpdatedAt)
            {
                throw new WorkflowConflictException(
                    "The prepared batch changed; refresh its eligibility and affected-task summary before confirming.");
            }

            var now = timeProvider.GetUtcNow();
            var queued = await batches.TransitionItemsAsync(
                batch.Id,
                [AdministrativeActionBatchItemStatuses.Eligible],
                AdministrativeActionBatchItemStatuses.Queued,
                now,
                cancellationToken);
            WorkflowJobRecord? job = null;
            if (queued > 0)
            {
                var workflow = await GetWorkflowAsync(batch.WorkflowDefinitionId, cancellationToken);
                var resolved = ResolveAction(
                    workflow,
                    batch.SourceNodeId,
                    batch.ActionKind,
                    batch.FlowId,
                    batch.BoundaryNodeId);
                job = await EnqueueBatchJobAsync(
                    batch.Id,
                    resolved,
                    WorkflowJobKinds.AdministrativeBatchExecute,
                    "execute",
                    SnapshotAllowedClaims(actor.Claims, contextOptions.AllowedClaims),
                    now,
                    cancellationToken);
            }
            batch = await batches.UpdateAsync(
                ToUpdate(batch) with
                {
                    Status = queued > 0
                        ? AdministrativeActionBatchStatuses.Queued
                        : AdministrativeActionBatchStatuses.CompletedWithIssues,
                    ConfirmedBy = RequireActor(actor),
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
        return ToDetail(batch);
    }

    public async Task<AdministrativeActionBatchDetailDto?> CancelAsync(
        long batchId,
        CancelAdministrativeActionBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePositive(batchId, "Batch id");
        var cancellationReason = NormalizeOptionalReason(request.Reason, "Cancellation reason");
        AdministrativeActionBatchRecord batch;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            batch = await batches.GetAsync(batchId, true, cancellationToken) ?? null!;
            if (batch is null)
            {
                return null;
            }
            if (batch.Status == AdministrativeActionBatchStatuses.Cancelled)
            {
                return ToDetail(batch);
            }
            if (batch.Status is AdministrativeActionBatchStatuses.Completed
                or AdministrativeActionBatchStatuses.CompletedWithIssues
                or AdministrativeActionBatchStatuses.Failed)
            {
                throw new WorkflowConflictException($"Terminal batch #{batch.Id} cannot be cancelled.");
            }
            var now = timeProvider.GetUtcNow();
            await batches.CancelUnstartedItemsAsync(batch.Id, now, cancellationToken);
            var counts = await batches.CountItemsByStatusAsync(batch.Id, cancellationToken);
            batch = await batches.UpdateAsync(
                ApplyCounts(ToUpdate(batch), counts) with
                {
                    Status = AdministrativeActionBatchStatuses.Cancelled,
                    CancelledBy = RequireActor(actor),
                    CancellationReason = cancellationReason,
                    CancelledAt = now,
                    CompletedAt = Count(counts, AdministrativeActionBatchItemStatuses.Queued) == 0
                        ? now
                        : null,
                    UpdatedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return ToDetail(batch);
    }

    private async Task<ResolvedAction> ResolveActionAsync(
        CreateAdministrativeActionBatchRequest request,
        CancellationToken cancellationToken)
    {
        var workflow = await GetWorkflowAsync(request.WorkflowDefinitionId, cancellationToken);
        return ResolveAction(
            workflow,
            request.SourceNodeId,
            request.ActionKind,
            request.FlowId,
            request.BoundaryNodeId);
    }

    private static ResolvedAction ResolveAction(
        WorkflowDefinitionRecord workflow,
        int sourceNodeId,
        string? actionKind,
        int flowId,
        int? boundaryNodeId)
    {
        var source = RequireSourceNode(workflow, sourceNodeId);
        var normalizedKind = actionKind?.Trim();
        if (!AdministrativeActionKinds.IsKnown(normalizedKind))
        {
            throw new WorkflowDomainException($"Unknown administrative action kind '{actionKind}'.");
        }
        var action = ResolveActions(workflow, sourceNodeId).SingleOrDefault(candidate =>
            string.Equals(candidate.ActionKind, normalizedKind, StringComparison.Ordinal)
            && candidate.FlowId == flowId
            && candidate.BoundaryNodeId == boundaryNodeId)
            ?? throw new WorkflowDomainException(
                "The selected authored action does not exist on the exact workflow version and source node.");
        var flow = workflow.Definition.SequenceFlows.Single(item => item.Id == flowId);
        var boundary = boundaryNodeId is int id
            ? workflow.Definition.FlowNodes.Single(item => item.Id == id)
            : null;
        return new ResolvedAction(workflow, source, flow, boundary, action);
    }

    private static IReadOnlyList<AdministrativeActionSummaryDto> ResolveActions(
        WorkflowDefinitionRecord workflow,
        int sourceNodeId)
    {
        var source = workflow.Definition.FlowNodes.SingleOrDefault(node => node.Id == sourceNodeId);
        if (source is null || !BpmnFlowNodeTypes.IsUserTask(source.Type))
        {
            return [];
        }
        var result = new List<AdministrativeActionSummaryDto>();
        foreach (var flow in workflow.Definition.SequenceFlows
                     .Where(flow => flow.SourceRef == source.Id && flow.IsSelectable && !flow.IsDefault)
                     .OrderBy(flow => flow.Id))
        {
            var target = workflow.Definition.FlowNodes.SingleOrDefault(node => node.Id == flow.TargetRef);
            if (target is null)
            {
                continue;
            }
            result.Add(ToActionSummary(
                workflow,
                source,
                flow,
                target,
                AdministrativeActionKinds.DirectFlow,
                null));
        }
        foreach (var boundary in workflow.Definition.FlowNodes
                     .Where(node => BpmnFlowNodeTypes.IsTimerBoundary(node.Type)
                                    && node.AttachedToRef == source.Id)
                     .OrderBy(node => node.Id))
        {
            var flow = workflow.Definition.SequenceFlows.SingleOrDefault(item => item.SourceRef == boundary.Id);
            var target = flow is null
                ? null
                : workflow.Definition.FlowNodes.SingleOrDefault(node => node.Id == flow.TargetRef);
            if (flow is null || target is null)
            {
                continue;
            }
            result.Add(ToActionSummary(
                workflow,
                source,
                flow,
                target,
                AdministrativeActionKinds.TimerBoundary,
                boundary));
        }
        return result;
    }

    private static AdministrativeActionSummaryDto ToActionSummary(
        WorkflowDefinitionRecord workflow,
        FlowNodeModel source,
        SequenceFlowModel flow,
        FlowNodeModel target,
        string actionKind,
        FlowNodeModel? boundary) =>
        new(
            workflow.Id,
            workflow.Version,
            actionKind,
            flow.Id,
            flow.ExternalId,
            string.IsNullOrWhiteSpace(flow.Name)
                ? boundary is null
                    ? $"Flow #{flow.Id}"
                    : string.IsNullOrWhiteSpace(boundary.Name)
                        ? $"Timer boundary #{boundary.Id}"
                        : boundary.Name
                : flow.Name,
            source.Id,
            source.Name,
            target.Id,
            target.Name,
            target.Type,
            flow.Variables)
        {
            Condition = flow.Condition,
            Roles = SnapshotRoles(flow.Roles),
            BoundaryNodeId = boundary?.Id,
            BoundaryNodeName = boundary?.Name,
            Timer = boundary?.Timer,
            AuthoredCancelActivity = boundary?.CancelActivity
        };

    private static AdministrativeActionCandidateQuery BuildCandidateQuery(
        AdministrativeActionCandidateSearchRequest request)
    {
        EnsurePositive(request.WorkflowDefinitionId, "Workflow definition id");
        EnsurePositive(request.SourceNodeId, "Source node id");
        if (request.PositionKind is not null
            && !AdministrativeActionPositionKinds.IsKnown(request.PositionKind))
        {
            throw new WorkflowDomainException($"Unknown position kind '{request.PositionKind}'.");
        }
        return new AdministrativeActionCandidateQuery
        {
            WorkflowDefinitionId = request.WorkflowDefinitionId,
            SourceNodeId = request.SourceNodeId,
            PositionKind = request.PositionKind,
            PositionId = PositiveOrNull(request.PositionId, "Position id"),
            InstanceId = PositiveOrNull(request.InstanceId, "Instance id"),
            BusinessKey = NormalizeOptional(request.BusinessKey),
            ExcludedPositions = NormalizePositionReferences(request.ExcludedPositions),
            VariableFilter = VariableFilterParser.Parse(request.VariableFilter),
            IncludeVariables = request.IncludeVariables ?? false,
            Page = Math.Max(1, request.Page ?? 1),
            PageSize = Math.Clamp(request.PageSize ?? 50, 1, 200)
        };
    }

    private static (AdministrativeActionCandidateQuery Query,
        IReadOnlyList<AdministrativeActionPositionKey> Excluded,
        int? ExpectedExplicit) BuildSelectionQuery(
        AdministrativeActionBatchSelectionDto selection,
        CreateAdministrativeActionBatchRequest request)
    {
        var excluded = NormalizePositionReferences(selection.ExcludedPositions);
        var mode = selection.Mode?.Trim();
        if (string.Equals(mode, AdministrativeActionBatchSelectionModes.Explicit, StringComparison.OrdinalIgnoreCase))
        {
            var positions = NormalizePositionReferences(selection.Positions);
            if (positions.Count == 0)
            {
                throw new WorkflowDomainException("Explicit selection requires at least one execution position.");
            }
            var excludedSet = excluded.ToHashSet();
            var expected = positions.Count(position => !excludedSet.Contains(position));
            return (new AdministrativeActionCandidateQuery
            {
                WorkflowDefinitionId = request.WorkflowDefinitionId,
                SourceNodeId = request.SourceNodeId,
                Positions = positions,
                Page = 1,
                PageSize = Math.Min(positions.Count, AdministrativeActionConstraints.MaxAffectedTasks + 1)
            }, excluded, expected);
        }
        if (!string.Equals(mode, AdministrativeActionBatchSelectionModes.AllMatching, StringComparison.OrdinalIgnoreCase)
            || selection.AllMatching is null)
        {
            throw new WorkflowDomainException("Selection mode must be 'explicit' or 'allMatching'.");
        }
        if (selection.AllMatching.WorkflowDefinitionId != request.WorkflowDefinitionId
            || selection.AllMatching.SourceNodeId != request.SourceNodeId)
        {
            throw new WorkflowDomainException(
                "The all-matching filter must use the batch's exact workflow definition and source node.");
        }
        return (BuildCandidateQuery(selection.AllMatching) with
        {
            Page = 1,
            PageSize = AdministrativeActionConstraints.MaxAffectedTasks + 1,
            IncludeVariables = false
        }, excluded, null);
    }

    private static IReadOnlyList<AdministrativeActionPositionKey> NormalizePositionReferences(
        IReadOnlyList<AdministrativeActionPositionReferenceDto>? values)
    {
        if (values is null)
        {
            return [];
        }
        var result = new HashSet<AdministrativeActionPositionKey>();
        foreach (var value in values)
        {
            if (value is null
                || !AdministrativeActionPositionKinds.IsKnown(value.PositionKind)
                || value.PositionId <= 0)
            {
                throw new WorkflowDomainException("Every position reference requires a known kind and positive id.");
            }
            result.Add(new AdministrativeActionPositionKey(value.PositionKind, value.PositionId));
        }
        return result.OrderBy(item => item.PositionKind, StringComparer.Ordinal)
            .ThenBy(item => item.PositionId)
            .ToArray();
    }

    private static NewAdministrativeActionBatchItemRecord ToNewItem(
        AdministrativeActionCandidateRecord candidate,
        ResolvedAction resolved,
        DateTimeOffset now)
    {
        var timer = resolved.Boundary is null
            ? null
            : candidate.TimerBoundaries.SingleOrDefault(item =>
                item.BoundaryNodeId == resolved.Boundary.Id);
        return new NewAdministrativeActionBatchItemRecord(
            candidate.PositionKind,
            candidate.PositionId,
            candidate.InstanceId,
            candidate.UserTaskId,
            candidate.MultiInstanceExecutionId,
            candidate.TokenId,
            candidate.TokenActivationId,
            candidate.WorkflowDefinitionId,
            candidate.NodeId,
            resolved.Flow.Id,
            candidate.PositionUpdatedAt,
            timer?.TimerSubscriptionId,
            timer?.TimerJobId,
            timer?.Occurrence,
            timer?.Status,
            timer?.UpdatedAt,
            candidate.AffectedTaskCount,
            now);
    }

    private static void ValidateMultiInstanceMode(ResolvedAction resolved, string? mode)
    {
        var normalized = NormalizeOptional(mode);
        if (resolved.Boundary is not null)
        {
            if (normalized is not null)
            {
                throw new WorkflowDomainException("Timer-boundary actions do not accept a multi-instance mode.");
            }
            return;
        }
        if (resolved.Source.MultiInstance is null)
        {
            if (normalized is not null)
            {
                throw new WorkflowDomainException("Ordinary user-task actions do not accept a multi-instance mode.");
            }
            return;
        }
        if (!AdministrativeActionMultiInstanceModes.IsKnown(normalized))
        {
            throw new WorkflowDomainException(
                "A direct multi-instance action requires mode 'forceParent' or 'completeAllChildren'.");
        }
    }

    private async Task<int> ResolveMaxAffectedTasksAsync(CancellationToken cancellationToken)
    {
        var setting = await engineSettings.GetByKeyAsync(
            AdministrativeActionConstraints.BatchMaxAffectedTasksSetting,
            cancellationToken);
        return int.TryParse(setting?.Value, out var value) && value > 0
            ? Math.Min(value, AdministrativeActionConstraints.MaxAffectedTasks)
            : AdministrativeActionConstraints.MaxAffectedTasks;
    }

    private async Task<WorkflowJobRecord> EnqueueBatchJobAsync(
        long batchId,
        ResolvedAction action,
        string kind,
        string phase,
        IReadOnlyDictionary<string, string> actorClaims,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await jobs.EnqueueAsync(
            new WorkflowJobCreateRecord
            {
                WorkflowDefinitionId = action.Workflow.Id,
                WorkflowKey = action.Workflow.WorkflowKey,
                ActivationId = Guid.NewGuid(),
                NodeId = action.Source.Id,
                NodeName = action.Source.Name,
                NodeType = action.Source.Type,
                Kind = kind,
                QueueClass = WorkflowJobClasses.Activity,
                Phase = phase,
                DueAt = now,
                MaxAttempts = BatchRetryDelays.Length + 1,
                RetryDelays = BatchRetryDelays,
                FailureHandling = WorkflowJobFailureHandling.RetryFirst,
                Payload = JsonSerializer.SerializeToElement(
                    new AdministrativeActionBatchJobPayload(batchId)
                    {
                        ActorClaims = actorClaims
                    })
            },
            cancellationToken);

    private async Task<WorkflowDefinitionRecord> GetWorkflowAsync(
        long workflowDefinitionId,
        CancellationToken cancellationToken)
    {
        EnsurePositive(workflowDefinitionId, "Workflow definition id");
        return await definitions.GetAsync(workflowDefinitionId, cancellationToken)
            ?? throw new WorkflowDomainException(
                $"Workflow definition #{workflowDefinitionId} does not exist.");
    }

    private static FlowNodeModel RequireSourceNode(WorkflowDefinitionRecord workflow, int sourceNodeId)
    {
        EnsurePositive(sourceNodeId, "Source node id");
        var source = workflow.Definition.FlowNodes.SingleOrDefault(node => node.Id == sourceNodeId);
        if (source is null || !BpmnFlowNodeTypes.IsUserTask(source.Type))
        {
            throw new WorkflowDomainException(
                $"Node #{sourceNodeId} is not a user task in workflow definition #{workflow.Id}.");
        }
        return source;
    }

    private static AdministrativeActionSnapshotRecord ToActionSnapshot(ResolvedAction action) =>
        new(
            action.Workflow.Id,
            action.Workflow.Version,
            action.Summary.ActionKind,
            action.Flow.Id,
            action.Flow.ExternalId,
            action.Summary.Name,
            action.Source.Id,
            action.Source.Name,
            action.Summary.TargetNodeId,
            action.Summary.TargetNodeName,
            action.Summary.TargetNodeType,
            action.Flow.Condition,
            SnapshotRoles(action.Flow.Roles),
            action.Flow.Variables,
            action.Boundary?.Id,
            action.Boundary?.Name,
            action.Boundary?.Timer,
            action.Boundary?.CancelActivity);

    private static AdministrativeActionCandidateDto ToCandidateDto(
        AdministrativeActionCandidateRecord record,
        int workflowVersion) =>
        new(
            record.PositionKind,
            record.PositionId,
            record.UserTaskId,
            record.MultiInstanceExecutionId,
            record.InstanceId,
            record.TokenId,
            record.TokenActivationId,
            record.WorkflowDefinitionId,
            workflowVersion,
            record.WorkflowKey,
            record.BusinessKey,
            record.NodeId,
            record.NodeName,
            record.NodeExternalId,
            record.PositionUpdatedAt,
            record.AffectedTaskCount,
            record.TimerBoundaries.Select(timer => new AdministrativeTimerBoundaryStateDto(
                timer.BoundaryNodeId,
                timer.TimerSubscriptionId,
                timer.TimerJobId,
                timer.Status,
                timer.NextDueAt,
                timer.Occurrence,
                timer.UpdatedAt,
                timer.Status is TimerSubscriptionStatuses.Active or TimerSubscriptionStatuses.Paused)).ToArray())
        {
            Variables = record.Variables
        };

    internal static AdministrativeActionBatchSummaryDto ToSummary(
        AdministrativeActionBatchRecord record) =>
        new(
            record.Id,
            record.WorkflowKey,
            record.WorkflowDefinitionId,
            record.Action.WorkflowVersion,
            record.SourceNodeId,
            record.Action.SourceNodeName,
            record.ActionKind,
            record.FlowId,
            record.BoundaryNodeId,
            record.MultiInstanceMode,
            record.Reason,
            record.Status,
            record.PreparedBy,
            record.ConfirmedBy,
            record.TotalItemCount,
            record.TotalAffectedTaskCount,
            record.EligibleItemCount,
            record.IneligibleItemCount,
            record.QueuedItemCount,
            record.SucceededItemCount,
            record.SkippedItemCount,
            record.FailedItemCount,
            record.CancelledItemCount,
            record.CreatedAt,
            record.UpdatedAt,
            record.CompletedAt);

    internal static AdministrativeActionBatchDetailDto ToDetail(
        AdministrativeActionBatchRecord record) =>
        new(
            ToSummary(record),
            new AdministrativeActionSummaryDto(
                record.Action.WorkflowDefinitionId,
                record.Action.WorkflowVersion,
                record.Action.ActionKind,
                record.Action.FlowId,
                record.Action.FlowExternalId,
                record.Action.FlowName,
                record.Action.SourceNodeId,
                record.Action.SourceNodeName,
                record.Action.TargetNodeId,
                record.Action.TargetNodeName,
                record.Action.TargetNodeType,
                record.Action.Variables)
            {
                Condition = record.Action.Condition,
                Roles = record.Action.Roles,
                BoundaryNodeId = record.Action.BoundaryNodeId,
                BoundaryNodeName = record.Action.BoundaryNodeName,
                Timer = record.Action.Timer,
                AuthoredCancelActivity = record.Action.AuthoredCancelActivity
            },
            record.CommonVariables,
            record.Selection,
            record.PreparedByRoles,
            record.ConfirmedByRoles,
            record.Issues,
            record.PreparationJobId,
            record.ExecutionJobId,
            record.CancelledBy,
            record.CancellationReason,
            record.PreparedAt,
            record.ConfirmedAt,
            record.StartedAt,
            record.CancelledAt);

    internal static AdministrativeActionBatchItemDto ToItem(
        AdministrativeActionBatchItemRecord record) =>
        new(
            record.Id,
            record.BatchId,
            record.PositionKind,
            record.PositionId,
            record.InstanceId,
            record.UserTaskId,
            record.MultiInstanceExecutionId,
            record.TokenId,
            record.TokenActivationId,
            record.WorkflowDefinitionId,
            record.SourceNodeId,
            record.FlowId,
            record.CapturedPositionUpdatedAt,
            record.TimerSubscriptionId,
            record.TimerJobId,
            record.CapturedTimerOccurrence,
            record.CapturedTimerStatus,
            record.CapturedTimerSubscriptionUpdatedAt,
            record.AffectedTaskCount,
            record.Status,
            record.Issues,
            record.Result,
            record.ErrorCode,
            record.ErrorDescription,
            record.CreatedAt,
            record.UpdatedAt,
            record.PreparedAt,
            record.StartedAt,
            record.CompletedAt);

    internal static AdministrativeActionBatchUpdateRecord ToUpdate(
        AdministrativeActionBatchRecord record) =>
        new(
            record.Id,
            record.Status,
            record.ConfirmedBy,
            record.ConfirmedByRoles,
            record.TotalItemCount,
            record.TotalAffectedTaskCount,
            record.EligibleItemCount,
            record.IneligibleItemCount,
            record.QueuedItemCount,
            record.SucceededItemCount,
            record.SkippedItemCount,
            record.FailedItemCount,
            record.CancelledItemCount,
            record.Issues,
            record.PreparationJobId,
            record.ExecutionJobId,
            record.CancelledBy,
            record.CancellationReason,
            record.UpdatedAt,
            record.PreparedAt,
            record.ConfirmedAt,
            record.StartedAt,
            record.CompletedAt,
            record.CancelledAt);

    internal static AdministrativeActionBatchUpdateRecord ApplyCounts(
        AdministrativeActionBatchUpdateRecord update,
        IReadOnlyDictionary<string, int> counts) =>
        update with
        {
            TotalItemCount = counts.Values.Sum(),
            EligibleItemCount = Count(counts, AdministrativeActionBatchItemStatuses.Eligible),
            IneligibleItemCount = Count(counts, AdministrativeActionBatchItemStatuses.Ineligible),
            QueuedItemCount = Count(counts, AdministrativeActionBatchItemStatuses.Queued),
            SucceededItemCount = Count(counts, AdministrativeActionBatchItemStatuses.Succeeded),
            SkippedItemCount = Count(counts, AdministrativeActionBatchItemStatuses.Skipped),
            FailedItemCount = Count(counts, AdministrativeActionBatchItemStatuses.Failed),
            CancelledItemCount = Count(counts, AdministrativeActionBatchItemStatuses.Cancelled)
        };

    internal static AdministrativeActionBatchItemUpdateRecord ToItemUpdate(
        AdministrativeActionBatchItemRecord record) =>
        new(
            record.Id,
            record.Status,
            record.AffectedTaskCount,
            record.Issues,
            record.Result,
            record.ErrorCode,
            record.ErrorDescription,
            record.UpdatedAt,
            record.PreparedAt,
            record.StartedAt,
            record.CompletedAt);

    internal static AdministrativeActionIssueDto Issue(string code, string message) =>
        new(code, Limit(message));

    internal static JsonElement SerializeIssues(
        IReadOnlyCollection<AdministrativeActionIssueDto> issues) =>
        JsonSerializer.SerializeToElement(issues);

    private static void ValidateCommonVariables(
        AdministrativeActionSummaryDto action,
        IReadOnlyDictionary<string, JsonElement>? values)
    {
        var supplied = ValidateVariableNameUniqueness(values);
        if (action.ActionKind == AdministrativeActionKinds.TimerBoundary
            && supplied is { Count: > 0 })
        {
            throw new WorkflowDomainException("Timer-boundary actions do not accept submitted variables.");
        }
        var declared = action.Variables.Select(variable => variable.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = supplied?.Where(name => !declared.Contains(name)).ToArray() ?? [];
        if (unknown.Length > 0)
        {
            throw new WorkflowDomainException(
                $"Unknown administrative-action variable(s): {string.Join(", ", unknown)}.");
        }
        var missing = action.Variables
            .Where(variable => variable.Required && (supplied is null || !supplied.Contains(variable.Name)))
            .Select(variable => variable.Name)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new WorkflowDomainException(
                $"Required administrative-action variable(s) are missing: {string.Join(", ", missing)}.");
        }
    }

    private static HashSet<string>? ValidateVariableNameUniqueness(
        IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (values is null)
        {
            return null;
        }
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in values.Keys)
        {
            if (string.IsNullOrWhiteSpace(name) || !result.Add(name.Trim()))
            {
                throw new WorkflowDomainException(
                    $"Administrative-action variables contain a blank or duplicate name '{name}'.");
            }
        }
        return result;
    }

    private static void EnsureIdempotentReplayMatches(
        AdministrativeActionBatchRecord existing,
        CreateAdministrativeActionBatchRequest request,
        string? reason,
        AdministrativeActionBatchSelectionDto selection)
    {
        var variables = request.Variables ?? new Dictionary<string, JsonElement>();
        var variablesMatch = existing.CommonVariables.Count == variables.Count
            && variables.All(pair => existing.CommonVariables.Any(stored =>
                string.Equals(stored.Key, pair.Key, StringComparison.OrdinalIgnoreCase)
                && JsonElement.DeepEquals(stored.Value, pair.Value)));
        if (existing.WorkflowDefinitionId != request.WorkflowDefinitionId
            || existing.SourceNodeId != request.SourceNodeId
            || existing.FlowId != request.FlowId
            || existing.BoundaryNodeId != request.BoundaryNodeId
            || !string.Equals(existing.ActionKind, request.ActionKind, StringComparison.Ordinal)
            || !string.Equals(existing.MultiInstanceMode, NormalizeOptional(request.MultiInstanceMode), StringComparison.Ordinal)
            || !string.Equals(existing.Reason, reason, StringComparison.Ordinal)
            || !variablesMatch
            || !JsonElement.DeepEquals(existing.Selection, JsonSerializer.SerializeToElement(selection)))
        {
            throw new WorkflowConflictException(
                "IdempotencyKey was already used for a different administrative batch request.");
        }
    }

    private static Dictionary<string, JsonElement> CloneVariables(
        IReadOnlyDictionary<string, JsonElement>? values) =>
        values is null
            ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            : values.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);

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

    private static IReadOnlyList<string> SnapshotRoles(IReadOnlyCollection<string> roles) =>
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
            throw new WorkflowUnauthorizedException("An authenticated administrative operator is required.");
        }
        if (user.Length > AdministrativeActionConstraints.MaxActorNameLength)
        {
            throw new WorkflowDomainException("The administrative operator name is too long.");
        }
        return user;
    }

    private static string? NormalizeOptionalReason(string? value, string label)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is not null
            && normalized.EnumerateRunes().Count() > AdministrativeActionConstraints.MaxReasonLength)
        {
            throw new WorkflowDomainException(
                $"{label} cannot exceed {AdministrativeActionConstraints.MaxReasonLength} characters.");
        }
        return normalized;
    }

    private static string? NormalizeIdempotencyKey(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized?.Length > AdministrativeActionConstraints.MaxIdempotencyKeyLength)
        {
            throw new WorkflowDomainException(
                $"IdempotencyKey cannot exceed {AdministrativeActionConstraints.MaxIdempotencyKeyLength} characters.");
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

    private static void EnsurePositive(long value, string name)
    {
        if (value <= 0)
        {
            throw new WorkflowDomainException($"{name} must be greater than zero.");
        }
    }

    private static int Count(IReadOnlyDictionary<string, int> counts, string status) =>
        counts.TryGetValue(status, out var value) ? value : 0;

    private static string Limit(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "Administrative batch processing failed."
            : value.Trim();
        return normalized.EnumerateRunes().Count() <= AdministrativeActionConstraints.MaxErrorDescriptionLength
            ? normalized
            : string.Concat(normalized.EnumerateRunes()
                .Take(AdministrativeActionConstraints.MaxErrorDescriptionLength)
                .Select(rune => rune.ToString()));
    }
}
