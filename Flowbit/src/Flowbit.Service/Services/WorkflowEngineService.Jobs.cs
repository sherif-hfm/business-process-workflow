using System.Diagnostics;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public sealed partial class WorkflowEngineService
{
    private const int DefaultJobPriority = 0;
    private const int DefaultMaxSnapshotBytes = 1_048_576;
    private static readonly TimeSpan TimerMisfireGrace = TimeSpan.FromMinutes(1);
    private static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];
    private long? _processingJobId;
    private long? _processingJobInstanceId;

    private sealed record PassThroughResume(
        Guid ActivationId,
        string Phase,
        bool ActivityAlreadyExecuted,
        TaskExecutionOutcome? Outcome);

    private sealed record DurableJobPayload(
        string? User,
        IReadOnlyList<string> Roles,
        IReadOnlyDictionary<string, string> Claims,
        string? ActingFor,
        long? DelegationId,
        int? SelectedFlowId = null,
        AdministrativeActionRequest? AdministrativeAction = null,
        IReadOnlyList<string>? TriggeringVariableNames = null);

    private sealed record ConditionalWakeLatchRequest(
        ExecutionTokenRecord Token,
        FlowNodeModel Node,
        SequenceFlowModel SelectedFlow,
        IReadOnlyList<string> TriggeringVariableNames);

    private sealed record StagedServiceInvocation(
        string Method,
        string Url,
        IReadOnlyList<ServiceTaskHeader> Headers,
        string? Body,
        int TimeoutSeconds,
        string? Failure = null);

    private async Task EnsureAsyncBeforeWaitAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        if (token.WaitState == ExecutionTokenWaitStates.AsyncBefore
            && token.WaitingJobId is not null)
        {
            return;
        }
        EnsureTokenCanEnterWait(token);

        var job = await EnqueueInstanceJobAsync(
            BuildJobCreate(
                instance,
                token,
                node,
                WorkflowJobKinds.AsyncBefore,
                WorkflowJobKinds.AsyncBefore,
                actor,
                dueAt: timeProvider.GetUtcNow()),
            cancellationToken,
            token,
            countAutomaticActivation: IsGuardedAutomaticActivity(node));
        if (!await runtime.SetExecutionTokenWaitAsync(
                token.Id,
                token.ActivationId,
                ExecutionTokenWaitStates.AsyncBefore,
                job.Id,
                null,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The execution token changed while its async-before job was being created.");
        }
    }

    private async Task EnsureAsyncAfterWaitAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        CancellationToken cancellationToken,
        int? selectedFlowId = null,
        long? multiInstanceExecutionId = null,
        long? userTaskId = null,
        AdministrativeBatchFlowContext? administrativeBatch = null)
    {
        if (token.WaitState == ExecutionTokenWaitStates.AsyncAfter
            && token.WaitingJobId is not null)
        {
            return;
        }
        EnsureTokenCanEnterWait(token);

        if (token.CurrentNodeExecutionId is not null
            && !await runtime.CompleteCurrentNodeForWaitAsync(
                token.Id,
                token.ActivationId,
                new NodeExecutionCompletionRecord(
                    NodeExecutionRecordStatuses.Completed,
                    administrativeBatch is null
                        ? NodeExecutionCompletionReasons.Normal
                        : NodeExecutionCompletionReasons.AdministrativeAction,
                    selectedFlowId,
                    null,
                    token.GatewayBranchId,
                    ToNodeExecutionActor(actor)),
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The node execution changed while its async-after job was being created.");
        }

        var job = await EnqueueInstanceJobAsync(
            BuildJobCreate(
                instance,
                token,
                node,
                WorkflowJobKinds.AsyncAfter,
                WorkflowJobKinds.AsyncAfter,
                actor,
                dueAt: timeProvider.GetUtcNow(),
                selectedFlowId: selectedFlowId,
                multiInstanceExecutionId: multiInstanceExecutionId,
                userTaskId: userTaskId,
                administrativeBatch: administrativeBatch),
            cancellationToken,
            token,
            // A plain task can author asyncAfter without a durable entry job.
            // Service/script asyncAfter always stages through asyncBefore, and
            // user-task phases are deliberately outside the automatic guard.
            countAutomaticActivation:
                BpmnFlowNodeTypes.IsAutomatic(node.Type) && !node.AsyncBefore);
        if (!await runtime.SetExecutionTokenWaitAsync(
                token.Id,
                token.ActivationId,
                ExecutionTokenWaitStates.AsyncAfter,
                job.Id,
                null,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The execution token changed while its async-after job was being created.");
        }
    }

    private async Task LatchConditionalWakesAsync(
        WorkflowInstanceRecord instance,
        IReadOnlyList<ConditionalWakeLatchRequest> requests,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return;
        }

        var tokenIds = new HashSet<long>();
        foreach (var request in requests)
        {
            if (request.Token.InstanceId != instance.Id
                || !string.Equals(
                    request.Token.Status,
                    ExecutionTokenRecordStatuses.Active,
                    StringComparison.Ordinal)
                || request.Token.NodeId != request.Node.Id
                || !string.Equals(request.Token.NodeType, request.Node.Type, StringComparison.Ordinal)
                || !BpmnFlowNodeTypes.IsConditionalCatch(request.Node.Type)
                || request.Node.Conditional?.EffectiveDeliveryMode
                   != ConditionalEventDeliveryModes.DurableAsync
                || request.SelectedFlow.SourceRef != request.Node.Id)
            {
                throw new WorkflowJobInvariantException(
                    "A conditional-wake latch does not match its active token and selected flow.");
            }
            if (!tokenIds.Add(request.Token.Id))
            {
                throw new WorkflowJobInvariantException(
                    $"Conditional-wake token #{request.Token.Id} was selected more than once in one evaluation wave.");
            }
            EnsureTokenCanEnterWait(request.Token);
        }

        var dueAt = timeProvider.GetUtcNow();
        var creates = requests.Select(request => BuildJobCreate(
                instance,
                request.Token,
                request.Node,
                WorkflowJobKinds.ConditionalWake,
                WorkflowJobKinds.ConditionalWake,
                actor,
                dueAt,
                selectedFlowId: request.SelectedFlow.Id,
                triggeringVariableNames: request.TriggeringVariableNames) with
            {
                QueueClass = WorkflowJobClasses.Control
            })
            .ToArray();
        var created = await EnqueueInstanceJobsAsync(creates, cancellationToken);
        if (created.Count != requests.Count)
        {
            throw new WorkflowJobInvariantException(
                "The conditional-wake batch did not create every requested durable job.");
        }

        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var job = created[index];
            if (job.TokenId != request.Token.Id
                || job.ActivationId != request.Token.ActivationId
                || job.Kind != WorkflowJobKinds.ConditionalWake)
            {
                throw new WorkflowJobInvariantException(
                    "A persisted conditional-wake job does not match its token activation.");
            }
            if (!await runtime.SetExecutionTokenWaitAsync(
                    request.Token.Id,
                    request.Token.ActivationId,
                    ExecutionTokenWaitStates.ConditionalWake,
                    job.Id,
                    null,
                    cancellationToken))
            {
                throw new WorkflowConflictException(
                    "The execution token changed while its conditional wake was being latched.");
            }

            await runtime.AddTokenHistoryAsync(
                instance.Id,
                request.Token.Id,
                null,
                request.Node.Id,
                request.Node.Id,
                actor.User,
                ConditionalHistoryPayload(
                    ConditionalEventDeliveryModes.DurableAsync,
                    request.SelectedFlow.Id,
                    request.TriggeringVariableNames,
                    job.Id),
                InstanceHistoryNotes.ConditionalLatched,
                cancellationToken,
                actor.ActingFor,
                actor.DelegationId);
            logger.LogInformation(
                "Conditional event latched for instance {InstanceId}, token {TokenId}, node {NodeId}, job {JobId}.",
                instance.Id,
                request.Token.Id,
                request.Node.Id,
                job.Id);
        }
    }

    private async Task EnsureTimerCatchWaitAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        if (token.WaitState == ExecutionTokenWaitStates.TimerCatch
            && token.WaitingJobId is not null
            && token.WaitingTimerSubscriptionId is not null)
        {
            return;
        }
        EnsureTokenCanEnterWait(token);

        var timer = node.Timer
            ?? throw new WorkflowDomainException(
                $"Timer catch event #{node.Id} has no timer configuration.");
        var schedule = WorkflowTimerSchedule.Resolve(timer, timeProvider.GetUtcNow());
        var subscription = await timerSubscriptions.CreateAsync(
            BuildTimerSubscription(
                instance,
                token,
                node,
                schedule.FirstOccurrenceAt,
                attachedToNodeId: null,
                cancelActivity: true),
            cancellationToken);
        var job = await EnqueueInstanceJobAsync(
            BuildTimerJob(
                instance,
                token,
                node,
                subscription,
                WorkflowJobKinds.Timer,
                actor,
                schedule.FirstOccurrenceAt),
            cancellationToken);
        if (!await runtime.SetExecutionTokenWaitAsync(
                token.Id,
                token.ActivationId,
                ExecutionTokenWaitStates.TimerCatch,
                job.Id,
                subscription.Id,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The execution token changed while its timer wait was being created.");
        }
    }

    private async Task EnsureAttachedTimerBoundaryWaitsAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel host,
        WorkflowModel definition,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var boundaries = definition.FlowNodes
            .Where(candidate =>
                BpmnFlowNodeTypes.IsTimerBoundary(candidate.Type)
                && candidate.AttachedToRef == host.Id)
            .OrderBy(candidate => candidate.Id)
            .ToList();
        if (boundaries.Count == 0)
        {
            return;
        }

        // Terminal subscriptions still own this activation/node identity. In
        // particular, manual retry after a one-shot boundary has fired must not
        // recreate that boundary or collide with the durable uniqueness fence.
        var existing = await timerSubscriptions.ListForActivationAsync(
            token.Id,
            token.ActivationId,
            cancellationToken);
        var existingNodeIds = existing.Select(item => item.TimerNodeId).ToHashSet();
        foreach (var boundary in boundaries.Where(item => !existingNodeIds.Contains(item.Id)))
        {
            var timer = boundary.Timer
                ?? throw new WorkflowDomainException(
                    $"Timer boundary event #{boundary.Id} has no timer configuration.");
            var schedule = WorkflowTimerSchedule.Resolve(timer, timeProvider.GetUtcNow());
            var subscription = await timerSubscriptions.CreateAsync(
                BuildTimerSubscription(
                    instance,
                    token,
                    boundary,
                    schedule.FirstOccurrenceAt,
                    host.Id,
                    boundary.CancelActivity ?? true),
                cancellationToken);
            await EnqueueInstanceJobAsync(
                BuildTimerJob(
                    instance,
                    token,
                    boundary,
                    subscription,
                    WorkflowJobKinds.TimerBoundary,
                    actor,
                    schedule.FirstOccurrenceAt),
                cancellationToken);
        }
    }

    private async Task CancelAttachedTimerBoundaryWaitsAsync(
        long instanceId,
        IReadOnlyCollection<long> tokenIds,
        CancellationToken cancellationToken)
    {
        await timerSubscriptions.CancelByTokenIdsAsync(
            instanceId,
            tokenIds,
            cancellationToken);
        await jobs.CancelTimerJobsByTokenIdsAsync(
            instanceId,
            tokenIds,
            _processingJobId,
            "hostCompleted",
            cancellationToken);
    }

    private async Task CancelDurableWorkForTokensAsync(
        long instanceId,
        IReadOnlyCollection<long> tokenIds,
        string reason,
        CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0)
        {
            return;
        }
        await timerSubscriptions.CancelByTokenIdsAsync(
            instanceId,
            tokenIds,
            cancellationToken);
        // A timer/scoped continuation may cancel its own host token while the
        // current durable job is still finalizing. Fence every other job in the
        // affected set so in-flight work receives a heartbeat abort without
        // cancelling the transaction's own lease.
        await jobs.CancelOtherJobsByTokenIdsAsync(
            instanceId,
            tokenIds,
            _processingJobId,
            reason,
            cancellationToken);
    }

    private WorkflowJobCreateRecord BuildJobCreate(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel node,
        string kind,
        string phase,
        ActorContext actor,
        DateTimeOffset dueAt,
        int? selectedFlowId = null,
        long? multiInstanceExecutionId = null,
        long? userTaskId = null,
        AdministrativeBatchFlowContext? administrativeBatch = null,
        IReadOnlyList<string>? triggeringVariableNames = null)
    {
        var retryDelays = ResolveRetryDelays(node);
        return new WorkflowJobCreateRecord
        {
            InstanceId = instance.Id,
            WorkflowDefinitionId = instance.WorkflowDefinitionId,
            WorkflowKey = instance.WorkflowKey,
            TokenId = token.Id,
            MultiInstanceExecutionId = multiInstanceExecutionId,
            UserTaskId = userTaskId,
            ActivationId = token.ActivationId,
            AutomaticActivationCount = token.AutomaticActivationCount,
            NodeId = node.Id,
            NodeName = node.Name,
            NodeType = node.Type,
            Kind = kind,
            QueueClass = BpmnFlowNodeTypes.IsServiceTask(node.Type)
                         || BpmnFlowNodeTypes.IsScriptTask(node.Type)
                ? WorkflowJobClasses.Activity
                : WorkflowJobClasses.Control,
            Phase = phase,
            DueAt = dueAt,
            Priority = DefaultJobPriority,
            MaxAttempts = retryDelays.Count + 1,
            FailureHandling = node.Job?.FailureHandling
                ?? WorkflowJobFailureHandling.BoundaryFirst,
            RetryDelays = retryDelays,
            Payload = JsonSerializer.SerializeToElement(
                new DurableJobPayload(
                    actor.User,
                    SnapshotRoles(actor.Roles),
                    new Dictionary<string, string>(
                    actor.Claims,
                    StringComparer.OrdinalIgnoreCase),
                    actor.ActingFor,
                    actor.DelegationId,
                    selectedFlowId,
                    administrativeBatch?.Request,
                    triggeringVariableNames))
        };
    }

    private async Task<WorkflowJobRecord> EnqueueInstanceJobAsync(
        WorkflowJobCreateRecord create,
        CancellationToken cancellationToken,
        ExecutionTokenRecord? activationToken = null,
        bool countAutomaticActivation = false)
    {
        if (create.InstanceId is not long instanceId)
        {
            return await jobs.EnqueueAsync(create, cancellationToken);
        }

        // Every caller creating instance-owned work already holds the instance
        // row lock (or is creating that instance in the same transaction), so
        // the count and insert are serialized across workers.
        var configured = await engineSettings.GetByKeyAsync(
            "Workflow.MultiInstance.MaxInstances",
            cancellationToken);
        var limit = WorkflowJobCapacity.ResolveOpenJobLimit(configured?.Value);
        var openJobs = await jobs.CountOpenByInstanceAsync(instanceId, cancellationToken);
        var completingCredit = _processingJobId is not null
                               && _processingJobInstanceId == instanceId
            ? 1
            : 0;
        if (WorkflowJobCapacity.WouldExceed(openJobs, limit, completingCredit))
        {
            throw new WorkflowJobCapacityExceededException(
                instanceId,
                openJobs,
                limit,
                $"Workflow instance #{instanceId} has reached its open-job limit of {limit}.");
        }

        if (countAutomaticActivation)
        {
            if (activationToken is null
                || create.TokenId != activationToken.Id
                || create.InstanceId != activationToken.InstanceId
                || create.ActivationId != activationToken.ActivationId
                || !IsGuardedAutomaticActivity(create.NodeType))
            {
                throw new WorkflowJobInvariantException(
                    "An automatic-activation job does not match its execution-token fence.");
            }

            var configuredGuard = await engineSettings.GetByKeyAsync(
                WorkflowAutomaticActivationGuard.SettingKey,
                cancellationToken);
            var decision = WorkflowAutomaticActivationGuard.EvaluateNext(
                activationToken.AutomaticActivationCount,
                configuredGuard?.Value);
            if (!await runtime.SetExecutionTokenAutomaticActivationCountAsync(
                    activationToken.Id,
                    activationToken.ActivationId,
                    decision.PersistedCount,
                    cancellationToken))
            {
                throw new WorkflowConflictException(
                    "The execution token changed while its automatic-activation count was being updated.");
            }

            create = create with
            {
                AutomaticActivationCount = decision.ShouldOpenIncident
                    ? decision.AttemptedCount
                    : decision.PersistedCount
            };
            if (decision.ShouldOpenIncident)
            {
                var details = JsonSerializer.Serialize(new
                {
                    observedCount = decision.AttemptedCount,
                    configuredLimit = decision.Limit,
                    previousCount = decision.PreviousCount,
                    instanceId,
                    tokenId = activationToken.Id,
                    nodeId = create.NodeId,
                    nodeName = create.NodeName,
                    nodeType = create.NodeType,
                    activationId = create.ActivationId,
                    phase = create.Phase,
                    jobKind = create.Kind
                });
                var blocked = await jobs.EnqueueIncidentAsync(
                    create,
                    WorkflowIncidentTypes.AutomaticLoopLimit,
                    $"Automatic activation limit reached at node #{create.NodeId}.",
                    details,
                    cancellationToken);
                WorkflowJobRuntimeTelemetry.RecordIncident();
                WorkflowJobRuntimeTelemetry.RecordAutomaticLoopLimit(
                    decision.AttemptedCount,
                    decision.Limit,
                    activationToken.Id,
                    create.NodeId,
                    create.ActivationId);
                logger.LogWarning(
                    "Paused workflow instance {InstanceId}, token {TokenId}, activation {ActivationId} "
                    + "at node {NodeId} before automatic activation {ObservedCount}; configured limit is {Limit}.",
                    instanceId,
                    activationToken.Id,
                    create.ActivationId,
                    create.NodeId,
                    decision.AttemptedCount,
                    decision.Limit);
                return blocked;
            }
        }

        return await jobs.EnqueueAsync(create, cancellationToken);
    }

    private async Task<IReadOnlyList<WorkflowJobRecord>> EnqueueInstanceJobsAsync(
        IReadOnlyList<WorkflowJobCreateRecord> creates,
        CancellationToken cancellationToken)
    {
        if (creates.Count == 0)
        {
            return [];
        }
        if (creates[0].InstanceId is not long instanceId
            || creates.Any(create => create.InstanceId != instanceId))
        {
            throw new WorkflowJobInvariantException(
                "A conditional-wake batch must belong to exactly one workflow instance.");
        }

        // The caller already owns the instance row lock, so one count protects
        // the complete wave from racing another API or worker replica.
        var configured = await engineSettings.GetByKeyAsync(
            "Workflow.MultiInstance.MaxInstances",
            cancellationToken);
        var limit = WorkflowJobCapacity.ResolveOpenJobLimit(configured?.Value);
        var openJobs = await jobs.CountOpenByInstanceAsync(instanceId, cancellationToken);
        var completingCredit = _processingJobId is not null
                               && _processingJobInstanceId == instanceId
            ? 1
            : 0;
        if (WorkflowJobCapacity.WouldExceed(
                openJobs,
                limit,
                completingCredit,
                creates.Count))
        {
            throw new WorkflowJobCapacityExceededException(
                instanceId,
                openJobs,
                limit,
                $"Workflow instance #{instanceId} cannot add {creates.Count} conditional-wake jobs without exceeding its open-job limit of {limit}.");
        }

        return await jobs.EnqueueManyAsync(creates, cancellationToken);
    }

    private static bool IsGuardedAutomaticActivity(FlowNodeModel node) =>
        IsGuardedAutomaticActivity(node.Type);

    private static bool IsGuardedAutomaticActivity(string nodeType) =>
        BpmnFlowNodeTypes.IsAutomatic(nodeType)
        || BpmnFlowNodeTypes.IsServiceTask(nodeType)
        || BpmnFlowNodeTypes.IsScriptTask(nodeType);

    private WorkflowJobCreateRecord BuildTimerJob(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel timerNode,
        TimerSubscriptionRecord subscription,
        string kind,
        ActorContext actor,
        DateTimeOffset dueAt)
    {
        var create = BuildJobCreate(
            instance,
            token,
            timerNode,
            kind,
            WorkflowJobKinds.Timer,
            actor,
            dueAt);
        return create with
        {
            TimerSubscriptionId = subscription.Id,
            QueueClass = WorkflowJobClasses.Control,
            ScheduledOccurrenceAt = dueAt
        };
    }

    private static TimerSubscriptionCreateRecord BuildTimerSubscription(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel timerNode,
        DateTimeOffset dueAt,
        int? attachedToNodeId,
        bool cancelActivity)
    {
        var timer = timerNode.Timer!;
        var (kind, expression) = GetTimerExpression(timer);
        return new TimerSubscriptionCreateRecord
        {
            InstanceId = instance.Id,
            WorkflowDefinitionId = instance.WorkflowDefinitionId,
            WorkflowKey = instance.WorkflowKey,
            TokenId = token.Id,
            ActivationId = token.ActivationId,
            TimerNodeId = timerNode.Id,
            TimerNodeName = timerNode.Name,
            AttachedToNodeId = attachedToNodeId,
            ScheduleKind = kind,
            ScheduleExpression = expression,
            CancelActivity = cancelActivity,
            NextDueAt = dueAt
        };
    }

    private static (string Kind, string Expression) GetTimerExpression(
        TimerDefinitionModel timer)
    {
        if (!string.IsNullOrWhiteSpace(timer.TimeDate))
        {
            return (TimerScheduleKinds.Date, timer.TimeDate.Trim());
        }
        if (!string.IsNullOrWhiteSpace(timer.TimeDuration))
        {
            return (TimerScheduleKinds.Duration, timer.TimeDuration.Trim());
        }
        if (!string.IsNullOrWhiteSpace(timer.TimeCycle))
        {
            return (TimerScheduleKinds.Cycle, timer.TimeCycle.Trim());
        }
        throw new WorkflowDomainException("Timer configuration has no schedule.");
    }

    private static IReadOnlyList<TimeSpan> ResolveRetryDelays(FlowNodeModel node)
    {
        if (node.Job?.RetryDelays is null)
        {
            return DefaultRetryDelays;
        }

        var authored = node.Job.RetryDelays;
        var result = new List<TimeSpan>(authored.Count);
        foreach (var text in authored)
        {
            if (!TimerDefinitionRules.TryParseFixedDuration(text, out var delay))
            {
                throw new WorkflowDomainException(
                    $"Node #{node.Id} has invalid retry delay '{text}'.");
            }
            result.Add(delay);
        }
        return result;
    }

    private static void EnsureTokenCanEnterWait(ExecutionTokenRecord token)
    {
        if (token.Status != ExecutionTokenRecordStatuses.Active)
        {
            throw new WorkflowConflictException(
                "Only an active execution token can enter a durable wait.");
        }
        if (token.ActivationId == Guid.Empty)
        {
            throw new WorkflowConflictException(
                "The execution token has no durable activation identifier.");
        }
        if (token.WaitState is not null
            || token.WaitingJobId is not null
            || token.WaitingTimerSubscriptionId is not null)
        {
            throw new WorkflowConflictException(
                "The execution token is already waiting on durable work.");
        }
    }

    private static ActorContext ReadJobActor(WorkflowJobRecord job)
    {
        var parsed = ReadJobPayload(job);
        if (parsed is null)
        {
            return ActorContext.Anonymous;
        }
        return new ActorContext(
            parsed.User,
            parsed.Roles,
            parsed.Claims)
        {
            ActingFor = parsed.ActingFor,
            DelegationId = parsed.DelegationId
        };
    }

    private static DurableJobPayload? ReadJobPayload(WorkflowJobRecord job) =>
        job.Payload?.Deserialize<DurableJobPayload>();

    public async Task ProcessAsync(
        WorkflowJobLeaseRecord lease,
        CancellationToken cancellationToken)
    {
        var fence = new WorkflowJobFence(
            lease.Job.Id,
            lease.Job.WorkerId
                ?? throw new WorkflowConflictException("A leased workflow job has no worker id."),
            lease.LeaseToken,
            lease.LeaseGeneration);

        var previousProcessingJobId = _processingJobId;
        var previousProcessingInstanceId = _processingJobInstanceId;
        _processingJobId = lease.Job.Id;
        _processingJobInstanceId = lease.Job.InstanceId;
        try
        {
            if (lease.Job.Kind == WorkflowJobKinds.TimerStart)
            {
                await ProcessTimerStartJobAsync(lease, fence, cancellationToken);
                return;
            }

            if (lease.Job.InstanceId is null || lease.Job.TokenId is null)
            {
                if (await jobs.OpenIncidentAsync(
                        fence,
                        "invalid_job_shape",
                        "The workflow job is missing its instance or token identity.",
                        null,
                        cancellationToken) is not null)
                {
                    WorkflowJobRuntimeTelemetry.RecordIncident();
                }
                return;
            }

            if (lease.Job.Kind == WorkflowJobKinds.AsyncBefore
                && (BpmnFlowNodeTypes.IsServiceTask(lease.Job.NodeType)
                    || BpmnFlowNodeTypes.IsScriptTask(lease.Job.NodeType))
                && lease.Job.Status != WorkflowJobStatuses.ResultReady)
            {
                if (BpmnFlowNodeTypes.IsServiceTask(lease.Job.NodeType))
                {
                    await StageAndInvokeServiceJobAsync(lease, fence, cancellationToken);
                }
                else
                {
                    await StageAndExecuteScriptJobAsync(lease, fence, cancellationToken);
                }
            }

            await FinalizeLeasedJobAsync(lease, fence, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkflowJobCapacityExceededException ex)
        {
            unitOfWork.DiscardChanges();
            try
            {
                if (await PauseRecurringTimerBoundaryForCapacityAsync(
                        lease,
                        fence,
                        ex,
                        cancellationToken))
                {
                    return;
                }
            }
            catch (WorkflowConflictException stale)
            {
                unitOfWork.DiscardChanges();
                logger.LogInformation(
                    "Workflow job {JobId} became stale while handling capacity overflow: {Reason}",
                    lease.Job.Id,
                    stale.Message);
                await jobs.CompleteAsync(fence, cancellationToken);
                return;
            }

            unitOfWork.DiscardChanges();
            await HandleProcessingFailureAsync(
                lease,
                fence,
                "job_capacity_exceeded",
                LimitJobFailure(ex.Message),
                cancellationToken);
        }
        catch (WorkflowJobInvariantException ex)
        {
            unitOfWork.DiscardChanges();
            logger.LogError(
                ex,
                "Workflow job {JobId} encountered a durable-state invariant violation.",
                lease.Job.Id);
            if (await jobs.OpenIncidentAsync(
                    fence,
                    "job_invariant_violation",
                    $"Durable state is inconsistent at node #{lease.Job.NodeId}.",
                    LimitJobFailure(ex.Message),
                    cancellationToken) is not null)
            {
                WorkflowJobRuntimeTelemetry.RecordIncident();
            }
        }
        catch (WorkflowConflictException ex)
        {
            unitOfWork.DiscardChanges();
            logger.LogInformation(
                "Workflow job {JobId} became stale and will not mutate runtime state: {Reason}",
                lease.Job.Id,
                ex.Message);
            await jobs.CompleteAsync(fence, cancellationToken);
        }
        catch (WorkflowOutputVersionConflictException ex)
        {
            unitOfWork.DiscardChanges();
            WorkflowJobRuntimeTelemetry.RecordConflict();
            if (await jobs.OpenIncidentAsync(
                    fence,
                    "output_version_conflict",
                    $"Async output conflict at node #{lease.Job.NodeId}.",
                    LimitJobFailure(ex.Message),
                    cancellationToken) is not null)
            {
                WorkflowJobRuntimeTelemetry.RecordIncident();
            }
        }
        catch (TimerMisfireExhaustedException ex)
        {
            unitOfWork.DiscardChanges();
            if (await jobs.OpenIncidentAsync(
                    fence,
                    "timer_misfire_exhausted",
                    $"Recurring timer catch exhausted at node #{lease.Job.NodeId}.",
                    LimitJobFailure(ex.Message),
                    cancellationToken) is not null)
            {
                WorkflowJobRuntimeTelemetry.RecordIncident();
            }
        }
        catch (Exception ex) when (ex is WorkflowDomainException
                                   or InvalidOperationException)
        {
            unitOfWork.DiscardChanges();
            await HandleProcessingFailureAsync(
                lease,
                fence,
                "job_execution_failed",
                LimitJobFailure(ex.Message),
                cancellationToken);
        }
        finally
        {
            _processingJobId = previousProcessingJobId;
            _processingJobInstanceId = previousProcessingInstanceId;
        }
    }

    private async Task StageAndInvokeServiceJobAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CancellationToken cancellationToken)
    {
        StagedServiceInvocation invocation;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var instance = await GetInstanceForJobUpdateAsync(
                lease.Job.InstanceId!.Value,
                cancellationToken)
                ?? throw new WorkflowConflictException("The workflow instance no longer exists.");
            var token = await runtime.GetExecutionTokenAsync(
                lease.Job.TokenId!.Value,
                true,
                cancellationToken);
            var lockedJob = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
                ?? throw new WorkflowConflictException("The workflow job no longer exists.");
            EnsureJobFence(lockedJob, fence);
            token = RequireFencedToken(instance, lockedJob, token);
            var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
            var node = GetFlowNode(workflow.Definition, token.NodeId);
            var actor = ReadJobActor(lockedJob);
            WorkflowJobSnapshotRecord snapshot;
            if (lockedJob.SnapshotId is long existingSnapshotId)
            {
                snapshot = await jobs.GetSnapshotAsync(
                    existingSnapshotId,
                    cancellationToken)
                    ?? throw new WorkflowJobInvariantException(
                        "The staged service-task snapshot is unavailable.");
            }
            else
            {
                var activated = await runtime.ActivatePendingNodeAsync(
                    token.Id,
                    token.ActivationId,
                    null,
                    cancellationToken);
                // A manual output-conflict retry deliberately discards the old
                // immutable snapshot after the node was already activated.
                // The token/job wait fence still proves ownership, so it is
                // safe to capture a fresh snapshot without creating a second
                // node visit.
                if (activated is null && token.CurrentNodeExecutionId is null)
                {
                    throw new WorkflowJobInvariantException(
                        "The async-before node activation is no longer available.");
                }
                var stagedToken = activated ?? token;
                if (activated is not null)
                {
                    await EnsureAttachedTimerBoundaryWaitsAsync(
                        instance,
                        stagedToken,
                        node,
                        workflow.Definition,
                        actor,
                        cancellationToken);
                }

                var versions = await runtime.LoadLatestVariableVersionsAsync(
                    instance.Id,
                    cancellationToken);
                var stored = versions.ToDictionary(
                    item => item.Name,
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase);
                var context = WithContext(
                    stored,
                    actor,
                    instance with
                    {
                        ActiveTokenId = stagedToken.Id,
                        CurrentStepId = stagedToken.NodeId,
                        CurrentNodeExecutionId = stagedToken.CurrentNodeExecutionId
                    },
                    workflow.Definition,
                    node);
                var evaluationTime = timeProvider.GetUtcNow();
                context["sys.now"] = JsonSerializer.SerializeToElement(
                    evaluationTime.ToString("o", CultureInfo.InvariantCulture));
                context["sys.today"] = JsonSerializer.SerializeToElement(
                    evaluationTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                context["sys.jobId"] = JsonSerializer.SerializeToElement(lockedJob.Id);
                context["sys.jobAttempt"] =
                    JsonSerializer.SerializeToElement(lease.AttemptNumber);
                var snapshotContext = FilterSnapshotContext(
                    context,
                    stored,
                    ServiceSnapshotExpressions(node, workflow.Definition),
                    ServiceOutputTargets(node, workflow.Definition));
                StagedServiceInvocation firstInvocation;
                try
                {
                    firstInvocation = BuildStagedServiceInvocation(node, snapshotContext);
                }
                catch (WorkflowDomainException ex)
                {
                    firstInvocation = new StagedServiceInvocation(
                        string.Empty,
                        string.Empty,
                        [],
                        null,
                        0,
                        LimitJobFailure(ex.Message));
                }

                var boundaryErrorVariable =
                    FindErrorBoundary(workflow.Definition, node.Id)?.ErrorVariable;
                var outputNames = (node.Service?.OutputMappings ?? [])
                    .Select(mapping => mapping.Variable)
                    .Concat(string.IsNullOrWhiteSpace(node.Service?.StatusVariable)
                        ? []
                        : [node.Service!.StatusVariable!])
                    .Concat(string.IsNullOrWhiteSpace(boundaryErrorVariable)
                        ? []
                        : [boundaryErrorVariable])
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var outputVersions = versions
                    .Where(item => outputNames.Contains(item.Name))
                    .ToDictionary(
                        item => item.Name,
                        item => item.Version,
                        StringComparer.OrdinalIgnoreCase);
                foreach (var missing in outputNames.Where(name =>
                             !outputVersions.ContainsKey(name)))
                {
                    outputVersions[missing] = 0;
                }

                snapshot = await jobs.SaveStageAsync(
                    fence,
                    new WorkflowJobStageRecord(
                        JsonSerializer.SerializeToElement(firstInvocation),
                        snapshotContext,
                        outputVersions,
                        null,
                        evaluationTime),
                    DefaultMaxSnapshotBytes,
                    cancellationToken)
                    ?? throw new WorkflowConflictException(
                        "The workflow job lease was lost while staging its invocation.");
            }

            invocation = RestoreStagedServiceInvocation(
                node,
                snapshot,
                lockedJob.Id,
                lease.AttemptNumber);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        // The external body may run for minutes while sibling branches mutate
        // the same instance. Drop every staged EF entity before releasing the
        // database entirely so finalization cannot identity-resolve stale
        // gateway/token state from this phase.
        unitOfWork.DiscardChanges();

        ServiceTaskResult result;
        if (invocation.Failure is not null)
        {
            result = new ServiceTaskResult(
                false,
                0,
                null,
                invocation.Failure);
        }
        else
        {
            try
            {
                result = await serviceTaskInvoker.InvokeAsync(
                    new ServiceTaskRequest(
                        invocation.Method,
                        invocation.Url,
                        invocation.Headers,
                        invocation.Body,
                        invocation.TimeoutSeconds),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new ServiceTaskResult(
                    false,
                    0,
                    null,
                    LimitJobFailure(ex.Message));
            }
        }
        if (!await jobs.SaveResultReadyAsync(
                fence,
                new WorkflowJobResultRecord(
                    JsonSerializer.SerializeToElement(result),
                    null,
                    result.IsSuccess ? null : "service_task_failed",
                    result.Error),
                cancellationToken))
        {
            logger.LogWarning(
                "Discarding late result for workflow job {JobId}; its lease fence is no longer current.",
                lease.Job.Id);
            unitOfWork.DiscardChanges();
            return;
        }
        // SaveResultReadyAsync is a short fenced database phase. Finalization
        // must reload the job and all workflow state under its own locks.
        unitOfWork.DiscardChanges();
    }

    private async Task StageAndExecuteScriptJobAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CancellationToken cancellationToken)
    {
        WorkflowJobSnapshotRecord snapshot;
        FlowNodeModel node;
        WorkflowModel definition;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var instance = await GetInstanceForJobUpdateAsync(
                lease.Job.InstanceId!.Value,
                cancellationToken)
                ?? throw new WorkflowConflictException("The workflow instance no longer exists.");
            var token = await runtime.GetExecutionTokenAsync(
                lease.Job.TokenId!.Value,
                true,
                cancellationToken);
            var lockedJob = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
                ?? throw new WorkflowConflictException("The workflow job no longer exists.");
            EnsureJobFence(lockedJob, fence);
            token = RequireFencedToken(instance, lockedJob, token);
            var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
            definition = workflow.Definition;
            node = GetFlowNode(definition, token.NodeId);
            var actor = ReadJobActor(lockedJob);
            if (lockedJob.SnapshotId is long existingSnapshotId)
            {
                snapshot = await jobs.GetSnapshotAsync(
                    existingSnapshotId,
                    cancellationToken)
                    ?? throw new WorkflowJobInvariantException(
                        "The staged script-task snapshot is unavailable.");
            }
            else
            {
                var activated = await runtime.ActivatePendingNodeAsync(
                    token.Id,
                    token.ActivationId,
                    null,
                    cancellationToken);
                if (activated is null && token.CurrentNodeExecutionId is null)
                {
                    throw new WorkflowJobInvariantException(
                        "The async-before script activation is no longer available.");
                }
                var stagedToken = activated ?? token;
                if (activated is not null)
                {
                    await EnsureAttachedTimerBoundaryWaitsAsync(
                        instance,
                        stagedToken,
                        node,
                        definition,
                        actor,
                        cancellationToken);
                }

                var versions = await runtime.LoadLatestVariableVersionsAsync(
                    instance.Id,
                    cancellationToken);
                var stored = versions.ToDictionary(
                    item => item.Name,
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase);
                var context = WithContext(
                    stored,
                    actor,
                    instance with
                    {
                        ActiveTokenId = stagedToken.Id,
                        CurrentStepId = stagedToken.NodeId,
                        CurrentNodeExecutionId = stagedToken.CurrentNodeExecutionId
                    },
                    definition,
                    node);
                var evaluationTime = timeProvider.GetUtcNow();
                context["sys.now"] = JsonSerializer.SerializeToElement(
                    evaluationTime.ToString("o", CultureInfo.InvariantCulture));
                context["sys.today"] = JsonSerializer.SerializeToElement(
                    evaluationTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                context["sys.jobId"] = JsonSerializer.SerializeToElement(lockedJob.Id);
                context["sys.jobAttempt"] =
                    JsonSerializer.SerializeToElement(lease.AttemptNumber);
                var snapshotContext = string.Equals(
                    node.ScriptFormat,
                    ScriptFormats.JavaScript,
                    StringComparison.Ordinal)
                    ? context
                    : FilterSnapshotContext(
                        context,
                        stored,
                        ScriptSnapshotExpressions(node, definition),
                        node.Assignments.Select(assignment => assignment.Variable));
                var flowInfo = await LoadSequenceFlowInfoAsync(
                    instance.Id,
                    definition,
                    cancellationToken);
                var outputVersions = versions.ToDictionary(
                    item => item.Name,
                    item => item.Version,
                    StringComparer.OrdinalIgnoreCase);
                foreach (var variable in definition.Variables.Where(variable =>
                             !string.IsNullOrWhiteSpace(variable.Name)
                             && !outputVersions.ContainsKey(variable.Name)))
                {
                    outputVersions[variable.Name!] = 0;
                }
                var boundaryErrorVariable =
                    FindErrorBoundary(definition, node.Id)?.ErrorVariable;
                if (!string.IsNullOrWhiteSpace(boundaryErrorVariable)
                    && !outputVersions.ContainsKey(boundaryErrorVariable))
                {
                    outputVersions[boundaryErrorVariable] = 0;
                }

                snapshot = await jobs.SaveStageAsync(
                    fence,
                    new WorkflowJobStageRecord(
                        null,
                        snapshotContext,
                        outputVersions,
                        flowInfo is null
                            ? null
                            : JsonSerializer.SerializeToElement(flowInfo),
                        evaluationTime),
                    DefaultMaxSnapshotBytes,
                    cancellationToken)
                    ?? throw new WorkflowConflictException(
                        "The workflow job lease was lost while staging its script.");
            }
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        unitOfWork.DiscardChanges();

        WorkflowJobResultRecord result;
        try
        {
            var writes = EvaluateStagedScript(
                node,
                definition,
                snapshot,
                lease.Job.Id,
                lease.AttemptNumber,
                cancellationToken);
            result = new WorkflowJobResultRecord(
                JsonSerializer.SerializeToElement(writes),
                null,
                null,
                null);
        }
        catch (Exception ex) when (ex is WorkflowDomainException
                                   or InvalidOperationException)
        {
            result = new WorkflowJobResultRecord(
                null,
                JsonSerializer.SerializeToElement(LimitJobFailure(ex.Message)),
                "script_task_failed",
                LimitJobFailure(ex.Message));
        }

        if (!await jobs.SaveResultReadyAsync(fence, result, cancellationToken))
        {
            logger.LogWarning(
                "Discarding late script result for workflow job {JobId}; its lease fence is no longer current.",
                lease.Job.Id);
        }
        unitOfWork.DiscardChanges();
    }

    private async Task FinalizeLeasedJobAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await GetInstanceForJobUpdateAsync(
            lease.Job.InstanceId!.Value,
            cancellationToken)
            ?? throw new WorkflowConflictException("The workflow instance no longer exists.");
        var token = await runtime.GetExecutionTokenAsync(
            lease.Job.TokenId!.Value,
            true,
            cancellationToken);
        var job = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow job no longer exists.");
        EnsureJobFence(job, fence);
        token = RequireFencedToken(instance, job, token);
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, job.NodeId);
        var actor = ReadJobActor(job);

        if (job.Kind == WorkflowJobKinds.Timer)
        {
            await FireTimerCatchAsync(instance, token, job, node, workflow.Definition, actor, cancellationToken);
        }
        else if (job.Kind == WorkflowJobKinds.TimerBoundary)
        {
            await FireTimerBoundaryAsync(instance, token, job, node, workflow.Definition, actor, cancellationToken);
        }
        else if (job.Kind == WorkflowJobKinds.ConditionalWake)
        {
            await FireConditionalWakeAsync(
                instance,
                token,
                job,
                node,
                workflow.Definition,
                actor,
                cancellationToken);
        }
        else
        {
            await FinalizeAsyncContinuationAsync(
                instance,
                token,
                job,
                node,
                workflow.Definition,
                actor,
                cancellationToken);
        }

        if (!await jobs.CompleteAsync(fence, cancellationToken))
        {
            throw new WorkflowConflictException(
                "The workflow job lease was lost before completion.");
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task FinalizeAsyncContinuationAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        WorkflowJobRecord job,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var payload = ReadJobPayload(job);
        if (job.Kind == WorkflowJobKinds.AsyncAfter
            && BpmnFlowNodeTypes.IsUserTask(node.Type)
            && payload?.SelectedFlowId is int selectedFlowId)
        {
            await FinalizeUserTaskAsyncAfterAsync(
                instance,
                token,
                job,
                node,
                definition,
                actor,
                selectedFlowId,
                cancellationToken);
            return;
        }

        TaskExecutionOutcome? outcome = null;
        var activityAlreadyExecuted = job.Kind == WorkflowJobKinds.AsyncAfter;
        if (job.Kind == WorkflowJobKinds.AsyncBefore
            && BpmnFlowNodeTypes.IsServiceTask(node.Type))
        {
            var result = ReadServiceTaskResult(job);
            var snapshot = job.SnapshotId is long snapshotId
                ? await jobs.GetSnapshotAsync(snapshotId, cancellationToken)
                : null;
            if (snapshot is null)
            {
                throw new WorkflowJobInvariantException(
                    "The staged service-task snapshot is unavailable.");
            }
            await EnsureOutputVersionsCurrentAsync(
                instance.Id,
                snapshot.OutputVariableVersions,
                cancellationToken);
            outcome = await ApplyStagedServiceResultAsync(
                instance,
                token,
                job,
                node,
                definition,
                actor,
                snapshot,
                result,
                cancellationToken);
            activityAlreadyExecuted = true;

            if (outcome is { Success: false }
                && ShouldRetryBeforeBoundary(job, definition, node))
            {
                throw new RetryableWorkflowJobException(outcome.Reason ?? "Service task failed.");
            }
        }
        else if (job.Kind == WorkflowJobKinds.AsyncBefore
                 && BpmnFlowNodeTypes.IsScriptTask(node.Type))
        {
            var snapshot = job.SnapshotId is long snapshotId
                ? await jobs.GetSnapshotAsync(snapshotId, cancellationToken)
                : null;
            if (snapshot is null)
            {
                throw new WorkflowJobInvariantException(
                    "The staged script-task snapshot is unavailable.");
            }
            if (job.Error is JsonElement error)
            {
                var boundaryErrorVariable =
                    FindErrorBoundary(definition, node.Id)?.ErrorVariable;
                if (!string.IsNullOrWhiteSpace(boundaryErrorVariable))
                {
                    await EnsureOutputVersionsCurrentAsync(
                        instance.Id,
                        snapshot.OutputVariableVersions
                            .Where(pair => string.Equals(
                                pair.Key,
                                boundaryErrorVariable,
                                StringComparison.OrdinalIgnoreCase))
                            .ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value,
                                StringComparer.OrdinalIgnoreCase),
                        cancellationToken);
                }
                var reason = error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : error.GetRawText();
                outcome = TaskExecutionOutcome.Fail(
                    reason ?? $"Script task #{node.Id} failed.");
                if (ShouldRetryBeforeBoundary(job, definition, node))
                {
                    throw new RetryableWorkflowJobException(outcome.Reason!);
                }
            }
            else
            {
                var writes = job.Result?.Deserialize<Dictionary<string, JsonElement>>()
                    ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                await EnsureOutputVersionsCurrentAsync(
                    instance.Id,
                    snapshot.OutputVariableVersions
                        .Where(pair => writes.ContainsKey(pair.Key))
                        .ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.OrdinalIgnoreCase),
                    cancellationToken);
                foreach (var pair in writes)
                {
                    await runtime.AddVariableAsync(
                        instance.Id,
                        pair.Key,
                        node.Id,
                        actor.User,
                        pair.Value,
                        cancellationToken,
                        token.CurrentNodeExecutionId,
                        actor.ActingFor,
                        actor.DelegationId);
                }
                outcome = TaskExecutionOutcome.Ok();
            }
            activityAlreadyExecuted = true;
        }
        else if (job.Kind == WorkflowJobKinds.AsyncBefore)
        {
            _ = await runtime.ActivatePendingNodeAsync(
                token.Id,
                token.ActivationId,
                null,
                cancellationToken);
        }

        if (!await runtime.ClearExecutionTokenWaitAsync(
                token.Id,
                token.ActivationId,
                job.Kind == WorkflowJobKinds.AsyncBefore
                    ? ExecutionTokenWaitStates.AsyncBefore
                    : ExecutionTokenWaitStates.AsyncAfter,
                job.Id,
                null,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The workflow job no longer owns the token wait.");
        }

        var flowInfo = await LoadSequenceFlowInfoAsync(instance.Id, definition, cancellationToken);
        var resumed = await ResolvePassThroughAsync(
            instance,
            definition,
            actor,
            flowInfo,
            token.Id,
            cancellationToken,
            new PassThroughResume(
                token.ActivationId,
                job.Kind,
                activityAlreadyExecuted,
                outcome),
            forceDurableActivities: true);
        await EnsureMultiInstanceInitializedAsync(resumed, definition, actor, cancellationToken);
        _ = await ApplyUserTaskOwnershipInheritanceAsync(resumed, definition, cancellationToken);
    }

    private async Task FinalizeUserTaskAsyncAfterAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        WorkflowJobRecord job,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        int selectedFlowId,
        CancellationToken cancellationToken)
    {
        var flow = OutgoingFlows(
                instance.WorkflowDefinitionId,
                definition,
                node.Id)
            .SingleOrDefault(candidate => candidate.Id == selectedFlowId)
            ?? throw new WorkflowJobInvariantException(
                "The selected async-after sequence flow no longer exists.");
        if (!await runtime.ClearExecutionTokenWaitAsync(
                token.Id,
                token.ActivationId,
                ExecutionTokenWaitStates.AsyncAfter,
                job.Id,
                null,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The async-after job no longer owns the user-task wait.");
        }

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var administrativeBatch = ReadJobPayload(job)?.AdministrativeAction is { } request
            ? new AdministrativeBatchFlowContext(request)
            : null;
        var flowInfo = await LoadSequenceFlowInfoAsync(
            instance.Id,
            definition,
            cancellationToken,
            force: administrativeBatch is not null);
        var historyNote = administrativeBatch is not null
            ? NodeExecutionCompletionReasons.AdministrativeAction
            : job.MultiInstanceExecutionId is null
                ? "userTaskAsyncAfter"
                : "multiInstanceAsyncAfter";
        if (administrativeBatch is null)
        {
            await RecordSequenceFlowOccurrenceAsync(
                flowInfo,
                instance.Id,
                token.Id,
                job.UserTaskId,
                job.MultiInstanceExecutionId,
                null,
                flow,
                historyNote,
                isAction: false,
                isTraversal: true,
                actor: actor,
                values: null,
                cancellationToken: cancellationToken);
        }
        var queue = new Queue<long>();
        await AdvanceAutomaticTokenAsync(
            instance,
            token,
            token.GatewayBranchId,
            node,
            flow,
            historyNote,
            definition,
            actor,
            stored,
            flowInfo,
            queue,
            cancellationToken,
            administrativeUserTaskId: administrativeBatch is null ? null : job.UserTaskId,
            administrativeMultiInstanceExecutionId:
                administrativeBatch is null ? null : job.MultiInstanceExecutionId,
            administrativeBatch: administrativeBatch);
        if (await IsInstanceRunningAsync(instance.Id, cancellationToken))
        {
            var resumed = await runtime.GetInstanceAsync(instance.Id, cancellationToken)
                ?? instance;
            resumed = await ResolvePassThroughAsync(
                resumed,
                definition,
                actor,
                flowInfo,
                token.Id,
                cancellationToken,
                forceDurableActivities: true);
            await EnsureMultiInstanceInitializedAsync(
                resumed,
                definition,
                actor,
                cancellationToken);
            _ = await ApplyUserTaskOwnershipInheritanceAsync(
                resumed,
                definition,
                cancellationToken);
        }
    }

    private StagedServiceInvocation RestoreStagedServiceInvocation(
        FlowNodeModel node,
        WorkflowJobSnapshotRecord snapshot,
        long jobId,
        int attemptNumber)
    {
        var frozen = snapshot.Invocation?.Deserialize<StagedServiceInvocation>()
            ?? throw new WorkflowJobInvariantException(
                "The staged service-task invocation is unavailable.");
        if (frozen.Failure is not null)
        {
            return frozen;
        }

        // Re-render only from the immutable snapshot so a retry observes the
        // original variables, FlowInfo, actor context and evaluation time. The
        // durable attempt number is deliberately refreshed for downstream
        // idempotency/diagnostics.
        var context = snapshot.Variables.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        context["sys.now"] = JsonSerializer.SerializeToElement(
            snapshot.EvaluationTime.ToString("o", CultureInfo.InvariantCulture));
        context["sys.today"] = JsonSerializer.SerializeToElement(
            snapshot.EvaluationTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        context["sys.jobId"] = JsonSerializer.SerializeToElement(jobId);
        context["sys.jobAttempt"] = JsonSerializer.SerializeToElement(attemptNumber);
        return BuildStagedServiceInvocation(node, context);
    }

    private static IReadOnlyList<string?> ServiceSnapshotExpressions(
        FlowNodeModel node,
        WorkflowModel definition)
    {
        var service = node.Service;
        if (service is null)
        {
            return [];
        }

        var expressions = new List<string?>
        {
            service.Url,
            service.Body
        };
        expressions.AddRange(service.Headers.Select(header => header.Value));
        expressions.AddRange(service.OutputMappings.Select(mapping => mapping.Validation));
        expressions.AddRange(service.OutputMappings
            .Where(mapping => mapping.DefaultValue is not null)
            .Select(mapping => mapping.DefaultValue!.Value.GetRawText()));
        var targets = ServiceOutputTargets(node, definition).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        expressions.AddRange(definition.Variables
            .Where(variable =>
                !string.IsNullOrWhiteSpace(variable.Name)
                && targets.Contains(variable.Name))
            .Select(variable => variable.Validation));
        return expressions;
    }

    private static IReadOnlyList<string> ServiceOutputTargets(
        FlowNodeModel node,
        WorkflowModel definition)
    {
        var targets = (node.Service?.OutputMappings ?? [])
            .Select(mapping => mapping.Variable)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        if (!string.IsNullOrWhiteSpace(node.Service?.StatusVariable))
        {
            targets.Add(node.Service.StatusVariable);
        }
        var boundaryErrorVariable =
            FindErrorBoundary(definition, node.Id)?.ErrorVariable;
        if (!string.IsNullOrWhiteSpace(boundaryErrorVariable))
        {
            targets.Add(boundaryErrorVariable);
        }
        return targets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string?> ScriptSnapshotExpressions(
        FlowNodeModel node,
        WorkflowModel definition)
    {
        var expressions = node.Assignments
            .Select(assignment => (string?)assignment.Expression)
            .ToList();
        var targets = node.Assignments
            .Select(assignment => assignment.Variable)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        expressions.AddRange(definition.Variables
            .Where(variable =>
                !string.IsNullOrWhiteSpace(variable.Name)
                && targets.Contains(variable.Name))
            .Select(variable => variable.Validation));
        return expressions;
    }

    private static Dictionary<string, JsonElement> FilterSnapshotContext(
        IReadOnlyDictionary<string, JsonElement> context,
        IReadOnlyDictionary<string, JsonElement> storedVariables,
        IEnumerable<string?> expressions,
        IEnumerable<string> alwaysInclude)
    {
        var authored = expressions
            .Where(expression => !string.IsNullOrWhiteSpace(expression))
            .Select(expression => expression!)
            .ToArray();
        var included = alwaysInclude
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in storedVariables.Keys)
        {
            if (authored.Any(expression => ReferencesVariable(expression, name)))
            {
                included.Add(name);
            }
        }

        return context
            .Where(pair =>
                !storedVariables.ContainsKey(pair.Key)
                || included.Contains(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool ReferencesVariable(string expression, string variableName)
    {
        if (expression.Contains(
                "${" + variableName + "}",
                StringComparison.OrdinalIgnoreCase)
            || expression.Contains(
                "[" + variableName + "]",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Regex.IsMatch(
            expression,
            $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(variableName)}(?![\p{{L}}\p{{N}}_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }

    private StagedServiceInvocation BuildStagedServiceInvocation(
        FlowNodeModel node,
        IReadOnlyDictionary<string, JsonElement> variables)
    {
        var service = node.Service
            ?? throw new WorkflowDomainException(
                $"Service task #{node.Id} has no service configuration.");
        if (!string.Equals(service.Type, ServiceConnectorTypes.Rest, StringComparison.Ordinal))
        {
            throw new WorkflowDomainException(
                $"Service task #{node.Id} has unsupported connector type '{service.Type ?? "null"}'.");
        }
        if (!ServiceTaskTemplating.TrySubstituteScalarStrict(
                service.Url,
                variables,
                out var url,
                out var missingUrlVariable))
        {
            throw new WorkflowDomainException(
                $"Service task #{node.Id} URL references missing variable '{missingUrlVariable}'.");
        }

        var headers = new List<ServiceTaskHeader>(service.Headers.Count);
        foreach (var header in service.Headers)
        {
            if (!ServiceTaskTemplating.TrySubstituteScalarStrict(
                    header.Value,
                    variables,
                    out var value,
                    out var missingHeaderVariable))
            {
                throw new WorkflowDomainException(
                    $"Service task #{node.Id} header '{header.Name}' references missing variable '{missingHeaderVariable}'.");
            }
            headers.Add(new ServiceTaskHeader(header.Name, value));
        }

        var body = string.IsNullOrEmpty(service.Body)
            ? null
            : ServiceTaskTemplating.SubstituteJson(service.Body, variables);
        if (body is not null && IsJsonRequest(headers))
        {
            try
            {
                using var _ = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new WorkflowDomainException(
                    $"Service task #{node.Id} rendered body is not valid JSON.");
            }
        }

        return new StagedServiceInvocation(
            service.Method,
            url,
            headers,
            body,
            service.TimeoutSeconds);
    }

    private async Task<TaskExecutionOutcome> ApplyStagedServiceResultAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        WorkflowJobRecord job,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        WorkflowJobSnapshotRecord snapshot,
        ServiceTaskResult result,
        CancellationToken cancellationToken)
    {
        var service = node.Service!;
        var storedOverlay = snapshot.Variables.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var context = new Dictionary<string, JsonElement>(
            storedOverlay,
            StringComparer.OrdinalIgnoreCase);
        context["sys.jobId"] = JsonSerializer.SerializeToElement(job.Id);
        context["sys.jobAttempt"] = JsonSerializer.SerializeToElement(job.AttemptCount);

        if (result.IsSuccess)
        {
            var mappingFailure = await ApplyServiceOutputsAsync(
                instance.Id,
                node.Id,
                actor.User,
                service,
                result,
                definition.Variables,
                context,
                storedOverlay,
                token.CurrentNodeExecutionId,
                actor,
                cancellationToken);
            await WriteStatusVariableAsync(
                instance.Id,
                node.Id,
                actor.User,
                service,
                result.StatusCode,
                storedOverlay,
                token.CurrentNodeExecutionId,
                actor,
                cancellationToken);
            return mappingFailure is null
                ? TaskExecutionOutcome.Ok()
                : TaskExecutionOutcome.Fail(mappingFailure);
        }

        await WriteStatusVariableAsync(
            instance.Id,
            node.Id,
            actor.User,
            service,
            result.StatusCode,
            storedOverlay,
            token.CurrentNodeExecutionId,
            actor,
            cancellationToken);
        var reason = result.Error ?? $"HTTP status {result.StatusCode}";
        return TaskExecutionOutcome.Fail(
            $"Service task #{node.Id} REST call failed ({reason}).");
    }

    private Dictionary<string, JsonElement> EvaluateStagedScript(
        FlowNodeModel node,
        WorkflowModel definition,
        WorkflowJobSnapshotRecord snapshot,
        long jobId,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        var declared = definition.Variables
            .Where(variable => !string.IsNullOrWhiteSpace(variable.Name))
            .ToDictionary(
                variable => variable.Name!,
                StringComparer.OrdinalIgnoreCase);
        var overlay = snapshot.Variables.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        overlay["sys.now"] = JsonSerializer.SerializeToElement(
            snapshot.EvaluationTime.ToString("o", CultureInfo.InvariantCulture));
        overlay["sys.today"] = JsonSerializer.SerializeToElement(
            snapshot.EvaluationTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        overlay["sys.jobId"] = JsonSerializer.SerializeToElement(jobId);
        overlay["sys.jobAttempt"] = JsonSerializer.SerializeToElement(attemptNumber);
        var writes = new List<(VariableModel Target, JsonElement Value)>();
        var flowInfo = snapshot.FlowInfo?.Deserialize<SequenceFlowInfoSnapshot>();

        if (string.Equals(node.ScriptFormat, ScriptFormats.JavaScript, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(node.Script))
            {
                throw new WorkflowDomainException(
                    $"Script task #{node.Id} failed: the JavaScript body is missing.");
            }
            var context = new EngineScriptContext(overlay, declared, writes, flowInfo);
            var result = scriptEvaluator.Evaluate(node.Script, context, cancellationToken);
            if (!result.Success)
            {
                throw new WorkflowDomainException(
                    $"Script task #{node.Id} failed: {result.Error}");
            }
        }
        else
        {
            foreach (var assignment in node.Assignments)
            {
                if (assignment is null
                    || string.IsNullOrWhiteSpace(assignment.Variable)
                    || !declared.TryGetValue(assignment.Variable, out var target))
                {
                    throw new WorkflowDomainException(
                        $"Script task #{node.Id} has an invalid assignment target.");
                }
                var raw = SequenceFlowConditionEvaluator.EvaluateValue(
                    assignment.Expression,
                    overlay,
                    flowInfo: flowInfo);
                var value = CoerceScriptValue(raw, target);
                overlay[target.Name!] = value;
                writes.Add((target, value));
            }
        }

        foreach (var target in writes.Select(write => write.Target).Distinct())
        {
            if (string.IsNullOrWhiteSpace(target.Validation)
                || target.Nullable
                   && overlay.TryGetValue(target.Name!, out var nullableValue)
                   && nullableValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }
            if (!SequenceFlowConditionEvaluator.Evaluate(target.Validation, overlay))
            {
                throw new WorkflowDomainException(
                    $"Variable '{target.Name}' failed validation: '{target.Validation}'.");
            }
        }

        var finalWrites = new Dictionary<string, JsonElement>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var write in writes)
        {
            finalWrites[write.Target.Name!] = write.Value;
        }
        return finalWrites;
    }

    private async Task EnsureOutputVersionsCurrentAsync(
        long instanceId,
        IReadOnlyDictionary<string, long> expected,
        CancellationToken cancellationToken)
    {
        if (expected.Count == 0)
        {
            return;
        }
        var actual = (await runtime.LoadLatestVariableVersionsAsync(
                instanceId,
                cancellationToken))
            .ToDictionary(
                item => item.Name,
                item => item.Version,
                StringComparer.OrdinalIgnoreCase);
        foreach (var pair in expected)
        {
            if (actual.GetValueOrDefault(pair.Key) != pair.Value)
            {
                throw new WorkflowOutputVersionConflictException(
                    $"Output variable '{pair.Key}' changed while async work was running.");
            }
        }
    }

    private static ServiceTaskResult ReadServiceTaskResult(WorkflowJobRecord job)
    {
        if (job.Result is not JsonElement result
            || result.Deserialize<ServiceTaskResult>() is not { } parsed)
        {
            throw new WorkflowJobInvariantException(
                "The service-task job has no readable staged result.");
        }
        return parsed;
    }

    private static bool ShouldRetryBeforeBoundary(
        WorkflowJobRecord job,
        WorkflowModel definition,
        FlowNodeModel node) =>
        job.AttemptCount < job.MaxAttempts
        && (job.FailureHandling == WorkflowJobFailureHandling.RetryFirst
            || FindErrorBoundary(definition, node.Id) is null);

    private static ExecutionTokenRecord RequireFencedToken(
        WorkflowInstanceRecord instance,
        WorkflowJobRecord job,
        ExecutionTokenRecord? token)
    {
        if (instance.Status != WorkflowInstanceStatuses.Running)
        {
            throw new WorkflowConflictException("The workflow instance is no longer running.");
        }
        if (token is null
            || token.InstanceId != instance.Id
            || token.Status != ExecutionTokenRecordStatuses.Active
            || token.ActivationId != job.ActivationId
            || token.NodeId != job.NodeId && job.Kind != WorkflowJobKinds.TimerBoundary
            || IsGuardedAutomaticActivity(job.NodeType)
               && job.Kind is WorkflowJobKinds.AsyncBefore or WorkflowJobKinds.AsyncAfter
               && token.AutomaticActivationCount != job.AutomaticActivationCount)
        {
            throw new WorkflowConflictException(
                "The workflow job no longer owns the token activation.");
        }

        var expectedWait = job.Kind switch
        {
            WorkflowJobKinds.AsyncBefore => ExecutionTokenWaitStates.AsyncBefore,
            WorkflowJobKinds.AsyncAfter => ExecutionTokenWaitStates.AsyncAfter,
            WorkflowJobKinds.Timer => ExecutionTokenWaitStates.TimerCatch,
            WorkflowJobKinds.ConditionalWake => ExecutionTokenWaitStates.ConditionalWake,
            _ => null
        };
        if (expectedWait is not null
            && (token.WaitState != expectedWait
                || token.WaitingJobId != job.Id
                || job.Kind == WorkflowJobKinds.Timer
                   && token.WaitingTimerSubscriptionId != job.TimerSubscriptionId))
        {
            throw new WorkflowConflictException(
                "The workflow job no longer owns the token wait.");
        }
        return token;
    }

    private static void EnsureJobFence(
        WorkflowJobRecord job,
        WorkflowJobFence fence)
    {
        var expectedPhase = job.Kind switch
        {
            WorkflowJobKinds.AsyncBefore => WorkflowJobKinds.AsyncBefore,
            WorkflowJobKinds.AsyncAfter => WorkflowJobKinds.AsyncAfter,
            WorkflowJobKinds.Timer
                or WorkflowJobKinds.TimerBoundary
                or WorkflowJobKinds.TimerStart => WorkflowJobKinds.Timer,
            WorkflowJobKinds.ConditionalWake => WorkflowJobKinds.ConditionalWake,
            _ => null
        };
        if (job.Id != fence.JobId
            || job.WorkerId != fence.WorkerId
            || job.LeaseToken != fence.LeaseToken
            || job.LeaseGeneration != fence.LeaseGeneration
            || expectedPhase is not null
               && !string.Equals(job.Phase, expectedPhase, StringComparison.Ordinal)
            || job.Status is not (WorkflowJobStatuses.Running
                or WorkflowJobStatuses.ResultReady))
        {
            throw new WorkflowConflictException("The workflow job lease fence is stale.");
        }
    }

    private async Task<bool> PauseRecurringTimerBoundaryForCapacityAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        WorkflowJobCapacityExceededException capacity,
        CancellationToken cancellationToken)
    {
        if (lease.Job.Kind != WorkflowJobKinds.TimerBoundary
            || lease.Job.InstanceId is not long instanceId
            || lease.Job.TokenId is not long tokenId
            || lease.Job.TimerSubscriptionId is not long subscriptionId)
        {
            return false;
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await GetInstanceForJobUpdateAsync(
            instanceId,
            cancellationToken)
            ?? throw new WorkflowConflictException("The workflow instance no longer exists.");
        var token = await runtime.GetExecutionTokenAsync(
            tokenId,
            true,
            cancellationToken);
        var job = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow job no longer exists.");
        EnsureJobFence(job, fence);
        token = RequireFencedToken(instance, job, token);
        var subscription = await timerSubscriptions.GetForUpdateAsync(
            subscriptionId,
            cancellationToken)
            ?? throw new WorkflowConflictException("The timer subscription no longer exists.");
        EnsureTimerSubscriptionFence(subscription, token, job);

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var boundary = GetFlowNode(workflow.Definition, job.NodeId);
        if (subscription.CancelActivity
            || !BpmnFlowNodeTypes.IsTimerBoundary(boundary.Type)
            || !string.Equals(
                subscription.ScheduleKind,
                TimerScheduleKinds.Cycle,
                StringComparison.Ordinal)
            || !TimerDefinitionRules.TryParseTimeCycle(
                boundary.Timer?.TimeCycle,
                out _,
                out _))
        {
            return false;
        }

        if (!await timerSubscriptions.PauseAsync(
                subscription.Id,
                subscription.Occurrence,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The recurring timer subscription changed before it could be paused.");
        }

        var incident = await jobs.OpenIncidentAsync(
            fence,
            "job_capacity_exceeded",
            $"Recurring timer boundary #{boundary.Id} was paused because the instance reached its open-job limit.",
            LimitJobFailure(capacity.Message),
            cancellationToken);
        if (incident is null)
        {
            throw new WorkflowConflictException(
                "The workflow job lease was lost before its capacity incident was created.");
        }
        WorkflowJobRuntimeTelemetry.RecordIncident();

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogWarning(
            "Paused recurring non-interrupting timer subscription {SubscriptionId} on instance {InstanceId}; open jobs {OpenJobs}, limit {Limit}.",
            subscription.Id,
            instance.Id,
            capacity.OpenJobs,
            capacity.Limit);
        return true;
    }

    private async Task HandleProcessingFailureAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        string code,
        string description,
        CancellationToken cancellationToken)
    {
        if (lease.AttemptNumber < lease.Job.MaxAttempts
            && lease.AttemptNumber <= lease.Job.RetryDelays.Count)
        {
            var delay = AddDeterministicRetryJitter(
                lease.Job.RetryDelays[lease.AttemptNumber - 1],
                lease.Job.Id,
                lease.AttemptNumber);
            var dueAt = timeProvider.GetUtcNow() + delay;
            if (await jobs.ScheduleRetryAsync(
                    fence,
                    dueAt,
                    new WorkflowJobResultRecord(null, null, code, description),
                    cancellationToken))
            {
                WorkflowJobRuntimeTelemetry.RecordRetry();
                logger.LogWarning(
                    "Workflow job {JobId} attempt {Attempt} failed; retry scheduled for {DueAt}.",
                    lease.Job.Id,
                    lease.AttemptNumber,
                    dueAt);
                return;
            }
        }

        if (await jobs.OpenIncidentAsync(
                fence,
                code,
                $"Workflow job failed at node #{lease.Job.NodeId}.",
                description,
                cancellationToken) is not null)
        {
            WorkflowJobRuntimeTelemetry.RecordIncident();
        }
    }

    private async Task<WorkflowInstanceRecord?> GetInstanceForJobUpdateAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await runtime.GetInstanceForUpdateAsync(
                instanceId,
                false,
                cancellationToken);
        }
        finally
        {
            WorkflowJobRuntimeTelemetry.RecordInstanceLockWait(
                Stopwatch.GetElapsedTime(started));
        }
    }

    private static TimeSpan AddDeterministicRetryJitter(
        TimeSpan delay,
        long jobId,
        int attempt)
    {
        // Stable across replicas/restarts while spreading retries across a
        // bounded +/-10% window. Timer occurrence due times never use this path.
        var mixed = unchecked((ulong)jobId * 11400714819323198485UL)
                    ^ unchecked((uint)attempt * 2654435761U);
        var basisPoints = (int)(mixed % 2001UL) - 1000;
        var adjustment = delay.Ticks * (long)basisPoints / 10_000L;
        return TimeSpan.FromTicks(Math.Max(1, delay.Ticks + adjustment));
    }

    private static string LimitJobFailure(string value)
    {
        const int maxLength = 1000;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed class WorkflowOutputVersionConflictException(string message)
        : InvalidOperationException(message);

    private sealed class TimerMisfireExhaustedException(string message)
        : InvalidOperationException(message);

    private sealed class RetryableWorkflowJobException(string message)
        : InvalidOperationException(message);

    private sealed class WorkflowJobInvariantException(string message)
        : InvalidOperationException(message);

    private sealed class WorkflowJobCapacityExceededException(
        long instanceId,
        long openJobs,
        long limit,
        string message)
        : WorkflowConflictException(message)
    {
        public long InstanceId { get; } = instanceId;
        public long OpenJobs { get; } = openJobs;
        public long Limit { get; } = limit;
    }

    private async Task FireConditionalWakeAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        WorkflowJobRecord job,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        if (!BpmnFlowNodeTypes.IsConditionalCatch(node.Type)
            || node.Conditional?.EffectiveDeliveryMode
               != ConditionalEventDeliveryModes.DurableAsync
            || job.TimerSubscriptionId is not null)
        {
            throw new WorkflowJobInvariantException(
                "The conditional-wake job shape is invalid.");
        }
        var payload = ReadJobPayload(job);
        if (payload?.SelectedFlowId is not int selectedFlowId)
        {
            throw new WorkflowJobInvariantException(
                "The conditional-wake job has no latched sequence flow.");
        }
        var outgoing = OutgoingFlows(
            instance.WorkflowDefinitionId,
            definition,
            node.Id).Take(2).ToList();
        if (outgoing.Count != 1
            || outgoing[0].Id != selectedFlowId
            || outgoing[0].SourceRef != node.Id)
        {
            throw new WorkflowJobInvariantException(
                $"Conditional-wake job #{job.Id} no longer matches the event's sole outgoing sequence flow.");
        }
        if (!await runtime.ClearExecutionTokenWaitAsync(
                token.Id,
                token.ActivationId,
                ExecutionTokenWaitStates.ConditionalWake,
                job.Id,
                null,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The conditional-wake job no longer owns the token wait.");
        }

        // The truth transition was durably latched by the variable writer. Do
        // not evaluate it again here: a later write may legitimately have made
        // the expression false before this worker acquired the instance lock.
        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var flowInfo = await LoadSequenceFlowInfoAsync(instance.Id, definition, cancellationToken);
        var queue = new Queue<long>();
        await jobs.CancelOtherJobsByTokenIdsAsync(
            instance.Id,
            [token.Id],
            job.Id,
            "conditionalWakeCompleted",
            cancellationToken);
        await AdvanceAutomaticTokenAsync(
            instance,
            token,
            token.GatewayBranchId,
            node,
            outgoing[0],
            InstanceHistoryNotes.ConditionalTriggered,
            definition,
            actor,
            stored,
            flowInfo,
            queue,
            cancellationToken,
            historyPayload: ConditionalHistoryPayload(
                ConditionalEventDeliveryModes.DurableAsync,
                outgoing[0].Id,
                payload.TriggeringVariableNames ?? [],
                job.Id));
        logger.LogInformation(
            "Durable conditional event triggered for instance {InstanceId}, token {TokenId}, node {NodeId}, job {JobId}.",
            instance.Id,
            token.Id,
            node.Id,
            job.Id);

        if (await IsInstanceRunningAsync(instance.Id, cancellationToken))
        {
            var resumed = await runtime.GetInstanceAsync(instance.Id, cancellationToken)
                ?? instance;
            resumed = await ResolvePassThroughAsync(
                resumed,
                definition,
                actor,
                flowInfo,
                token.Id,
                cancellationToken,
                forceDurableActivities: true);
            await EnsureMultiInstanceInitializedAsync(
                resumed,
                definition,
                actor,
                cancellationToken);
            _ = await ApplyUserTaskOwnershipInheritanceAsync(
                resumed,
                definition,
                cancellationToken);
        }
    }

    private async Task FireTimerCatchAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        WorkflowJobRecord job,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        if (!BpmnFlowNodeTypes.IsTimerCatch(node.Type)
            || job.TimerSubscriptionId is not long subscriptionId)
        {
            throw new WorkflowJobInvariantException("The timer-catch job shape is invalid.");
        }
        var subscription = await timerSubscriptions.GetForUpdateAsync(
            subscriptionId,
            cancellationToken)
            ?? throw new WorkflowJobInvariantException("The timer subscription no longer exists.");
        EnsureTimerSubscriptionFence(subscription, token, job);
        var now = timeProvider.GetUtcNow();
        if (IsRecurringTimerMisfire(subscription, job, now))
        {
            await SkipMisfiredTimerCatchAsync(
                instance,
                token,
                job,
                node,
                subscription,
                actor,
                now.AddTicks(1),
                cancellationToken);
            return;
        }

        if (!await runtime.ClearExecutionTokenWaitAsync(
                token.Id,
                token.ActivationId,
                ExecutionTokenWaitStates.TimerCatch,
                job.Id,
                subscription.Id,
                cancellationToken))
        {
            throw new WorkflowConflictException("The timer job no longer owns the token wait.");
        }

        var outgoing = OutgoingFlows(
            instance.WorkflowDefinitionId,
            definition,
            node.Id).Take(2).ToList();
        if (outgoing.Count != 1)
        {
            throw new WorkflowDomainException(
                $"Timer catch event #{node.Id} must have exactly one outgoing sequence flow.");
        }
        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var flowInfo = await LoadSequenceFlowInfoAsync(instance.Id, definition, cancellationToken);
        var queue = new Queue<long>();
        var timerActor = TimerActor();
        // Leaving the catch host retires every attached boundary clock and its
        // outstanding occurrence, while preserving this primary catch job until
        // it is atomically completed below.
        await timerSubscriptions.CancelOtherForTokenAsync(
            instance.Id,
            token.Id,
            subscription.Id,
            cancellationToken);
        await jobs.CancelOtherJobsByTokenIdsAsync(
            instance.Id,
            [token.Id],
            job.Id,
            "timerCatchCompleted",
            cancellationToken);
        await AdvanceAutomaticTokenAsync(
            instance,
            token,
            token.GatewayBranchId,
            node,
            outgoing[0],
            "timer",
            definition,
            timerActor,
            stored,
            flowInfo,
            queue,
            cancellationToken);
        if (!await timerSubscriptions.AdvanceAsync(
                subscription.Id,
                subscription.Occurrence,
                subscription.Occurrence + 1,
                subscription.NextDueAt,
                complete: true,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The timer-catch subscription changed while it was firing.");
        }

        if (await IsInstanceRunningAsync(instance.Id, cancellationToken))
        {
            var resumed = await runtime.GetInstanceAsync(instance.Id, cancellationToken)
                ?? instance;
            resumed = await ResolvePassThroughAsync(
                resumed,
                definition,
                timerActor,
                flowInfo,
                token.Id,
                cancellationToken,
                forceDurableActivities: true);
            await EnsureMultiInstanceInitializedAsync(
                resumed,
                definition,
                timerActor,
                cancellationToken);
            _ = await ApplyUserTaskOwnershipInheritanceAsync(
                resumed,
                definition,
                cancellationToken);
        }
    }

    private async Task SkipMisfiredTimerCatchAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        WorkflowJobRecord job,
        FlowNodeModel timerNode,
        TimerSubscriptionRecord subscription,
        ActorContext actor,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken)
    {
        var timer = timerNode.Timer
            ?? throw new WorkflowDomainException(
                $"Timer catch event #{timerNode.Id} has no timer configuration.");
        var next = ResolveNextOccurrence(timer, subscription, notBefore);
        if (next is null)
        {
            throw new TimerMisfireExhaustedException(
                $"Recurring timer catch #{timerNode.Id} has no occurrence at or after "
                + $"{notBefore:o}; occurrence {subscription.Occurrence} due "
                + $"{subscription.NextDueAt:o} was skipped.");
        }

        if (!await runtime.ClearExecutionTokenWaitAsync(
                token.Id,
                token.ActivationId,
                ExecutionTokenWaitStates.TimerCatch,
                job.Id,
                subscription.Id,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The misfired timer job no longer owns the token wait.");
        }

        var (occurrence, dueAt) = next.Value;
        if (!await timerSubscriptions.AdvanceAsync(
                subscription.Id,
                subscription.Occurrence,
                occurrence,
                dueAt,
                complete: false,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The recurring timer-catch subscription changed while skipping a misfire.");
        }

        var advanced = subscription with
        {
            Occurrence = occurrence,
            NextDueAt = dueAt
        };
        var nextJob = await EnqueueInstanceJobAsync(
            BuildTimerJob(
                instance,
                token,
                timerNode,
                advanced,
                WorkflowJobKinds.Timer,
                actor,
                dueAt),
            cancellationToken);
        if (!await runtime.SetExecutionTokenWaitAsync(
                token.Id,
                token.ActivationId,
                ExecutionTokenWaitStates.TimerCatch,
                nextJob.Id,
                subscription.Id,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The execution token changed while its timer misfire was being skipped.");
        }
    }

    private async Task FireTimerBoundaryAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord hostToken,
        WorkflowJobRecord job,
        FlowNodeModel boundary,
        WorkflowModel definition,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        if (!BpmnFlowNodeTypes.IsTimerBoundary(boundary.Type)
            || job.TimerSubscriptionId is not long subscriptionId)
        {
            throw new WorkflowJobInvariantException("The timer-boundary job shape is invalid.");
        }
        var subscription = await timerSubscriptions.GetForUpdateAsync(
            subscriptionId,
            cancellationToken)
            ?? throw new WorkflowJobInvariantException("The timer subscription no longer exists.");
        EnsureTimerSubscriptionFence(subscription, hostToken, job);
        if (subscription.AttachedToNodeId != hostToken.NodeId
            || boundary.AttachedToRef != hostToken.NodeId)
        {
            throw new WorkflowConflictException(
                "The timer boundary host activation is no longer current.");
        }
        var now = timeProvider.GetUtcNow();
        if (IsRecurringTimerMisfire(subscription, job, now))
        {
            await ScheduleNextTimerOccurrenceAsync(
                instance,
                hostToken,
                boundary,
                subscription,
                actor,
                cancellationToken,
                now.AddTicks(1));
            return;
        }

        // Consume the exact interrupting occurrence before routing. Boundary
        // traversal performs generic attached-timer cleanup on the host token;
        // leaving this subscription active until after routing would let that
        // cleanup cancel the very occurrence being fired. This update remains
        // in the same instance transaction, so downstream failure rolls it back.
        if (subscription.CancelActivity
            && !await timerSubscriptions.AdvanceAsync(
                subscription.Id,
                subscription.Occurrence,
                subscription.Occurrence + 1,
                subscription.NextDueAt,
                complete: true,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The interrupting timer subscription changed while it was firing.");
        }

        var timerActor = TimerActor();
        long continuationTokenId;
        if (subscription.CancelActivity)
        {
            var ids = new[] { hostToken.Id };
            var completionActor = ToNodeExecutionActor(timerActor);
            await runtime.CancelOpenUserTasksForTokensAsync(
                ids,
                NodeExecutionCompletionReasons.TimerFired,
                completionActor,
                cancellationToken);
            await runtime.CancelActiveMultiInstancesForTokensAsync(
                ids,
                NodeExecutionCompletionReasons.TimerFired,
                completionActor,
                cancellationToken);
            await timerSubscriptions.CancelOtherForTokenAsync(
                instance.Id,
                hostToken.Id,
                subscription.Id,
                cancellationToken);
            await jobs.CancelOtherJobsByTokenIdsAsync(
                instance.Id,
                ids,
                job.Id,
                "interruptingTimerBoundary",
                cancellationToken);

            await runtime.AddTokenHistoryAsync(
                instance.Id,
                hostToken.Id,
                null,
                hostToken.NodeId,
                boundary.Id,
                timerActor.User,
                null,
                "timer",
                cancellationToken);
            await runtime.UpdateExecutionTokenAsync(
                hostToken.Id,
                ToSnapshot(boundary),
                ExecutionTokenRecordStatuses.Active,
                hostToken.GatewayBranchId,
                null,
                null,
                null,
                completionActor,
                new NodeExecutionCompletionRecord(
                    NodeExecutionRecordStatuses.Cancelled,
                    NodeExecutionCompletionReasons.TimerFired,
                    null,
                    null,
                    hostToken.GatewayBranchId,
                    completionActor),
                cancellationToken,
                automaticActivationCount:
                    WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger());
            continuationTokenId = hostToken.Id;
        }
        else
        {
            var sibling = await runtime.AddExecutionTokenAsync(
                instance.Id,
                ToSnapshot(boundary),
                hostToken.GatewayBranchId,
                null,
                ToNodeExecutionActor(timerActor),
                cancellationToken,
                automaticActivationCount:
                    WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger(),
                automaticActivationStateIds: hostToken.AutomaticActivationStateIds);
            await runtime.AddTokenHistoryAsync(
                instance.Id,
                sibling.Id,
                null,
                hostToken.NodeId,
                boundary.Id,
                timerActor.User,
                null,
                "timer",
                cancellationToken);
            continuationTokenId = sibling.Id;
        }

        var flowInfo = await LoadSequenceFlowInfoAsync(instance.Id, definition, cancellationToken);
        var resumed = await ResolvePassThroughAsync(
            instance,
            definition,
            timerActor,
            flowInfo,
            continuationTokenId,
            cancellationToken,
            forceDurableActivities: true);
        await EnsureMultiInstanceInitializedAsync(
            resumed,
            definition,
            timerActor,
            cancellationToken);
        _ = await ApplyUserTaskOwnershipInheritanceAsync(
            resumed,
            definition,
            cancellationToken);

        if (!subscription.CancelActivity)
        {
            var currentHost = await runtime.GetExecutionTokenAsync(
                hostToken.Id,
                false,
                cancellationToken);
            if (currentHost is not null
                && currentHost.Status == ExecutionTokenRecordStatuses.Active
                && currentHost.ActivationId == hostToken.ActivationId)
            {
                await ScheduleNextTimerOccurrenceAsync(
                    instance,
                    currentHost,
                    boundary,
                    subscription,
                    actor,
                    cancellationToken);
            }
        }
    }

    private async Task ProcessTimerStartJobAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // Match default-version mutation ordering: workflow family first, then
        // the occurrence job and its subscription. StartInstanceCoreAsync takes
        // the same shared transaction-scoped family lock reentrantly.
        await definitions.LockFamilyForStartAsync(
            lease.Job.WorkflowKey,
            cancellationToken);
        var job = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
            ?? throw new WorkflowConflictException("The timer-start job no longer exists.");
        EnsureJobFence(job, fence);
        if (job.TimerSubscriptionId is not long subscriptionId)
        {
            throw new WorkflowJobInvariantException("The timer-start job has no subscription.");
        }
        var subscription = await timerSubscriptions.GetForUpdateAsync(
            subscriptionId,
            cancellationToken)
            ?? throw new WorkflowJobInvariantException("The timer-start subscription no longer exists.");
        if (subscription.Status != TimerSubscriptionStatuses.Active
            || subscription.ActivationId != job.ActivationId
            || subscription.TimerNodeId != job.NodeId
            || job.ScheduledOccurrenceAt != subscription.NextDueAt)
        {
            throw new WorkflowConflictException(
                "The timer-start subscription is no longer active.");
        }

        var workflow = await definitions.GetDefaultByWorkflowKeyAsync(
            job.WorkflowKey,
            cancellationToken)
            ?? throw new WorkflowConflictException(
                "The workflow family no longer has a published default definition.");
        if (workflow.Id != job.WorkflowDefinitionId
            || !workflow.IsPublished
            || !workflow.IsDefault)
        {
            throw new WorkflowConflictException(
                "The timer-start definition is no longer the published default.");
        }

        var now = timeProvider.GetUtcNow();
        if (IsRecurringTimerMisfire(subscription, job, now))
        {
            await ScheduleNextTimerStartOccurrenceAsync(
                workflow,
                subscription,
                cancellationToken,
                now.AddTicks(1));
            if (!await jobs.CompleteAsync(fence, cancellationToken))
            {
                throw new WorkflowConflictException(
                    "The timer-start job lease was lost before completion.");
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        _ = await StartInstanceCoreAsync(
            null,
            job.WorkflowKey,
            TimerActor(),
            job.NodeId,
            null,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            cancellationToken,
            allowTimerStart: true,
            joinAmbientTransaction: true,
            forceDurableActivities: true,
            requiredDefaultWorkflowId: job.WorkflowDefinitionId);
        await ScheduleNextTimerStartOccurrenceAsync(
            workflow,
            subscription,
            cancellationToken);
        if (!await jobs.CompleteAsync(fence, cancellationToken))
        {
            throw new WorkflowConflictException(
                "The timer-start job lease was lost before completion.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ScheduleNextTimerOccurrenceAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord hostToken,
        FlowNodeModel timerNode,
        TimerSubscriptionRecord subscription,
        ActorContext actor,
        CancellationToken cancellationToken,
        DateTimeOffset? notBefore = null)
    {
        var next = ResolveNextOccurrence(
            timerNode.Timer!,
            subscription,
            notBefore ?? timeProvider.GetUtcNow() - TimerMisfireGrace);
        if (next is null)
        {
            if (!await timerSubscriptions.AdvanceAsync(
                    subscription.Id,
                    subscription.Occurrence,
                    subscription.Occurrence + 1,
                    subscription.NextDueAt,
                    complete: true,
                    cancellationToken))
            {
                throw new WorkflowConflictException(
                    "The recurring timer subscription changed while completing.");
            }
            return;
        }
        var (occurrence, dueAt) = next.Value;
        if (!await timerSubscriptions.AdvanceAsync(
                subscription.Id,
                subscription.Occurrence,
                occurrence,
                dueAt,
                complete: false,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The recurring timer subscription changed while advancing.");
        }
        var advanced = subscription with
        {
            Occurrence = occurrence,
            NextDueAt = dueAt
        };
        await EnqueueInstanceJobAsync(
            BuildTimerJob(
                instance,
                hostToken,
                timerNode,
                advanced,
                WorkflowJobKinds.TimerBoundary,
                actor,
                dueAt),
            cancellationToken);
    }

    private async Task ScheduleNextTimerStartOccurrenceAsync(
        WorkflowDefinitionRecord workflow,
        TimerSubscriptionRecord subscription,
        CancellationToken cancellationToken,
        DateTimeOffset? notBefore = null)
    {
        var node = GetFlowNode(workflow.Definition, subscription.TimerNodeId);
        var next = ResolveNextOccurrence(
            node.Timer!,
            subscription,
            notBefore ?? timeProvider.GetUtcNow() - TimerMisfireGrace);
        if (next is null)
        {
            if (!await timerSubscriptions.AdvanceAsync(
                    subscription.Id,
                    subscription.Occurrence,
                    subscription.Occurrence + 1,
                    subscription.NextDueAt,
                    complete: true,
                    cancellationToken))
            {
                throw new WorkflowConflictException(
                    "The timer-start subscription changed while completing.");
            }
            return;
        }
        var (occurrence, dueAt) = next.Value;
        if (!await timerSubscriptions.AdvanceAsync(
                subscription.Id,
                subscription.Occurrence,
                occurrence,
                dueAt,
                complete: false,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The timer-start subscription changed while advancing.");
        }
        await jobs.EnqueueAsync(
            new WorkflowJobCreateRecord
            {
                WorkflowDefinitionId = workflow.Id,
                WorkflowKey = workflow.WorkflowKey,
                TimerSubscriptionId = subscription.Id,
                ActivationId = subscription.ActivationId,
                NodeId = node.Id,
                NodeName = node.Name,
                NodeType = node.Type,
                Kind = WorkflowJobKinds.TimerStart,
                QueueClass = WorkflowJobClasses.Control,
                Phase = WorkflowJobKinds.Timer,
                DueAt = dueAt,
                Priority = DefaultJobPriority,
                MaxAttempts = DefaultRetryDelays.Count + 1,
                RetryDelays = DefaultRetryDelays,
                ScheduledOccurrenceAt = dueAt
            },
            cancellationToken);
    }

    private static bool IsRecurringTimerMisfire(
        TimerSubscriptionRecord subscription,
        WorkflowJobRecord job,
        DateTimeOffset now) =>
        WorkflowTimerSchedule.IsRecurringMisfire(
            subscription.ScheduleKind,
            job.ScheduledOccurrenceAt,
            now,
            TimerMisfireGrace);

    private (long Occurrence, DateTimeOffset DueAt)? ResolveNextOccurrence(
        TimerDefinitionModel timer,
        TimerSubscriptionRecord subscription,
        DateTimeOffset notBefore)
    {
        var next = WorkflowTimerSchedule.ResolveNextCycleOccurrence(
            timer,
            subscription.Occurrence,
            subscription.NextDueAt,
            notBefore);
        return next is null
            ? null
            : (next.Occurrence, next.DueAt);
    }

    private static void EnsureTimerSubscriptionFence(
        TimerSubscriptionRecord subscription,
        ExecutionTokenRecord token,
        WorkflowJobRecord job)
    {
        if (subscription.Status != TimerSubscriptionStatuses.Active
            || subscription.TokenId != token.Id
            || subscription.ActivationId != token.ActivationId
            || subscription.Id != job.TimerSubscriptionId
            || subscription.TimerNodeId != job.NodeId
            || job.ScheduledOccurrenceAt != subscription.NextDueAt)
        {
            throw new WorkflowConflictException(
                "The timer subscription no longer owns the token activation.");
        }
    }

    private static ActorContext TimerActor() =>
        new(
            "timer",
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}
