using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdministrativeTimerBoundaryOverrideApiTests(
    PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(TimerSubscriptionStatuses.Active)]
    [InlineData(TimerSubscriptionStatuses.Paused)]
    public async Task Override_FiresActiveOrPausedTimerBeforeItsDueTime(
        string frozenStatus)
    {
        var workflowId = await CreateWorkflowAsync(
            CreateSingleTimerWorkflow(useFlowEvidence: false));
        var instance = await StartAsync(workflowId);
        long? incidentId = null;
        if (frozenStatus == TimerSubscriptionStatuses.Paused)
        {
            incidentId = await PauseTimerWithIncidentAsync(instance.Id, 6);
        }

        var candidate = await GetSingleCandidateAsync(workflowId, 2);
        var timer = Assert.Single(candidate.TimerBoundaries);
        Assert.Equal(frozenStatus, timer.Status);
        Assert.True(timer.Eligible);
        Assert.True(timer.NextDueAt > DateTimeOffset.UtcNow.AddHours(12));

        var batch = await CreatePrepareConfirmAsync(
            workflowId,
            sourceNodeId: 2,
            boundaryNodeId: 6,
            flowId: 401,
            candidate,
            "override-operator");
        await ProcessBatchJobAsync(batch.ExecutionJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id, "override-operator");

        Assert.Equal(AdministrativeActionBatchStatuses.Completed, batch.Summary.Status);
        Assert.Equal(1, batch.Summary.SucceededItemCount);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(
            WorkflowInstanceStatuses.Completed,
            await db.WorkflowInstances
                .Where(item => item.Id == instance.Id)
                .Select(item => item.Status)
                .SingleAsync());
        Assert.Equal(
            UserTaskRecordStatuses.Cancelled,
            await db.UserTasks
                .Where(item => item.InstanceId == instance.Id)
                .Select(item => item.Status)
                .SingleAsync());
        Assert.Equal(
            TimerSubscriptionStatuses.Completed,
            await db.TimerSubscriptions
                .Where(item => item.Id == timer.TimerSubscriptionId)
                .Select(item => item.Status)
                .SingleAsync());
        Assert.Equal(
            WorkflowJobStatuses.Cancelled,
            await db.WorkflowJobs
                .Where(item => item.Id == timer.TimerJobId)
                .Select(item => item.Status)
                .SingleAsync());
        if (incidentId is not null)
        {
            var incident = await db.WorkflowIncidents.SingleAsync(
                item => item.Id == incidentId.Value);
            Assert.Equal(WorkflowIncidentStatuses.Resolved, incident.Status);
            Assert.Equal("system:cancellation", incident.ResolvedBy);
        }
    }

    [Fact]
    public async Task Override_CompletesSelectedTimerAndCancelsSiblingTimerJobAndIncident()
    {
        var workflowId = await CreateWorkflowAsync(CreateMultipleTimerWorkflow());
        var instance = await StartAsync(workflowId);
        var siblingIncidentId = await PauseTimerWithIncidentAsync(instance.Id, 7);

        var candidate = await GetSingleCandidateAsync(workflowId, 2);
        Assert.Equal(2, candidate.TimerBoundaries.Count);
        var selected = Assert.Single(
            candidate.TimerBoundaries,
            timer => timer.BoundaryNodeId == 6);
        var sibling = Assert.Single(
            candidate.TimerBoundaries,
            timer => timer.BoundaryNodeId == 7);
        Assert.Equal(TimerSubscriptionStatuses.Active, selected.Status);
        Assert.Equal(TimerSubscriptionStatuses.Paused, sibling.Status);

        var batch = await CreatePrepareConfirmAsync(
            workflowId,
            sourceNodeId: 2,
            boundaryNodeId: 6,
            flowId: 401,
            candidate,
            "multiple-timer-operator");
        await ProcessBatchJobAsync(batch.ExecutionJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id, "multiple-timer-operator");
        Assert.Equal(AdministrativeActionBatchStatuses.Completed, batch.Summary.Status);

        await using var db = fixture.CreateDbContext();
        var subscriptions = await db.TimerSubscriptions
            .Where(item => item.InstanceId == instance.Id)
            .OrderBy(item => item.TimerNodeId)
            .ToListAsync();
        Assert.Equal(2, subscriptions.Count);
        Assert.Equal(
            TimerSubscriptionStatuses.Completed,
            subscriptions.Single(item => item.TimerNodeId == 6).Status);
        Assert.Equal(
            TimerSubscriptionStatuses.Cancelled,
            subscriptions.Single(item => item.TimerNodeId == 7).Status);

        var timerJobs = await db.WorkflowJobs
            .Where(item => item.InstanceId == instance.Id
                           && item.Kind == WorkflowJobKinds.TimerBoundary)
            .OrderBy(item => item.NodeId)
            .ToListAsync();
        Assert.Equal(2, timerJobs.Count);
        Assert.All(
            timerJobs,
            job => Assert.Equal(WorkflowJobStatuses.Cancelled, job.Status));

        var incident = await db.WorkflowIncidents.SingleAsync(
            item => item.Id == siblingIncidentId);
        Assert.Equal(WorkflowIncidentStatuses.Resolved, incident.Status);
        Assert.Equal("system:cancellation", incident.ResolvedBy);
        Assert.Equal(
            1,
            await db.InstanceHistory.CountAsync(item =>
                item.InstanceId == instance.Id
                && item.ActionId == 401
                && item.AdministrativeActionBatchId == batch.Summary.Id));
        Assert.False(await db.InstanceHistory.AnyAsync(item =>
            item.InstanceId == instance.Id
            && item.ActionId == 402));
    }

    [Fact]
    public async Task NaturalTimerAndAdministrativeOverrideRace_CommitsOneBoundaryTraversal()
    {
        var workflowId = await CreateWorkflowAsync(
            CreateSingleTimerWorkflow(useFlowEvidence: true));
        var instance = await StartAsync(workflowId);
        var candidate = await GetSingleCandidateAsync(workflowId, 2);
        var timer = Assert.Single(candidate.TimerBoundaries);
        var batch = await CreatePrepareConfirmAsync(
            workflowId,
            sourceNodeId: 2,
            boundaryNodeId: 6,
            flowId: 401,
            candidate,
            "race-operator");

        var delay = timer.NextDueAt!.Value
            - DateTimeOffset.UtcNow
            + TimeSpan.FromMilliseconds(100);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        var timerLease = await LeaseSpecificJobAsync(
            timer.TimerJobId!.Value,
            WorkflowJobClasses.Control);
        var batchLease = await LeaseSpecificJobAsync(
            batch.ExecutionJobId!.Value,
            WorkflowJobClasses.Activity);

        await Task.WhenAll(
            ProcessLeaseAsync(timerLease),
            ProcessLeaseAsync(batchLease));

        batch = await GetBatchAsync(batch.Summary.Id, "race-operator");
        Assert.Contains(
            batch.Summary.Status,
            new[]
            {
                AdministrativeActionBatchStatuses.Completed,
                AdministrativeActionBatchStatuses.CompletedWithIssues
            });
        Assert.Equal(
            1,
            batch.Summary.SucceededItemCount + batch.Summary.SkippedItemCount);

        await using var db = fixture.CreateDbContext();
        var instanceStatus = await db.WorkflowInstances
            .Where(item => item.Id == instance.Id)
            .Select(item => item.Status)
            .SingleAsync();
        var storedSubscription = await db.TimerSubscriptions.SingleAsync(
            item => item.Id == timer.TimerSubscriptionId);
        var timerJobStatus = await db.WorkflowJobs
            .Where(item => item.Id == timer.TimerJobId)
            .Select(item => item.Status)
            .SingleAsync();
        Assert.Equal(WorkflowInstanceStatuses.Completed, instanceStatus);
        var boundaryOccurrences = await db.SequenceFlowOccurrences
            .Where(item => item.InstanceId == instance.Id
                           && item.SequenceFlowId == 401
                           && item.IsTraversal)
            .ToListAsync();
        Assert.Single(boundaryOccurrences);
        Assert.Equal(
            1,
            await db.InstanceHistory.CountAsync(item =>
                item.InstanceId == instance.Id
                && item.ActionId == 401));
        Assert.Equal(TimerSubscriptionStatuses.Completed, storedSubscription.Status);
        Assert.Contains(
            timerJobStatus,
            new[]
            {
                WorkflowJobStatuses.Completed,
                WorkflowJobStatuses.Cancelled
            });
    }

    private async Task<long> PauseTimerWithIncidentAsync(
        long instanceId,
        int timerNodeId)
    {
        await using var db = fixture.CreateDbContext();
        var subscription = await db.TimerSubscriptions.SingleAsync(item =>
            item.InstanceId == instanceId
            && item.TimerNodeId == timerNodeId);
        var job = await db.WorkflowJobs.SingleAsync(item =>
            item.TimerSubscriptionId == subscription.Id);
        var now = DateTimeOffset.UtcNow;
        subscription.Status = TimerSubscriptionStatuses.Paused;
        subscription.UpdatedAt = now;
        job.Status = WorkflowJobStatuses.Incident;
        job.LastFailureCode = "test-paused-timer";
        job.LastFailureDescription = "Paused to exercise administrative override.";
        job.UpdatedAt = now;
        var incident = new WorkflowIncidentEntity
        {
            JobId = job.Id,
            OriginalJobId = job.Id,
            InstanceId = instanceId,
            WorkflowDefinitionId = job.WorkflowDefinitionId,
            WorkflowKey = job.WorkflowKey,
            NodeId = job.NodeId,
            NodeName = job.NodeName,
            Type = "testPausedTimer",
            Status = WorkflowIncidentStatuses.Open,
            Summary = "Timer paused for administrative override test.",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.WorkflowIncidents.Add(incident);
        await db.SaveChangesAsync();
        return incident.Id;
    }

    private async Task<AdministrativeActionCandidateDto> GetSingleCandidateAsync(
        long workflowId,
        int sourceNodeId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/administrative-actions/candidates/search",
            new AdministrativeActionCandidateSearchRequest
            {
                WorkflowDefinitionId = workflowId,
                SourceNodeId = sourceNodeId,
                Page = 1,
                PageSize = 20
            },
            "candidate-operator");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(
            (await ReadAsync<PagedResult<AdministrativeActionCandidateDto>>(response)).Items);
    }

    private async Task<AdministrativeActionBatchDetailDto>
        CreatePrepareConfirmAsync(
            long workflowId,
            int sourceNodeId,
            int boundaryNodeId,
            int flowId,
            AdministrativeActionCandidateDto candidate,
            string user)
    {
        AdministrativeActionBatchDetailDto batch;
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   new CreateAdministrativeActionBatchRequest(
                       workflowId,
                       sourceNodeId,
                       AdministrativeActionKinds.TimerBoundary,
                       flowId,
                       boundaryNodeId,
                       null,
                       "Fire the timer boundary now",
                       null,
                       new AdministrativeActionBatchSelectionDto(
                           AdministrativeActionBatchSelectionModes.Explicit,
                           [new AdministrativeActionPositionReferenceDto(
                               candidate.PositionKind,
                               candidate.PositionId)],
                           null,
                           null),
                       $"timer-override-{Guid.NewGuid():N}"),
                   user))
        {
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
        }

        await ProcessBatchJobAsync(batch.PreparationJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id, user);
        Assert.Equal(AdministrativeActionBatchStatuses.Ready, batch.Summary.Status);
        Assert.Equal(1, batch.Summary.EligibleItemCount);

        using (var confirm = await SendAsync(
                   HttpMethod.Post,
                   $"/api/administrative-action-batches/{batch.Summary.Id}/confirm",
                   new ConfirmAdministrativeActionBatchRequest(
                       batch.Summary.EligibleItemCount,
                       batch.Summary.TotalAffectedTaskCount,
                       batch.Summary.UpdatedAt),
                   user))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(confirm);
        }
        Assert.Equal(AdministrativeActionBatchStatuses.Queued, batch.Summary.Status);
        return batch;
    }

    private async Task ProcessBatchJobAsync(long jobId)
    {
        var lease = await LeaseSpecificJobAsync(
            jobId,
            WorkflowJobClasses.Activity);
        await ProcessLeaseAsync(lease);
    }

    private async Task<WorkflowJobLeaseRecord> LeaseSpecificJobAsync(
        long jobId,
        string queueClass)
    {
        await using (var db = fixture.CreateDbContext())
        {
            var changed = await db.WorkflowJobs
                .Where(item => item.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Priority, 1_000_000)
                    .SetProperty(item => item.DueAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            Assert.Equal(1, changed);
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider
            .GetRequiredService<IWorkflowJobRepository>();
        var leases = await repository.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"timer-override-test-{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount:
                    queueClass == WorkflowJobClasses.Activity ? 1 : 0,
                MaxPerInstance: 4,
                LeaseDuration: TimeSpan.FromMinutes(2)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        Assert.Equal(queueClass, lease.Job.QueueClass);
        return lease;
    }

    private async Task ProcessLeaseAsync(WorkflowJobLeaseRecord lease)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<IWorkflowJobProcessor>()
            .ProcessAsync(lease, CancellationToken.None);
    }

    private async Task<AdministrativeActionBatchDetailDto> GetBatchAsync(
        long batchId,
        string user)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/administrative-action-batches/{batchId}",
            user: user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<AdministrativeActionBatchDetailDto>(response);
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
            new StartInstanceRequest(workflowId, null, null, null),
            "starter");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
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
        ApiTestAuth.Authorize(request, user, []);
        request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel CreateSingleTimerWorkflow(bool useFlowEvidence)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var nodes = new List<FlowNodeModel>
        {
            new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new()
            {
                Id = 2,
                Name = "Wait for approval",
                Type = BpmnFlowNodeTypes.UserTask,
                Roles = ["Approver"],
                RequiresClaim = true
            },
            new() { Id = 3, Name = "Approved", Type = BpmnFlowNodeTypes.EndEvent },
            new()
            {
                Id = 6,
                Name = "Approval deadline",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = 2,
                CancelActivity = useFlowEvidence,
                Timer = new TimerDefinitionModel
                {
                    TimeDuration = useFlowEvidence ? "PT4S" : "P2D"
                }
            }
        };
        var flows = new List<SequenceFlowModel>
        {
            new() { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
            new() { Id = 201, Name = "Approve", SourceRef = 2, TargetRef = 3 }
        };
        if (useFlowEvidence)
        {
            nodes.AddRange(
            [
                new FlowNodeModel
                {
                    Id = 7,
                    Name = "Observe timer",
                    Type = BpmnFlowNodeTypes.ExclusiveGateway
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Timer observed",
                    Type = BpmnFlowNodeTypes.EndEvent
                },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Fallback",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ]);
            flows.AddRange(
            [
                new SequenceFlowModel
                {
                    Id = 401,
                    Name = "Escalate",
                    SourceRef = 6,
                    TargetRef = 7
                },
                new SequenceFlowModel
                {
                    Id = 501,
                    Name = "Observed exactly once",
                    SourceRef = 7,
                    TargetRef = 4,
                    Condition = "FlowInfo(401, 'traversals.count') == 1",
                    ConditionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 502,
                    Name = "Fallback",
                    SourceRef = 7,
                    TargetRef = 5,
                    IsDefault = true
                }
            ]);
        }
        else
        {
            nodes.Add(new FlowNodeModel
            {
                Id = 4,
                Name = "Escalated",
                Type = BpmnFlowNodeTypes.EndEvent
            });
            flows.Add(new SequenceFlowModel
            {
                Id = 401,
                Name = "Escalate",
                SourceRef = 6,
                TargetRef = 4
            });
        }

        return new WorkflowModel
        {
            Id = $"administrative-timer-override-{suffix}",
            Name = $"Administrative timer override {suffix}",
            InitialEventId = 1,
            FlowNodes = nodes,
            SequenceFlows = flows
        };
    }

    private static WorkflowModel CreateMultipleTimerWorkflow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"administrative-multiple-timers-{suffix}",
            Name = $"Administrative multiple timers {suffix}",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Wait for approval",
                    Type = BpmnFlowNodeTypes.UserTask
                },
                new FlowNodeModel { Id = 3, Name = "Approved", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 4, Name = "Escalated", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 5, Name = "Reminded", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel
                {
                    Id = 6,
                    Name = "Escalation deadline",
                    Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                    AttachedToRef = 2,
                    CancelActivity = true,
                    Timer = new TimerDefinitionModel { TimeDuration = "P2D" }
                },
                new FlowNodeModel
                {
                    Id = 7,
                    Name = "Reminder deadline",
                    Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                    AttachedToRef = 2,
                    CancelActivity = false,
                    Timer = new TimerDefinitionModel { TimeDuration = "P3D" }
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "Approve", SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 401, Name = "Escalate", SourceRef = 6, TargetRef = 4 },
                new SequenceFlowModel { Id = 402, Name = "Remind", SourceRef = 7, TargetRef = 5 }
            ]
        };
    }
}
