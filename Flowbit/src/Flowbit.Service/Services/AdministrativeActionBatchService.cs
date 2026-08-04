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
    IWorkflowEngineService engine,
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

    public async Task<IReadOnlyList<WorkflowSummaryDto>> ListWorkflowCatalogAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await AuthorizeGlobalAsync(actor, requireBatchRole: true, cancellationToken);

        var latestByFamily = await definitions.ListLatestAsync(cancellationToken);
        var authorized = new List<WorkflowSummaryDto>();
        foreach (var family in latestByFamily
                     .GroupBy(item => item.WorkflowKey, StringComparer.Ordinal)
                     .Select(group => group.Key))
        {
            var versions = await definitions.ListVersionsByKeyAsync(
                family,
                cancellationToken);
            authorized.AddRange(versions
                .Where(version => version.IsPublished
                                  && ResolveActions(version, actor, batchableOnly: true).Count > 0)
                .Select(WorkflowDefinitionService.ToSummary));
        }

        return authorized
            .OrderBy(version => version.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(version => version.WorkflowKey, StringComparer.Ordinal)
            .ThenByDescending(version => version.Version)
            .ToArray();
    }

    public async Task<IReadOnlyList<AdministrativeActionSummaryDto>> ListActionsAsync(
        long workflowId,
        ActorContext actor,
        bool batchableOnly,
        CancellationToken cancellationToken)
    {
        var target = await RequirePublishedTargetAsync(workflowId, cancellationToken);
        await AuthorizeGlobalAsync(actor, requireBatchRole: batchableOnly, cancellationToken);
        return ResolveActions(target, actor, batchableOnly);
    }

    public async Task<PagedResult<AdministrativeActionCandidateDto>> SearchCandidatesAsync(
        AdministrativeActionCandidateSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (target, action) = await ResolveAuthorizedActionAsync(
            request.TargetWorkflowId,
            request.FlowExternalId,
            actor,
            requireBatchable: true,
            cancellationToken);
        var query = BuildCandidateQuery(request, target, action);
        var page = await candidates.SearchAsync(query, cancellationToken);
        var result = new List<AdministrativeActionCandidateDto>(page.Items.Count);
        foreach (var candidate in page.Items)
        {
            var issues = await InspectCandidateAsync(
                candidate,
                target.Id,
                action.FlowExternalId,
                actor,
                cancellationToken);
            result.Add(ToCandidateDto(candidate, issues));
        }
        return new PagedResult<AdministrativeActionCandidateDto>(
            result,
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
        var reason = NormalizeReason(request.Reason);
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);
        var selection = request.Selection
            ?? throw new WorkflowDomainException("A batch selection is required.");
        ValidateVariableNameUniqueness(request.Variables);
        if (idempotencyKey is not null)
        {
            var existing = await batches.FindByIdempotencyKeyAsync(
                user,
                idempotencyKey,
                cancellationToken);
            if (existing is not null)
            {
                EnsureIdempotentReplayMatches(existing, request, reason, selection);
                await AuthorizeBatchRecordAsync(existing, actor, cancellationToken);
                return ToDetail(existing);
            }
        }

        var (target, action) = await ResolveAuthorizedActionAsync(
            request.TargetWorkflowId,
            request.FlowExternalId,
            actor,
            requireBatchable: true,
            cancellationToken);
        ValidateCommonVariables(action, request.Variables);
        var maxItems = await ResolveMaxItemsAsync(cancellationToken);
        var query = BuildSelectionQuery(selection, target, action);
        var excluded = selection.ExcludedUserTaskIds?
            .Where(id => id > 0)
            .Distinct()
            .ToArray() ?? [];
        var frozen = await candidates.MaterializeAsync(
            query,
            excluded,
            maxItems,
            cancellationToken);
        if (string.Equals(
                selection.Mode?.Trim(),
                AdministrativeActionBatchSelectionModes.Explicit,
                StringComparison.OrdinalIgnoreCase))
        {
            var excludedSet = excluded.ToHashSet();
            var expectedCount = selection.UserTaskIds!
                .Where(id => !excludedSet.Contains(id))
                .Distinct()
                .Count();
            if (expectedCount > maxItems)
            {
                throw new WorkflowDomainException(
                    $"The frozen selection exceeds the configured maximum of {maxItems:N0} tasks.");
            }
            if (frozen.Count != expectedCount)
            {
                throw new WorkflowDomainException(
                    "One or more explicitly selected tasks do not exist in the target workflow family. Refresh the selection and try again.");
            }
        }
        if (frozen.Count > maxItems)
        {
            throw new WorkflowDomainException(
                $"The frozen selection exceeds the configured maximum of {maxItems:N0} tasks.");
        }
        if (frozen.Count == 0)
        {
            throw new WorkflowDomainException("The frozen selection contains no active candidate tasks.");
        }

        var now = timeProvider.GetUtcNow();
        var selectionSnapshot = JsonSerializer.SerializeToElement(selection);
        AdministrativeActionBatchRecord batch;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            if (idempotencyKey is not null)
            {
                await batches.LockIdempotencyKeyAsync(
                    user,
                    idempotencyKey,
                    cancellationToken);
                var winner = await batches.FindByIdempotencyKeyAsync(
                    user,
                    idempotencyKey,
                    cancellationToken);
                if (winner is not null)
                {
                    EnsureIdempotentReplayMatches(winner, request, reason, selection);
                    await AuthorizeBatchRecordAsync(winner, actor, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return ToDetail(winner);
                }
            }

            batch = await batches.AddAsync(
                new NewAdministrativeActionBatchRecord(
                    target.Id,
                    target.WorkflowKey,
                    action.FlowExternalId,
                    reason,
                    CloneVariables(request.Variables),
                    selectionSnapshot,
                    user,
                    SnapshotRoles(actor.Roles),
                    idempotencyKey,
                    now),
                cancellationToken);
            await batches.AddItemsAsync(
                batch.Id,
                frozen.Select(candidate => new NewAdministrativeActionBatchItemRecord(
                    candidate.InstanceId,
                    candidate.UserTaskId,
                    candidate.TokenId,
                    candidate.SourceWorkflowDefinitionId,
                    target.Id,
                    candidate.InstanceUpdatedAt,
                    candidate.UserTaskUpdatedAt,
                    now)).ToArray(),
                cancellationToken);
            var job = await EnqueueBatchJobAsync(
                batch.Id,
                target,
                action,
                WorkflowJobKinds.AdministrativeBatchPrepare,
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
        return ToDetail(batch);
    }

    public async Task<PagedResult<AdministrativeActionBatchSummaryDto>> ListAsync(
        AdministrativeActionBatchSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await AuthorizeGlobalAsync(actor, requireBatchRole: true, cancellationToken);
        var page = Math.Max(1, request.Page ?? 1);
        var pageSize = Math.Clamp(request.PageSize ?? 50, 1, 200);
        if (!string.IsNullOrWhiteSpace(request.Status)
            && !AdministrativeActionBatchStatuses.IsKnown(request.Status.Trim()))
        {
            throw new WorkflowDomainException($"Unknown batch status '{request.Status}'.");
        }
        var records = await batches.ListAsync(
            new AdministrativeActionBatchSearch(
                NormalizeOptional(request.WorkflowKey),
                NormalizeOptional(request.Status),
                NormalizeOptional(request.PreparedBy),
                page,
                pageSize),
            new AdministrativeActionBatchListAuthorization(
                actor.Roles
                    .Where(role => !string.IsNullOrWhiteSpace(role))
                    .Select(role => role.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()),
            cancellationToken);
        return new PagedResult<AdministrativeActionBatchSummaryDto>(
            records.Items.Select(ToSummary).ToArray(),
            records.Page,
            records.PageSize,
            records.TotalCount);
    }

    public async Task<AdministrativeActionBatchDetailDto?> GetAsync(
        long batchId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        EnsurePositive(batchId, "Batch id");
        var batch = await batches.GetAsync(batchId, false, cancellationToken);
        if (batch is null)
        {
            return null;
        }
        await AuthorizeBatchRecordAsync(batch, actor, cancellationToken);
        return ToDetail(batch);
    }

    public async Task<PagedResult<AdministrativeActionBatchItemDto>?> ListItemsAsync(
        long batchId,
        string? status,
        int page,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        EnsurePositive(batchId, "Batch id");
        var batch = await batches.GetAsync(batchId, false, cancellationToken);
        if (batch is null)
        {
            return null;
        }
        await AuthorizeBatchRecordAsync(batch, actor, cancellationToken);
        var normalizedStatus = NormalizeOptional(status);
        if (normalizedStatus is not null
            && !AdministrativeActionBatchItemStatuses.IsKnown(normalizedStatus))
        {
            throw new WorkflowDomainException($"Unknown batch item status '{status}'.");
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
            batch = await batches.GetAsync(batchId, true, cancellationToken)
                ?? null!;
            if (batch is null)
            {
                return null;
            }
            await AuthorizeBatchRecordAsync(batch, actor, cancellationToken);
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
                || request.ExpectedBatchUpdatedAt != batch.UpdatedAt)
            {
                throw new WorkflowConflictException(
                    "The prepared batch changed; refresh its eligibility summary before confirming.");
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
                var target = await RequirePublishedTargetAsync(
                    batch.TargetWorkflowDefinitionId,
                    cancellationToken);
                var action = ResolveAction(target, batch.FlowExternalId, requireBatchable: true);
                job = await EnqueueBatchJobAsync(
                    batch.Id,
                    target,
                    action,
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
        var cancellationReason = NormalizeOptionalReason(request.Reason);
        AdministrativeActionBatchRecord batch;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            batch = await batches.GetAsync(batchId, true, cancellationToken)
                ?? null!;
            if (batch is null)
            {
                return null;
            }
            await AuthorizeBatchRecordAsync(batch, actor, cancellationToken);
            if (batch.Status == AdministrativeActionBatchStatuses.Cancelled)
            {
                return ToDetail(batch);
            }
            if (batch.Status is AdministrativeActionBatchStatuses.Completed
                or AdministrativeActionBatchStatuses.CompletedWithIssues
                or AdministrativeActionBatchStatuses.Failed)
            {
                throw new WorkflowConflictException(
                    $"Terminal batch #{batch.Id} cannot be cancelled.");
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
                    CompletedAt = Count(
                        counts,
                        AdministrativeActionBatchItemStatuses.Queued) == 0
                        ? now
                        : null,
                    UpdatedAt = now
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return ToDetail(batch);
    }

    private async Task<(WorkflowDefinitionRecord Target, AdministrativeActionSummaryDto Action)>
        ResolveAuthorizedActionAsync(
            long workflowId,
            string flowExternalId,
            ActorContext actor,
            bool requireBatchable,
            CancellationToken cancellationToken)
    {
        var target = await RequirePublishedTargetAsync(workflowId, cancellationToken);
        await AuthorizeGlobalAsync(actor, requireBatchable, cancellationToken);
        var action = ResolveAction(target, flowExternalId, requireBatchable);
        if (!HasRole(actor.Roles, target.Definition.SequenceFlows
                .Single(flow => flow.Id == action.FlowId).Roles))
        {
            throw new WorkflowForbiddenException(
                "The actor does not have a role permitted for this administrative action.");
        }
        return (target, action);
    }

    private async Task AuthorizeBatchRecordAsync(
        AdministrativeActionBatchRecord batch,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await AuthorizeGlobalAsync(actor, requireBatchRole: true, cancellationToken);
        var target = await definitions.GetAsync(
            batch.TargetWorkflowDefinitionId,
            cancellationToken)
            ?? throw new WorkflowDomainException(
                $"Target workflow #{batch.TargetWorkflowDefinitionId} no longer exists.");
        var action = ResolveAction(target, batch.FlowExternalId, requireBatchable: true);
        var flow = target.Definition.SequenceFlows.Single(item => item.Id == action.FlowId);
        if (!HasRole(actor.Roles, flow.Roles))
        {
            throw new WorkflowForbiddenException(
                "The actor does not have a role permitted for this administrative action.");
        }
    }

    private async Task AuthorizeGlobalAsync(
        ActorContext actor,
        bool requireBatchRole,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var administrative = await engineSettings.GetByKeyAsync(
            AdministrativeActionConstraints.AdministrativeRequiredRoleSetting,
            cancellationToken);
        if (!HasRole(actor.Roles, WorkflowEngineService.ParseAdministrativeActionRoles(administrative?.Value)))
        {
            throw new WorkflowForbiddenException(
                $"A {AdministrativeActionConstraints.AdministrativeRequiredRoleSetting} role is required.");
        }
        if (!requireBatchRole)
        {
            return;
        }
        var batch = await engineSettings.GetByKeyAsync(
            AdministrativeActionConstraints.BatchRequiredRoleSetting,
            cancellationToken);
        if (!HasRole(actor.Roles, WorkflowJobOperationsService.ParseRoles(batch?.Value)))
        {
            throw new WorkflowForbiddenException(
                $"A {AdministrativeActionConstraints.BatchRequiredRoleSetting} role is required.");
        }
    }

    private async Task<WorkflowDefinitionRecord> RequirePublishedTargetAsync(
        long workflowId,
        CancellationToken cancellationToken)
    {
        EnsurePositive(workflowId, "Target workflow id");
        return await definitions.GetPublishedAsync(workflowId, cancellationToken)
            ?? throw new WorkflowDomainException(
                $"Target workflow #{workflowId} does not exist or is not published.");
    }

    private static IReadOnlyList<AdministrativeActionSummaryDto> ResolveActions(
        WorkflowDefinitionRecord target,
        ActorContext actor,
        bool batchableOnly)
    {
        var nodes = target.Definition.FlowNodes.ToDictionary(node => node.Id);
        return target.Definition.SequenceFlows
            .Where(flow => flow.IsAdministrative
                           && (!batchableOnly || flow.IsBatchable)
                           && flow.IsSelectable
                           && !flow.IsDefault
                           && !string.IsNullOrWhiteSpace(flow.ExternalId)
                           && nodes.TryGetValue(flow.SourceRef, out var source)
                           && BpmnFlowNodeTypes.IsUserTask(source.Type)
                           && source.MultiInstance is null
                           && !source.AsyncAfter
                           && nodes.TryGetValue(flow.TargetRef, out var destination)
                           && BpmnFlowNodeTypes.IsUserTask(destination.Type)
                           && destination.MultiInstance is null
                           && !destination.AsyncBefore
                           && HasRole(actor.Roles, flow.Roles))
            .Select(flow => new AdministrativeActionSummaryDto(
                flow.Id,
                flow.ExternalId!.Trim(),
                flow.Name,
                flow.SourceRef,
                nodes[flow.SourceRef].Name,
                flow.TargetRef,
                nodes[flow.TargetRef].Name,
                flow.IsBatchable,
                flow.Variables))
            .OrderBy(action => action.SourceNodeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(action => action.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static AdministrativeActionSummaryDto ResolveAction(
        WorkflowDefinitionRecord target,
        string flowExternalId,
        bool requireBatchable)
    {
        var normalized = NormalizeFlowExternalId(flowExternalId);
        return ResolveActions(
                target,
                new ActorContext(null, target.Definition.SequenceFlows
                    .SelectMany(flow => flow.Roles)
                    .ToArray(), new Dictionary<string, string>()),
                requireBatchable)
            .SingleOrDefault(action => string.Equals(
                action.FlowExternalId,
                normalized,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkflowDomainException(
                $"Administrative action '{normalized}' is not available in published workflow #{target.Id}.");
    }

    private static AdministrativeActionCandidateQuery BuildCandidateQuery(
        AdministrativeActionCandidateSearchRequest request,
        WorkflowDefinitionRecord target,
        AdministrativeActionSummaryDto action) =>
        new()
        {
            WorkflowKey = target.WorkflowKey,
            SourceNodeId = action.SourceNodeId,
            SourceNodeExternalId = target.Definition.FlowNodes
                .Single(node => node.Id == action.SourceNodeId).ExternalId,
            UserTaskId = PositiveOrNull(request.UserTaskId, "UserTaskId"),
            InstanceId = PositiveOrNull(request.InstanceId, "InstanceId"),
            SourceWorkflowDefinitionId = PositiveOrNull(request.SourceWorkflowId, "SourceWorkflowId"),
            BusinessKey = NormalizeOptional(request.BusinessKey),
            VariableFilter = VariableFilterParser.Parse(request.VariableFilter),
            IncludeVariables = request.IncludeVariables ?? false,
            Page = Math.Max(1, request.Page ?? 1),
            PageSize = Math.Clamp(request.PageSize ?? 50, 1, 200)
        };

    private static AdministrativeActionCandidateQuery BuildSelectionQuery(
        AdministrativeActionBatchSelectionDto selection,
        WorkflowDefinitionRecord target,
        AdministrativeActionSummaryDto action)
    {
        var mode = selection.Mode?.Trim();
        if (string.Equals(mode, AdministrativeActionBatchSelectionModes.Explicit, StringComparison.OrdinalIgnoreCase))
        {
            var ids = selection.UserTaskIds?
                .Distinct()
                .ToArray() ?? [];
            if (ids.Length == 0 || ids.Any(id => id <= 0))
            {
                throw new WorkflowDomainException(
                    "Explicit selection requires at least one positive userTaskId.");
            }
            return new AdministrativeActionCandidateQuery
            {
                WorkflowKey = target.WorkflowKey,
                SourceNodeId = action.SourceNodeId,
                SourceNodeExternalId = target.Definition.FlowNodes
                    .Single(node => node.Id == action.SourceNodeId).ExternalId,
                UserTaskIds = ids,
                Page = 1,
                PageSize = 200
            };
        }
        if (!string.Equals(mode, AdministrativeActionBatchSelectionModes.AllMatching, StringComparison.OrdinalIgnoreCase)
            || selection.AllMatching is null)
        {
            throw new WorkflowDomainException(
                "Selection mode must be 'explicit' or 'allMatching'.");
        }
        var filter = selection.AllMatching;
        if (filter.TargetWorkflowId != target.Id
            || !string.Equals(filter.FlowExternalId?.Trim(), action.FlowExternalId, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowDomainException(
                "The allMatching filter must identify the batch's exact target workflow and flow.");
        }
        return BuildCandidateQuery(filter, target, action) with
        {
            IncludeVariables = false,
            Page = 1,
            PageSize = 200
        };
    }

    private async Task<IReadOnlyList<InstanceVersionChangeIssueDto>> InspectCandidateAsync(
        AdministrativeActionCandidateRecord candidate,
        long targetWorkflowId,
        string flowExternalId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        try
        {
            var actions = await engine.GetUserTaskAdministrativeActionsAsync(
                candidate.UserTaskId,
                targetWorkflowId,
                actor,
                cancellationToken);
            if (actions.Any(action => string.Equals(
                    action.FlowExternalId,
                    flowExternalId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return [];
            }
            return [Issue("administrative_action_unavailable",
                "The task is not currently compatible with this administrative action.")];
        }
        catch (WorkflowForbiddenException)
        {
            throw;
        }
        catch (Exception exception) when (exception is WorkflowDomainException or WorkflowConflictException)
        {
            return [Issue("administrative_action_unavailable", exception.Message)];
        }
    }

    private async Task<int> ResolveMaxItemsAsync(CancellationToken cancellationToken)
    {
        var setting = await engineSettings.GetByKeyAsync(
            AdministrativeActionConstraints.BatchMaxItemsSetting,
            cancellationToken);
        return int.TryParse(setting?.Value, out var configured) && configured > 0
            ? Math.Min(configured, AdministrativeActionConstraints.MaxBatchItems)
            : AdministrativeActionConstraints.MaxBatchItems;
    }

    private async Task<WorkflowJobRecord> EnqueueBatchJobAsync(
        long batchId,
        WorkflowDefinitionRecord target,
        AdministrativeActionSummaryDto action,
        string kind,
        string phase,
        IReadOnlyDictionary<string, string> actorClaims,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await jobs.EnqueueAsync(
            new WorkflowJobCreateRecord
            {
                WorkflowDefinitionId = target.Id,
                WorkflowKey = target.WorkflowKey,
                ActivationId = Guid.NewGuid(),
                NodeId = action.SourceNodeId,
                NodeName = action.SourceNodeName,
                NodeType = BpmnFlowNodeTypes.UserTask,
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

    private static IReadOnlyDictionary<string, string> SnapshotAllowedClaims(
        IReadOnlyDictionary<string, string> actorClaims,
        IEnumerable<string> allowedClaims)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configured in allowedClaims)
        {
            var allowed = configured?.Trim();
            if (string.IsNullOrEmpty(allowed)
                || !TryResolveClaim(actorClaims, allowed, out var value))
            {
                continue;
            }
            snapshot.TryAdd(allowed, value);
        }
        return snapshot;
    }

    private static bool TryResolveClaim(
        IReadOnlyDictionary<string, string> claims,
        string name,
        out string value)
    {
        if (claims.TryGetValue(name, out var direct))
        {
            value = direct;
            return true;
        }
        foreach (var pair in claims)
        {
            var slash = pair.Key.LastIndexOf('/');
            if (slash >= 0
                && slash < pair.Key.Length - 1
                && string.Equals(
                    pair.Key[(slash + 1)..],
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }
        value = string.Empty;
        return false;
    }

    private static AdministrativeActionCandidateDto ToCandidateDto(
        AdministrativeActionCandidateRecord record,
        IReadOnlyList<InstanceVersionChangeIssueDto> issues) =>
        new(
            record.UserTaskId,
            record.InstanceId,
            record.TokenId,
            record.SourceWorkflowDefinitionId,
            record.WorkflowKey,
            record.BusinessKey,
            record.NodeId,
            record.NodeName,
            record.NodeExternalId,
            record.InstanceUpdatedAt,
            record.UserTaskUpdatedAt,
            issues.Count == 0,
            issues)
        {
            Variables = record.Variables
        };

    internal static AdministrativeActionBatchSummaryDto ToSummary(
        AdministrativeActionBatchRecord record) =>
        new(
            record.Id,
            record.TargetWorkflowDefinitionId,
            record.WorkflowKey,
            record.FlowExternalId,
            record.Reason,
            record.Status,
            record.PreparedBy,
            record.ConfirmedBy,
            record.TotalItemCount,
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
            record.InstanceId,
            record.UserTaskId,
            record.TokenId,
            record.SourceWorkflowDefinitionId,
            record.TargetWorkflowDefinitionId,
            record.CapturedInstanceUpdatedAt,
            record.CapturedUserTaskUpdatedAt,
            record.Status,
            record.Issues,
            record.Result,
            record.ErrorCode,
            record.ErrorDescription,
            record.NewUserTaskId,
            record.VersionChangeAuditId,
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
            record.Issues,
            record.Result,
            record.ErrorCode,
            record.ErrorDescription,
            record.NewUserTaskId,
            record.VersionChangeAuditId,
            record.UpdatedAt,
            record.PreparedAt,
            record.StartedAt,
            record.CompletedAt);

    internal static InstanceVersionChangeIssueDto Issue(
        string code,
        string message,
        string? variableName = null) =>
        new(code, Limit(message), VariableName: variableName);

    internal static JsonElement SerializeIssues(
        IReadOnlyCollection<InstanceVersionChangeIssueDto> issues) =>
        JsonSerializer.SerializeToElement(issues);

    private static void ValidateCommonVariables(
        AdministrativeActionSummaryDto action,
        IReadOnlyDictionary<string, JsonElement>? values)
    {
        var suppliedNames = ValidateVariableNameUniqueness(values);
        var missing = action.Variables
            .Where(variable => variable.Required
                               && (suppliedNames is null || !suppliedNames.Contains(variable.Name)))
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

        var suppliedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in values.Keys)
        {
            if (!suppliedNames.Add(name))
            {
                throw new WorkflowDomainException(
                    $"Administrative-action variables contain duplicate name '{name}' (names are case-insensitive).");
            }
        }
        return suppliedNames;
    }

    private static void EnsureIdempotentReplayMatches(
        AdministrativeActionBatchRecord existing,
        CreateAdministrativeActionBatchRequest request,
        string normalizedReason,
        AdministrativeActionBatchSelectionDto selection)
    {
        var variables = request.Variables
                        ?? new Dictionary<string, JsonElement>();
        var variablesMatch = existing.CommonVariables.Count == variables.Count
                             && variables.All(pair => existing.CommonVariables
                                 .Any(stored => string.Equals(
                                                    stored.Key,
                                                    pair.Key,
                                                    StringComparison.OrdinalIgnoreCase)
                                                && JsonElement.DeepEquals(
                                                    stored.Value,
                                                    pair.Value)));
        var selectionElement = JsonSerializer.SerializeToElement(selection);
        if (existing.TargetWorkflowDefinitionId != request.TargetWorkflowId
            || !string.Equals(
                existing.FlowExternalId,
                NormalizeFlowExternalId(request.FlowExternalId),
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.Reason, normalizedReason, StringComparison.Ordinal)
            || !variablesMatch
            || !JsonElement.DeepEquals(existing.Selection, selectionElement))
        {
            throw new WorkflowConflictException(
                "The IdempotencyKey is already associated with a different administrative batch request.");
        }
    }

    private static Dictionary<string, JsonElement> CloneVariables(
        IReadOnlyDictionary<string, JsonElement>? values) =>
        values?.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.OrdinalIgnoreCase)
        ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    private static bool HasRole(
        IReadOnlyCollection<string> actorRoles,
        IReadOnlyCollection<string> allowedRoles)
    {
        var normalized = actorRoles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allowedRoles.Any(role => !string.IsNullOrWhiteSpace(role)
                                        && normalized.Contains(role.Trim()));
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
            throw new WorkflowUnauthorizedException(
                "An authenticated administrative operator is required.");
        }
        if (user.Length > AdministrativeActionConstraints.MaxActorNameLength)
        {
            throw new WorkflowDomainException("The administrative operator name is too long.");
        }
        return user;
    }

    private static string NormalizeReason(string? reason)
    {
        var normalized = reason?.Trim() ?? string.Empty;
        if (normalized.Length == 0
            || normalized.EnumerateRunes().Count() > AdministrativeActionConstraints.MaxReasonLength)
        {
            throw new WorkflowDomainException(
                $"Reason must contain 1 to {AdministrativeActionConstraints.MaxReasonLength} characters.");
        }
        return normalized;
    }

    private static string? NormalizeOptionalReason(string? reason)
    {
        var normalized = NormalizeOptional(reason);
        if (normalized is not null
            && normalized.EnumerateRunes().Count() > AdministrativeActionConstraints.MaxReasonLength)
        {
            throw new WorkflowDomainException(
                $"Cancellation reason cannot exceed {AdministrativeActionConstraints.MaxReasonLength} characters.");
        }
        return normalized;
    }

    private static string NormalizeFlowExternalId(string? externalId)
    {
        var normalized = externalId?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new WorkflowDomainException("FlowExternalId is required.");
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

    private static string Limit(string message) =>
        message.Length <= AdministrativeActionConstraints.MaxErrorDescriptionLength
            ? message
            : message[..AdministrativeActionConstraints.MaxErrorDescriptionLength];
}
