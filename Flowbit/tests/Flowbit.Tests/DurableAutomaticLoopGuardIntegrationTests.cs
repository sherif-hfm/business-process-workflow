using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class DurableAutomaticLoopGuardIntegrationTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task AsyncBeforeAndAfterSelfLoopBlocksNextActivationBeforeSecondBodyInvocation()
    {
        var workflowKey = $"automatic-loop-guard-{Guid.NewGuid():N}";
        EngineSettingRecord? previousSetting = null;
        fixture.ServiceInvocations.Reset();

        try
        {
            await using (var settingsScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var settings = settingsScope.ServiceProvider
                    .GetRequiredService<IEngineSettingsRepository>();
                previousSetting = await settings.GetByKeyAsync(
                    WorkflowAutomaticActivationGuard.SettingKey,
                    CancellationToken.None);
                await settings.SetAsync(
                    WorkflowAutomaticActivationGuard.SettingKey,
                    "1",
                    CancellationToken.None);
            }

            var instanceId = await InsertAndStartAsync(
                workflowKey,
                CreateAsyncBeforeAndAfterSelfLoop(workflowKey));

            long asyncBeforeJobId;
            await using (var queued = fixture.CreateDbContext())
            {
                var job = await queued.WorkflowJobs.SingleAsync(job =>
                    job.InstanceId == instanceId
                    && job.Kind == WorkflowJobKinds.AsyncBefore);
                asyncBeforeJobId = job.Id;
                Assert.Equal(WorkflowJobStatuses.Queued, job.Status);
                Assert.Equal(1, job.AutomaticActivationCount);
            }

            await PromoteAndProcessAsync(asyncBeforeJobId);

            long asyncAfterJobId;
            await using (var afterBody = fixture.CreateDbContext())
            {
                var jobs = await afterBody.WorkflowJobs
                    .Where(job => job.InstanceId == instanceId)
                    .OrderBy(job => job.Id)
                    .ToListAsync();
                Assert.Equal(2, jobs.Count);
                Assert.Equal(WorkflowJobStatuses.Completed, jobs[0].Status);
                var asyncAfter = Assert.Single(
                    jobs,
                    job => job.Kind == WorkflowJobKinds.AsyncAfter);
                asyncAfterJobId = asyncAfter.Id;
                Assert.Equal(WorkflowJobStatuses.Queued, asyncAfter.Status);
                Assert.Equal(1, asyncAfter.AutomaticActivationCount);
                Assert.Single(fixture.ServiceInvocations.Snapshot());
            }

            await PromoteAndProcessAsync(asyncAfterJobId);

            await using var blocked = fixture.CreateDbContext();
            var storedJobs = await blocked.WorkflowJobs
                .Where(job => job.InstanceId == instanceId)
                .OrderBy(job => job.Id)
                .ToListAsync();
            Assert.Equal(3, storedJobs.Count);
            Assert.Equal(
                2,
                storedJobs.Count(job => job.Status == WorkflowJobStatuses.Completed));

            var blockedJob = Assert.Single(
                storedJobs,
                job => job.Status == WorkflowJobStatuses.Incident);
            Assert.Equal(WorkflowJobKinds.AsyncBefore, blockedJob.Kind);
            Assert.Equal(2, blockedJob.AutomaticActivationCount);
            Assert.Equal(0, blockedJob.AttemptCount);

            var incident = await blocked.WorkflowIncidents.SingleAsync(incident =>
                incident.InstanceId == instanceId);
            Assert.Equal(WorkflowIncidentTypes.AutomaticLoopLimit, incident.Type);
            Assert.Equal(WorkflowIncidentStatuses.Open, incident.Status);
            Assert.Equal(blockedJob.Id, incident.JobId);
            Assert.Contains("\"observedCount\":2", incident.Details, StringComparison.Ordinal);
            Assert.Contains("\"configuredLimit\":1", incident.Details, StringComparison.Ordinal);

            var token = await blocked.ExecutionTokens.SingleAsync(token =>
                token.InstanceId == instanceId
                && token.Status == ExecutionTokenStatuses.Active);
            Assert.Equal(2, token.NodeId);
            Assert.Equal(1, token.AutomaticActivationCount);
            Assert.Equal(ExecutionTokenWaitStates.AsyncBefore, token.WaitState);
            Assert.Equal(blockedJob.Id, token.WaitingJobId);

            Assert.Equal(
                WorkflowInstanceStatuses.Running,
                await blocked.WorkflowInstances
                    .Where(instance => instance.Id == instanceId)
                    .Select(instance => instance.Status)
                    .SingleAsync());
            Assert.Single(fixture.ServiceInvocations.Snapshot());
        }
        finally
        {
            fixture.ServiceInvocations.Reset();
            await DeleteWorkflowAsync(workflowKey);
            await RestoreSettingAsync(previousSetting);
        }
    }

    [Fact]
    public async Task DuplicateFinalizationAcrossIndependentWorkerScopesCreatesOneBlockedTransition()
    {
        var workflowKey = $"automatic-loop-race-{Guid.NewGuid():N}";
        EngineSettingRecord? previousSetting = null;
        fixture.ServiceInvocations.Reset();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await using (var settingsScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var settings = settingsScope.ServiceProvider
                    .GetRequiredService<IEngineSettingsRepository>();
                previousSetting = await settings.GetByKeyAsync(
                    WorkflowAutomaticActivationGuard.SettingKey,
                    timeout.Token);
                await settings.SetAsync(
                    WorkflowAutomaticActivationGuard.SettingKey,
                    "1",
                    timeout.Token);
            }

            var instanceId = await InsertAndStartAsync(
                workflowKey,
                CreateAsyncBeforeAndAfterSelfLoop(workflowKey));
            long asyncBeforeJobId;
            await using (var queued = fixture.CreateDbContext())
            {
                asyncBeforeJobId = await queued.WorkflowJobs
                    .Where(job =>
                        job.InstanceId == instanceId
                        && job.Kind == WorkflowJobKinds.AsyncBefore)
                    .Select(job => job.Id)
                    .SingleAsync(timeout.Token);
            }
            await PromoteAndProcessAsync(asyncBeforeJobId);

            long asyncAfterJobId;
            await using (var queued = fixture.CreateDbContext())
            {
                asyncAfterJobId = await queued.WorkflowJobs
                    .Where(job =>
                        job.InstanceId == instanceId
                        && job.Kind == WorkflowJobKinds.AsyncAfter
                        && job.Status == WorkflowJobStatuses.Queued)
                    .Select(job => job.Id)
                    .SingleAsync(timeout.Token);
                Assert.Equal(
                    1,
                    await queued.ExecutionTokens
                        .Where(token =>
                            token.InstanceId == instanceId
                            && token.Status == ExecutionTokenStatuses.Active)
                        .Select(token => token.AutomaticActivationCount)
                        .SingleAsync(timeout.Token));
            }

            var lease = await PromoteAndLeaseAsync(
                asyncAfterJobId,
                $"automatic-loop-race:{Guid.NewGuid():N}",
                timeout.Token);
            await using var firstScope = fixture.Factory.Services.CreateAsyncScope();
            await using var secondScope = fixture.Factory.Services.CreateAsyncScope();
            var firstProcessor = firstScope.ServiceProvider
                .GetRequiredService<IWorkflowJobProcessor>();
            var secondProcessor = secondScope.ServiceProvider
                .GetRequiredService<IWorkflowJobProcessor>();
            var bothReady = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var readyCount = 0;

            async Task RaceAsync(IWorkflowJobProcessor processor)
            {
                if (Interlocked.Increment(ref readyCount) == 2)
                {
                    bothReady.TrySetResult();
                }
                await release.Task.WaitAsync(timeout.Token);
                await processor.ProcessAsync(lease, timeout.Token);
            }

            var first = RaceAsync(firstProcessor);
            var second = RaceAsync(secondProcessor);
            await bothReady.Task.WaitAsync(timeout.Token);
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(timeout.Token);

            await using var verification = fixture.CreateDbContext();
            var jobs = await verification.WorkflowJobs
                .Where(job => job.InstanceId == instanceId)
                .OrderBy(job => job.Id)
                .ToListAsync(timeout.Token);
            Assert.Equal(3, jobs.Count);
            Assert.Equal(
                2,
                jobs.Count(job => job.Status == WorkflowJobStatuses.Completed));
            var blockedJob = Assert.Single(
                jobs,
                job => job.Status == WorkflowJobStatuses.Incident);
            Assert.Equal(WorkflowJobKinds.AsyncBefore, blockedJob.Kind);
            Assert.Equal(2, blockedJob.AutomaticActivationCount);

            var incident = Assert.Single(await verification.WorkflowIncidents
                .Where(item => item.InstanceId == instanceId)
                .ToListAsync(timeout.Token));
            Assert.Equal(WorkflowIncidentTypes.AutomaticLoopLimit, incident.Type);
            Assert.Equal(blockedJob.Id, incident.JobId);

            Assert.Single(await verification.InstanceHistory
                .Where(history =>
                    history.InstanceId == instanceId
                    && history.FromStepId == 2
                    && history.ToStepId == 2)
                .ToListAsync(timeout.Token));
            Assert.Equal(
                2,
                await verification.NodeExecutions.CountAsync(execution =>
                    execution.InstanceId == instanceId
                    && execution.NodeId == 2,
                    timeout.Token));
            var token = await verification.ExecutionTokens.SingleAsync(item =>
                item.InstanceId == instanceId
                && item.Status == ExecutionTokenStatuses.Active,
                timeout.Token);
            Assert.Equal(blockedJob.Id, token.WaitingJobId);
            Assert.Equal(ExecutionTokenWaitStates.AsyncBefore, token.WaitState);
            Assert.Single(fixture.ServiceInvocations.Snapshot());
        }
        finally
        {
            fixture.ServiceInvocations.Reset();
            await DeleteWorkflowAsync(workflowKey);
            await RestoreSettingAsync(previousSetting);
        }
    }

    [Fact]
    public async Task PersistedCountSurvivesFreshServiceScopeBeforeNextActivationBlocks()
    {
        var workflowKey = $"automatic-loop-restart-{Guid.NewGuid():N}";
        EngineSettingRecord? previousSetting = null;
        fixture.ServiceInvocations.Reset();

        try
        {
            await using (var settingsScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var settings = settingsScope.ServiceProvider
                    .GetRequiredService<IEngineSettingsRepository>();
                previousSetting = await settings.GetByKeyAsync(
                    WorkflowAutomaticActivationGuard.SettingKey,
                    CancellationToken.None);
                await settings.SetAsync(
                    WorkflowAutomaticActivationGuard.SettingKey,
                    "1",
                    CancellationToken.None);
            }

            var instanceId = await InsertAndStartAsync(
                workflowKey,
                CreateAsyncBeforeAndAfterSelfLoop(workflowKey));
            long asyncBeforeJobId;
            await using (var queued = fixture.CreateDbContext())
            {
                asyncBeforeJobId = await queued.WorkflowJobs
                    .Where(job =>
                        job.InstanceId == instanceId
                        && job.Kind == WorkflowJobKinds.AsyncBefore)
                    .Select(job => job.Id)
                    .SingleAsync();
            }
            await PromoteAndProcessAsync(asyncBeforeJobId);

            long tokenId;
            Guid activationId;
            long asyncAfterJobId;
            await using (var restartScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var runtime = restartScope.ServiceProvider
                    .GetRequiredService<IWorkflowRuntimeRepository>();
                await using var read = fixture.CreateDbContext();
                var persistedTokenId = await read.ExecutionTokens
                    .Where(token =>
                        token.InstanceId == instanceId
                        && token.Status == ExecutionTokenStatuses.Active)
                    .Select(token => token.Id)
                    .SingleAsync();
                var reloaded = await runtime.GetExecutionTokenAsync(
                    persistedTokenId,
                    false,
                    CancellationToken.None);
                Assert.NotNull(reloaded);
                tokenId = reloaded.Id;
                activationId = reloaded.ActivationId;
                Assert.Equal(1, reloaded.AutomaticActivationCount);

                var jobs = restartScope.ServiceProvider
                    .GetRequiredService<IWorkflowJobRepository>();
                var page = await jobs.SearchJobsAsync(
                    new WorkflowJobQuery
                    {
                        InstanceId = instanceId,
                        Kinds = [WorkflowJobKinds.AsyncAfter],
                        Statuses = [WorkflowJobStatuses.Queued]
                    },
                    CancellationToken.None);
                var asyncAfter = Assert.Single(page.Items);
                asyncAfterJobId = asyncAfter.Id;
                Assert.Equal(tokenId, asyncAfter.TokenId);
                Assert.Equal(activationId, asyncAfter.ActivationId);
                Assert.Equal(1, asyncAfter.AutomaticActivationCount);
            }

            // The processor and repositories used below are resolved from a
            // later scope, modeling worker/service restart with no in-memory
            // engine state carried across the durable boundary.
            await PromoteAndProcessAsync(asyncAfterJobId);

            await using var blocked = fixture.CreateDbContext();
            var blockedJob = await blocked.WorkflowJobs.SingleAsync(job =>
                job.InstanceId == instanceId
                && job.Status == WorkflowJobStatuses.Incident);
            Assert.Equal(2, blockedJob.AutomaticActivationCount);
            Assert.Equal(WorkflowJobKinds.AsyncBefore, blockedJob.Kind);
            Assert.Single(await blocked.WorkflowIncidents
                .Where(incident =>
                    incident.InstanceId == instanceId
                    && incident.Type == WorkflowIncidentTypes.AutomaticLoopLimit)
                .ToListAsync());
            var token = await blocked.ExecutionTokens.SingleAsync(item =>
                item.Id == tokenId);
            Assert.Equal(1, token.AutomaticActivationCount);
            Assert.NotEqual(activationId, token.ActivationId);
            Assert.Equal(blockedJob.Id, token.WaitingJobId);
            Assert.Single(fixture.ServiceInvocations.Snapshot());
        }
        finally
        {
            fixture.ServiceInvocations.Reset();
            await DeleteWorkflowAsync(workflowKey);
            await RestoreSettingAsync(previousSetting);
        }
    }

    [Fact]
    public async Task ComplexEmptyResetCarriesActivationOutputCountIntoNextCycle()
    {
        var workflowKey = $"automatic-complex-empty-reset-{Guid.NewGuid():N}";
        EngineSettingRecord? previousSetting = null;
        fixture.ServiceInvocations.Reset();

        try
        {
            await using (var settingsScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var settings = settingsScope.ServiceProvider
                    .GetRequiredService<IEngineSettingsRepository>();
                previousSetting = await settings.GetByKeyAsync(
                    WorkflowAutomaticActivationGuard.SettingKey,
                    CancellationToken.None);
                await settings.SetAsync(
                    WorkflowAutomaticActivationGuard.SettingKey,
                    "1",
                    CancellationToken.None);
            }

            var instanceId = await InsertAndStartAsync(
                workflowKey,
                CreateComplexEmptyResetLoop(workflowKey, includeUserReset: false));

            long firstJobId;
            await using (var queued = fixture.CreateDbContext())
            {
                var firstJob = await queued.WorkflowJobs.SingleAsync(job =>
                    job.InstanceId == instanceId
                    && job.Kind == WorkflowJobKinds.AsyncBefore);
                firstJobId = firstJob.Id;
                Assert.Equal(WorkflowJobStatuses.Queued, firstJob.Status);
                Assert.Equal(1, firstJob.AutomaticActivationCount);
            }

            await PromoteAndProcessAsync(firstJobId);

            await using var blocked = fixture.CreateDbContext();
            var jobs = await blocked.WorkflowJobs
                .Where(job => job.InstanceId == instanceId)
                .OrderBy(job => job.Id)
                .ToListAsync();
            var diagnosticTokens = await blocked.ExecutionTokens
                .Where(token => token.InstanceId == instanceId)
                .OrderBy(token => token.Id)
                .Select(token => new
                {
                    token.Id,
                    token.NodeId,
                    token.Status,
                    token.WaitState,
                    token.AutomaticActivationCount,
                    token.AutomaticActivationStateIds
                })
                .ToListAsync();
            var diagnosticStates = await blocked.ComplexGatewayStates
                .Where(state => state.InstanceId == instanceId)
                .Select(state => new { state.Cycle, state.Phase, state.RemainingFlowIds })
                .ToListAsync();
            Assert.True(
                jobs.Count == 2,
                $"Jobs={JsonSerializer.Serialize(jobs.Select(job => new { job.Id, job.Status, job.Kind }))}; "
                + $"Tokens={JsonSerializer.Serialize(diagnosticTokens)}; "
                + $"States={JsonSerializer.Serialize(diagnosticStates)}");
            Assert.Equal(WorkflowJobStatuses.Completed, jobs[0].Status);
            Assert.Equal(1, jobs[0].AutomaticActivationCount);

            var blockedJob = jobs[1];
            Assert.Equal(WorkflowJobStatuses.Incident, blockedJob.Status);
            Assert.Equal(WorkflowJobKinds.AsyncBefore, blockedJob.Kind);
            Assert.Equal(2, blockedJob.AutomaticActivationCount);
            Assert.Equal(0, blockedJob.AttemptCount);

            var incident = await blocked.WorkflowIncidents.SingleAsync(incident =>
                incident.InstanceId == instanceId);
            Assert.Equal(WorkflowIncidentTypes.AutomaticLoopLimit, incident.Type);
            Assert.Equal(blockedJob.Id, incident.JobId);
            Assert.Single(fixture.ServiceInvocations.Snapshot());

            var complexState = await blocked.ComplexGatewayStates.SingleAsync(state =>
                state.InstanceId == instanceId);
            Assert.Equal(1, complexState.Cycle);
            Assert.Equal(ComplexGatewayStateRecordPhases.WaitingForReset, complexState.Phase);

            var blockedToken = await blocked.ExecutionTokens.SingleAsync(token =>
                token.InstanceId == instanceId
                && token.WaitingJobId == blockedJob.Id);
            Assert.Equal(1, blockedToken.AutomaticActivationCount);
            Assert.Contains(complexState.Id, blockedToken.AutomaticActivationStateIds);
        }
        finally
        {
            fixture.ServiceInvocations.Reset();
            await DeleteWorkflowAsync(workflowKey);
            await RestoreSettingAsync(previousSetting);
        }
    }

    [Fact]
    public async Task ComplexEmptyResetUsesCountAfterUserActionReset()
    {
        var workflowKey = $"automatic-complex-user-reset-{Guid.NewGuid():N}";
        EngineSettingRecord? previousSetting = null;
        fixture.ServiceInvocations.Reset();

        try
        {
            await using (var settingsScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var settings = settingsScope.ServiceProvider
                    .GetRequiredService<IEngineSettingsRepository>();
                previousSetting = await settings.GetByKeyAsync(
                    WorkflowAutomaticActivationGuard.SettingKey,
                    CancellationToken.None);
                await settings.SetAsync(
                    WorkflowAutomaticActivationGuard.SettingKey,
                    "1",
                    CancellationToken.None);
            }

            var instanceId = await InsertAndStartAsync(
                workflowKey,
                CreateComplexEmptyResetLoop(workflowKey, includeUserReset: true));

            long firstJobId;
            await using (var queued = fixture.CreateDbContext())
            {
                firstJobId = await queued.WorkflowJobs
                    .Where(job => job.InstanceId == instanceId)
                    .Select(job => job.Id)
                    .SingleAsync();
            }
            await PromoteAndProcessAsync(firstJobId);

            long userTaskId;
            await using (var waiting = fixture.CreateDbContext())
            {
                var activeUserTasks = await waiting.UserTasks
                    .Where(task =>
                        task.InstanceId == instanceId
                        && task.Status == UserTaskStatuses.Active
                        && task.NodeId == 11)
                    .ToListAsync();
                var diagnosticTokens = await waiting.ExecutionTokens
                    .Where(token => token.InstanceId == instanceId)
                    .OrderBy(token => token.Id)
                    .Select(token => new
                    {
                        token.Id,
                        token.NodeId,
                        token.Status,
                        token.WaitState,
                        token.AutomaticActivationCount
                    })
                    .ToListAsync();
                var diagnosticJobs = await waiting.WorkflowJobs
                    .Where(job => job.InstanceId == instanceId)
                    .Select(job => new { job.Id, job.Status, job.Kind })
                    .ToListAsync();
                Assert.True(
                    activeUserTasks.Count == 1,
                    $"Tasks={JsonSerializer.Serialize(activeUserTasks.Select(task => new { task.Id, task.NodeId, task.Status }))}; "
                    + $"Jobs={JsonSerializer.Serialize(diagnosticJobs)}; "
                    + $"Tokens={JsonSerializer.Serialize(diagnosticTokens)}");
                userTaskId = activeUserTasks[0].Id;
                Assert.Empty(await waiting.WorkflowIncidents
                    .Where(incident => incident.InstanceId == instanceId)
                    .ToListAsync());
            }

            await using (var actionScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var engine = actionScope.ServiceProvider
                    .GetRequiredService<IWorkflowEngineService>();
                var action = await engine.TakeUserTaskFlowAsync(
                    userTaskId,
                    1101,
                    new ActorContext(
                        "reviewer",
                        ["User", "admin"],
                        new Dictionary<string, string>()),
                    null,
                    CancellationToken.None);
                Assert.NotNull(action);
            }

            long secondJobId;
            await using (var reset = fixture.CreateDbContext())
            {
                var jobs = await reset.WorkflowJobs
                    .Where(job => job.InstanceId == instanceId)
                    .OrderBy(job => job.Id)
                    .ToListAsync();
                Assert.Equal(2, jobs.Count);
                Assert.Equal(WorkflowJobStatuses.Completed, jobs[0].Status);
                Assert.Equal(WorkflowJobStatuses.Queued, jobs[1].Status);
                Assert.Equal(1, jobs[1].AutomaticActivationCount);
                secondJobId = jobs[1].Id;
                Assert.Empty(await reset.WorkflowIncidents
                    .Where(incident => incident.InstanceId == instanceId)
                    .ToListAsync());
            }

            await PromoteAndProcessAsync(secondJobId);

            await using var verified = fixture.CreateDbContext();
            Assert.Equal(2, fixture.ServiceInvocations.Snapshot().Count);
            Assert.Single(await verified.UserTasks
                .Where(task =>
                    task.InstanceId == instanceId
                    && task.Status == UserTaskStatuses.Active
                    && task.NodeId == 11)
                .ToListAsync());
            Assert.Empty(await verified.WorkflowIncidents
                .Where(incident => incident.InstanceId == instanceId)
                .ToListAsync());
        }
        finally
        {
            fixture.ServiceInvocations.Reset();
            await DeleteWorkflowAsync(workflowKey);
            await RestoreSettingAsync(previousSetting);
        }
    }

    [Fact]
    public async Task OrdinaryRetryReusesAutomaticActivationCountAndJobIdentity()
    {
        var workflowKey = $"automatic-retry-count-{Guid.NewGuid():N}";
        EngineSettingRecord? previousSetting = null;
        fixture.ServiceInvocations.Reset();

        try
        {
            await using (var settingsScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var settings = settingsScope.ServiceProvider
                    .GetRequiredService<IEngineSettingsRepository>();
                previousSetting = await settings.GetByKeyAsync(
                    WorkflowAutomaticActivationGuard.SettingKey,
                    CancellationToken.None);
                await settings.SetAsync(
                    WorkflowAutomaticActivationGuard.SettingKey,
                    "1",
                    CancellationToken.None);
            }

            var instanceId = await InsertAndStartAsync(
                workflowKey,
                CreateFailingRetryWorkflow(workflowKey));

            long jobId;
            long tokenId;
            Guid activationId;
            await using (var staged = fixture.CreateDbContext())
            {
                var job = await staged.WorkflowJobs.SingleAsync(item =>
                    item.InstanceId == instanceId);
                jobId = job.Id;
                Assert.Equal(WorkflowJobStatuses.Queued, job.Status);
                Assert.Equal(1, job.AutomaticActivationCount);
                Assert.Equal(0, job.AttemptCount);
                Assert.Equal(2, job.MaxAttempts);

                var token = await staged.ExecutionTokens.SingleAsync(item =>
                    item.InstanceId == instanceId
                    && item.Status == ExecutionTokenStatuses.Active);
                tokenId = token.Id;
                activationId = token.ActivationId;
                Assert.Equal(1, token.AutomaticActivationCount);
                Assert.Equal(jobId, token.WaitingJobId);
            }

            await PromoteAndProcessAsync(jobId);

            await using (var retryScheduled = fixture.CreateDbContext())
            {
                var job = await retryScheduled.WorkflowJobs.SingleAsync(item =>
                    item.Id == jobId);
                Assert.Equal(WorkflowJobStatuses.Retry, job.Status);
                Assert.Equal(1, job.AttemptCount);
                Assert.Equal(1, job.AutomaticActivationCount);

                var token = await retryScheduled.ExecutionTokens.SingleAsync(item =>
                    item.Id == tokenId);
                Assert.Equal(activationId, token.ActivationId);
                Assert.Equal(1, token.AutomaticActivationCount);
                Assert.Equal(jobId, token.WaitingJobId);
                Assert.Empty(await retryScheduled.WorkflowIncidents
                    .Where(incident => incident.InstanceId == instanceId)
                    .ToListAsync());
            }

            await PromoteAndProcessAsync(jobId);

            await using var exhausted = fixture.CreateDbContext();
            var exhaustedJob = await exhausted.WorkflowJobs.SingleAsync(item =>
                item.Id == jobId);
            Assert.Equal(WorkflowJobStatuses.Incident, exhaustedJob.Status);
            Assert.Equal(2, exhaustedJob.AttemptCount);
            Assert.Equal(1, exhaustedJob.AutomaticActivationCount);

            var exhaustedToken = await exhausted.ExecutionTokens.SingleAsync(item =>
                item.Id == tokenId);
            Assert.Equal(activationId, exhaustedToken.ActivationId);
            Assert.Equal(1, exhaustedToken.AutomaticActivationCount);
            Assert.Equal(jobId, exhaustedToken.WaitingJobId);

            var incident = await exhausted.WorkflowIncidents.SingleAsync(item =>
                item.InstanceId == instanceId);
            Assert.Equal("job_execution_failed", incident.Type);
            Assert.NotEqual(WorkflowIncidentTypes.AutomaticLoopLimit, incident.Type);
            Assert.Equal(jobId, incident.JobId);
            Assert.Equal(2, fixture.ServiceInvocations.Snapshot().Count);
        }
        finally
        {
            fixture.ServiceInvocations.Reset();
            await DeleteWorkflowAsync(workflowKey);
            await RestoreSettingAsync(previousSetting);
        }
    }

    private async Task<long> InsertAndStartAsync(
        string workflowKey,
        WorkflowModel definition)
    {
        long workflowId;
        await using (var setup = fixture.CreateDbContext())
        {
            var entity = new WorkflowDefinitionEntity
            {
                Name = workflowKey,
                WorkflowKey = workflowKey,
                Version = 1,
                IsPublished = true,
                IsDefault = true,
                DefaultActivationId = Guid.NewGuid(),
                DefaultActivatedAt = DateTimeOffset.UtcNow,
                Definition = definition
            };
            setup.WorkflowDefinitions.Add(entity);
            await setup.SaveChangesAsync();
            workflowId = entity.Id;
        }

        await using var startScope = fixture.Factory.Services.CreateAsyncScope();
        var engine = startScope.ServiceProvider.GetRequiredService<IWorkflowEngineService>();
        var started = await engine.StartInstanceSlimAsync(
            workflowId,
            null,
            new ActorContext(
                "starter",
                ["User", "admin"],
                new Dictionary<string, string>()),
            null,
            null,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        return started.Id;
    }

    private async Task PromoteAndProcessAsync(long jobId)
    {
        await using (var promote = fixture.CreateDbContext())
        {
            var changed = await promote.WorkflowJobs
                .Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Priority, int.MaxValue)
                    .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow));
            Assert.Equal(1, changed);
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var leases = await repository.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"automatic-loop-test:{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: 1,
                MaxPerInstance: 4,
                LeaseDuration: TimeSpan.FromMinutes(1)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);

        var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();
        await processor.ProcessAsync(lease, CancellationToken.None);
    }

    private async Task<WorkflowJobLeaseRecord> PromoteAndLeaseAsync(
        long jobId,
        string workerId,
        CancellationToken cancellationToken)
    {
        await using (var promote = fixture.CreateDbContext())
        {
            var changed = await promote.WorkflowJobs
                .Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Priority, int.MaxValue)
                    .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow),
                    cancellationToken);
            Assert.Equal(1, changed);
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var leases = await repository.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                workerId,
                MaxCount: 1,
                MaxActivityCount: 1,
                MaxPerInstance: 4,
                LeaseDuration: TimeSpan.FromMinutes(1)),
            cancellationToken);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        return lease;
    }

    private async Task RestoreSettingAsync(EngineSettingRecord? previousSetting)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<IEngineSettingsRepository>();
        if (previousSetting is null)
        {
            await settings.DeleteAsync(
                WorkflowAutomaticActivationGuard.SettingKey,
                CancellationToken.None);
            return;
        }

        await settings.SetAsync(
            WorkflowAutomaticActivationGuard.SettingKey,
            previousSetting.Value,
            CancellationToken.None);
    }

    private async Task DeleteWorkflowAsync(string workflowKey)
    {
        await using var cleanup = fixture.CreateDbContext();
        await cleanup.WorkflowIncidents
            .Where(incident => incident.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowJobs
            .Where(job => job.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.TimerSubscriptions
            .Where(subscription => subscription.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowInstances
            .Where(instance => instance.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowDefinitions
            .Where(definition => definition.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowJobSnapshots
            .Where(snapshot => !cleanup.WorkflowJobs.Any(job => job.SnapshotId == snapshot.Id))
            .ExecuteDeleteAsync();
    }

    private static WorkflowModel CreateAsyncBeforeAndAfterSelfLoop(string workflowKey) =>
        new()
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "marker",
                    DataType = WorkflowVariableTypes.String,
                    DefaultValue = JsonSerializer.SerializeToElement("initial")
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Durable automatic loop",
                    Type = BpmnFlowNodeTypes.ServiceTask,
                    AsyncBefore = true,
                    AsyncAfter = true,
                    Job = new JobPolicyModel { RetryDelays = [] },
                    Service = new ServiceTaskModel
                    {
                        Url = "https://tests.local/send-reminder",
                        Method = "POST",
                        Body = """{"kind":"automatic-loop-guard"}"""
                    }
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 2 }
            ]
        };

    private static WorkflowModel CreateComplexEmptyResetLoop(
        string workflowKey,
        bool includeUserReset)
    {
        var nodes = new List<FlowNodeModel>
        {
            new()
            {
                Id = 1,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent
            },
            new()
            {
                Id = 2,
                Name = "Keep instance alive",
                Type = BpmnFlowNodeTypes.ParallelGateway
            },
            new()
            {
                Id = 3,
                Name = "Manual keepalive",
                Type = BpmnFlowNodeTypes.UserTask
            },
            new()
            {
                Id = 4,
                Name = "Complex cycle",
                Type = BpmnFlowNodeTypes.ComplexGateway,
                ActivationCondition = "IncomingCount(1001) >= 1"
            },
            new()
            {
                Id = 5,
                Name = "Durable activation output",
                Type = BpmnFlowNodeTypes.ServiceTask,
                AsyncBefore = true,
                Job = new JobPolicyModel { RetryDelays = [] },
                Service = new ServiceTaskModel
                {
                    Url = "https://tests.local/send-reminder",
                    Method = "POST",
                    Body = """{"kind":"complex-empty-reset"}"""
                }
            },
            new()
            {
                Id = 6,
                Name = "Body route",
                Type = BpmnFlowNodeTypes.ExclusiveGateway
            },
            new()
            {
                Id = 7,
                Name = "Reset route",
                Type = BpmnFlowNodeTypes.Task
            },
            new()
            {
                Id = 8,
                Name = "Activation branch end",
                Type = BpmnFlowNodeTypes.EndEvent
            },
            new()
            {
                Id = 9,
                Name = "Stop keepalive",
                Type = BpmnFlowNodeTypes.TerminateEndEvent
            },
            new()
            {
                Id = 10,
                Name = "Complex input merge",
                Type = BpmnFlowNodeTypes.ExclusiveGateway
            }
        };
        if (includeUserReset)
        {
            nodes.Add(new FlowNodeModel
            {
                Id = 11,
                Name = "Intentional external wait",
                Type = BpmnFlowNodeTypes.UserTask
            });
        }

        var flows = new List<SequenceFlowModel>
        {
            new() { Id = 101, SourceRef = 1, TargetRef = 2 },
            new() { Id = 201, SourceRef = 2, TargetRef = 3 },
            new() { Id = 202, SourceRef = 2, TargetRef = 10 },
            new() { Id = 301, SourceRef = 3, TargetRef = 9 },
            new()
            {
                Id = 401,
                SourceRef = 4,
                TargetRef = 5,
                Condition = "[gateway.waitingForStart]"
            },
            new()
            {
                Id = 402,
                SourceRef = 4,
                TargetRef = 7,
                Condition = "not [gateway.waitingForStart]"
            },
            new()
            {
                Id = 601,
                SourceRef = 6,
                TargetRef = 10,
                Condition = "[loopBack] == true",
                ConditionPriority = 1
            },
            new()
            {
                Id = 602,
                SourceRef = 6,
                TargetRef = 8,
                IsDefault = true
            },
            new() { Id = 701, SourceRef = 7, TargetRef = 10 },
            new() { Id = 1001, SourceRef = 10, TargetRef = 4 }
        };
        if (includeUserReset)
        {
            flows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 11 });
            flows.Add(new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 6 });
        }
        else
        {
            flows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 });
        }

        return new WorkflowModel
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "loopBack",
                    DataType = WorkflowVariableTypes.Boolean,
                    DefaultValue = JsonSerializer.SerializeToElement(false)
                }
            ],
            FlowNodes = nodes,
            SequenceFlows = flows
        };
    }

    private static WorkflowModel CreateFailingRetryWorkflow(string workflowKey) =>
        new()
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Retrying durable service",
                    Type = BpmnFlowNodeTypes.ServiceTask,
                    AsyncBefore = true,
                    Job = new JobPolicyModel
                    {
                        FailureHandling = JobFailureHandling.RetryFirst,
                        RetryDelays = ["PT1S"]
                    },
                    Service = new ServiceTaskModel
                    {
                        Url = "https://tests.local/unconfigured-retry-failure",
                        Method = "POST",
                        Body = """{"kind":"automatic-retry-count"}"""
                    }
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "End",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
            ]
        };
}
