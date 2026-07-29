using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class GenericGatewayApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ActorRoles = ["User"];

    [Fact]
    public async Task ExclusiveMerge_PassesEachArrivalWithoutSynchronizing()
    {
        var workflowId = await CreateWorkflowAsync(CreateExclusiveMergeWorkflow());
        var started = await StartAsync(workflowId);

        var initialTasks = await ListActiveTasksAsync(started.Id);
        Assert.Equal([3, 4], initialTasks.Select(task => task.NodeId).OrderBy(id => id));

        var firstBranch = initialTasks.Single(task => task.NodeId == 3);
        var firstArrival = await TakeTaskAsync(firstBranch, 301);

        Assert.Equal("running", firstArrival.InstanceStatus);
        var afterFirstArrival = await ListActiveTasksAsync(started.Id);
        Assert.Equal(
            [4, 6],
            afterFirstArrival.Select(task => task.NodeId).OrderBy(id => id));

        var secondBranch = afterFirstArrival.Single(task => task.NodeId == 4);
        var secondArrival = await TakeTaskAsync(secondBranch, 401);

        Assert.Equal("running", secondArrival.InstanceStatus);
        var downstreamTasks = await ListActiveTasksAsync(started.Id);
        Assert.Equal(2, downstreamTasks.Count);
        Assert.All(downstreamTasks, task => Assert.Equal(6, task.NodeId));
        Assert.Equal(2, downstreamTasks.Select(task => task.TokenId).Distinct().Count());

        var firstCompletion = await TakeTaskAsync(downstreamTasks[0], 601);
        Assert.Equal("running", firstCompletion.InstanceStatus);
        var secondCompletion = await TakeTaskAsync(downstreamTasks[1], 601);
        Assert.Equal("completed", secondCompletion.InstanceStatus);

        var detail = await GetInstanceAsync(started.Id);
        Assert.Equal(2, detail.History.Count(entry =>
            entry.FromNodeId == 5
            && entry.ToNodeId == 6
            && entry.Note == "gateway"));
    }

    [Fact]
    public async Task ParallelJoin_TerminateAfterFirstSurplusBatch_DoesNotResurrectTokens()
    {
        var workflowId = await CreateWorkflowAsync(
            CreateSurplusParallelJoinTerminateWorkflow());
        var started = await StartAsync(workflowId);

        Assert.Equal("completed", started.Status);
        Assert.Empty(await ListActiveTasksAsync(started.Id));
        var detail = await GetInstanceAsync(started.Id);
        Assert.Single(detail.GatewayExecutions, execution =>
            execution.GatewayNodeId == 9
            && execution.Direction == GatewayExecutionDirections.Merge);
        Assert.DoesNotContain(detail.ExecutionPositions, position =>
            position.TokenStatus == ExecutionTokenStatuses.Active);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task InclusiveSplit_SelectsEveryMatchOrDefault_AndMergeUsesReachability(
        bool takeA,
        bool takeB)
    {
        var workflowId = await CreateWorkflowAsync(CreateInclusiveMergeWorkflow(takeA, takeB));
        var started = await StartAsync(workflowId);

        Assert.Equal("running", started.Status);
        var task = Assert.Single(await ListActiveTasksAsync(started.Id));
        Assert.Equal(7, task.NodeId);

        var expectedSelectedFlows = takeA || takeB
            ? new[] { (takeA, 201), (takeB, 202) }
                .Where(item => item.Item1)
                .Select(item => item.Item2)
                .ToArray()
            : [203];
        var split = Assert.Single(started.GatewayExecutions, execution =>
            execution.GatewayNodeId == 2
            && execution.Direction == GatewayExecutionDirections.Split);
        Assert.Equal(BpmnFlowNodeTypes.InclusiveGateway, split.GatewayType);
        Assert.Equal(expectedSelectedFlows, split.SelectedFlowIds);

        var merge = Assert.Single(started.GatewayExecutions, execution =>
            execution.GatewayNodeId == 6
            && execution.Direction == GatewayExecutionDirections.Merge);
        Assert.Equal(BpmnFlowNodeTypes.InclusiveGateway, merge.GatewayType);
        Assert.Equal([601], merge.SelectedFlowIds);
        Assert.Equal(GatewayExecutionStatuses.Joined, merge.Status);
        Assert.Equal(expectedSelectedFlows.Length, started.History.Count(entry =>
            entry.Note == "inclusiveSplit"));
        Assert.Single(started.History, entry => entry.Note == "inclusiveMerge");

        var completed = await TakeTaskAsync(task, 701);
        Assert.Equal("completed", completed.InstanceStatus);
    }

    [Fact]
    public async Task ComplexSplit_WaitsForTopologyReset_ThenZeroOutputCompletes()
    {
        var workflowId = await CreateWorkflowAsync(CreateComplexCycleWorkflow());
        var started = await StartAsync(workflowId);

        var firstTask = Assert.Single(await ListActiveTasksAsync(started.Id));
        Assert.Equal(4, firstTask.NodeId);
        var initialState = Assert.Single(started.ComplexGatewayStates);
        Assert.Equal(3, initialState.GatewayNodeId);
        Assert.Equal(ComplexGatewayStatePhases.WaitingForReset, initialState.Phase);
        Assert.Equal(0, initialState.Cycle);
        Assert.Equal([201], initialState.ContributingFlowIds);
        Assert.Equal([201], initialState.RemainingFlowIds);

        var firstActivation = Assert.Single(started.GatewayExecutions, execution =>
            execution.GatewayNodeId == 3);
        Assert.Equal(BpmnFlowNodeTypes.ComplexGateway, firstActivation.GatewayType);
        Assert.Equal("start", firstActivation.Phase);
        Assert.Equal(0, firstActivation.Cycle);
        Assert.Equal([301], firstActivation.SelectedFlowIds);
        Assert.DoesNotContain(started.GatewayExecutions, execution =>
            execution.GatewayNodeId == 3 && execution.Phase == "reset");

        var looped = await TakeTaskAsync(firstTask, 401);
        Assert.Equal("completed", looped.InstanceStatus);
        Assert.Empty(await ListActiveTasksAsync(started.Id));

        var detail = await GetInstanceAsync(started.Id);
        var state = Assert.Single(detail.ComplexGatewayStates);
        Assert.Equal(ComplexGatewayStatePhases.WaitingForStart, state.Phase);
        Assert.Equal(1, state.Cycle);
        Assert.Equal(
            [0],
            detail.GatewayExecutions
                .Where(execution =>
                    execution.GatewayNodeId == 3
                    && execution.Phase == "start")
                .Select(execution => Assert.IsType<int>(execution.Cycle))
                .OrderBy(cycle => cycle));
        var reset = Assert.Single(detail.GatewayExecutions, execution =>
            execution.GatewayNodeId == 3
            && execution.Phase == "reset");
        Assert.Equal(0, reset.Cycle);
        Assert.Empty(reset.SelectedFlowIds);
        Assert.Equal(GatewayExecutionStatuses.Completed, reset.Status);
        Assert.Equal("resetNoOutput", reset.CompletionReason);
        Assert.Single(detail.History, entry => entry.Note == "complexActivation");
        Assert.DoesNotContain(detail.History, entry => entry.Note == "complexReset");
    }

    [Fact]
    public async Task ComplexSplit_ResetOutputStartsRepeatedCycles()
    {
        var workflowId = await CreateWorkflowAsync(CreateComplexResetRoutingWorkflow());
        var started = await StartAsync(workflowId);

        var startWork = Assert.Single(await ListActiveTasksAsync(started.Id));
        Assert.Equal(4, startWork.NodeId);
        Assert.Equal(
            ComplexGatewayStatePhases.WaitingForReset,
            Assert.Single(started.ComplexGatewayStates).Phase);

        var resetAck = await TakeTaskAsync(startWork, 401);
        Assert.Equal("running", resetAck.InstanceStatus);
        var resetWork = Assert.Single(await ListActiveTasksAsync(started.Id));
        Assert.Equal(5, resetWork.NodeId);

        var nextStartAck = await TakeTaskAsync(resetWork, 501);
        Assert.Equal("running", nextStartAck.InstanceStatus);
        var nextStartWork = Assert.Single(await ListActiveTasksAsync(started.Id));
        Assert.Equal(4, nextStartWork.NodeId);

        var detail = await GetInstanceAsync(started.Id);
        var state = Assert.Single(detail.ComplexGatewayStates);
        Assert.Equal(1, state.Cycle);
        Assert.Equal(ComplexGatewayStatePhases.WaitingForReset, state.Phase);
        Assert.Equal(
            [0, 1],
            detail.GatewayExecutions
                .Where(execution => execution.GatewayNodeId == 3 && execution.Phase == "start")
                .Select(execution => execution.Cycle)
                .OrderBy(cycle => cycle));
        Assert.Single(detail.GatewayExecutions, execution =>
            execution.GatewayNodeId == 3
            && execution.Phase == "reset"
            && execution.Cycle == 0
            && execution.SelectedFlowIds.SequenceEqual([302]));
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.EndEvent, "completed")]
    [InlineData(BpmnFlowNodeTypes.TerminateEndEvent, "completed")]
    [InlineData(BpmnFlowNodeTypes.ErrorEndEvent, "faulted")]
    public async Task ComplexTerminalStart_DoesNotEmitResetOutput(
        string terminalType,
        string expectedStatus)
    {
        var workflowId = await CreateWorkflowAsync(
            CreateComplexTerminalWorkflow(terminalType));
        var started = await StartAsync(workflowId);

        Assert.Equal(expectedStatus, started.Status);
        Assert.Empty(await ListActiveTasksAsync(started.Id));
        var detail = await GetInstanceAsync(started.Id);
        Assert.DoesNotContain(detail.GatewayExecutions, execution =>
            execution.GatewayNodeId == 2 && execution.Phase == "reset");
        Assert.DoesNotContain(detail.History, entry => entry.Note == "complexReset");
    }

    [Fact]
    public async Task ScopedInterrupt_InterruptsInclusiveScope_ThenStaleEventContinues()
    {
        var workflowId = await CreateWorkflowAsync(CreateInclusiveInterruptWorkflow());
        var started = await StartAsync(workflowId);
        var initialTasks = await ListActiveTasksAsync(started.Id);
        Assert.Equal([3, 4], initialTasks.Select(task => task.NodeId).OrderBy(id => id));

        var trigger = initialTasks.Single(task => task.NodeId == 3);
        var interrupted = await TakeTaskAsync(trigger, 301);
        Assert.Equal("running", interrupted.InstanceStatus);

        var emergency = Assert.Single(await ListActiveTasksAsync(started.Id));
        Assert.Equal(7, emergency.NodeId);
        var afterInterrupt = await GetInstanceAsync(started.Id);
        var scope = Assert.Single(afterInterrupt.GatewayExecutions, execution =>
            execution.GatewayNodeId == 2
            && execution.Direction == GatewayExecutionDirections.Split);
        Assert.Equal(BpmnFlowNodeTypes.InclusiveGateway, scope.GatewayType);
        Assert.Equal(GatewayExecutionStatuses.Interrupted, scope.Status);
        Assert.Equal(6, scope.InterruptingNodeId);
        Assert.Equal(1, scope.CancelledBranchCount);
        Assert.Equal(1, scope.InterruptedBranchCount);
        Assert.Single(afterInterrupt.History, entry => entry.Note == "scopedInterrupt");

        var stale = await TakeTaskAsync(emergency, 701);
        Assert.Equal("running", stale.InstanceStatus);
        var afterStaleTask = Assert.Single(await ListActiveTasksAsync(started.Id));
        Assert.Equal(9, afterStaleTask.NodeId);

        var afterStale = await GetInstanceAsync(started.Id);
        Assert.Single(afterStale.History, entry => entry.Note == "scopedInterrupt");
        Assert.Single(afterStale.History, entry => entry.Note == "scopedInterruptSkipped");
        Assert.Equal(
            GatewayExecutionStatuses.Interrupted,
            Assert.Single(afterStale.GatewayExecutions, execution =>
                execution.GatewayNodeId == 2).Status);

        var completed = await TakeTaskAsync(afterStaleTask, 901);
        Assert.Equal("completed", completed.InstanceStatus);
    }

    [Fact]
    public async Task ScopedInterrupt_InterruptsComplexSplitWithoutChangingNewerCycle()
    {
        var workflowId = await CreateWorkflowAsync(CreateComplexInterruptWorkflow());
        var started = await StartAsync(workflowId);
        var initialTasks = await ListActiveTasksAsync(started.Id);
        Assert.Equal([3, 4], initialTasks.Select(task => task.NodeId).OrderBy(id => id));

        var stateAfterReset = Assert.Single(started.ComplexGatewayStates);
        Assert.Equal(1, stateAfterReset.Cycle);
        Assert.Equal(ComplexGatewayStatePhases.WaitingForStart, stateAfterReset.Phase);

        var trigger = initialTasks.Single(task => task.NodeId == 3);
        var interrupted = await TakeTaskAsync(trigger, 301);
        Assert.Equal("running", interrupted.InstanceStatus);
        var emergency = Assert.Single(await ListActiveTasksAsync(started.Id));
        Assert.Equal(6, emergency.NodeId);

        var detail = await GetInstanceAsync(started.Id);
        var interruptedActivation = Assert.Single(detail.GatewayExecutions, execution =>
            execution.GatewayNodeId == 2
            && execution.Phase == "start"
            && execution.Cycle == 0);
        Assert.Equal(BpmnFlowNodeTypes.ComplexGateway, interruptedActivation.GatewayType);
        Assert.Equal(GatewayExecutionStatuses.Interrupted, interruptedActivation.Status);
        Assert.Equal([201, 202], interruptedActivation.SelectedFlowIds);
        Assert.Equal(5, interruptedActivation.InterruptingNodeId);
        Assert.Equal(1, interruptedActivation.CancelledBranchCount);
        Assert.Equal(1, interruptedActivation.InterruptedBranchCount);

        var state = Assert.Single(detail.ComplexGatewayStates);
        Assert.Equal(1, state.Cycle);
        Assert.Equal(ComplexGatewayStatePhases.WaitingForStart, state.Phase);
        Assert.Single(detail.History, entry => entry.Note == "scopedInterrupt");

        var completed = await TakeTaskAsync(emergency, 601);
        Assert.Equal("completed", completed.InstanceStatus);
    }

    [Fact]
    public async Task ScopedInterrupt_OfOlderComplexActivation_DoesNotCancelRestartedCycle()
    {
        var workflowId = await CreateWorkflowAsync(
            CreateComplexOlderActivationInterruptWorkflow());
        var started = await StartAsync(workflowId);
        var initialTasks = await ListActiveTasksAsync(started.Id);
        Assert.Equal([3, 4], initialTasks.Select(task => task.NodeId).OrderBy(id => id));

        var oldInterruptingBranch = initialTasks.Single(task => task.NodeId == 4);
        var resetBranch = initialTasks.Single(task => task.NodeId == 3);
        var restarted = await TakeTaskAsync(resetBranch, 301);
        Assert.Equal("running", restarted.InstanceStatus);

        var tasksAfterRestart = await ListActiveTasksAsync(started.Id);
        Assert.Equal(
            [3, 4, 4],
            tasksAfterRestart.Select(task => task.NodeId).OrderBy(id => id));
        var beforeInterrupt = await GetInstanceAsync(started.Id);
        var oldActivation = Assert.Single(beforeInterrupt.GatewayExecutions, execution =>
            execution.GatewayNodeId == 2
            && execution.Phase == "start"
            && execution.Cycle == 0);
        var newerActivation = Assert.Single(beforeInterrupt.GatewayExecutions, execution =>
            execution.GatewayNodeId == 2
            && execution.Phase == "start"
            && execution.Cycle == 1);
        var executionById = beforeInterrupt.GatewayExecutions.ToDictionary(
            execution => execution.Id);
        var newerAncestors = new HashSet<long>();
        var parentExecutionId = newerActivation.ParentExecutionId;
        while (parentExecutionId is long ancestorId)
        {
            Assert.True(newerAncestors.Add(ancestorId), "Gateway scope ancestry contains a cycle.");
            parentExecutionId = executionById[ancestorId].ParentExecutionId;
        }
        Assert.DoesNotContain(oldActivation.Id, newerAncestors);

        var stateBeforeInterrupt = Assert.Single(beforeInterrupt.ComplexGatewayStates);
        Assert.Equal(1, stateBeforeInterrupt.Cycle);
        Assert.Equal(ComplexGatewayStatePhases.WaitingForReset, stateBeforeInterrupt.Phase);
        Assert.Equal(newerActivation.Id, stateBeforeInterrupt.ActiveExecutionId);

        var interrupted = await TakeTaskAsync(oldInterruptingBranch, 401);
        Assert.Equal("running", interrupted.InstanceStatus);
        Assert.Equal(
            [3, 4, 6],
            (await ListActiveTasksAsync(started.Id))
            .Select(task => task.NodeId)
            .OrderBy(id => id));

        var afterInterrupt = await GetInstanceAsync(started.Id);
        Assert.Equal(
            GatewayExecutionStatuses.Interrupted,
            Assert.Single(afterInterrupt.GatewayExecutions, execution =>
                execution.Id == oldActivation.Id).Status);
        Assert.Equal(
            GatewayExecutionStatuses.Active,
            Assert.Single(afterInterrupt.GatewayExecutions, execution =>
                execution.Id == newerActivation.Id).Status);
        var stateAfterInterrupt = Assert.Single(afterInterrupt.ComplexGatewayStates);
        Assert.Equal(1, stateAfterInterrupt.Cycle);
        Assert.Equal(ComplexGatewayStatePhases.WaitingForReset, stateAfterInterrupt.Phase);
        Assert.Equal(newerActivation.Id, stateAfterInterrupt.ActiveExecutionId);

        var emergency = Assert.Single(
            await ListActiveTasksAsync(started.Id),
            task => task.NodeId == 6);
        var completed = await TakeTaskAsync(emergency, 601);
        Assert.Equal("completed", completed.InstanceStatus);
    }

    [Fact]
    public async Task ComplexReset_ReconcilesToContainingScope_WhenStartOutputEndsImmediately()
    {
        var workflowId = await CreateWorkflowAsync(
            CreateNestedComplexImmediateEndWorkflow());
        var started = await StartAsync(workflowId);
        Assert.Equal("running", started.Status);
        Assert.Equal(
            [7, 11],
            (await ListActiveTasksAsync(started.Id))
            .Select(task => task.NodeId)
            .OrderBy(id => id));
        var initialState = Assert.Single(started.ComplexGatewayStates);
        Assert.Equal(0, initialState.Cycle);
        Assert.Equal(ComplexGatewayStatePhases.WaitingForReset, initialState.Phase);
        Assert.NotNull(initialState.ActiveExecutionId);

        var lateReset = Assert.Single(
            await ListActiveTasksAsync(started.Id),
            task => task.NodeId == 7);
        var reset = await TakeTaskAsync(lateReset, 701);
        Assert.Equal("running", reset.InstanceStatus);

        var detail = await GetInstanceAsync(started.Id);
        var outerExecution = Assert.Single(detail.GatewayExecutions, execution =>
            execution.GatewayNodeId == 2
            && execution.Direction == GatewayExecutionDirections.Split);
        var resetExecution = Assert.Single(detail.GatewayExecutions, execution =>
            execution.GatewayNodeId == 6
            && execution.Phase == "reset"
            && execution.Cycle == 0);
        Assert.Equal(outerExecution.Id, resetExecution.ParentExecutionId);
        var resetState = Assert.Single(detail.ComplexGatewayStates);
        Assert.Equal(1, resetState.Cycle);
        Assert.Equal(ComplexGatewayStatePhases.WaitingForStart, resetState.Phase);
        Assert.Equal(
            [10, 11],
            (await ListActiveTasksAsync(started.Id))
            .Select(task => task.NodeId)
            .OrderBy(id => id));

        var resetWork = Assert.Single(
            await ListActiveTasksAsync(started.Id),
            task => task.NodeId == 10);
        var completed = await TakeTaskAsync(resetWork, 1001);
        Assert.Equal("completed", completed.InstanceStatus);
    }

    [Fact]
    public async Task ScopedInterrupt_DrainsPreInterruptComplexResetArrivals()
    {
        var workflowId = await CreateWorkflowAsync(CreateComplexDrainInterruptWorkflow());
        var started = await StartAsync(workflowId);
        var initialTasks = await ListActiveTasksAsync(started.Id);
        Assert.Equal(
            [4, 7, 8],
            initialTasks.Select(task => task.NodeId).OrderBy(id => id));
        var initialState = Assert.Single(started.ComplexGatewayStates);
        Assert.Equal(ComplexGatewayStatePhases.WaitingForReset, initialState.Phase);
        Assert.Equal(0, initialState.Cycle);

        var trigger = initialTasks.Single(task => task.NodeId == 7);
        var lateReset = initialTasks.Single(task => task.NodeId == 4);
        var interrupted = await TakeTaskAsync(trigger, 701);
        Assert.Equal("running", interrupted.InstanceStatus);

        var draining = await GetInstanceAsync(started.Id);
        var drainingState = Assert.Single(draining.ComplexGatewayStates);
        Assert.Equal(ComplexGatewayStatePhases.InterruptedDraining, drainingState.Phase);
        Assert.Equal(0, drainingState.Cycle);
        Assert.Contains(lateReset.TokenId, drainingState.DrainingTokenIds);
        Assert.DoesNotContain(await ListActiveTasksAsync(started.Id), task => task.NodeId == 8);

        var drained = await TakeTaskAsync(lateReset, 401);
        Assert.Equal("running", drained.InstanceStatus);
        var afterDrain = await GetInstanceAsync(started.Id);
        var resetState = Assert.Single(afterDrain.ComplexGatewayStates);
        Assert.Equal(ComplexGatewayStatePhases.WaitingForStart, resetState.Phase);
        Assert.Equal(1, resetState.Cycle);
        Assert.Empty(resetState.DrainingTokenIds);
        Assert.DoesNotContain(afterDrain.GatewayExecutions, execution =>
            execution.GatewayNodeId == 6 && execution.Phase == "reset");
        Assert.Contains(afterDrain.ExecutionPositions, position =>
            position.TokenId == lateReset.TokenId
            && position.TokenStatus == ExecutionTokenStatuses.Cancelled
            && position.TerminationReason == "gatewayScopeCancelled");

        var emergency = Assert.Single(await ListActiveTasksAsync(started.Id));
        Assert.Equal(10, emergency.NodeId);
        var completed = await TakeTaskAsync(emergency, 1001);
        Assert.Equal("completed", completed.InstanceStatus);
    }

    [Fact]
    public async Task ScopedInterrupt_DrainsTriggerWhenContinuationLoopsToComplexInput()
    {
        var workflowId = await CreateWorkflowAsync(
            CreateComplexDrainInterruptWorkflow(continuationCanLoop: true));
        var started = await StartAsync(workflowId);
        var initialTasks = await ListActiveTasksAsync(started.Id);
        var trigger = initialTasks.Single(task => task.NodeId == 7);
        var lateReset = initialTasks.Single(task => task.NodeId == 4);

        await TakeTaskAsync(trigger, 701);
        var interrupted = await GetInstanceAsync(started.Id);
        var interruptedState = Assert.Single(interrupted.ComplexGatewayStates);
        Assert.Equal(ComplexGatewayStatePhases.InterruptedDraining, interruptedState.Phase);
        Assert.Contains(trigger.TokenId, interruptedState.DrainingTokenIds);
        Assert.Contains(lateReset.TokenId, interruptedState.DrainingTokenIds);

        await TakeTaskAsync(lateReset, 401);
        var stillDraining = await GetInstanceAsync(started.Id);
        Assert.Equal(
            ComplexGatewayStatePhases.InterruptedDraining,
            Assert.Single(stillDraining.ComplexGatewayStates).Phase);

        var emergency = Assert.Single(await ListActiveTasksAsync(started.Id));
        Assert.Equal(10, emergency.NodeId);
        var drained = await TakeTaskAsync(emergency, 1002);
        Assert.Equal("completed", drained.InstanceStatus);
        var final = await GetInstanceAsync(started.Id);
        var resetState = Assert.Single(final.ComplexGatewayStates);
        Assert.Equal(ComplexGatewayStatePhases.WaitingForStart, resetState.Phase);
        Assert.Equal(1, resetState.Cycle);
        Assert.DoesNotContain(final.GatewayExecutions, execution =>
            execution.GatewayNodeId == 6 && execution.Phase == "reset");
    }

    [Fact]
    public async Task ScopedInterrupt_QueryBudgetDoesNotGrowPerCancelledBranch()
    {
        var smallWorkflowId = await CreateWorkflowAsync(CreateInterruptFanOutWorkflow(2));
        var small = await StartAsync(smallWorkflowId);
        var smallTrigger = (await ListActiveTasksAsync(small.Id))
            .Single(task => task.NodeId == 3);
        fixture.CommandCounter.Reset();
        await TakeTaskAsync(smallTrigger, 301);
        var smallBudget = fixture.CommandCounter.ReaderCommands;

        var largeWorkflowId = await CreateWorkflowAsync(CreateInterruptFanOutWorkflow(20));
        var large = await StartAsync(largeWorkflowId);
        var largeTrigger = (await ListActiveTasksAsync(large.Id))
            .Single(task => task.NodeId == 3);
        fixture.CommandCounter.Reset();
        await TakeTaskAsync(largeTrigger, 301);
        var largeBudget = fixture.CommandCounter.ReaderCommands;

        Assert.Equal(smallBudget, largeBudget);
    }

    [Fact]
    public async Task ComplexTransition_QueryBudgetDoesNotGrowWithGatewayHistory()
    {
        var workflowId = await CreateWorkflowAsync(CreateComplexResetRoutingWorkflow());
        var started = await StartAsync(workflowId);

        var startWork = Assert.Single(await ListActiveTasksAsync(started.Id));
        await TakeTaskAsync(startWork, 401);
        var resetWork = Assert.Single(await ListActiveTasksAsync(started.Id));
        fixture.CommandCounter.Reset();
        await TakeTaskAsync(resetWork, 501);
        var firstActivationBudget = fixture.CommandCounter.ReaderCommands;

        for (var cycle = 0; cycle < 5; cycle++)
        {
            startWork = Assert.Single(await ListActiveTasksAsync(started.Id));
            await TakeTaskAsync(startWork, 401);
            resetWork = Assert.Single(await ListActiveTasksAsync(started.Id));
            if (cycle < 4)
            {
                await TakeTaskAsync(resetWork, 501);
            }
        }

        fixture.CommandCounter.Reset();
        await TakeTaskAsync(resetWork, 501);
        var laterActivationBudget = fixture.CommandCounter.ReaderCommands;

        Assert.Equal(firstActivationBudget, laterActivationBudget);
    }

    private async Task<long> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<InstanceDetailDto> GetInstanceAsync(long instanceId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<IReadOnlyList<UserTaskDto>> ListActiveTasksAsync(long instanceId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}/user-tasks?status=active&page=1&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<PagedResult<UserTaskDto>>(response)).Items;
    }

    private async Task<UserTaskActionAckDto> TakeTaskAsync(UserTaskDto task, int flowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/{flowId}",
            new TakeFlowRequest(null),
            "gateway-tester");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<UserTaskActionAckDto>(response);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin")
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, ActorRoles);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel CreateExclusiveMergeWorkflow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "exclusive-merge-api-" + suffix,
            Name = "Exclusive merge API " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                Node(1, "Start", BpmnFlowNodeTypes.StartEvent),
                Node(2, "Parallel split", BpmnFlowNodeTypes.ParallelGateway),
                Node(3, "First branch", BpmnFlowNodeTypes.UserTask),
                Node(4, "Second branch", BpmnFlowNodeTypes.UserTask),
                Node(5, "Exclusive merge", BpmnFlowNodeTypes.ExclusiveGateway),
                Node(6, "Downstream work", BpmnFlowNodeTypes.UserTask),
                Node(7, "End", BpmnFlowNodeTypes.EndEvent)
            ],
            SequenceFlows =
            [
                Flow(101, 1, 2),
                Flow(201, 2, 3),
                Flow(202, 2, 4),
                Flow(301, 3, 5),
                Flow(401, 4, 5),
                Flow(501, 5, 6),
                Flow(601, 6, 7)
            ]
        };
    }

    private static WorkflowModel CreateSurplusParallelJoinTerminateWorkflow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "parallel-surplus-terminate-api-" + suffix,
            Name = "Parallel surplus terminate API " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                Node(1, "Start", BpmnFlowNodeTypes.StartEvent),
                Node(2, "Four-way split", BpmnFlowNodeTypes.ParallelGateway),
                Node(3, "A one", BpmnFlowNodeTypes.Task),
                Node(4, "A two", BpmnFlowNodeTypes.Task),
                Node(5, "B one", BpmnFlowNodeTypes.Task),
                Node(6, "B two", BpmnFlowNodeTypes.Task),
                Node(7, "A merge", BpmnFlowNodeTypes.ExclusiveGateway),
                Node(8, "B merge", BpmnFlowNodeTypes.ExclusiveGateway),
                Node(9, "Parallel join", BpmnFlowNodeTypes.ParallelGateway),
                Node(10, "Terminate", BpmnFlowNodeTypes.TerminateEndEvent)
            ],
            SequenceFlows =
            [
                Flow(101, 1, 2),
                Flow(201, 2, 3),
                Flow(202, 2, 4),
                Flow(203, 2, 5),
                Flow(204, 2, 6),
                Flow(301, 3, 7),
                Flow(401, 4, 7),
                Flow(501, 5, 8),
                Flow(601, 6, 8),
                Flow(701, 7, 9),
                Flow(801, 8, 9),
                Flow(901, 9, 10)
            ]
        };
    }

    private static WorkflowModel CreateInclusiveMergeWorkflow(bool takeA, bool takeB)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "inclusive-merge-api-" + suffix,
            Name = "Inclusive merge API " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                Node(1, "Start", BpmnFlowNodeTypes.StartEvent),
                Node(2, "Inclusive split", BpmnFlowNodeTypes.InclusiveGateway),
                Node(3, "Path A", BpmnFlowNodeTypes.Task),
                Node(4, "Path B", BpmnFlowNodeTypes.Task),
                Node(5, "Fallback path", BpmnFlowNodeTypes.Task),
                Node(6, "Inclusive merge", BpmnFlowNodeTypes.InclusiveGateway),
                Node(7, "After merge", BpmnFlowNodeTypes.UserTask),
                Node(8, "End", BpmnFlowNodeTypes.EndEvent)
            ],
            SequenceFlows =
            [
                Flow(101, 1, 2),
                Flow(201, 2, 3, condition: takeA ? "true" : "false"),
                Flow(202, 2, 4, condition: takeB ? "true" : "false"),
                Flow(203, 2, 5, isDefault: true),
                Flow(301, 3, 6),
                Flow(401, 4, 6),
                Flow(501, 5, 6),
                Flow(601, 6, 7),
                Flow(701, 7, 8)
            ]
        };
    }

    private static WorkflowModel CreateComplexCycleWorkflow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "complex-cycle-api-" + suffix,
            Name = "Complex cycle API " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                Node(1, "Start", BpmnFlowNodeTypes.StartEvent),
                Node(2, "Loop merge", BpmnFlowNodeTypes.ExclusiveGateway),
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Complex split",
                    Type = BpmnFlowNodeTypes.ComplexGateway,
                    ActivationCondition = "IncomingCount(201) >= 1"
                },
                Node(4, "Repeat", BpmnFlowNodeTypes.UserTask),
                Node(5, "Never selected", BpmnFlowNodeTypes.EndEvent)
            ],
            SequenceFlows =
            [
                Flow(101, 1, 2),
                Flow(401, 4, 2),
                Flow(201, 2, 3),
                Flow(301, 3, 4, condition: "[gateway.waitingForStart]"),
                Flow(302, 3, 5, condition: "false")
            ]
        };
    }

    private static WorkflowModel CreateComplexResetRoutingWorkflow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "complex-reset-routing-api-" + suffix,
            Name = "Complex reset routing API " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                Node(1, "Start", BpmnFlowNodeTypes.StartEvent),
                Node(2, "Loop merge", BpmnFlowNodeTypes.ExclusiveGateway),
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Complex split",
                    Type = BpmnFlowNodeTypes.ComplexGateway,
                    ActivationCondition = "IncomingCount(201) >= 1"
                },
                Node(4, "Start work", BpmnFlowNodeTypes.UserTask),
                Node(5, "Reset work", BpmnFlowNodeTypes.UserTask),
                Node(6, "End", BpmnFlowNodeTypes.EndEvent)
            ],
            SequenceFlows =
            [
                Flow(101, 1, 2),
                Flow(401, 4, 2),
                Flow(402, 4, 6),
                Flow(501, 5, 2),
                Flow(201, 2, 3),
                Flow(301, 3, 4, condition: "[gateway.waitingForStart]"),
                Flow(302, 3, 5, condition: "not [gateway.waitingForStart]")
            ]
        };
    }

    private static WorkflowModel CreateComplexTerminalWorkflow(string terminalType)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var terminal = Node(3, "Terminal", terminalType);
        if (terminalType == BpmnFlowNodeTypes.ErrorEndEvent)
        {
            terminal.ErrorCode = "COMPLEX_TEST";
            terminal.ErrorDescription = "Complex terminal test";
        }
        return new WorkflowModel
        {
            Id = "complex-terminal-api-" + suffix,
            Name = "Complex terminal API " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                Node(1, "Start", BpmnFlowNodeTypes.StartEvent),
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Complex split",
                    Type = BpmnFlowNodeTypes.ComplexGateway,
                    ActivationCondition = "IncomingCount(101) >= 1"
                },
                terminal,
                Node(4, "Forbidden reset work", BpmnFlowNodeTypes.UserTask),
                Node(5, "End", BpmnFlowNodeTypes.EndEvent)
            ],
            SequenceFlows =
            [
                Flow(101, 1, 2),
                Flow(201, 2, 3, condition: "[gateway.waitingForStart]"),
                Flow(202, 2, 4, condition: "not [gateway.waitingForStart]"),
                Flow(401, 4, 5)
            ]
        };
    }

    private static WorkflowModel CreateInclusiveInterruptWorkflow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "inclusive-interrupt-api-" + suffix,
            Name = "Inclusive interrupt API " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                Node(1, "Start", BpmnFlowNodeTypes.StartEvent),
                Node(2, "Inclusive split", BpmnFlowNodeTypes.InclusiveGateway),
                Node(3, "Interrupting branch", BpmnFlowNodeTypes.UserTask),
                Node(4, "Sibling branch", BpmnFlowNodeTypes.UserTask),
                new FlowNodeModel
                {
                    Id = 6,
                    Name = "Interrupt inclusive scope",
                    Type = BpmnFlowNodeTypes.ScopedInterruptEvent,
                    GatewayRef = 2
                },
                Node(7, "Emergency work", BpmnFlowNodeTypes.UserTask),
                new FlowNodeModel
                {
                    Id = 8,
                    Name = "Stale interrupt",
                    Type = BpmnFlowNodeTypes.ScopedInterruptEvent,
                    GatewayRef = 2
                },
                Node(9, "After stale interrupt", BpmnFlowNodeTypes.UserTask),
                Node(10, "End", BpmnFlowNodeTypes.EndEvent)
            ],
            SequenceFlows =
            [
                Flow(101, 1, 2),
                Flow(201, 2, 3, condition: "true"),
                Flow(202, 2, 4, condition: "true"),
                Flow(203, 2, 10, isDefault: true),
                Flow(301, 3, 6),
                Flow(401, 4, 10),
                Flow(601, 6, 7),
                Flow(701, 7, 8),
                Flow(801, 8, 9),
                Flow(901, 9, 10)
            ]
        };
    }

    private static WorkflowModel CreateComplexInterruptWorkflow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "complex-interrupt-api-" + suffix,
            Name = "Complex interrupt API " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                Node(1, "Start", BpmnFlowNodeTypes.StartEvent),
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Complex split",
                    Type = BpmnFlowNodeTypes.ComplexGateway,
                    ActivationCondition = "IncomingCount(101) >= 1"
                },
                Node(3, "Interrupting branch", BpmnFlowNodeTypes.UserTask),
                Node(4, "Sibling branch", BpmnFlowNodeTypes.UserTask),
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Interrupt complex scope",
                    Type = BpmnFlowNodeTypes.ScopedInterruptEvent,
                    GatewayRef = 2
                },
                Node(6, "Emergency work", BpmnFlowNodeTypes.UserTask),
                Node(7, "End", BpmnFlowNodeTypes.EndEvent)
            ],
            SequenceFlows =
            [
                Flow(101, 1, 2),
                Flow(201, 2, 3, condition: "[gateway.waitingForStart]"),
                Flow(202, 2, 4, condition: "[gateway.waitingForStart]"),
                Flow(301, 3, 5),
                Flow(401, 4, 7),
                Flow(501, 5, 6),
                Flow(601, 6, 7)
            ]
        };
    }

    private static WorkflowModel CreateComplexOlderActivationInterruptWorkflow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "complex-older-activation-interrupt-api-" + suffix,
            Name = "Complex older activation interrupt API " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                Node(1, "Start", BpmnFlowNodeTypes.StartEvent),
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Complex split",
                    Type = BpmnFlowNodeTypes.ComplexGateway,
                    ActivationCondition = "IncomingCount(101) >= 1"
                },
                Node(3, "Reset branch", BpmnFlowNodeTypes.UserTask),
                Node(4, "Interrupting branch", BpmnFlowNodeTypes.UserTask),
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Interrupt old activation",
                    Type = BpmnFlowNodeTypes.ScopedInterruptEvent,
                    GatewayRef = 2
                },
                Node(6, "Emergency work", BpmnFlowNodeTypes.UserTask),
                Node(7, "Terminate", BpmnFlowNodeTypes.TerminateEndEvent),
                Node(8, "Restart route", BpmnFlowNodeTypes.Task),
                Node(10, "Complex input merge", BpmnFlowNodeTypes.ExclusiveGateway)
            ],
            SequenceFlows =
            [
                Flow(11, 1, 10),
                Flow(101, 10, 2),
                Flow(201, 2, 3, condition: "[gateway.waitingForStart]"),
                Flow(202, 2, 4, condition: "[gateway.waitingForStart]"),
                Flow(203, 2, 8, condition: "not [gateway.waitingForStart]"),
                Flow(301, 3, 10),
                Flow(401, 4, 5),
                Flow(501, 5, 6),
                Flow(601, 6, 7),
                Flow(801, 8, 10)
            ]
        };
    }

    private static WorkflowModel CreateNestedComplexImmediateEndWorkflow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "nested-complex-immediate-end-api-" + suffix,
            Name = "Nested Complex immediate end API " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                Node(1, "Start", BpmnFlowNodeTypes.StartEvent),
                Node(2, "Outer split", BpmnFlowNodeTypes.ParallelGateway),
                Node(3, "Inner split", BpmnFlowNodeTypes.ParallelGateway),
                Node(4, "Activation route", BpmnFlowNodeTypes.Task),
                Node(5, "Complex input merge", BpmnFlowNodeTypes.ExclusiveGateway),
                new FlowNodeModel
                {
                    Id = 6,
                    Name = "Complex split",
                    Type = BpmnFlowNodeTypes.ComplexGateway,
                    ActivationCondition = "IncomingCount(501) >= 1"
                },
                Node(7, "Late reset", BpmnFlowNodeTypes.UserTask),
                Node(8, "Immediate branch end", BpmnFlowNodeTypes.EndEvent),
                Node(10, "Reset work", BpmnFlowNodeTypes.UserTask),
                Node(11, "Outer keepalive", BpmnFlowNodeTypes.UserTask),
                Node(12, "Keepalive end", BpmnFlowNodeTypes.EndEvent),
                Node(13, "Terminate", BpmnFlowNodeTypes.TerminateEndEvent)
            ],
            SequenceFlows =
            [
                Flow(101, 1, 2),
                Flow(201, 2, 3),
                Flow(202, 2, 11),
                Flow(301, 3, 4),
                Flow(302, 3, 7),
                Flow(401, 4, 5),
                Flow(501, 5, 6),
                Flow(601, 6, 8, condition: "[gateway.waitingForStart]"),
                Flow(602, 6, 10, condition: "not [gateway.waitingForStart]"),
                Flow(701, 7, 5),
                Flow(1001, 10, 13),
                Flow(1101, 11, 12)
            ]
        };
    }

    private static WorkflowModel CreateComplexDrainInterruptWorkflow(
        bool continuationCanLoop = false)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var flows = new List<SequenceFlowModel>
        {
            Flow(101, 1, 2),
            Flow(201, 2, 3),
            Flow(202, 2, 4),
            Flow(301, 3, 5),
            Flow(401, 4, 5),
            Flow(501, 5, 6),
            Flow(601, 6, 7, condition: "[gateway.waitingForStart]"),
            Flow(602, 6, 8, condition: "[gateway.waitingForStart]"),
            Flow(701, 7, 9),
            Flow(801, 8, 11),
            Flow(901, 9, 10),
            Flow(1001, 10, 11)
        };
        if (continuationCanLoop)
        {
            flows.Add(Flow(1002, 10, 5));
        }
        return new WorkflowModel
        {
            Id = "complex-drain-interrupt-api-" + suffix,
            Name = "Complex drain interrupt API " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                Node(1, "Start", BpmnFlowNodeTypes.StartEvent),
                Node(2, "Outer parallel split", BpmnFlowNodeTypes.ParallelGateway),
                Node(3, "Activation route", BpmnFlowNodeTypes.Task),
                Node(4, "Late reset work", BpmnFlowNodeTypes.UserTask),
                Node(5, "Complex input merge", BpmnFlowNodeTypes.ExclusiveGateway),
                new FlowNodeModel
                {
                    Id = 6,
                    Name = "Complex split",
                    Type = BpmnFlowNodeTypes.ComplexGateway,
                    ActivationCondition = "IncomingCount(501) >= 1"
                },
                Node(7, "Interrupting branch", BpmnFlowNodeTypes.UserTask),
                Node(8, "Complex sibling", BpmnFlowNodeTypes.UserTask),
                new FlowNodeModel
                {
                    Id = 9,
                    Name = "Interrupt complex activation",
                    Type = BpmnFlowNodeTypes.ScopedInterruptEvent,
                    GatewayRef = 6
                },
                Node(10, "Emergency work", BpmnFlowNodeTypes.UserTask),
                Node(11, "End", BpmnFlowNodeTypes.EndEvent)
            ],
            SequenceFlows = flows
        };
    }

    private static WorkflowModel CreateInterruptFanOutWorkflow(int branchCount)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var nodes = new List<FlowNodeModel>
        {
            Node(1, "Start", BpmnFlowNodeTypes.StartEvent),
            Node(2, "Parallel split", BpmnFlowNodeTypes.ParallelGateway),
            Node(3, "Interrupting work", BpmnFlowNodeTypes.UserTask),
            new()
            {
                Id = 4,
                Name = "Scoped interrupt",
                Type = BpmnFlowNodeTypes.ScopedInterruptEvent,
                GatewayRef = 2
            },
            Node(5, "Emergency work", BpmnFlowNodeTypes.UserTask),
            Node(6, "End", BpmnFlowNodeTypes.EndEvent)
        };
        var flows = new List<SequenceFlowModel>
        {
            Flow(101, 1, 2),
            Flow(201, 2, 3),
            Flow(301, 3, 4),
            Flow(401, 4, 5),
            Flow(501, 5, 6)
        };
        for (var index = 1; index < branchCount; index++)
        {
            var nodeId = 10 + index;
            nodes.Add(Node(nodeId, $"Sibling {index}", BpmnFlowNodeTypes.UserTask));
            flows.Add(Flow(201 + index, 2, nodeId));
            flows.Add(Flow(1000 + index, nodeId, 6));
        }
        return new WorkflowModel
        {
            Id = "interrupt-fanout-api-" + suffix,
            Name = "Interrupt fanout API " + suffix,
            InitialEventId = 1,
            FlowNodes = nodes,
            SequenceFlows = flows
        };
    }

    private static FlowNodeModel Node(int id, string name, string type) =>
        new()
        {
            Id = id,
            Name = name,
            ExternalId = name.ToLowerInvariant().Replace(' ', '-'),
            Type = type
        };

    private static SequenceFlowModel Flow(
        int id,
        int sourceRef,
        int targetRef,
        string? condition = null,
        bool isDefault = false) =>
        new()
        {
            Id = id,
            Name = $"Flow {id}",
            SourceRef = sourceRef,
            TargetRef = targetRef,
            Condition = condition,
            IsDefault = isDefault
        };
}
