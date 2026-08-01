using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AutomaticActivationPersistenceTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task BlockedJobAndIncidentParticipateInTheCallerTransaction()
    {
        var workflowKey = $"automatic-activation-rollback-{Guid.NewGuid():N}";
        try
        {
            long definitionId;
            long instanceId;
            long tokenId;
            Guid activationId;
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(workflowKey);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();
                definitionId = definition.Id;

                var runtime = new WorkflowRuntimeRepository(setup);
                var instance = await runtime.AddInstanceAsync(
                    definitionId,
                    workflowKey,
                    null,
                    null,
                    null,
                    Node(1, "Automatic service", BpmnFlowNodeTypes.ServiceTask),
                    "tester",
                    [],
                    CancellationToken.None);
                instanceId = instance.Id;
                var token = await runtime.GetExecutionTokenAsync(
                    instance.ActiveTokenId,
                    false,
                    CancellationToken.None);
                Assert.NotNull(token);
                tokenId = token.Id;
                activationId = token.ActivationId;
            }

            await using (var staging = fixture.CreateDbContext())
            await using (var transaction = await staging.Database.BeginTransactionAsync())
            {
                var jobs = new WorkflowJobRepository(staging, fixture.DataSource);
                var runtime = new WorkflowRuntimeRepository(staging);
                var blocked = await jobs.EnqueueIncidentAsync(
                    BlockedJob(
                        definitionId,
                        workflowKey,
                        instanceId,
                        tokenId,
                        activationId,
                        automaticActivationCount: 1_001),
                    WorkflowIncidentTypes.AutomaticLoopLimit,
                    "Automatic loop limit reached.",
                    "attempted=1001; limit=1000",
                    CancellationToken.None);
                Assert.True(await runtime.SetExecutionTokenWaitAsync(
                    tokenId,
                    activationId,
                    ExecutionTokenWaitStates.AsyncBefore,
                    blocked.Id,
                    null,
                    CancellationToken.None));
                await staging.SaveChangesAsync();

                Assert.Equal(WorkflowJobStatuses.Incident, blocked.Status);
                Assert.Equal(1, await staging.WorkflowIncidents.CountAsync(
                    incident => incident.WorkflowKey == workflowKey));

                await transaction.RollbackAsync();
            }

            await using var verification = fixture.CreateDbContext();
            Assert.False(await verification.WorkflowJobs
                .AnyAsync(job => job.WorkflowKey == workflowKey));
            Assert.False(await verification.WorkflowIncidents
                .AnyAsync(incident => incident.WorkflowKey == workflowKey));
            var storedToken = await verification.ExecutionTokens
                .AsNoTracking()
                .SingleAsync(token => token.Id == tokenId);
            Assert.Null(storedToken.WaitState);
            Assert.Null(storedToken.WaitingJobId);
            Assert.Equal(0, storedToken.AutomaticActivationCount);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task AutomaticLoopIncidentRetryPreservesJobIdentityAndRestartsExactFencedAllowance()
    {
        var workflowKey = $"automatic-activation-retry-{Guid.NewGuid():N}";
        try
        {
            long definitionId;
            long instanceId;
            long tokenId;
            Guid activationId;
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(workflowKey);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();
                definitionId = definition.Id;

                var runtime = new WorkflowRuntimeRepository(setup);
                var instance = await runtime.AddInstanceAsync(
                    definitionId,
                    workflowKey,
                    null,
                    null,
                    null,
                    Node(7, "Automatic service", BpmnFlowNodeTypes.ServiceTask),
                    "tester",
                    [],
                    CancellationToken.None);
                instanceId = instance.Id;
                var token = await runtime.GetExecutionTokenAsync(
                    instance.ActiveTokenId,
                    false,
                    CancellationToken.None);
                Assert.NotNull(token);
                tokenId = token.Id;
                activationId = token.ActivationId;
            }

            long jobId;
            long incidentId;
            await using (var staging = fixture.CreateDbContext())
            await using (var transaction = await staging.Database.BeginTransactionAsync())
            {
                var jobs = new WorkflowJobRepository(staging, fixture.DataSource);
                var runtime = new WorkflowRuntimeRepository(staging);
                Assert.True(await runtime.SetExecutionTokenAutomaticActivationCountAsync(
                    tokenId,
                    activationId,
                    1_000,
                    CancellationToken.None));
                var blocked = await jobs.EnqueueIncidentAsync(
                    BlockedJob(
                        definitionId,
                        workflowKey,
                        instanceId,
                        tokenId,
                        activationId,
                        automaticActivationCount: 1_001),
                    WorkflowIncidentTypes.AutomaticLoopLimit,
                    "Automatic loop limit reached.",
                    $"tokenId={tokenId}; activationId={activationId}; observedCount=1001; limit=1000",
                    CancellationToken.None);
                jobId = blocked.Id;
                Assert.True(await runtime.SetExecutionTokenWaitAsync(
                    tokenId,
                    activationId,
                    ExecutionTokenWaitStates.AsyncBefore,
                    jobId,
                    null,
                    CancellationToken.None));
                await staging.SaveChangesAsync();
                await transaction.CommitAsync();

                incidentId = await staging.WorkflowIncidents
                    .Where(incident => incident.JobId == jobId)
                    .Select(incident => incident.Id)
                    .SingleAsync();
            }

            await using (var blockedState = fixture.CreateDbContext())
            {
                var job = await blockedState.WorkflowJobs
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == jobId);
                Assert.Equal(WorkflowJobStatuses.Incident, job.Status);
                Assert.Equal(1_001, job.AutomaticActivationCount);
                Assert.Equal(0, job.AttemptCount);
                Assert.Null(job.WorkerId);
                Assert.Null(job.LeaseToken);

                var incident = await blockedState.WorkflowIncidents
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == incidentId);
                Assert.Equal(WorkflowIncidentTypes.AutomaticLoopLimit, incident.Type);
                Assert.Equal(WorkflowIncidentStatuses.Open, incident.Status);
                Assert.Equal(jobId, incident.JobId);
                Assert.Equal(jobId, incident.OriginalJobId);
                Assert.Contains("observedCount=1001", incident.Details);
                Assert.Contains("limit=1000", incident.Details);

                var token = await blockedState.ExecutionTokens
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == tokenId);
                Assert.Equal(activationId, token.ActivationId);
                Assert.Equal(1_000, token.AutomaticActivationCount);
                Assert.Equal(ExecutionTokenWaitStates.AsyncBefore, token.WaitState);
                Assert.Equal(jobId, token.WaitingJobId);
                Assert.Null(token.WaitingTimerSubscriptionId);
            }

            // A manual retry is fenced by both activation identity and the
            // exact token wait phase, not just by the incident/job link.
            await using (var makeWaitStale = fixture.CreateDbContext())
            {
                var token = await makeWaitStale.ExecutionTokens
                    .SingleAsync(item => item.Id == tokenId);
                token.WaitState = ExecutionTokenWaitStates.AsyncAfter;
                await makeWaitStale.SaveChangesAsync();
            }
            await using (var staleRetry = fixture.CreateDbContext())
            {
                var repository = new WorkflowJobRepository(staleRetry, fixture.DataSource);
                await Assert.ThrowsAsync<WorkflowConflictException>(() =>
                    repository.RetryIncidentAsync(
                        incidentId,
                        "operations-admin",
                        DateTimeOffset.UtcNow,
                        CancellationToken.None));
            }
            await using (var restoreWait = fixture.CreateDbContext())
            {
                var token = await restoreWait.ExecutionTokens
                    .SingleAsync(item => item.Id == tokenId);
                token.WaitState = ExecutionTokenWaitStates.AsyncBefore;
                await restoreWait.SaveChangesAsync();
            }

            var retryDueAt = DateTimeOffset.UtcNow.AddSeconds(5);
            await using (var retry = fixture.CreateDbContext())
            {
                var repository = new WorkflowJobRepository(retry, fixture.DataSource);
                var retried = await repository.RetryIncidentAsync(
                    incidentId,
                    "operations-admin",
                    retryDueAt,
                    CancellationToken.None);
                Assert.NotNull(retried);
                Assert.Equal(jobId, retried.Id);
                Assert.Equal(WorkflowJobStatuses.Queued, retried.Status);
                Assert.Equal(1, retried.AutomaticActivationCount);
            }

            await using (var recovered = fixture.CreateDbContext())
            {
                Assert.Equal(1, await recovered.WorkflowJobs.CountAsync(
                    job => job.WorkflowKey == workflowKey));
                Assert.Equal(1, await recovered.WorkflowIncidents.CountAsync(
                    incident => incident.WorkflowKey == workflowKey));

                var job = await recovered.WorkflowJobs
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == jobId);
                Assert.Equal(WorkflowJobStatuses.Queued, job.Status);
                Assert.Equal(activationId, job.ActivationId);
                Assert.Equal(1, job.AutomaticActivationCount);
                Assert.Equal(0, job.AttemptCount);
                Assert.InRange(
                    (job.DueAt - retryDueAt).Duration(),
                    TimeSpan.Zero,
                    TimeSpan.FromMicroseconds(1));

                var token = await recovered.ExecutionTokens
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == tokenId);
                Assert.Equal(activationId, token.ActivationId);
                Assert.Equal(1, token.AutomaticActivationCount);
                Assert.Equal(ExecutionTokenWaitStates.AsyncBefore, token.WaitState);
                Assert.Equal(jobId, token.WaitingJobId);

                var incident = await recovered.WorkflowIncidents
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == incidentId);
                Assert.Equal(WorkflowIncidentStatuses.Resolved, incident.Status);
                Assert.Equal("operations-admin", incident.ResolvedBy);
                Assert.NotNull(incident.ResolvedAt);
            }
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task TokenAndComplexStateCountersSupportInheritancePreservationResetAndFencing()
    {
        var workflowKey = $"automatic-activation-persistence-{Guid.NewGuid():N}";
        try
        {
            long definitionId;
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(workflowKey);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();
                definitionId = definition.Id;
            }

            await using var db = fixture.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var repository = new WorkflowRuntimeRepository(db);
            var instance = await repository.AddInstanceAsync(
                definitionId,
                workflowKey,
                null,
                null,
                null,
                Node(1, "Start"),
                "tester",
                [],
                CancellationToken.None);
            var token = await repository.GetExecutionTokenAsync(
                instance.ActiveTokenId,
                false,
                CancellationToken.None);
            Assert.NotNull(token);
            Assert.Equal(0, token.AutomaticActivationCount);
            Assert.Empty(token.AutomaticActivationStateIds);

            Assert.True(await repository.SetExecutionTokenAutomaticActivationCountAsync(
                token.Id,
                token.ActivationId,
                17,
                CancellationToken.None));
            Assert.True(await repository.SetExecutionTokenAutomaticActivationStateIdsAsync(
                token.Id,
                token.ActivationId,
                [802, 801, 802],
                CancellationToken.None));
            await db.SaveChangesAsync();
            Assert.False(await repository.SetExecutionTokenAutomaticActivationCountAsync(
                token.Id,
                Guid.NewGuid(),
                18,
                CancellationToken.None));
            Assert.False(await repository.SetExecutionTokenAutomaticActivationStateIdsAsync(
                token.Id,
                Guid.NewGuid(),
                [803],
                CancellationToken.None));

            await repository.UpdateExecutionTokenAsync(
                token.Id,
                Node(2, "Preserve"),
                ExecutionTokenRecordStatuses.Active,
                null,
                100,
                null,
                null,
                Actor(),
                null,
                CancellationToken.None);
            var preserved = await repository.GetExecutionTokenAsync(
                token.Id,
                false,
                CancellationToken.None);
            Assert.NotNull(preserved);
            Assert.Equal(17, preserved.AutomaticActivationCount);
            Assert.Equal([801, 802], preserved.AutomaticActivationStateIds);

            var inherited = await repository.AddExecutionTokenAsync(
                instance.Id,
                Node(3, "Inherited"),
                null,
                101,
                Actor(),
                CancellationToken.None,
                automaticActivationCount: preserved.AutomaticActivationCount,
                automaticActivationStateIds: preserved.AutomaticActivationStateIds);
            Assert.Equal(17, inherited.AutomaticActivationCount);
            Assert.Equal([801, 802], inherited.AutomaticActivationStateIds);

            var gatewayExecution = await repository.AddGatewayExecutionAsync(
                instance.Id,
                4,
                BpmnFlowNodeTypes.ParallelGateway,
                GatewayExecutionRecordDirections.Split,
                null,
                null,
                null,
                [200, 201],
                CancellationToken.None);
            var branches = await repository.ListGatewayBranchesAsync(
                gatewayExecution.Id,
                CancellationToken.None);
            var forked = await repository.AddGatewayBranchTokensAsync(
                instance.Id,
                Node(4, "Fork", BpmnFlowNodeTypes.ParallelGateway),
                null,
                [branches[1].Id],
                [],
                Actor(),
                CancellationToken.None,
                automaticActivationCount: preserved.AutomaticActivationCount,
                automaticActivationStateIds: preserved.AutomaticActivationStateIds);
            var forkedToken = Assert.Single(forked);
            Assert.Equal(17, forkedToken.AutomaticActivationCount);
            Assert.Equal([801, 802], forkedToken.AutomaticActivationStateIds);

            var complexState = await repository.SaveComplexGatewayStateAsync(
                instance.Id,
                5,
                ComplexGatewayStateRecordPhases.WaitingForStart,
                0,
                [],
                [300],
                [],
                [],
                null,
                CancellationToken.None,
                automaticActivationCount: 17);
            Assert.Equal(17, complexState.AutomaticActivationCount);
            complexState = await repository.SaveComplexGatewayStateAsync(
                instance.Id,
                5,
                ComplexGatewayStateRecordPhases.WaitingForStart,
                1,
                [],
                [300],
                [],
                [],
                null,
                CancellationToken.None);
            Assert.Equal(17, complexState.AutomaticActivationCount);
            complexState = await repository.SaveComplexGatewayStateAsync(
                instance.Id,
                5,
                ComplexGatewayStateRecordPhases.WaitingForStart,
                2,
                [],
                [300],
                [],
                [],
                null,
                CancellationToken.None,
                automaticActivationCount: 0);
            Assert.Equal(0, complexState.AutomaticActivationCount);

            await repository.UpdateExecutionTokenAsync(
                token.Id,
                Node(6, "Reset"),
                ExecutionTokenRecordStatuses.Active,
                null,
                102,
                null,
                null,
                Actor(),
                null,
                CancellationToken.None,
                automaticActivationCount: 0,
                automaticActivationStateIds: []);
            var reset = await repository.GetExecutionTokenAsync(
                token.Id,
                false,
                CancellationToken.None);
            Assert.NotNull(reset);
            Assert.Equal(0, reset.AutomaticActivationCount);
            Assert.Empty(reset.AutomaticActivationStateIds);

            complexState = await repository.SaveComplexGatewayStateAsync(
                instance.Id,
                5,
                ComplexGatewayStateRecordPhases.WaitingForReset,
                2,
                [300],
                [300],
                [],
                [],
                null,
                CancellationToken.None,
                automaticActivationCount: 17);
            Assert.True(await repository.SetExecutionTokenAutomaticActivationStateIdsAsync(
                reset.Id,
                reset.ActivationId,
                [complexState.Id, 801],
                CancellationToken.None));
            Assert.True(await repository.SetExecutionTokenAutomaticActivationCountAsync(
                inherited.Id,
                inherited.ActivationId,
                0,
                CancellationToken.None));
            Assert.True(await repository.SetExecutionTokenAutomaticActivationStateIdsAsync(
                inherited.Id,
                inherited.ActivationId,
                [complexState.Id, 803],
                CancellationToken.None));
            Assert.True(await repository.SetExecutionTokenAutomaticActivationStateIdsAsync(
                forkedToken.Id,
                forkedToken.ActivationId,
                [complexState.Id, 801, 802],
                CancellationToken.None));
            await db.SaveChangesAsync();
            await repository.SetExecutionTokenStatusAsync(
                inherited.Id,
                ExecutionTokenRecordStatuses.Completed,
                ExecutionTokenTerminationReasons.NormalEnd,
                new NodeExecutionCompletionRecord(
                    NodeExecutionRecordStatuses.Completed,
                    NodeExecutionCompletionReasons.NormalEnd,
                    null,
                    null,
                    inherited.GatewayBranchId,
                    Actor()),
                CancellationToken.None);
            await db.SaveChangesAsync();

            // A marked row is authoritative even when its count was reset
            // below the fallback stored on the Complex Gateway state.
            var consumed = await repository.ConsumeExecutionTokenAutomaticActivationStateAsync(
                instance.Id,
                complexState.Id,
                fallbackAutomaticActivationCount: 17,
                CancellationToken.None);
            Assert.Equal(17, consumed.MaximumCount);
            Assert.Equal([801, 802, 803], consumed.InheritedStateIds);
            await db.SaveChangesAsync();
            var consumedTokens = await repository.GetExecutionTokensAsync(
                [reset.Id, inherited.Id, forkedToken.Id],
                CancellationToken.None);
            Assert.All(consumedTokens, item =>
                Assert.DoesNotContain(complexState.Id, item.AutomaticActivationStateIds));

            Assert.True(await repository.SetExecutionTokenAutomaticActivationCountAsync(
                reset.Id,
                reset.ActivationId,
                17,
                CancellationToken.None));
            Assert.True(await repository.SetExecutionTokenAutomaticActivationStateIdsAsync(
                reset.Id,
                reset.ActivationId,
                [complexState.Id],
                CancellationToken.None));
            await db.SaveChangesAsync();
            Assert.True(await repository.SetExecutionTokenAutomaticActivationCountAsync(
                reset.Id,
                reset.ActivationId,
                0,
                CancellationToken.None));
            var resetConsumed = await repository.ConsumeExecutionTokenAutomaticActivationStateAsync(
                instance.Id,
                complexState.Id,
                fallbackAutomaticActivationCount: 17,
                CancellationToken.None);
            Assert.Equal(0, resetConsumed.MaximumCount);
            Assert.Empty(resetConsumed.InheritedStateIds);

            var emptyConsumed = await repository.ConsumeExecutionTokenAutomaticActivationStateAsync(
                instance.Id,
                complexState.Id,
                fallbackAutomaticActivationCount: 9,
                CancellationToken.None);
            Assert.Equal(9, emptyConsumed.MaximumCount);
            Assert.Empty(emptyConsumed.InheritedStateIds);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repository.SetExecutionTokenAutomaticActivationCountAsync(
                    token.Id,
                    reset.ActivationId,
                    -1,
                    CancellationToken.None));
            await transaction.CommitAsync();
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task DurableJobCreateGetAndSearchPreserveAutomaticActivationCount()
    {
        var workflowKey = $"automatic-activation-job-{Guid.NewGuid():N}";
        try
        {
            long definitionId;
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(workflowKey);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();
                definitionId = definition.Id;
            }

            await using var db = fixture.CreateDbContext();
            var repository = new WorkflowJobRepository(db, fixture.DataSource);
            var job = await repository.EnqueueAsync(
                new WorkflowJobCreateRecord
                {
                    WorkflowDefinitionId = definitionId,
                    WorkflowKey = workflowKey,
                    ActivationId = Guid.NewGuid(),
                    AutomaticActivationCount = 23,
                    NodeId = 7,
                    NodeName = "Automatic service",
                    NodeType = BpmnFlowNodeTypes.ServiceTask,
                    Kind = WorkflowJobKinds.AsyncBefore,
                    QueueClass = WorkflowJobClasses.Activity,
                    Phase = "before",
                    DueAt = DateTimeOffset.UtcNow
                },
                CancellationToken.None);
            Assert.Equal(23, job.AutomaticActivationCount);

            var loaded = await repository.GetAsync(job.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(23, loaded.AutomaticActivationCount);

            var page = await repository.SearchJobsAsync(
                new WorkflowJobQuery
                {
                    WorkflowKey = workflowKey,
                    PageSize = 50
                },
                CancellationToken.None);
            Assert.Equal(23, Assert.Single(page.Items).AutomaticActivationCount);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repository.EnqueueAsync(
                    new WorkflowJobCreateRecord
                    {
                        WorkflowDefinitionId = definitionId,
                        WorkflowKey = workflowKey,
                        ActivationId = Guid.NewGuid(),
                        AutomaticActivationCount = -1,
                        NodeId = 8,
                        NodeName = "Invalid",
                        NodeType = BpmnFlowNodeTypes.ServiceTask,
                        Kind = WorkflowJobKinds.AsyncBefore,
                        QueueClass = WorkflowJobClasses.Activity,
                        Phase = "before",
                        DueAt = DateTimeOffset.UtcNow
                    },
                    CancellationToken.None));
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    private async Task DeleteWorkflowAsync(string workflowKey)
    {
        await using var cleanup = fixture.CreateDbContext();
        await cleanup.WorkflowInstances
            .Where(instance => instance.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowJobs
            .Where(job => job.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowDefinitions
            .Where(definition => definition.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
    }

    private static WorkflowDefinitionEntity NewDefinition(string workflowKey) => new()
    {
        Name = workflowKey,
        WorkflowKey = workflowKey,
        Version = 1,
        IsPublished = true,
        Definition = new WorkflowModel
        {
            Id = workflowKey,
            Name = workflowKey
        }
    };

    private static CurrentNodeSnapshot Node(
        int id,
        string name,
        string type = BpmnFlowNodeTypes.Task) =>
        new(id, name, null, type, [], false, false, null);

    private static NodeExecutionActorRecord Actor() => new("tester", []);

    private static WorkflowJobCreateRecord BlockedJob(
        long workflowDefinitionId,
        string workflowKey,
        long instanceId,
        long tokenId,
        Guid activationId,
        int automaticActivationCount) =>
        new()
        {
            InstanceId = instanceId,
            WorkflowDefinitionId = workflowDefinitionId,
            WorkflowKey = workflowKey,
            TokenId = tokenId,
            ActivationId = activationId,
            AutomaticActivationCount = automaticActivationCount,
            NodeId = 7,
            NodeName = "Automatic service",
            NodeType = BpmnFlowNodeTypes.ServiceTask,
            Kind = WorkflowJobKinds.AsyncBefore,
            QueueClass = WorkflowJobClasses.Activity,
            Phase = ExecutionTokenWaitStates.AsyncBefore,
            DueAt = DateTimeOffset.UtcNow
        };
}
