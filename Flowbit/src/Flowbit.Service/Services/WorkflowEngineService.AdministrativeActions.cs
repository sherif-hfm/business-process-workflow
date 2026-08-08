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
        AdministrativeActionRequest Request)
    {
        public long BatchId => Request.BatchId;
        public long BatchItemId => Request.BatchItemId;
        public string? Reason => NormalizeOptionalReason(Request.Reason);
    }

    private sealed record AdministrativeInitialTraversal(
        int SourceNodeId,
        int FlowId,
        long? UserTaskId,
        long? MultiInstanceExecutionId,
        Dictionary<string, JsonElement>? Values,
        AdministrativeBatchFlowContext AdministrativeBatch);

    private sealed record AdministrativePositionState(
        WorkflowInstanceRecord Instance,
        WorkflowDefinitionRecord Workflow,
        FlowNodeModel Source,
        ExecutionTokenRecord Token,
        UserTaskRecord? UserTask,
        MultiInstanceExecutionRecord? MultiInstance,
        IReadOnlyList<UserTaskRecord> OpenMultiInstanceTasks,
        SequenceFlowModel Flow,
        FlowNodeModel? Boundary,
        TimerSubscriptionRecord? TimerSubscription,
        WorkflowJobRecord? TimerJob,
        int AffectedTaskCount);

    public async Task<AdministrativeActionEligibilityDto>
        PreviewAdministrativeBatchActionAsync(
            AdministrativeActionRequest request,
            ActorContext actor,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAuthenticatedAdministrativeOperator(actor);
        await LoadSettingsAsync(cancellationToken);

        var issues = new List<AdministrativeActionIssueDto>();
        if (!TryValidateAdministrativeRequest(request, issues))
        {
            return new AdministrativeActionEligibilityDto(false, 0, issues);
        }

        AdministrativePositionState? state = null;
        try
        {
            state = await LoadAdministrativePositionAsync(
                request,
                forUpdate: false,
                validateTimerFence: true,
                cancellationToken);
            await ValidateAdministrativeVariablesAsync(
                state,
                request,
                actor,
                cancellationToken);
        }
        catch (Exception exception) when (exception is
            WorkflowDomainException or WorkflowConflictException)
        {
            issues.Add(AdministrativeActionIssue(
                exception is WorkflowConflictException ? "stalePosition" : "actionNotAvailable",
                exception.Message,
                stateId: request.PositionId,
                nodeId: request.SourceNodeId,
                flowId: request.FlowId));
        }

        return new AdministrativeActionEligibilityDto(
            issues.Count == 0,
            state?.AffectedTaskCount ?? 0,
            issues);
    }

    public async Task<AdministrativeActionResultDto?>
        ExecuteAdministrativeBatchActionAsync(
            AdministrativeActionRequest request,
            ActorContext actor,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAuthenticatedAdministrativeOperator(actor);
        var validationIssues = new List<AdministrativeActionIssueDto>();
        if (!TryValidateAdministrativeRequest(request, validationIssues))
        {
            throw new WorkflowDomainException(validationIssues[0].Message);
        }

        if (request.ActionKind == AdministrativeActionKinds.DirectFlow
            && request.PositionKind == AdministrativeActionPositionKinds.UserTask)
        {
            if (request.UserTaskId is not long taskId)
            {
                throw new WorkflowDomainException(
                    "An ordinary administrative position requires UserTaskId.");
            }
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
                administrativeBatch: new AdministrativeBatchFlowContext(request));
            return detail is null
                ? null
                : new AdministrativeActionResultDto(
                    detail,
                    request.PositionKind,
                    request.PositionId,
                    1,
                    request.BatchId);
        }

        return request.ActionKind switch
        {
            AdministrativeActionKinds.DirectFlow =>
                await ExecuteAdministrativeMultiInstanceFlowAsync(
                    request,
                    actor,
                    cancellationToken),
            AdministrativeActionKinds.TimerBoundary =>
                await ExecuteAdministrativeTimerBoundaryAsync(
                    request,
                    actor,
                    cancellationToken),
            _ => throw new WorkflowDomainException(
                $"Unknown administrative action kind '{request.ActionKind}'.")
        };
    }

    private async Task<AdministrativeActionResultDto>
        ExecuteAdministrativeMultiInstanceFlowAsync(
            AdministrativeActionRequest request,
            ActorContext actor,
            CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var state = await LoadAdministrativePositionAsync(
            request,
            forUpdate: true,
            validateTimerFence: false,
            cancellationToken);
        if (state.MultiInstance is null)
        {
            throw new WorkflowConflictException(
                "The selected multi-instance execution is no longer active.");
        }

        var flowValues = await ResolveAdministrativeVariablesAsync(
            state,
            request,
            actor,
            cancellationToken);
        var context = await BuildAdministrativeFlowContextAsync(
            state,
            actor,
            flowValues,
            cancellationToken);
        foreach (var pair in flowValues)
        {
            await runtime.AddVariableAsync(
                state.Instance.Id,
                pair.Key,
                state.Flow.Id,
                actor.User,
                pair.Value,
                cancellationToken);
        }

        var flowInfo = await LoadSequenceFlowInfoAsync(
            state.Instance.Id,
            state.Workflow.Definition,
            cancellationToken,
            force: true);
        var administrative = new AdministrativeBatchFlowContext(request);
        if (request.MultiInstanceMode
            == AdministrativeActionMultiInstanceModes.CompleteAllChildren)
        {
            foreach (var task in state.OpenMultiInstanceTasks.OrderBy(item => item.ItemIndex))
            {
                await runtime.CompleteMultiInstanceItemAsync(
                    task.Id,
                    state.Flow.Id,
                    NormalizeUser(actor.User),
                    SnapshotRoles(actor.Roles),
                    flowValues,
                    cancellationToken,
                    actingFor: actor.ActingFor,
                    delegationId: actor.DelegationId,
                    completionKind: NodeExecutionCompletionReasons.AdministrativeAction,
                    completionReason: administrative.Reason,
                    administrativeActionBatchId: request.BatchId);
                await RecordSequenceFlowOccurrenceAsync(
                    flowInfo,
                    state.Instance.Id,
                    state.Token.Id,
                    task.Id,
                    state.MultiInstance.Id,
                    task.ItemIndex,
                    state.Flow,
                    NodeExecutionCompletionReasons.AdministrativeAction,
                    isAction: true,
                    isTraversal: false,
                    actor,
                    flowValues,
                    cancellationToken,
                    administrative);
                await runtime.AddMultiInstanceHistoryAsync(
                    state.Instance.Id,
                    state.Token.Id,
                    task.Id,
                    state.MultiInstance.Id,
                    task.ItemIndex,
                    state.Flow.Id,
                    state.Source.Id,
                    state.Source.Id,
                    actor.User,
                    CloneDictionary(flowValues),
                    NodeExecutionCompletionReasons.AdministrativeAction,
                    cancellationToken,
                    actor.ActingFor,
                    actor.DelegationId,
                    administrative.Reason,
                    request.BatchId);
            }
        }

        var forceParent = request.MultiInstanceMode
            == AdministrativeActionMultiInstanceModes.ForceParent;
        WorkflowInstanceRecord advanced;
        try
        {
            advanced = await CloseAndAdvanceMultiInstanceAsync(
                state.MultiInstance,
                state.Instance,
                state.Workflow,
                state.Source,
                state.Flow,
                forceParent ? "interrupt" : "all",
                actor,
                flowValues,
                context,
                null,
                null,
                flowInfo,
                forceParent,
                cancellationToken,
                administrative);
        }
        catch (WorkflowDomainException exception)
        {
            throw new AdministrativeActionExecutionException(exception.Message, exception);
        }

        await CompleteAdministrativeBatchItemAsync(
            request,
            state.Instance.Id,
            state.AffectedTaskCount,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var detail = await BuildDetailAsync(advanced.Id, cancellationToken)
            ?? throw new WorkflowConflictException(
                "The workflow instance disappeared after administrative execution.");
        return new AdministrativeActionResultDto(
            detail,
            request.PositionKind,
            request.PositionId,
            state.AffectedTaskCount,
            request.BatchId);
    }

    private async Task<AdministrativeActionResultDto>
        ExecuteAdministrativeTimerBoundaryAsync(
            AdministrativeActionRequest request,
            ActorContext actor,
            CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var state = await LoadAdministrativePositionAsync(
            request,
            forUpdate: true,
            validateTimerFence: true,
            cancellationToken);
        var boundary = state.Boundary
            ?? throw new WorkflowDomainException(
                "The selected timer boundary no longer exists.");
        var subscription = state.TimerSubscription
            ?? throw new WorkflowConflictException(
                "The selected timer subscription is no longer available.");
        var timerJob = state.TimerJob
            ?? throw new WorkflowConflictException(
                "The selected timer job is no longer available.");
        if (!await timerSubscriptions.CompleteAdministrativeOverrideAsync(
                subscription.Id,
                request.ExpectedTimerOccurrence!.Value,
                request.ExpectedTimerStatus!,
                request.ExpectedTimerSubscriptionUpdatedAt!.Value,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The timer subscription changed before the administrative override executed.");
        }

        var tokenIds = new[] { state.Token.Id };
        var completionActor = ToNodeExecutionActor(actor);
        await runtime.CancelOpenUserTasksForTokensAsync(
            tokenIds,
            NodeExecutionCompletionReasons.AdministrativeAction,
            completionActor,
            cancellationToken,
            completionKind: NodeExecutionCompletionReasons.AdministrativeAction,
            administrativeReason: NormalizeOptionalReason(request.Reason),
            administrativeActionBatchId: request.BatchId);
        await runtime.CancelActiveMultiInstancesForTokensAsync(
            tokenIds,
            NodeExecutionCompletionReasons.AdministrativeAction,
            completionActor,
            cancellationToken);
        await timerSubscriptions.CancelOtherForTokenAsync(
            state.Instance.Id,
            state.Token.Id,
            subscription.Id,
            cancellationToken);
        await jobs.CancelTimerJobsByTokenIdsAsync(
            state.Instance.Id,
            tokenIds,
            exceptJobId: null,
            "administrativeTimerBoundary",
            cancellationToken);

        if (state.UserTask is not null)
        {
            await runtime.AddUserTaskActionHistoryAsync(
                state.Instance.Id,
                state.Token.Id,
                state.UserTask.Id,
                state.Flow.Id,
                state.Source.Id,
                state.Flow.TargetRef,
                NormalizeUser(actor.User),
                [],
                cancellationToken,
                actor.ActingFor,
                actor.DelegationId,
                NodeExecutionCompletionReasons.AdministrativeAction,
                NormalizeOptionalReason(request.Reason),
                request.BatchId);
        }
        else if (state.MultiInstance is not null)
        {
            await runtime.AddMultiInstanceHistoryAsync(
                state.Instance.Id,
                state.Token.Id,
                null,
                state.MultiInstance.Id,
                null,
                state.Flow.Id,
                state.Source.Id,
                state.Flow.TargetRef,
                actor.User,
                null,
                NodeExecutionCompletionReasons.AdministrativeAction,
                cancellationToken,
                actor.ActingFor,
                actor.DelegationId,
                NormalizeOptionalReason(request.Reason),
                request.BatchId);
        }

        await runtime.UpdateExecutionTokenAsync(
            state.Token.Id,
            ToSnapshot(boundary),
            ExecutionTokenRecordStatuses.Active,
            state.Token.GatewayBranchId,
            null,
            null,
            null,
            completionActor,
            new NodeExecutionCompletionRecord(
                NodeExecutionRecordStatuses.Cancelled,
                NodeExecutionCompletionReasons.AdministrativeAction,
                null,
                null,
                state.Token.GatewayBranchId,
                completionActor),
            cancellationToken,
            automaticActivationCount:
                WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger());

        var flowInfo = await LoadSequenceFlowInfoAsync(
            state.Instance.Id,
            state.Workflow.Definition,
            cancellationToken,
            force: true);
        var administrative = new AdministrativeBatchFlowContext(request);
        try
        {
            var resumed = await ResolvePassThroughAsync(
                state.Instance,
                state.Workflow.Definition,
                actor,
                flowInfo,
                state.Token.Id,
                cancellationToken,
                forceDurableActivities: true,
                administrativeInitialTraversal: new AdministrativeInitialTraversal(
                    boundary.Id,
                    state.Flow.Id,
                    state.UserTask?.Id,
                    state.MultiInstance?.Id,
                    null,
                    administrative));
            await EnsureMultiInstanceInitializedAsync(
                resumed,
                state.Workflow.Definition,
                actor,
                cancellationToken);
            _ = await ApplyUserTaskOwnershipInheritanceAsync(
                resumed,
                state.Workflow.Definition,
                cancellationToken);
        }
        catch (WorkflowDomainException exception)
        {
            throw new AdministrativeActionExecutionException(exception.Message, exception);
        }

        await CompleteAdministrativeBatchItemAsync(
            request,
            state.Instance.Id,
            state.AffectedTaskCount,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var detail = await BuildDetailAsync(state.Instance.Id, cancellationToken)
            ?? throw new WorkflowConflictException(
                "The workflow instance disappeared after the timer override.");
        logger.LogInformation(
            "Administrative batch {BatchId} fired timer boundary {BoundaryNodeId} early on token {TokenId} (job {TimerJobId}).",
            request.BatchId,
            boundary.Id,
            state.Token.Id,
            timerJob.Id);
        return new AdministrativeActionResultDto(
            detail,
            request.PositionKind,
            request.PositionId,
            state.AffectedTaskCount,
            request.BatchId);
    }

    private async Task<AdministrativePositionState> LoadAdministrativePositionAsync(
        AdministrativeActionRequest request,
        bool forUpdate,
        bool validateTimerFence,
        CancellationToken cancellationToken)
    {
        UserTaskRecord? task = null;
        MultiInstanceExecutionRecord? execution = null;
        long instanceId;
        if (request.PositionKind == AdministrativeActionPositionKinds.UserTask)
        {
            if (request.UserTaskId is not long taskId
                || request.PositionId != taskId
                || request.MultiInstanceExecutionId is not null)
            {
                throw new WorkflowDomainException(
                    "The ordinary position identity is invalid.");
            }
            task = await runtime.GetUserTaskAsync(taskId, false, cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The selected user task no longer exists.");
            instanceId = task.InstanceId;
        }
        else
        {
            if (request.MultiInstanceExecutionId is not long executionId
                || request.PositionId != executionId
                || request.UserTaskId is not null)
            {
                throw new WorkflowDomainException(
                    "The multi-instance position identity is invalid.");
            }
            execution = await runtime.GetMultiInstanceAsync(
                    executionId,
                    false,
                    cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The selected multi-instance execution no longer exists.");
            instanceId = execution.InstanceId;
        }

        WorkflowInstanceRecord instance;
        if (forUpdate)
        {
            instance = await runtime.GetInstanceForUpdateAsync(
                    instanceId,
                    lockActiveUserTask: true,
                    cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The workflow instance no longer exists.");
            if (task is not null)
            {
                task = await runtime.GetUserTaskAsync(task.Id, true, cancellationToken);
            }
            if (execution is not null)
            {
                execution = await runtime.GetMultiInstanceAsync(
                    execution.Id,
                    true,
                    cancellationToken);
            }
        }
        else
        {
            instance = await runtime.GetInstanceAsync(instanceId, cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The workflow instance no longer exists.");
        }

        if (instance.Status != WorkflowInstanceStatuses.Running)
        {
            throw new WorkflowConflictException(
                "Only running workflow positions can take an administrative action.");
        }
        if (instance.WorkflowDefinitionId != request.ExpectedWorkflowDefinitionId)
        {
            throw new WorkflowConflictException(
                "The workflow definition changed after batch selection.");
        }

        var token = await runtime.GetExecutionTokenAsync(
                request.ExpectedTokenId,
                forUpdate,
                cancellationToken)
            ?? throw new WorkflowConflictException(
                "The selected execution token no longer exists.");
        if (token.InstanceId != instance.Id
            || token.Status != ExecutionTokenRecordStatuses.Active
            || token.ActivationId != request.ExpectedTokenActivationId
            || token.NodeId != request.SourceNodeId)
        {
            throw new WorkflowConflictException(
                "The selected token activation is no longer at the requested source node.");
        }

        IReadOnlyList<UserTaskRecord> openTasks = [];
        if (task is not null)
        {
            if (task.InstanceId != instance.Id
                || task.TokenId != token.Id
                || task.NodeId != request.SourceNodeId
                || task.MultiInstanceExecutionId is not null
                || task.Status != UserTaskRecordStatuses.Active
                || task.UpdatedAt != request.ExpectedPositionUpdatedAt)
            {
                throw new WorkflowConflictException(
                    "The selected ordinary user-task position changed after selection.");
            }
        }
        else
        {
            if (execution is null
                || execution.InstanceId != instance.Id
                || execution.TokenId != token.Id
                || execution.NodeId != request.SourceNodeId
                || execution.Status != MultiInstanceRecordStatuses.Active
                || execution.UpdatedAt != request.ExpectedPositionUpdatedAt)
            {
                throw new WorkflowConflictException(
                    "The selected multi-instance position changed after selection.");
            }
            openTasks = (await runtime.ListExecutionTasksAsync(
                    execution.Id,
                    cancellationToken))
                .Where(item => item.Status is
                    UserTaskRecordStatuses.Active or UserTaskRecordStatuses.Pending)
                .OrderBy(item => item.ItemIndex)
                .ToList();
            if (openTasks.Count == 0)
            {
                throw new WorkflowConflictException(
                    "The multi-instance execution has no unfinished children.");
            }
        }

        var workflow = await GetWorkflowAsync(
            request.ExpectedWorkflowDefinitionId,
            cancellationToken);
        var source = GetFlowNode(workflow.Definition, request.SourceNodeId);
        if (!BpmnFlowNodeTypes.IsUserTask(source.Type)
            || (task is null) != (source.MultiInstance is not null))
        {
            throw new WorkflowDomainException(
                "The selected source node is not the expected ordinary or multi-instance user task.");
        }

        SequenceFlowModel flow;
        FlowNodeModel? boundary = null;
        TimerSubscriptionRecord? subscription = null;
        WorkflowJobRecord? timerJob = null;
        if (request.ActionKind == AdministrativeActionKinds.DirectFlow)
        {
            flow = OutgoingFlows(workflow.Id, workflow.Definition, source.Id)
                .SingleOrDefault(candidate => candidate.Id == request.FlowId)
                ?? throw new WorkflowDomainException(
                    "The selected flow is not authored from the source user task.");
            if (!flow.IsSelectable || flow.IsDefault)
            {
                throw new WorkflowDomainException(
                    "Default and engine-only flows cannot be selected administratively.");
            }
            if (request.BoundaryNodeId is not null
                || HasAnyTimerFence(request))
            {
                throw new WorkflowDomainException(
                    "A direct-flow action cannot include timer-boundary identity.");
            }
            ValidateAdministrativeMultiInstanceMode(request, task is null);
        }
        else
        {
            boundary = workflow.Definition.FlowNodes.SingleOrDefault(candidate =>
                candidate.Id == request.BoundaryNodeId
                && BpmnFlowNodeTypes.IsTimerBoundary(candidate.Type)
                && candidate.AttachedToRef == source.Id)
                ?? throw new WorkflowDomainException(
                    "The selected timer boundary is not attached to the source user task.");
            flow = OutgoingFlows(workflow.Id, workflow.Definition, boundary.Id)
                .SingleOrDefault(candidate => candidate.Id == request.FlowId)
                ?? throw new WorkflowDomainException(
                    "The selected flow is not the timer boundary continuation.");
            if (request.MultiInstanceMode is not null)
            {
                throw new WorkflowDomainException(
                    "Timer-boundary actions always interrupt the host and do not accept a multi-instance mode.");
            }
            if (request.Variables is { Count: > 0 } || flow.Variables.Count > 0)
            {
                throw new WorkflowDomainException(
                    "Timer-boundary actions do not accept flow variables.");
            }
            if (validateTimerFence)
            {
                timerJob = forUpdate
                    ? await jobs.GetForUpdateAsync(
                        request.ExpectedTimerJobId!.Value,
                        cancellationToken)
                    : await jobs.GetAsync(
                        request.ExpectedTimerJobId!.Value,
                        cancellationToken);
                subscription = forUpdate
                    ? await timerSubscriptions.GetForUpdateAsync(
                        request.ExpectedTimerSubscriptionId!.Value,
                        cancellationToken)
                    : await timerSubscriptions.GetAsync(
                        request.ExpectedTimerSubscriptionId!.Value,
                        cancellationToken);
                ValidateAdministrativeTimerSubscription(
                    request,
                    instance,
                    token,
                    boundary,
                    subscription);
                ValidateAdministrativeTimerJob(
                    request,
                    instance,
                    token,
                    boundary,
                    subscription!,
                    timerJob);
            }
        }

        return new AdministrativePositionState(
            instance,
            workflow,
            source,
            token,
            task,
            execution,
            openTasks,
            flow,
            boundary,
            subscription,
            timerJob,
            task is null ? openTasks.Count : 1);
    }

    private async Task ValidateAdministrativeVariablesAsync(
        AdministrativePositionState state,
        AdministrativeActionRequest request,
        ActorContext actor,
        CancellationToken cancellationToken) =>
        _ = await ResolveAdministrativeVariablesAsync(
            state,
            request,
            actor,
            cancellationToken);

    private async Task<Dictionary<string, JsonElement>>
        ResolveAdministrativeVariablesAsync(
            AdministrativePositionState state,
            AdministrativeActionRequest request,
            ActorContext actor,
            CancellationToken cancellationToken)
    {
        if (request.ActionKind == AdministrativeActionKinds.TimerBoundary)
        {
            return [];
        }
        var stored = await LoadVariablesAsync(state.Instance.Id, cancellationToken);
        var positioned = state.Instance with
        {
            ActiveTokenId = state.Token.Id,
            CurrentStepId = state.Token.NodeId,
            ActiveUserTaskId = state.UserTask?.Id,
            ClaimedBy = state.UserTask?.ClaimedBy
        };
        var context = WithContext(
            stored,
            actor,
            positioned,
            state.Workflow.Definition,
            state.Source);
        if (state.MultiInstance is not null
            && request.MultiInstanceMode
            == AdministrativeActionMultiInstanceModes.CompleteAllChildren)
        {
            Dictionary<string, JsonElement>? common = null;
            foreach (var task in state.OpenMultiInstanceTasks)
            {
                var childContext = new Dictionary<string, JsonElement>(
                    context,
                    StringComparer.OrdinalIgnoreCase);
                AddMultiInstanceContext(childContext, task, state.MultiInstance);
                ValidateVariableValues(state.Flow.Variables, request.Variables);
                var childValues = ResolveAndValidateVariables(
                    state.Flow.Variables,
                    request.Variables,
                    childContext);
                if (common is null)
                {
                    common = childValues;
                }
                else if (!AdministrativeValuesEqual(common, childValues))
                {
                    throw new WorkflowDomainException(
                        "The common payload resolves to different flow-variable values for unfinished multi-instance children.");
                }
            }
            return common ?? [];
        }
        if (state.MultiInstance is not null)
        {
            AddMultiInstanceExecutionContext(context, state.MultiInstance);
        }
        ValidateVariableValues(state.Flow.Variables, request.Variables);
        return ResolveAndValidateVariables(
            state.Flow.Variables,
            request.Variables,
            context);
    }

    private static bool AdministrativeValuesEqual(
        IReadOnlyDictionary<string, JsonElement> left,
        IReadOnlyDictionary<string, JsonElement> right) =>
        left.Count == right.Count
        && left.All(pair =>
            right.TryGetValue(pair.Key, out var value)
            && JsonElement.DeepEquals(pair.Value, value));

    private async Task<Dictionary<string, JsonElement>>
        BuildAdministrativeFlowContextAsync(
            AdministrativePositionState state,
            ActorContext actor,
            Dictionary<string, JsonElement> flowValues,
            CancellationToken cancellationToken)
    {
        var stored = await LoadVariablesAsync(state.Instance.Id, cancellationToken);
        var positioned = state.Instance with
        {
            ActiveTokenId = state.Token.Id,
            CurrentStepId = state.Token.NodeId,
            ActiveUserTaskId = state.UserTask?.Id,
            ClaimedBy = state.UserTask?.ClaimedBy
        };
        var context = WithContext(
            stored,
            actor,
            positioned,
            state.Workflow.Definition,
            state.Source);
        if (state.MultiInstance is not null)
        {
            AddMultiInstanceExecutionContext(context, state.MultiInstance);
        }
        foreach (var pair in flowValues)
        {
            context[pair.Key] = pair.Value;
        }
        return context;
    }

    private async Task CompleteAdministrativeBatchItemAsync(
        AdministrativeActionRequest request,
        long instanceId,
        int affectedTaskCount,
        CancellationToken cancellationToken)
    {
        // Flush the routed workflow state inside the still-open transaction so
        // the item result is derived from the committed-shape projection, not
        // from the authored target. A later item-update failure still rolls
        // this save back with the workflow transition.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var instance = await runtime.GetInstanceAsync(instanceId, cancellationToken)
            ?? throw new WorkflowConflictException(
                "The workflow instance disappeared before the administrative result was recorded.");
        var projection = await BuildExecutionProjectionAsync(instance, cancellationToken);
        await runtime.CompleteAdministrativeActionBatchItemAsync(
            request.BatchItemId,
            request.BatchId,
            instanceId,
            request.PositionKind,
            request.PositionId,
            request.ExpectedTokenId,
            request.ExpectedTokenActivationId,
            request.ExpectedWorkflowDefinitionId,
            request.SourceNodeId,
            request.FlowId,
            affectedTaskCount,
            JsonSerializer.SerializeToElement(new
            {
                workflowDefinitionId = request.ExpectedWorkflowDefinitionId,
                sourceNodeId = request.SourceNodeId,
                actionKind = request.ActionKind,
                selectedFlowId = request.FlowId,
                boundaryNodeId = request.BoundaryNodeId,
                multiInstanceMode = request.MultiInstanceMode,
                positionKind = request.PositionKind,
                positionId = request.PositionId,
                affectedTaskCount,
                instanceStatus = instance.Status,
                currentNodeId = instance.CurrentStepId,
                executionPositions = projection.ExecutionPositions,
                completion = projection.Completion
            }),
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private static void ValidateAdministrativeMultiInstanceMode(
        AdministrativeActionRequest request,
        bool isMultiInstance)
    {
        if (isMultiInstance)
        {
            if (!AdministrativeActionMultiInstanceModes.IsKnown(
                    request.MultiInstanceMode))
            {
                throw new WorkflowDomainException(
                    "A direct multi-instance action requires forceParent or completeAllChildren mode.");
            }
        }
        else if (request.MultiInstanceMode is not null)
        {
            throw new WorkflowDomainException(
                "An ordinary user-task action cannot specify a multi-instance mode.");
        }
    }

    private static void ValidateAdministrativeTimerSubscription(
        AdministrativeActionRequest request,
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel boundary,
        TimerSubscriptionRecord? subscription)
    {
        if (subscription is null
            || subscription.Id != request.ExpectedTimerSubscriptionId
            || subscription.InstanceId != instance.Id
            || subscription.WorkflowDefinitionId != instance.WorkflowDefinitionId
            || subscription.TokenId != token.Id
            || subscription.ActivationId != token.ActivationId
            || subscription.TimerNodeId != boundary.Id
            || subscription.AttachedToNodeId != token.NodeId
            || subscription.Occurrence != request.ExpectedTimerOccurrence
            || subscription.Status != request.ExpectedTimerStatus
            || subscription.UpdatedAt != request.ExpectedTimerSubscriptionUpdatedAt
            || subscription.Status is not (
                TimerSubscriptionStatuses.Active or TimerSubscriptionStatuses.Paused))
        {
            throw new WorkflowConflictException(
                "The timer subscription changed or is no longer active/paused.");
        }
    }

    private static void ValidateAdministrativeTimerJob(
        AdministrativeActionRequest request,
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel boundary,
        TimerSubscriptionRecord subscription,
        WorkflowJobRecord? job)
    {
        if (job is null
            || job.Id != request.ExpectedTimerJobId
            || job.InstanceId != instance.Id
            || job.WorkflowDefinitionId != instance.WorkflowDefinitionId
            || job.TokenId != token.Id
            || job.ActivationId != token.ActivationId
            || job.TimerSubscriptionId != subscription.Id
            || job.NodeId != boundary.Id
            || job.Kind != WorkflowJobKinds.TimerBoundary
            || job.Status is WorkflowJobStatuses.Completed
                or WorkflowJobStatuses.Cancelled
                or WorkflowJobStatuses.Skipped)
        {
            throw new WorkflowConflictException(
                "The timer job changed or is no longer open for this subscription.");
        }
    }

    private static bool TryValidateAdministrativeRequest(
        AdministrativeActionRequest request,
        ICollection<AdministrativeActionIssueDto> issues)
    {
        if (request.BatchId <= 0
            || request.BatchItemId <= 0
            || request.ExpectedWorkflowDefinitionId <= 0
            || request.SourceNodeId <= 0
            || request.FlowId <= 0
            || request.PositionId <= 0
            || request.ExpectedTokenId <= 0
            || request.ExpectedTokenActivationId == Guid.Empty
            || request.ExpectedPositionUpdatedAt == default
            || !AdministrativeActionKinds.IsKnown(request.ActionKind)
            || !AdministrativeActionPositionKinds.IsKnown(request.PositionKind))
        {
            issues.Add(AdministrativeActionIssue(
                "invalidExpectedState",
                "Batch item, workflow, action, position, token activation, and position timestamp are required."));
            return false;
        }
        var reason = NormalizeOptionalReason(request.Reason);
        if (reason is not null
            && reason.EnumerateRunes().Count()
            > AdministrativeActionConstraints.MaxReasonLength)
        {
            issues.Add(AdministrativeActionIssue(
                "invalidReason",
                $"Reason cannot exceed {AdministrativeActionConstraints.MaxReasonLength} characters."));
            return false;
        }
        if (request.ActionKind == AdministrativeActionKinds.TimerBoundary
            && (request.BoundaryNodeId is not > 0
                || request.ExpectedTimerSubscriptionId is not > 0
                || request.ExpectedTimerJobId is not > 0
                || request.ExpectedTimerOccurrence is not >= 0
                || request.ExpectedTimerSubscriptionUpdatedAt is not DateTimeOffset timerAt
                || timerAt == default
                || request.ExpectedTimerStatus is not (
                    TimerSubscriptionStatuses.Active or TimerSubscriptionStatuses.Paused)))
        {
            issues.Add(AdministrativeActionIssue(
                "invalidTimerFence",
                "A timer action requires exact boundary, subscription, job, occurrence, status, and timestamp fences."));
            return false;
        }
        return true;
    }

    private static bool HasAnyTimerFence(AdministrativeActionRequest request) =>
        request.ExpectedTimerSubscriptionId is not null
        || request.ExpectedTimerJobId is not null
        || request.ExpectedTimerOccurrence is not null
        || request.ExpectedTimerStatus is not null
        || request.ExpectedTimerSubscriptionUpdatedAt is not null;

    private static void EnsureAuthenticatedAdministrativeOperator(
        ActorContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.User))
        {
            throw new WorkflowUnauthorizedException(
                "An authenticated administrative batch operator is required.");
        }
    }

    private static string? NormalizeOptionalReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

    private static AdministrativeActionIssueDto AdministrativeActionIssue(
        string code,
        string message,
        long? stateId = null,
        int? nodeId = null,
        int? flowId = null) =>
        new(
            code,
            message,
            StateType: stateId is null ? null : "administrativeActionPosition",
            StateId: stateId,
            NodeId: nodeId,
            FlowId: flowId);
}
