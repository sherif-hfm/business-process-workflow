using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

public sealed partial class WorkflowEngineService
{
    private sealed record AdministrativeBatchFlowContext(
        long BatchId,
        long ExpectedWorkflowDefinitionId,
        long ExpectedTokenId,
        DateTimeOffset ExpectedInstanceUpdatedAt,
        DateTimeOffset ExpectedUserTaskUpdatedAt,
        string Reason);

    public async Task<AdministrativeActionEligibilityDto>
        PreviewAdministrativeBatchFlowAsync(
            long taskId,
            AdministrativeActionRequest request,
            ActorContext actor,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await AuthorizeAdministrativeBatchRoleAsync(actor, cancellationToken);
        await LoadSettingsAsync(cancellationToken);

        var issues = new List<AdministrativeActionIssueDto>();
        if (!TryValidateAdministrativeBatchRequest(taskId, request, issues))
        {
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
                "Only a running workflow instance can take an administrative batch action."));
        }
        if (instance.WorkflowDefinitionId != request.ExpectedWorkflowDefinitionId)
        {
            issues.Add(AdministrativeActionIssue(
                "workflowVersionChanged",
                "The workflow instance version changed after selection.",
                stateId: instance.Id));
        }
        if (instance.UpdatedAt != request.ExpectedInstanceUpdatedAt)
        {
            issues.Add(AdministrativeActionIssue(
                "instanceChanged",
                "The workflow instance changed after selection.",
                stateId: instance.Id));
        }
        if (task.UpdatedAt != request.ExpectedUserTaskUpdatedAt)
        {
            issues.Add(AdministrativeActionIssue(
                "taskChanged",
                "The user task changed after selection.",
                stateId: task.Id,
                nodeId: task.NodeId));
        }
        if (task.TokenId != request.ExpectedTokenId)
        {
            issues.Add(AdministrativeActionIssue(
                "tokenChanged",
                "The execution token changed after selection.",
                stateId: task.Id,
                nodeId: task.NodeId));
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
            || openTasks[0].Status != UserTaskRecordStatuses.Active
            || activeTokens[0].Id != task.TokenId
            || activeTokens[0].NodeId != task.NodeId
            || task.MultiInstanceExecutionId is not null)
        {
            issues.Add(AdministrativeActionIssue(
                "singlePositionRequired",
                "Administrative batch execution requires exactly one active token and one active ordinary user task."));
        }

        WorkflowDefinitionRecord workflow;
        try
        {
            workflow = await GetWorkflowAsync(
                request.ExpectedWorkflowDefinitionId,
                cancellationToken);
        }
        catch (WorkflowDomainException exception)
        {
            issues.Add(AdministrativeActionIssue("workflowMissing", exception.Message));
            return new AdministrativeActionEligibilityDto(false, issues);
        }

        var source = workflow.Definition.FlowNodes.SingleOrDefault(
            node => node.Id == task.NodeId);
        var flow = workflow.Definition.SequenceFlows.SingleOrDefault(
            candidate => candidate.Id == request.FlowId
                         && candidate.SourceRef == task.NodeId);
        if (source is null
            || flow is null
            || !IsAdministrativeBatchFlowContract(workflow.Definition, source, flow))
        {
            issues.Add(AdministrativeActionIssue(
                "actionNotAvailable",
                $"Flow #{request.FlowId} is not a compatible administrative batch action from the selected task.",
                flowId: request.FlowId));
            return new AdministrativeActionEligibilityDto(false, issues);
        }

        var actorRoles = NormalizeRoles(actor.Roles);
        if (!RoleAllowed(flow.Roles, actorRoles))
        {
            issues.Add(AdministrativeActionIssue(
                "flowRoleRequired",
                "The operator does not have a role permitted for this flow.",
                flowId: flow.Id));
        }

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var taskInstance = instance with
        {
            ActiveTokenId = task.TokenId,
            CurrentStepId = task.NodeId,
            ActiveUserTaskId = task.Id,
            ClaimedBy = task.ClaimedBy
        };
        var context = WithContext(
            stored,
            actor,
            taskInstance,
            workflow.Definition,
            source);
        if (!string.IsNullOrWhiteSpace(flow.Condition)
            && !SequenceFlowConditionEvaluator.Evaluate(flow.Condition, context))
        {
            issues.Add(AdministrativeActionIssue(
                "conditionNotSatisfied",
                $"Flow #{flow.Id} condition is not satisfied.",
                flowId: flow.Id));
        }
        try
        {
            ValidateVariableValues(flow.Variables, request.Variables);
            _ = ResolveAndValidateVariables(flow.Variables, request.Variables, context);
        }
        catch (WorkflowDomainException exception)
        {
            issues.Add(AdministrativeActionIssue(
                "invalidVariables",
                exception.Message,
                flowId: flow.Id));
        }

        return new AdministrativeActionEligibilityDto(issues.Count == 0, issues);
    }

    public async Task<AdministrativeActionResultDto?>
        ExecuteAdministrativeBatchFlowAsync(
            long taskId,
            AdministrativeActionRequest request,
            ActorContext actor,
            long administrativeActionBatchId,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (administrativeActionBatchId <= 0)
        {
            throw new WorkflowDomainException(
                "Administrative action batch id must be greater than zero.");
        }
        var validationIssues = new List<AdministrativeActionIssueDto>();
        if (!TryValidateAdministrativeBatchRequest(taskId, request, validationIssues))
        {
            throw new WorkflowDomainException(validationIssues[0].Message);
        }
        await AuthorizeAdministrativeBatchRoleAsync(actor, cancellationToken);

        var initialTask = await runtime.GetUserTaskAsync(taskId, false, cancellationToken);
        if (initialTask is null)
        {
            return null;
        }

        var detail = await TakeFlowCoreAsync(
            initialTask.InstanceId,
            request.FlowId,
            actor,
            request.Variables,
            taskId,
            cancellationToken,
            administrativeBatch: new AdministrativeBatchFlowContext(
                administrativeActionBatchId,
                request.ExpectedWorkflowDefinitionId,
                request.ExpectedTokenId!.Value,
                request.ExpectedInstanceUpdatedAt,
                request.ExpectedUserTaskUpdatedAt!.Value,
                request.Reason.Trim()));
        if (detail is null)
        {
            return null;
        }

        var newTaskRecord = (await runtime.ListUserTasksAsync(
                detail.Id,
                UserTaskRecordStatuses.Active,
                cancellationToken))
            .Where(candidate => candidate.TokenId == request.ExpectedTokenId
                                && candidate.Id != taskId)
            .OrderByDescending(candidate => candidate.Id)
            .FirstOrDefault();
        var newTask = newTaskRecord is null
            ? null
            : await BuildUserTaskDtoAsync(newTaskRecord, actor, cancellationToken);

        logger.LogInformation(
            "Administrative batch {BatchId} moved instance {InstanceId} through flow {FlowId} from task {TaskId} by {Actor}.",
            administrativeActionBatchId,
            detail.Id,
            request.FlowId,
            taskId,
            actor.User ?? "anonymous");
        return new AdministrativeActionResultDto(
            detail,
            taskId,
            newTask,
            administrativeActionBatchId);
    }

    private async Task AuthorizeAdministrativeBatchRoleAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.User))
        {
            throw new WorkflowUnauthorizedException(
                "An authenticated administrative batch operator is required.");
        }
        var setting = await engineSettings.GetByKeyAsync(
            AdministrativeActionConstraints.BatchRequiredRoleSetting,
            cancellationToken);
        var requiredRoles = WorkflowJobOperationsService.ParseRoles(setting?.Value);
        if (!actor.Roles.Any(role => !string.IsNullOrWhiteSpace(role)
                                     && requiredRoles.Contains(
                                         role.Trim(),
                                         StringComparer.OrdinalIgnoreCase)))
        {
            throw new WorkflowForbiddenException(
                $"A {AdministrativeActionConstraints.BatchRequiredRoleSetting} role is required.");
        }
    }

    private static bool IsAdministrativeBatchFlowContract(
        WorkflowModel definition,
        FlowNodeModel source,
        SequenceFlowModel flow)
    {
        if (!flow.IsSelectable
            || flow.IsDefault
            || flow.SourceRef != source.Id
            || !BpmnFlowNodeTypes.IsUserTask(source.Type)
            || source.MultiInstance is not null
            || source.AsyncAfter
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

    private static bool TryValidateAdministrativeBatchRequest(
        long taskId,
        AdministrativeActionRequest request,
        ICollection<AdministrativeActionIssueDto> issues)
    {
        if (taskId <= 0
            || request.ExpectedWorkflowDefinitionId <= 0
            || request.FlowId <= 0
            || request.ExpectedTokenId is not > 0
            || request.ExpectedInstanceUpdatedAt == default
            || request.ExpectedUserTaskUpdatedAt is not DateTimeOffset taskUpdatedAt
            || taskUpdatedAt == default)
        {
            issues.Add(AdministrativeActionIssue(
                "invalidExpectedState",
                "Task, workflow definition, flow, token, and optimistic-concurrency values are required."));
            return false;
        }
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length == 0
            || reason.EnumerateRunes().Count() > AdministrativeActionConstraints.MaxReasonLength)
        {
            issues.Add(AdministrativeActionIssue(
                "invalidReason",
                $"Reason must contain 1 to {AdministrativeActionConstraints.MaxReasonLength} characters."));
            return false;
        }
        return true;
    }

    private static AdministrativeActionIssueDto AdministrativeActionIssue(
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
}
