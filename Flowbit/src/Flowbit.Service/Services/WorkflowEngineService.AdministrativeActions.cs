using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

public sealed partial class WorkflowEngineService
{
    internal const string AdministrativeActionsRequiredRoleSettingKey =
        "WorkflowAdministrativeActions.RequiredRole";
    internal const string DefaultAdministrativeActionsRequiredRole = "admin";
    private const string AdministrativeActionKind =
        NodeExecutionCompletionReasons.AdministrativeAction;

    public async Task<IReadOnlyList<AdministrativeActionSummaryDto>>
        GetWorkflowAdministrativeActionsAsync(
            long workflowId,
            ActorContext actor,
            bool batchableOnly,
            CancellationToken cancellationToken)
    {
        if (workflowId <= 0)
        {
            throw new WorkflowDomainException(
                "Workflow id must be greater than zero.");
        }
        await AuthorizeAdministrativeActionRoleAsync(actor, cancellationToken);
        var workflow = await definitions.GetPublishedAsync(
            workflowId,
            cancellationToken);
        if (workflow is null)
        {
            return [];
        }

        var roles = NormalizeRoles(actor.Roles);
        return workflow.Definition.SequenceFlows
            .Where(flow => flow.IsAdministrative
                           && (!batchableOnly || flow.IsBatchable)
                           && RoleAllowed(flow.Roles, roles))
            .Select(flow =>
            {
                var source = workflow.Definition.FlowNodes
                    .SingleOrDefault(node => node.Id == flow.SourceRef);
                if (source is null
                    || !IsAdministrativeActionContract(
                        workflow.Definition,
                        source,
                        flow))
                {
                    return null;
                }
                var target = GetFlowNode(
                    workflow.Definition,
                    flow.TargetRef);
                return ToAdministrativeActionSummary(flow, source, target);
            })
            .Where(summary => summary is not null)
            .Cast<AdministrativeActionSummaryDto>()
            .ToList();
    }

    public async Task<AdministrativeActionEligibilityDto>
        PreviewUserTaskAdministrativeActionAsync(
            long taskId,
            AdministrativeActionRequest request,
            ActorContext actor,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAdministrativeActionIdentifiers(taskId, request.TargetWorkflowId);
        await AuthorizeAdministrativeActionRoleAsync(actor, cancellationToken);
        await LoadSettingsAsync(cancellationToken);

        var issues = new List<InstanceVersionChangeIssueDto>();
        string flowExternalId;
        try
        {
            flowExternalId = NormalizeAdministrativeFlowExternalId(
                request.FlowExternalId);
            _ = NormalizeVersionChangeReason(request.Reason);
        }
        catch (WorkflowDomainException exception)
        {
            issues.Add(AdministrativeActionIssue(
                "invalidRequest",
                exception.Message));
            return new AdministrativeActionEligibilityDto(false, issues);
        }

        if (request.ExpectedSourceWorkflowId <= 0
            || request.ExpectedInstanceUpdatedAt == default
            || request.ExpectedTokenId is not long expectedTokenId
            || expectedTokenId <= 0
            || request.ExpectedUserTaskUpdatedAt is not DateTimeOffset expectedTaskTimestamp
            || expectedTaskTimestamp == default)
        {
            issues.Add(AdministrativeActionIssue(
                "invalidExpectedState",
                "ExpectedSourceWorkflowId, ExpectedInstanceUpdatedAt, ExpectedTokenId, and ExpectedUserTaskUpdatedAt are required."));
            return new AdministrativeActionEligibilityDto(false, issues);
        }

        var task = await runtime.GetUserTaskAsync(taskId, false, cancellationToken);
        if (task is null)
        {
            issues.Add(AdministrativeActionIssue(
                "taskNotFound",
                "The selected user task no longer exists."));
            return new AdministrativeActionEligibilityDto(false, issues);
        }
        var instance = await runtime.GetInstanceAsync(task.InstanceId, cancellationToken);
        if (instance is null)
        {
            issues.Add(AdministrativeActionIssue(
                "instanceNotFound",
                "The workflow instance no longer exists."));
            return new AdministrativeActionEligibilityDto(false, issues);
        }
        if (instance.Status != WorkflowInstanceStatuses.Running)
        {
            issues.Add(AdministrativeActionIssue(
                "instanceNotRunning",
                "Only a running workflow instance can take an administrative action."));
        }
        if (instance.WorkflowDefinitionId != request.ExpectedSourceWorkflowId)
        {
            issues.Add(AdministrativeActionIssue(
                "sourceVersionChanged",
                "The workflow instance source version changed.",
                stateId: instance.Id));
        }
        if (instance.UpdatedAt != request.ExpectedInstanceUpdatedAt)
        {
            issues.Add(AdministrativeActionIssue(
                "instanceChanged",
                "The workflow instance changed after selection.",
                stateId: instance.Id));
        }
        if (request.ExpectedUserTaskUpdatedAt is DateTimeOffset expectedTaskUpdatedAt
            && task.UpdatedAt != expectedTaskUpdatedAt)
        {
            issues.Add(AdministrativeActionIssue(
                "taskChanged",
                "The user task changed after selection.",
                stateId: task.Id,
                nodeId: task.NodeId));
        }
        if (request.ExpectedTokenId is long expectedTaskTokenId
            && task.TokenId != expectedTaskTokenId)
        {
            issues.Add(AdministrativeActionIssue(
                "tokenChanged",
                "The user task execution token changed after selection.",
                stateId: task.Id,
                nodeId: task.NodeId));
        }

        var source = await definitions.GetAsync(
            instance.WorkflowDefinitionId,
            cancellationToken);
        var target = await definitions.GetPublishedAsync(
            request.TargetWorkflowId,
            cancellationToken);
        if (source is null)
        {
            issues.Add(AdministrativeActionIssue(
                "sourceVersionMissing",
                "The source workflow version no longer exists."));
            return new AdministrativeActionEligibilityDto(false, issues);
        }
        if (target is null)
        {
            issues.Add(AdministrativeActionIssue(
                "targetNotPublished",
                "The target workflow version does not exist or is not published."));
            return new AdministrativeActionEligibilityDto(false, issues);
        }
        if (!string.Equals(source.WorkflowKey, target.WorkflowKey, StringComparison.Ordinal)
            || !string.Equals(instance.WorkflowKey, target.WorkflowKey, StringComparison.Ordinal))
        {
            issues.Add(AdministrativeActionIssue(
                "workflowFamilyMismatch",
                "The target workflow version does not belong to the instance workflow family."));
            return new AdministrativeActionEligibilityDto(false, issues);
        }

        var activeTokens = await runtime.ListExecutionTokensAsync(
            instance.Id,
            ExecutionTokenRecordStatuses.Active,
            cancellationToken);
        var openTasks = (await runtime.ListUserTasksAsync(
                instance.Id,
                null,
                cancellationToken))
            .Where(item => item.Status is
                UserTaskRecordStatuses.Active or UserTaskRecordStatuses.Pending)
            .ToList();
        if (activeTokens.Count != 1 || openTasks.Count != 1)
        {
            issues.Add(AdministrativeActionIssue(
                "singlePositionRequired",
                "Administrative actions require exactly one active execution token and one active ordinary user task."));
        }
        else if (openTasks[0].Id != task.Id
                 || openTasks[0].Status != UserTaskRecordStatuses.Active
                 || activeTokens[0].Id != task.TokenId
                 || activeTokens[0].NodeId != task.NodeId
                 || task.MultiInstanceExecutionId is not null)
        {
            issues.Add(AdministrativeActionIssue(
                "taskNotCurrent",
                "The selected ordinary user task is no longer current for its execution token.",
                stateId: task.Id,
                nodeId: task.NodeId));
        }

        var sourceNode = source.Definition.FlowNodes
            .SingleOrDefault(node => node.Id == task.NodeId);
        var targetSourceNode = target.Definition.FlowNodes
            .SingleOrDefault(node => node.Id == task.NodeId);
        if (!IsOrdinarySynchronousAdministrativeSource(sourceNode)
            || !IsOrdinarySynchronousAdministrativeSource(targetSourceNode))
        {
            issues.Add(AdministrativeActionIssue(
                "unsupportedSourceTask",
                "Administrative actions require an ordinary synchronous user task in both workflow versions.",
                stateId: task.Id,
                nodeId: task.NodeId));
            return new AdministrativeActionEligibilityDto(false, issues);
        }

        var flow = target.Definition.SequenceFlows.SingleOrDefault(candidate =>
            string.Equals(
                candidate.ExternalId?.Trim(),
                flowExternalId,
                StringComparison.OrdinalIgnoreCase));
        if (flow is null
            || flow.SourceRef != task.NodeId
            || !IsAdministrativeActionContract(
                target.Definition,
                targetSourceNode!,
                flow))
        {
            issues.Add(AdministrativeActionIssue(
                "actionNotAvailable",
                $"Administrative action '{flowExternalId}' is not available from the selected task in target workflow #{target.Id}."));
            return new AdministrativeActionEligibilityDto(false, issues);
        }
        if (!RoleAllowed(flow.Roles, NormalizeRoles(actor.Roles)))
        {
            issues.Add(AdministrativeActionIssue(
                "flowRoleRequired",
                "The actor does not have a role permitted for this administrative action.",
                flowId: flow.Id));
        }

        if (source.Id != target.Id)
        {
            var compatibilityContext = await BuildVersionCompatibilityContextAsync(
                instance,
                source,
                target,
                actor,
                lockDurableState: false,
                cancellationToken);
            var compatibility = WorkflowVersionCompatibilityEvaluator.Evaluate(
                compatibilityContext);
            issues.AddRange(compatibility.Blockers.Select(ToVersionChangeIssue));
        }

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var targetInstance = instance with
        {
            WorkflowDefinitionId = target.Id,
            ActiveTokenId = task.TokenId,
            CurrentStepId = task.NodeId,
            ActiveUserTaskId = task.Id,
            ClaimedBy = task.ClaimedBy
        };
        var context = WithContext(
            stored,
            actor,
            targetInstance,
            target.Definition,
            targetSourceNode!);
        if (!string.IsNullOrWhiteSpace(flow.Condition)
            && !SequenceFlowConditionEvaluator.Evaluate(flow.Condition, context))
        {
            issues.Add(AdministrativeActionIssue(
                "conditionNotSatisfied",
                $"Administrative action '{flow.Name}' condition is not satisfied.",
                flowId: flow.Id));
        }
        try
        {
            ValidateVariableValues(flow.Variables, request.Variables);
            _ = ResolveAndValidateVariables(
                flow.Variables,
                request.Variables,
                context);
        }
        catch (WorkflowDomainException exception)
        {
            issues.Add(AdministrativeActionIssue(
                "invalidVariables",
                exception.Message,
                flowId: flow.Id));
        }

        return new AdministrativeActionEligibilityDto(
            issues.Count == 0,
            issues);
    }

    public async Task<IReadOnlyList<AdministrativeActionSummaryDto>>
        GetUserTaskAdministrativeActionsAsync(
            long taskId,
            long targetWorkflowId,
            ActorContext actor,
            CancellationToken cancellationToken)
    {
        ValidateAdministrativeActionIdentifiers(taskId, targetWorkflowId);
        await AuthorizeAdministrativeActionRoleAsync(actor, cancellationToken);
        await LoadSettingsAsync(cancellationToken);

        var task = await runtime.GetUserTaskAsync(taskId, false, cancellationToken);
        if (task is null
            || task.Status != UserTaskRecordStatuses.Active
            || task.MultiInstanceExecutionId is not null)
        {
            return [];
        }

        var instance = await runtime.GetInstanceAsync(task.InstanceId, cancellationToken);
        if (instance is null
            || !string.Equals(
                instance.Status,
                WorkflowInstanceStatuses.Running,
                StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var source = await definitions.GetAsync(
            instance.WorkflowDefinitionId,
            cancellationToken);
        var target = await definitions.GetPublishedAsync(
            targetWorkflowId,
            cancellationToken);
        if (source is null || target is null)
        {
            return [];
        }

        EnsureAdministrativeActionFamily(source, target);
        if (!string.Equals(instance.WorkflowKey, target.WorkflowKey, StringComparison.Ordinal))
        {
            return [];
        }

        var activeTokens = await runtime.ListExecutionTokensAsync(
            instance.Id,
            ExecutionTokenRecordStatuses.Active,
            cancellationToken);
        var openTasks = (await runtime.ListUserTasksAsync(
                instance.Id,
                null,
                cancellationToken))
            .Where(item => item.Status is
                UserTaskRecordStatuses.Active or UserTaskRecordStatuses.Pending)
            .ToList();
        if (activeTokens.Count != 1
            || openTasks.Count != 1
            || openTasks[0].Id != task.Id
            || activeTokens[0].Id != task.TokenId
            || activeTokens[0].NodeId != task.NodeId)
        {
            return [];
        }

        var sourceNode = source.Definition.FlowNodes
            .SingleOrDefault(node => node.Id == task.NodeId);
        var targetSourceNode = target.Definition.FlowNodes
            .SingleOrDefault(node => node.Id == task.NodeId);
        if (!IsOrdinarySynchronousAdministrativeSource(sourceNode)
            || !IsOrdinarySynchronousAdministrativeSource(targetSourceNode))
        {
            return [];
        }

        if (source.Id != target.Id)
        {
            var compatibilityContext = await BuildVersionCompatibilityContextAsync(
                instance,
                source,
                target,
                actor,
                lockDurableState: false,
                cancellationToken);
            if (!WorkflowVersionCompatibilityEvaluator.Evaluate(compatibilityContext).IsCompatible)
            {
                return [];
            }
        }

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var targetInstance = instance with { WorkflowDefinitionId = target.Id };
        var context = WithContext(
            stored,
            actor,
            targetInstance,
            target.Definition,
            targetSourceNode!);
        var roles = NormalizeRoles(actor.Roles);

        return OutgoingFlows(target.Id, target.Definition, task.NodeId)
            .Where(flow => IsAdministrativeActionContract(target.Definition, targetSourceNode!, flow)
                           && RoleAllowed(flow.Roles, roles)
                           && (string.IsNullOrWhiteSpace(flow.Condition)
                               || SequenceFlowConditionEvaluator.Evaluate(flow.Condition, context)))
            .Select(flow =>
            {
                var targetNode = GetFlowNode(target.Definition, flow.TargetRef);
                return new AdministrativeActionSummaryDto(
                    flow.Id,
                    flow.ExternalId!.Trim(),
                    flow.Name,
                    targetSourceNode!.Id,
                    targetSourceNode.Name,
                    targetNode.Id,
                    targetNode.Name,
                    flow.IsBatchable,
                    flow.Variables);
            })
            .ToList();
    }

    public async Task<AdministrativeActionTaskContextDto?>
        GetAdministrativeActionTaskContextAsync(
            long taskId,
            ActorContext actor,
            CancellationToken cancellationToken)
    {
        if (taskId <= 0)
        {
            throw new WorkflowDomainException("Task id must be greater than zero.");
        }
        await AuthorizeAdministrativeActionRoleAsync(actor, cancellationToken);
        await LoadSettingsAsync(cancellationToken);

        var task = await runtime.GetUserTaskAsync(taskId, false, cancellationToken);
        if (task is null
            || task.Status != UserTaskRecordStatuses.Active
            || task.MultiInstanceExecutionId is not null)
        {
            return null;
        }
        var instance = await runtime.GetInstanceAsync(task.InstanceId, cancellationToken);
        if (instance is null || instance.Status != WorkflowInstanceStatuses.Running)
        {
            return null;
        }
        var source = await definitions.GetAsync(
            instance.WorkflowDefinitionId,
            cancellationToken);
        if (source is null
            || !string.Equals(source.WorkflowKey, instance.WorkflowKey, StringComparison.Ordinal))
        {
            return null;
        }
        var sourceNode = source.Definition.FlowNodes
            .SingleOrDefault(node => node.Id == task.NodeId);
        if (!IsOrdinarySynchronousAdministrativeSource(sourceNode))
        {
            return null;
        }

        var activeTokens = await runtime.ListExecutionTokensAsync(
            instance.Id,
            ExecutionTokenRecordStatuses.Active,
            cancellationToken);
        var openTasks = (await runtime.ListUserTasksAsync(
                instance.Id,
                null,
                cancellationToken))
            .Where(item => item.Status is
                UserTaskRecordStatuses.Active or UserTaskRecordStatuses.Pending)
            .ToList();
        if (activeTokens.Count != 1
            || openTasks.Count != 1
            || openTasks[0].Id != task.Id
            || activeTokens[0].Id != task.TokenId
            || activeTokens[0].NodeId != task.NodeId)
        {
            return null;
        }

        var authorizedTargets = new List<WorkflowSummaryDto>();
        var versions = await definitions.ListVersionsByKeyAsync(
            source.WorkflowKey,
            cancellationToken);
        foreach (var target in versions
                     .Where(version => version.IsPublished)
                     .OrderByDescending(version => version.Version)
                     .ThenByDescending(version => version.Id))
        {
            var actions = await GetUserTaskAdministrativeActionsAsync(
                task.Id,
                target.Id,
                actor,
                cancellationToken);
            if (actions.Count > 0)
            {
                authorizedTargets.Add(WorkflowDefinitionService.ToSummary(target));
            }
        }
        if (authorizedTargets.Count == 0)
        {
            return null;
        }

        return new AdministrativeActionTaskContextDto(
            task.Id,
            instance.Id,
            task.TokenId,
            task.NodeId,
            task.NodeName,
            task.NodeExternalId,
            source.Id,
            source.WorkflowKey,
            source.Name,
            source.Version,
            instance.UpdatedAt,
            task.UpdatedAt,
            authorizedTargets);
    }

    public async Task<AdministrativeActionResultDto?>
        ExecuteUserTaskAdministrativeActionAsync(
            long taskId,
            AdministrativeActionRequest request,
            ActorContext actor,
            CancellationToken cancellationToken,
            long? administrativeActionBatchId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAdministrativeActionIdentifiers(taskId, request.TargetWorkflowId);
        if (request.ExpectedSourceWorkflowId <= 0)
        {
            throw new WorkflowDomainException(
                "ExpectedSourceWorkflowId must be greater than zero.");
        }
        if (request.ExpectedInstanceUpdatedAt == default)
        {
            throw new WorkflowDomainException("ExpectedInstanceUpdatedAt is required.");
        }
        if (request.ExpectedTokenId is not long expectedTokenId || expectedTokenId <= 0)
        {
            throw new WorkflowDomainException("ExpectedTokenId is required.");
        }
        if (request.ExpectedUserTaskUpdatedAt is not DateTimeOffset expectedTaskTimestamp
            || expectedTaskTimestamp == default)
        {
            throw new WorkflowDomainException("ExpectedUserTaskUpdatedAt is required.");
        }
        var flowExternalId = NormalizeAdministrativeFlowExternalId(
            request.FlowExternalId);
        var reason = NormalizeVersionChangeReason(request.Reason);
        await AuthorizeAdministrativeActionRoleAsync(actor, cancellationToken);
        await LoadSettingsAsync(cancellationToken);

        // Resolve the family before opening the transaction. Publication,
        // version, task, token, and optimistic-concurrency state are all
        // rechecked after the family and instance locks are acquired.
        var initialTask = await runtime.GetUserTaskAsync(
            taskId,
            false,
            cancellationToken);
        if (initialTask is null)
        {
            return null;
        }
        var previewInstance = await runtime.GetInstanceAsync(
            initialTask.InstanceId,
            cancellationToken);
        if (previewInstance is null)
        {
            return null;
        }
        var previewSource = await definitions.GetAsync(
            previewInstance.WorkflowDefinitionId,
            cancellationToken);
        var previewTarget = await definitions.GetAsync(
            request.TargetWorkflowId,
            cancellationToken);
        if (previewSource is null || previewTarget is null)
        {
            return null;
        }
        EnsureAdministrativeActionFamily(previewSource, previewTarget);

        WorkflowInstanceVersionChangeRecord? versionChangeRecord = null;
        WorkflowDefinitionRecord source;
        WorkflowDefinitionRecord target;
        int selectedFlowId;
        int targetNodeId;
        long instanceId;
        long tokenId;
        long? newUserTaskId = null;

        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await definitions.LockFamilyForStartAsync(
                previewSource.WorkflowKey,
                cancellationToken);

            var instance = await runtime.GetInstanceForUpdateAsync(
                    initialTask.InstanceId,
                    lockActiveUserTask: false,
                    cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The workflow instance no longer exists.");
            instanceId = instance.Id;
            ValidateLockedAdministrativeInstance(instance, request);

            source = await definitions.GetAsync(
                    instance.WorkflowDefinitionId,
                    cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The source workflow version no longer exists.");
            target = await definitions.GetPublishedAsync(
                    request.TargetWorkflowId,
                    cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The target workflow version is no longer published.");
            EnsureAdministrativeActionFamily(source, target);
            if (!string.Equals(instance.WorkflowKey, source.WorkflowKey, StringComparison.Ordinal)
                || !string.Equals(instance.WorkflowKey, target.WorkflowKey, StringComparison.Ordinal))
            {
                throw new WorkflowConflictException(
                    "The workflow instance family no longer matches the requested versions.");
            }

            var activeTokens = await runtime.ListExecutionTokensAsync(
                instance.Id,
                ExecutionTokenRecordStatuses.Active,
                cancellationToken);
            var openTasks = (await runtime.ListUserTasksAsync(
                    instance.Id,
                    null,
                    cancellationToken))
                .Where(item => item.Status is
                    UserTaskRecordStatuses.Active or UserTaskRecordStatuses.Pending)
                .ToList();
            if (activeTokens.Count != 1 || openTasks.Count != 1)
            {
                throw new WorkflowConflictException(
                    "Administrative actions require exactly one active execution token and one active ordinary user task.");
            }
            if (openTasks[0].Id != taskId
                || openTasks[0].Status != UserTaskRecordStatuses.Active)
            {
                throw new WorkflowConflictException(
                    "The selected user task is no longer the instance's active task.");
            }

            // Preserve the global runtime lock order: instance, token, task.
            var token = await runtime.GetExecutionTokenAsync(
                    activeTokens[0].Id,
                    true,
                    cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The selected user task execution token no longer exists.");
            var task = await runtime.GetUserTaskAsync(
                    taskId,
                    true,
                    cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The selected user task no longer exists.");
            ValidateLockedAdministrativeTask(instance, task, token);
            if (token.Id != expectedTokenId)
            {
                throw new WorkflowConflictException(
                    "The execution token changed after selection; refresh and try again.");
            }
            if (request.ExpectedUserTaskUpdatedAt is DateTimeOffset expectedTaskUpdatedAt
                && task.UpdatedAt != expectedTaskUpdatedAt)
            {
                throw new WorkflowConflictException(
                    "The user task changed after selection; refresh and try again.");
            }
            tokenId = token.Id;

            var currentSourceNode = GetFlowNode(source.Definition, task.NodeId);
            var targetSourceNode = target.Definition.FlowNodes
                .SingleOrDefault(node => node.Id == task.NodeId);
            if (!IsOrdinarySynchronousAdministrativeSource(currentSourceNode)
                || !IsOrdinarySynchronousAdministrativeSource(targetSourceNode))
            {
                throw new WorkflowConflictException(
                    "Administrative actions require an ordinary synchronous user task in both workflow versions.");
            }

            var flow = target.Definition.SequenceFlows.SingleOrDefault(candidate =>
                string.Equals(
                    candidate.ExternalId?.Trim(),
                    flowExternalId,
                    StringComparison.OrdinalIgnoreCase));
            if (flow is null
                || flow.SourceRef != task.NodeId
                || !IsAdministrativeActionContract(
                    target.Definition,
                    targetSourceNode!,
                    flow))
            {
                throw new WorkflowDomainException(
                    $"Administrative action '{flowExternalId}' is not available from the selected task in target workflow #{target.Id}.");
            }
            var actorRoles = NormalizeRoles(actor.Roles);
            if (!RoleAllowed(flow.Roles, actorRoles))
            {
                throw new WorkflowForbiddenException(
                    "The actor does not have a role permitted for this administrative action.");
            }

            if (source.Id != target.Id)
            {
                var compatibilityContext = await BuildVersionCompatibilityContextAsync(
                    instance,
                    source,
                    target,
                    actor,
                    lockDurableState: true,
                    cancellationToken);
                var compatibility = WorkflowVersionCompatibilityEvaluator.Evaluate(
                    compatibilityContext);
                if (!compatibility.IsCompatible)
                {
                    throw AdministrativeActionCompatibilityConflict(compatibility);
                }

                versionChangeRecord = await runtime.ChangeInstanceWorkflowVersionAsync(
                    instance.Id,
                    request.ExpectedSourceWorkflowId,
                    request.ExpectedInstanceUpdatedAt,
                    target.Id,
                    target.Definition,
                    ToNodeExecutionActor(actor),
                    reason,
                    cancellationToken,
                    administrativeActionBatchId);
                instance = instance with
                {
                    WorkflowDefinitionId = target.Id,
                    UpdatedAt = versionChangeRecord.ChangedAt
                };

                // Re-read the locked state after the repository has updated all
                // open runtime snapshots to the target definition.
                token = await runtime.GetExecutionTokenAsync(
                        token.Id,
                        true,
                        cancellationToken)
                    ?? throw new WorkflowConflictException(
                        "The administrative-action execution token disappeared during the version change.");
                task = await runtime.GetUserTaskAsync(
                        task.Id,
                        true,
                        cancellationToken)
                    ?? throw new WorkflowConflictException(
                        "The administrative-action user task disappeared during the version change.");
                ValidateLockedAdministrativeTask(instance, task, token);
                if (token.Id != expectedTokenId)
                {
                    throw new WorkflowConflictException(
                        "The execution token changed during the workflow version change.");
                }
                if (request.ExpectedUserTaskUpdatedAt is DateTimeOffset expectedTaskUpdatedAtAfterVersionChange
                    && task.UpdatedAt != expectedTaskUpdatedAtAfterVersionChange)
                {
                    throw new WorkflowConflictException(
                        "The user task changed during the workflow version change.");
                }
            }

            var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
            var taskInstance = instance with
            {
                ActiveTokenId = token.Id,
                CurrentStepId = token.NodeId,
                ActiveUserTaskId = task.Id,
                ClaimedBy = task.ClaimedBy
            };
            var storedContext = WithContext(
                stored,
                actor,
                taskInstance,
                target.Definition,
                targetSourceNode!);
            if (!string.IsNullOrWhiteSpace(flow.Condition)
                && !SequenceFlowConditionEvaluator.Evaluate(
                    flow.Condition,
                    storedContext))
            {
                throw new WorkflowDomainException(
                    $"Administrative action '{flow.Name}' condition is not satisfied: '{flow.Condition}'.");
            }

            ValidateVariableValues(flow.Variables, request.Variables);
            var flowValues = ResolveAndValidateVariables(
                flow.Variables,
                request.Variables,
                storedContext);
            var flowContext = new Dictionary<string, JsonElement>(
                storedContext,
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in flowValues)
            {
                flowContext[pair.Key] = pair.Value;
            }

            var performedBy = NormalizeUser(actor.User);
            var flowInfo = await LoadSequenceFlowInfoAsync(
                instance.Id,
                target.Definition,
                cancellationToken);
            await runtime.CompleteUserTaskAsync(
                task.Id,
                flow.Id,
                performedBy,
                SnapshotRoles(actor.Roles),
                flowValues,
                cancellationToken,
                completionKind: AdministrativeActionKind,
                completionReason: reason,
                administrativeActionBatchId: administrativeActionBatchId);
            await RecordSequenceFlowOccurrenceAsync(
                flowInfo,
                instance.Id,
                task.TokenId,
                task.Id,
                null,
                null,
                flow,
                AdministrativeActionKind,
                isAction: true,
                isTraversal: true,
                actor,
                flowValues,
                cancellationToken);

            foreach (var pair in flowValues)
            {
                await runtime.AddVariableAsync(
                    instance.Id,
                    pair.Key,
                    flow.Id,
                    performedBy,
                    pair.Value,
                    cancellationToken,
                    task.NodeExecutionId);
            }

            await runtime.AddUserTaskActionHistoryAsync(
                instance.Id,
                task.TokenId,
                task.Id,
                flow.Id,
                targetSourceNode!.Id,
                flow.TargetRef,
                performedBy,
                CloneDictionary(flowValues) ?? [],
                cancellationToken,
                note: AdministrativeActionKind,
                reason: reason,
                administrativeActionBatchId: administrativeActionBatchId);

            if (!await runtime.SetExecutionTokenAutomaticActivationCountAsync(
                    token.Id,
                    token.ActivationId,
                    WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger(),
                    cancellationToken))
            {
                throw new WorkflowConflictException(
                    "The administrative-action execution token changed while resetting its automatic-activation count.");
            }

            var nextNode = GetFlowNode(target.Definition, flow.TargetRef);
            targetNodeId = nextNode.Id;
            taskInstance = taskInstance with
            {
                WorkflowDefinitionId = target.Id,
                CurrentStepId = nextNode.Id,
                ActiveUserTaskId = null,
                ClaimedBy = null,
                UpdatedAt = timeProvider.GetUtcNow()
            };
            var nextContext = WithContext(
                flowContext,
                actor,
                taskInstance,
                target.Definition,
                nextNode);
            await CancelAttachedTimerBoundaryWaitsAsync(
                instance.Id,
                [token.Id],
                cancellationToken);
            await runtime.UpdateExecutionTokenAsync(
                token.Id,
                ToSnapshot(nextNode, nextContext, instance.Id),
                ExecutionTokenRecordStatuses.Active,
                token.GatewayBranchId,
                flow.Id,
                null,
                null,
                ToNodeExecutionActor(actor),
                null,
                cancellationToken,
                automaticActivationCount:
                    WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger());

            instance = await ResolvePassThroughAsync(
                taskInstance,
                target.Definition,
                actor,
                flowInfo,
                token.Id,
                cancellationToken);
            await EnsureMultiInstanceInitializedAsync(
                instance,
                target.Definition,
                actor,
                cancellationToken);
            instance = await ApplyUserTaskOwnershipInheritanceAsync(
                instance,
                target.Definition,
                cancellationToken);

            newUserTaskId = (await runtime.ListUserTasksAsync(
                    instance.Id,
                    null,
                    cancellationToken))
                .Where(candidate => candidate.TokenId == token.Id
                                    && candidate.NodeId == nextNode.Id
                                    && candidate.Id != task.Id)
                .OrderByDescending(candidate => candidate.Id)
                .Select(candidate => (long?)candidate.Id)
                .FirstOrDefault();
            if (newUserTaskId is null)
            {
                throw new WorkflowConflictException(
                    "The administrative action did not create its expected target user task.");
            }

            if (administrativeActionBatchId is long batchId)
            {
                await runtime.CompleteAdministrativeActionBatchItemAsync(
                    batchId,
                    task.Id,
                    newUserTaskId,
                    versionChangeRecord?.Id,
                    JsonSerializer.SerializeToElement(new
                    {
                        selectedFlowId = flow.Id,
                        flowExternalId,
                        targetNodeId = nextNode.Id,
                        newUserTaskId,
                        versionChangeAuditId = versionChangeRecord?.Id
                    }),
                    timeProvider.GetUtcNow(),
                    cancellationToken);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            selectedFlowId = flow.Id;
        }

        var detail = await BuildDetailAsync(instanceId, cancellationToken)
            ?? throw new WorkflowConflictException(
                "The workflow instance no longer exists after the administrative action.");
        var newTaskRecord = newUserTaskId is long createdTaskId
            ? await runtime.GetUserTaskAsync(
                createdTaskId,
                false,
                cancellationToken)
            : null;
        var newTask = newTaskRecord is null
            ? null
            : await BuildUserTaskDtoAsync(newTaskRecord, actor, cancellationToken);
        var versionChange = versionChangeRecord is null
            ? null
            : ToVersionChangeAudit(versionChangeRecord, source, target);

        logger.LogInformation(
            "Administrative action {FlowId} ({FlowExternalId}) moved instance {InstanceId} from user task {TaskId} to node {TargetNodeId} by {Actor} (batch {BatchId}).",
            selectedFlowId,
            flowExternalId,
            instanceId,
            taskId,
            targetNodeId,
            actor.User ?? "anonymous",
            administrativeActionBatchId);

        return new AdministrativeActionResultDto(
            detail,
            taskId,
            newTask,
            versionChange,
            administrativeActionBatchId);
    }

    private async Task AuthorizeAdministrativeActionRoleAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var setting = await engineSettings.GetByKeyAsync(
            AdministrativeActionsRequiredRoleSettingKey,
            cancellationToken);
        var requiredRoles = ParseAdministrativeActionRoles(setting?.Value);
        var authorized = actor.Roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Any(role => requiredRoles.Contains(
                role.Trim(),
                StringComparer.OrdinalIgnoreCase));
        if (!authorized)
        {
            throw new WorkflowForbiddenException(
                $"A {AdministrativeActionsRequiredRoleSettingKey} role is required to inspect or execute administrative actions.");
        }
    }

    internal static IReadOnlyList<string> ParseAdministrativeActionRoles(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [DefaultAdministrativeActionsRequiredRole];
        }
        var roles = value
            .Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Where(role => role.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return roles.Length == 0
            ? [DefaultAdministrativeActionsRequiredRole]
            : roles;
    }

    private static bool IsOrdinarySynchronousAdministrativeSource(
        FlowNodeModel? node) =>
        node is not null
        && BpmnFlowNodeTypes.IsUserTask(node.Type)
        && node.MultiInstance is null
        && !node.AsyncAfter;

    private static bool IsAdministrativeActionContract(
        WorkflowModel definition,
        FlowNodeModel source,
        SequenceFlowModel flow)
    {
        if (!flow.IsAdministrative
            || !flow.IsSelectable
            || flow.IsDefault
            || flow.SourceRef != source.Id
            || source.MultiInstance is not null
            || source.AsyncAfter
            || string.IsNullOrWhiteSpace(flow.ExternalId)
            || flow.Roles.All(string.IsNullOrWhiteSpace))
        {
            return false;
        }
        var target = definition.FlowNodes.SingleOrDefault(
            node => node.Id == flow.TargetRef);
        return target is not null
               && BpmnFlowNodeTypes.IsUserTask(target.Type)
               && target.MultiInstance is null
               && !target.AsyncBefore;
    }

    private static AdministrativeActionSummaryDto ToAdministrativeActionSummary(
        SequenceFlowModel flow,
        FlowNodeModel source,
        FlowNodeModel target) =>
        new(
            flow.Id,
            flow.ExternalId!.Trim(),
            flow.Name,
            source.Id,
            source.Name,
            target.Id,
            target.Name,
            flow.IsBatchable,
            flow.Variables);

    private static InstanceVersionChangeIssueDto AdministrativeActionIssue(
        string code,
        string message,
        long? stateId = null,
        int? nodeId = null,
        int? flowId = null) =>
        new(
            code,
            message,
            StateType: stateId is null ? null : "administrativeActionCandidate",
            StateId: stateId,
            NodeId: nodeId,
            FlowId: flowId);

    private static void ValidateAdministrativeActionIdentifiers(
        long taskId,
        long targetWorkflowId)
    {
        if (taskId <= 0)
        {
            throw new WorkflowDomainException(
                "User task id must be greater than zero.");
        }
        if (targetWorkflowId <= 0)
        {
            throw new WorkflowDomainException(
                "TargetWorkflowId must be greater than zero.");
        }
    }

    private static string NormalizeAdministrativeFlowExternalId(string? raw)
    {
        var externalId = raw?.Trim() ?? string.Empty;
        if (externalId.Length == 0)
        {
            throw new WorkflowDomainException("FlowExternalId is required.");
        }
        return externalId;
    }

    private static void EnsureAdministrativeActionFamily(
        WorkflowDefinitionRecord source,
        WorkflowDefinitionRecord target)
    {
        if (!string.Equals(
                source.WorkflowKey,
                target.WorkflowKey,
                StringComparison.Ordinal))
        {
            throw new WorkflowDomainException(
                "The target workflow version must belong to the same workflow family.");
        }
    }

    private static void ValidateLockedAdministrativeInstance(
        WorkflowInstanceRecord instance,
        AdministrativeActionRequest request)
    {
        if (!string.Equals(
                instance.Status,
                WorkflowInstanceStatuses.Running,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowConflictException(
                "Only a running workflow instance can take an administrative action.");
        }
        if (instance.WorkflowDefinitionId != request.ExpectedSourceWorkflowId)
        {
            throw new WorkflowConflictException(
                "The workflow instance source version changed; refresh and try again.");
        }
        if (instance.UpdatedAt != request.ExpectedInstanceUpdatedAt)
        {
            throw new WorkflowConflictException(
                "The workflow instance changed after selection; refresh and try again.");
        }
    }

    private static void ValidateLockedAdministrativeTask(
        WorkflowInstanceRecord instance,
        UserTaskRecord task,
        ExecutionTokenRecord token)
    {
        if (task.InstanceId != instance.Id
            || task.MultiInstanceExecutionId is not null
            || task.Status != UserTaskRecordStatuses.Active
            || task.TokenId != token.Id
            || task.NodeId != token.NodeId
            || token.InstanceId != instance.Id
            || token.Status != ExecutionTokenRecordStatuses.Active)
        {
            throw new WorkflowConflictException(
                "The selected ordinary user task is no longer current for its execution token.");
        }
    }

    private static WorkflowConflictException AdministrativeActionCompatibilityConflict(
        WorkflowVersionCompatibilityResult compatibility)
    {
        var descriptions = compatibility.Blockers
            .Take(5)
            .Select(blocker => $"{blocker.Code}: {blocker.Message}");
        var suffix = compatibility.Blockers.Count > 5
            ? $" (+{compatibility.Blockers.Count - 5} more)"
            : string.Empty;
        return new WorkflowConflictException(
            "The target workflow version is incompatible with the active instance state: "
            + string.Join("; ", descriptions)
            + suffix);
    }
}
