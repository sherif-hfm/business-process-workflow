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

    private sealed record ResolvedFlowMapping(
        WorkflowDefinitionRecord Workflow,
        AdministrativeActionSummaryDto Action,
        SequenceFlowModel Flow);

    public async Task<IReadOnlyList<WorkflowSummaryDto>> ListWorkflowCatalogAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await AuthorizeGlobalAsync(actor, cancellationToken);
        var latestByFamily = await definitions.ListLatestAsync(cancellationToken);
        var authorized = new List<WorkflowSummaryDto>();
        foreach (var workflowKey in latestByFamily
                     .Select(item => item.WorkflowKey)
                     .Distinct(StringComparer.Ordinal))
        {
            var versions = await definitions.ListVersionsByKeyAsync(
                workflowKey,
                cancellationToken);
            authorized.AddRange(versions
                .Where(version => ResolveActions(version, actor).Count > 0)
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
        CancellationToken cancellationToken)
    {
        EnsurePositive(workflowId, "Workflow definition id");
        await AuthorizeGlobalAsync(actor, cancellationToken);
        var workflow = await definitions.GetAsync(workflowId, cancellationToken)
            ?? throw new WorkflowDomainException(
                $"Workflow definition #{workflowId} does not exist.");
        return ResolveActions(workflow, actor);
    }

    public async Task<PagedResult<AdministrativeActionCandidateDto>> SearchCandidatesAsync(
        AdministrativeActionCandidateSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolved = await ResolveAuthorizedMappingsAsync(
            request.FlowMappings,
            actor,
            cancellationToken);
        var query = BuildCandidateQuery(request, resolved);
        var page = await candidates.SearchAsync(query, cancellationToken);
        var byPair = resolved.ToDictionary(
            mapping => (mapping.Workflow.Id, mapping.Action.FlowId));
        var result = new List<AdministrativeActionCandidateDto>(page.Items.Count);
        foreach (var candidate in page.Items)
        {
            if (!byPair.TryGetValue(
                    (candidate.WorkflowDefinitionId, candidate.FlowId),
                    out var mapping))
            {
                continue;
            }
            var issues = await InspectCandidateAsync(
                candidate,
                actor,
                variables: null,
                cancellationToken);
            result.Add(ToCandidateDto(candidate, mapping, issues));
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

        var resolved = await ResolveAuthorizedMappingsAsync(
            request.FlowMappings,
            actor,
            cancellationToken);
        ValidateCommonVariables(resolved[0].Action, request.Variables);
        var maxItems = await ResolveMaxItemsAsync(cancellationToken);
        var query = BuildSelectionQuery(selection, resolved);
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
                    "One or more explicitly selected tasks do not match an exact selected workflow-version flow mapping.");
            }
        }
        if (frozen.Count > maxItems)
        {
            throw new WorkflowDomainException(
                $"The frozen selection exceeds the configured maximum of {maxItems:N0} tasks.");
        }
        if (frozen.Count == 0)
        {
            throw new WorkflowDomainException(
                "The frozen selection contains no active candidate tasks.");
        }

        var now = timeProvider.GetUtcNow();
        var mappingSnapshots = resolved
            .Select(ToFlowMappingRecord)
            .ToArray();
        var selectionSnapshot = JsonSerializer.SerializeToElement(selection);
        AdministrativeActionBatchRecord batch;
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
                    await AuthorizeBatchRecordAsync(winner, actor, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return ToDetail(winner);
                }
            }

            batch = await batches.AddAsync(
                new NewAdministrativeActionBatchRecord(
                    resolved[0].Workflow.WorkflowKey,
                    mappingSnapshots,
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
                    candidate.WorkflowDefinitionId,
                    candidate.FlowId,
                    candidate.InstanceUpdatedAt,
                    candidate.UserTaskUpdatedAt,
                    now)).ToArray(),
                cancellationToken);
            var job = await EnqueueBatchJobAsync(
                batch.Id,
                resolved[0],
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
        await AuthorizeGlobalAsync(actor, cancellationToken);
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
                var first = await ResolveStoredMappingAsync(
                    batch.FlowMappings[0],
                    actor,
                    cancellationToken);
                job = await EnqueueBatchJobAsync(
                    batch.Id,
                    first,
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

    private async Task<IReadOnlyList<ResolvedFlowMapping>> ResolveAuthorizedMappingsAsync(
        IReadOnlyList<AdministrativeActionFlowMappingDto>? mappings,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await AuthorizeGlobalAsync(actor, cancellationToken);
        if (mappings is null || mappings.Count == 0)
        {
            throw new WorkflowDomainException(
                "At least one exact workflow-definition flow mapping is required.");
        }
        if (mappings.Any(mapping => mapping.WorkflowDefinitionId <= 0 || mapping.FlowId <= 0))
        {
            throw new WorkflowDomainException(
                "Every flow mapping requires a positive workflowDefinitionId and flowId.");
        }
        var duplicateVersion = mappings
            .GroupBy(mapping => mapping.WorkflowDefinitionId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateVersion is not null)
        {
            throw new WorkflowDomainException(
                $"Workflow definition #{duplicateVersion.Key} has more than one selected flow mapping.");
        }

        var resolved = new List<ResolvedFlowMapping>(mappings.Count);
        string? workflowKey = null;
        foreach (var mapping in mappings.OrderBy(item => item.WorkflowDefinitionId))
        {
            var workflow = await definitions.GetAsync(
                    mapping.WorkflowDefinitionId,
                    cancellationToken)
                ?? throw new WorkflowDomainException(
                    $"Workflow definition #{mapping.WorkflowDefinitionId} does not exist.");
            if (workflowKey is null)
            {
                workflowKey = workflow.WorkflowKey;
            }
            else if (!string.Equals(workflowKey, workflow.WorkflowKey, StringComparison.Ordinal))
            {
                throw new WorkflowDomainException(
                    "Every selected workflow definition must belong to the same workflow family.");
            }
            var action = ResolveAction(workflow, mapping.FlowId);
            var flow = workflow.Definition.SequenceFlows.Single(item => item.Id == action.FlowId);
            if (!HasRole(actor.Roles, flow.Roles))
            {
                throw new WorkflowForbiddenException(
                    $"The operator does not have a role permitted for flow #{flow.Id} in workflow definition #{workflow.Id}.");
            }
            resolved.Add(new ResolvedFlowMapping(workflow, action, flow));
        }
        EnsureCompatibleVariableContracts(resolved);
        return resolved;
    }

    private async Task<ResolvedFlowMapping> ResolveStoredMappingAsync(
        AdministrativeActionFlowMappingRecord stored,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAuthorizedMappingsAsync(
            [new AdministrativeActionFlowMappingDto(stored.WorkflowDefinitionId, stored.FlowId)],
            actor,
            cancellationToken);
        return resolved[0];
    }

    private async Task AuthorizeBatchRecordAsync(
        AdministrativeActionBatchRecord batch,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await AuthorizeGlobalAsync(actor, cancellationToken);
        if (batch.FlowMappings.Count == 0
            || batch.FlowMappings.Any(mapping => !HasRole(actor.Roles, mapping.Roles)))
        {
            throw new WorkflowForbiddenException(
                "The operator must match a flow role for every version mapping in this batch.");
        }
    }

    private async Task AuthorizeGlobalAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var setting = await engineSettings.GetByKeyAsync(
            AdministrativeActionConstraints.BatchRequiredRoleSetting,
            cancellationToken);
        if (!HasRole(actor.Roles, WorkflowJobOperationsService.ParseRoles(setting?.Value)))
        {
            throw new WorkflowForbiddenException(
                $"A {AdministrativeActionConstraints.BatchRequiredRoleSetting} role is required.");
        }
    }

    private static IReadOnlyList<AdministrativeActionSummaryDto> ResolveActions(
        WorkflowDefinitionRecord workflow,
        ActorContext actor)
    {
        var nodes = workflow.Definition.FlowNodes.ToDictionary(node => node.Id);
        return workflow.Definition.SequenceFlows
            .Where(flow => flow.IsSelectable
                           && !flow.IsDefault
                           && flow.Roles.Any(role => !string.IsNullOrWhiteSpace(role))
                           && nodes.TryGetValue(flow.SourceRef, out var source)
                           && BpmnFlowNodeTypes.IsUserTask(source.Type)
                           && source.MultiInstance is null
                           && !source.AsyncAfter
                           && nodes.TryGetValue(flow.TargetRef, out var target)
                           && BpmnFlowNodeTypes.IsUserTask(target.Type)
                           && target.MultiInstance is null
                           && !target.AsyncBefore
                           && HasRole(actor.Roles, flow.Roles))
            .Select(flow => new AdministrativeActionSummaryDto(
                workflow.Id,
                workflow.Version,
                flow.Id,
                string.IsNullOrWhiteSpace(flow.ExternalId) ? null : flow.ExternalId.Trim(),
                flow.Name,
                flow.SourceRef,
                nodes[flow.SourceRef].Name,
                flow.TargetRef,
                nodes[flow.TargetRef].Name,
                flow.Variables))
            .OrderBy(action => action.SourceNodeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(action => action.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(action => action.FlowId)
            .ToArray();
    }

    private static AdministrativeActionSummaryDto ResolveAction(
        WorkflowDefinitionRecord workflow,
        int flowId) =>
        ResolveActions(
                workflow,
                new ActorContext(
                    null,
                    workflow.Definition.SequenceFlows.SelectMany(flow => flow.Roles).ToArray(),
                    new Dictionary<string, string>()))
            .SingleOrDefault(action => action.FlowId == flowId)
        ?? throw new WorkflowDomainException(
            $"Flow #{flowId} is not eligible for administrative batch execution in workflow definition #{workflow.Id}.");

    private static AdministrativeActionCandidateQuery BuildCandidateQuery(
        AdministrativeActionCandidateSearchRequest request,
        IReadOnlyList<ResolvedFlowMapping> mappings) =>
        new()
        {
            Targets = mappings.Select(mapping => new AdministrativeActionFlowTarget(
                mapping.Workflow.Id,
                mapping.Action.FlowId,
                mapping.Action.SourceNodeId)).ToArray(),
            UserTaskId = PositiveOrNull(request.UserTaskId, "UserTaskId"),
            InstanceId = PositiveOrNull(request.InstanceId, "InstanceId"),
            BusinessKey = NormalizeOptional(request.BusinessKey),
            VariableFilter = VariableFilterParser.Parse(request.VariableFilter),
            IncludeVariables = request.IncludeVariables ?? false,
            Page = Math.Max(1, request.Page ?? 1),
            PageSize = Math.Clamp(request.PageSize ?? 50, 1, 200)
        };

    private static AdministrativeActionCandidateQuery BuildSelectionQuery(
        AdministrativeActionBatchSelectionDto selection,
        IReadOnlyList<ResolvedFlowMapping> mappings)
    {
        var targets = mappings.Select(mapping => new AdministrativeActionFlowTarget(
            mapping.Workflow.Id,
            mapping.Action.FlowId,
            mapping.Action.SourceNodeId)).ToArray();
        var mode = selection.Mode?.Trim();
        if (string.Equals(
                mode,
                AdministrativeActionBatchSelectionModes.Explicit,
                StringComparison.OrdinalIgnoreCase))
        {
            var ids = selection.UserTaskIds?.Distinct().ToArray() ?? [];
            if (ids.Length == 0 || ids.Any(id => id <= 0))
            {
                throw new WorkflowDomainException(
                    "Explicit selection requires at least one positive userTaskId.");
            }
            return new AdministrativeActionCandidateQuery
            {
                Targets = targets,
                UserTaskIds = ids,
                Page = 1,
                PageSize = 200
            };
        }
        if (!string.Equals(
                mode,
                AdministrativeActionBatchSelectionModes.AllMatching,
                StringComparison.OrdinalIgnoreCase)
            || selection.AllMatching is null)
        {
            throw new WorkflowDomainException(
                "Selection mode must be 'explicit' or 'allMatching'.");
        }
        var expectedMappings = mappings
            .Select(mapping => new AdministrativeActionFlowMappingDto(
                mapping.Workflow.Id,
                mapping.Action.FlowId))
            .ToArray();
        if (!MappingSetsEqual(selection.AllMatching.FlowMappings, expectedMappings))
        {
            throw new WorkflowDomainException(
                "The allMatching filter must use the batch's exact version/flow mappings.");
        }
        return BuildCandidateQuery(selection.AllMatching, mappings) with
        {
            IncludeVariables = false,
            Page = 1,
            PageSize = 200
        };
    }

    private async Task<IReadOnlyList<AdministrativeActionIssueDto>> InspectCandidateAsync(
        AdministrativeActionCandidateRecord candidate,
        ActorContext actor,
        Dictionary<string, JsonElement>? variables,
        CancellationToken cancellationToken)
    {
        try
        {
            var eligibility = await engine.PreviewAdministrativeBatchFlowAsync(
                candidate.UserTaskId,
                new AdministrativeActionRequest(
                    candidate.WorkflowDefinitionId,
                    candidate.FlowId,
                    candidate.InstanceUpdatedAt,
                    "Administrative batch eligibility preview",
                    variables)
                {
                    ExpectedTokenId = candidate.TokenId,
                    ExpectedUserTaskUpdatedAt = candidate.UserTaskUpdatedAt
                },
                actor,
                cancellationToken);
            return variables is null
                ? eligibility.Issues
                    .Where(issue => !string.Equals(
                        issue.Code,
                        "invalidVariables",
                        StringComparison.Ordinal))
                    .ToArray()
                : eligibility.Issues;
        }
        catch (WorkflowForbiddenException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            WorkflowDomainException or WorkflowConflictException)
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
        ResolvedFlowMapping mapping,
        string kind,
        string phase,
        IReadOnlyDictionary<string, string> actorClaims,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await jobs.EnqueueAsync(
            new WorkflowJobCreateRecord
            {
                WorkflowDefinitionId = mapping.Workflow.Id,
                WorkflowKey = mapping.Workflow.WorkflowKey,
                ActivationId = Guid.NewGuid(),
                NodeId = mapping.Action.SourceNodeId,
                NodeName = mapping.Action.SourceNodeName,
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
        ResolvedFlowMapping mapping,
        IReadOnlyList<AdministrativeActionIssueDto> issues) =>
        new(
            record.UserTaskId,
            record.InstanceId,
            record.TokenId,
            record.WorkflowDefinitionId,
            mapping.Workflow.Version,
            record.FlowId,
            mapping.Action.Name,
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

    private static AdministrativeActionFlowMappingRecord ToFlowMappingRecord(
        ResolvedFlowMapping mapping) =>
        new(
            mapping.Workflow.Id,
            mapping.Workflow.Version,
            mapping.Action.FlowId,
            mapping.Action.FlowExternalId,
            mapping.Action.Name,
            mapping.Action.SourceNodeId,
            mapping.Action.SourceNodeName,
            mapping.Action.TargetNodeId,
            mapping.Action.TargetNodeName,
            SnapshotRoles(mapping.Flow.Roles),
            mapping.Action.Variables);

    internal static AdministrativeActionBatchSummaryDto ToSummary(
        AdministrativeActionBatchRecord record) =>
        new(
            record.Id,
            record.WorkflowKey,
            record.FlowMappings.Count,
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
            record.FlowMappings.Select(mapping =>
                new AdministrativeActionFlowMappingSnapshotDto(
                    mapping.WorkflowDefinitionId,
                    mapping.WorkflowVersion,
                    mapping.FlowId,
                    mapping.FlowExternalId,
                    mapping.FlowName,
                    mapping.SourceNodeId,
                    mapping.SourceNodeName,
                    mapping.TargetNodeId,
                    mapping.TargetNodeName,
                    mapping.Roles,
                    mapping.Variables)).ToArray(),
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
            record.WorkflowDefinitionId,
            record.FlowId,
            record.CapturedInstanceUpdatedAt,
            record.CapturedUserTaskUpdatedAt,
            record.Status,
            record.Issues,
            record.Result,
            record.ErrorCode,
            record.ErrorDescription,
            record.NewUserTaskId,
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
            record.UpdatedAt,
            record.PreparedAt,
            record.StartedAt,
            record.CompletedAt);

    internal static AdministrativeActionIssueDto Issue(
        string code,
        string message) =>
        new(code, Limit(message));

    internal static JsonElement SerializeIssues(
        IReadOnlyCollection<AdministrativeActionIssueDto> issues) =>
        JsonSerializer.SerializeToElement(issues);

    private static void EnsureCompatibleVariableContracts(
        IReadOnlyList<ResolvedFlowMapping> mappings)
    {
        var expected = VariableContract(mappings[0].Action.Variables);
        foreach (var mapping in mappings.Skip(1))
        {
            if (!expected.SequenceEqual(
                    VariableContract(mapping.Action.Variables),
                    StringComparer.OrdinalIgnoreCase))
            {
                throw new WorkflowDomainException(
                    "Every mapped flow must declare the same variable names, types, array flags, and required flags.");
            }
        }
    }

    private static string[] VariableContract(IReadOnlyList<VariableModel> variables) =>
        variables
            .Select(variable => string.Join(
                "|",
                variable.Name.Trim(),
                variable.DataType.Trim(),
                variable.IsArray,
                variable.Required))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void ValidateCommonVariables(
        AdministrativeActionSummaryDto action,
        IReadOnlyDictionary<string, JsonElement>? values)
    {
        var suppliedNames = ValidateVariableNameUniqueness(values);
        var missing = action.Variables
            .Where(variable => variable.Required
                               && (suppliedNames is null
                                   || !suppliedNames.Contains(variable.Name)))
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
        var variables = request.Variables ?? new Dictionary<string, JsonElement>();
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
        var storedMappings = existing.FlowMappings.Select(mapping =>
            new AdministrativeActionFlowMappingDto(
                mapping.WorkflowDefinitionId,
                mapping.FlowId)).ToArray();
        if (!MappingSetsEqual(storedMappings, request.FlowMappings)
            || !string.Equals(existing.Reason, normalizedReason, StringComparison.Ordinal)
            || !variablesMatch
            || !JsonElement.DeepEquals(existing.Selection, selectionElement))
        {
            throw new WorkflowConflictException(
                "The IdempotencyKey is already associated with a different administrative batch request.");
        }
    }

    private static bool MappingSetsEqual(
        IReadOnlyList<AdministrativeActionFlowMappingDto>? left,
        IReadOnlyList<AdministrativeActionFlowMappingDto>? right)
    {
        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }
        return left
            .OrderBy(mapping => mapping.WorkflowDefinitionId)
            .ThenBy(mapping => mapping.FlowId)
            .SequenceEqual(right
                .OrderBy(mapping => mapping.WorkflowDefinitionId)
                .ThenBy(mapping => mapping.FlowId));
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
            throw new WorkflowDomainException(
                "The administrative operator name is too long.");
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
        return normalized.EnumerateRunes().Count()
               <= AdministrativeActionConstraints.MaxErrorDescriptionLength
            ? normalized
            : string.Concat(normalized.EnumerateRunes()
                .Take(AdministrativeActionConstraints.MaxErrorDescriptionLength)
                .Select(rune => rune.ToString()));
    }
}
