using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class DurableJobRepositoryTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task EnqueueManyPersistsConditionalWakeWaveInInputOrder()
    {
        var suffix = $"conditional-wave-{Guid.NewGuid():N}";
        try
        {
            long definitionId;
            long instanceId;
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(suffix);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();
                var instance = NewInstance(definition, suffix);
                setup.WorkflowInstances.Add(instance);
                await setup.SaveChangesAsync();
                definitionId = definition.Id;
                instanceId = instance.Id;
            }

            var dueAt = DateTimeOffset.UtcNow;
            var creates = Enumerable.Range(1, 3)
                .Select(index => new WorkflowJobCreateRecord
                {
                    InstanceId = instanceId,
                    WorkflowDefinitionId = definitionId,
                    WorkflowKey = suffix,
                    ActivationId = Guid.NewGuid(),
                    NodeId = index,
                    NodeName = $"condition-{index}",
                    NodeType = "intermediateConditionalCatchEvent",
                    Kind = WorkflowJobKinds.ConditionalWake,
                    QueueClass = WorkflowJobClasses.Control,
                    Phase = WorkflowJobKinds.ConditionalWake,
                    DueAt = dueAt,
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        selectedFlowId = index + 100
                    })
                })
                .ToArray();

            await using var db = fixture.CreateDbContext();
            var repository = new WorkflowJobRepository(db, fixture.DataSource);
            var created = await repository.EnqueueManyAsync(creates, CancellationToken.None);

            Assert.Equal(creates.Select(item => item.NodeId), created.Select(item => item.NodeId));
            Assert.All(created, job =>
            {
                Assert.Equal(WorkflowJobKinds.ConditionalWake, job.Kind);
                Assert.Equal(WorkflowJobClasses.Control, job.QueueClass);
                Assert.Equal(WorkflowJobStatuses.Queued, job.Status);
                Assert.True(job.Id > 0);
            });
            Assert.Equal(
                created.Select(job => job.Id),
                await db.WorkflowJobs
                    .Where(job => job.InstanceId == instanceId)
                    .OrderBy(job => job.Id)
                    .Select(job => job.Id)
                    .ToArrayAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(suffix);
        }
    }

    [Fact]
    public async Task AcquisitionSelectsOneFairCandidatePerInstanceBeforeLimit()
    {
        var suffix = $"fair-instance-{Guid.NewGuid():N}";
        try
        {
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(suffix);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();

                var instances = Enumerable.Range(0, 3)
                    .Select(_ => NewInstance(definition, suffix))
                    .ToArray();
                setup.WorkflowInstances.AddRange(instances);
                await setup.SaveChangesAsync();

                var jobs = Enumerable.Range(0, 12)
                    .Select(index => NewJob(
                        definition,
                        instances[0].Id,
                        suffix,
                        index,
                        priority: 1_000_000))
                    .Concat(
                        [
                            NewJob(definition, instances[1].Id, suffix, 100, priority: 999_999),
                            NewJob(definition, instances[2].Id, suffix, 101, priority: 999_998)
                        ])
                    .ToArray();
                setup.WorkflowJobs.AddRange(jobs);
                await setup.SaveChangesAsync();
            }

            await using var db = fixture.CreateDbContext();
            var repository = new WorkflowJobRepository(db, fixture.DataSource);
            var leases = await repository.LeaseRunnableAsync(
                new WorkflowJobLeaseRequest(
                    $"fairness-test-{suffix}",
                    MaxCount: 3,
                    MaxActivityCount: 0,
                    MaxPerInstance: 4,
                    LeaseDuration: TimeSpan.FromMinutes(1)),
                CancellationToken.None);

            var matching = leases
                .Where(lease => lease.Job.WorkflowKey == suffix)
                .ToArray();
            Assert.Equal(3, matching.Length);
            Assert.Equal(3, matching.Select(lease => lease.Job.InstanceId).Distinct().Count());
        }
        finally
        {
            await DeleteWorkflowAsync(suffix);
        }
    }

    [Fact]
    public async Task AcquisitionAppliesFairnessBeforeAnyBoundedCandidateLimit()
    {
        var suffix = $"fair-deep-backlog-{Guid.NewGuid():N}";
        try
        {
            long[] instanceIds;
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(suffix);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();

                var instances = Enumerable.Range(0, 3)
                    .Select(_ => NewInstance(definition, suffix))
                    .ToArray();
                setup.WorkflowInstances.AddRange(instances);
                await setup.SaveChangesAsync();
                instanceIds = instances.Select(instance => instance.Id).ToArray();

                // This exceeds the old 128-row acquisition frontier. All of
                // those globally earlier rows belong to one instance.
                setup.WorkflowJobs.AddRange(
                    Enumerable.Range(0, 400)
                        .Select(index => NewJob(
                            definition,
                            instances[0].Id,
                            suffix,
                            index,
                            priority: 1_000_000))
                        .Concat(
                        [
                            NewJob(definition, instances[1].Id, suffix, 500, 999_999),
                            NewJob(definition, instances[2].Id, suffix, 501, 999_998)
                        ]));
                await setup.SaveChangesAsync();
            }

            await using var db = fixture.CreateDbContext();
            var repository = new WorkflowJobRepository(db, fixture.DataSource);
            var leases = await repository.LeaseRunnableAsync(
                new WorkflowJobLeaseRequest(
                    $"deep-fairness-test-{suffix}",
                    MaxCount: 3,
                    MaxActivityCount: 0,
                    MaxPerInstance: 4,
                    LeaseDuration: TimeSpan.FromMinutes(1)),
                CancellationToken.None);

            Assert.Equal(
                instanceIds.Order(),
                leases.Select(lease => lease.Job.InstanceId!.Value).Order());
        }
        finally
        {
            await DeleteWorkflowAsync(suffix);
        }
    }

    [Fact]
    public async Task SaturatedActivityInstanceCannotHideAnotherRunnableInstance()
    {
        var suffix = $"fair-saturated-{Guid.NewGuid():N}";
        try
        {
            long eligibleInstanceId;
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(suffix);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();

                var saturated = NewInstance(definition, suffix);
                var eligible = NewInstance(definition, suffix);
                setup.WorkflowInstances.AddRange(saturated, eligible);
                await setup.SaveChangesAsync();
                eligibleInstanceId = eligible.Id;

                var active = NewJob(
                    definition,
                    saturated.Id,
                    suffix,
                    0,
                    priority: 2_000_000);
                active.QueueClass = WorkflowJobClasses.Activity;
                active.Status = WorkflowJobStatuses.Running;
                active.AttemptCount = 1;
                active.WorkerId = $"active-{suffix}";
                active.LeaseToken = Guid.NewGuid();
                active.LeaseGeneration = 1;
                active.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
                active.HeartbeatAt = DateTimeOffset.UtcNow;
                active.StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
                active.Attempts.Add(new WorkflowJobAttemptEntity
                {
                    AttemptNumber = 1,
                    Status = WorkflowJobAttemptStatuses.Running,
                    WorkerId = active.WorkerId,
                    LeaseGeneration = 1,
                    StartedAt = active.StartedAt.Value
                });

                var hidden = Enumerable.Range(1, 200)
                    .Select(index =>
                    {
                        var job = NewJob(
                            definition,
                            saturated.Id,
                            suffix,
                            index,
                            priority: 1_000_000);
                        job.QueueClass = WorkflowJobClasses.Activity;
                        return job;
                    });
                var eligibleJob = NewJob(
                    definition,
                    eligible.Id,
                    suffix,
                    500,
                    priority: 1);
                eligibleJob.QueueClass = WorkflowJobClasses.Activity;
                setup.WorkflowJobs.Add(active);
                setup.WorkflowJobs.AddRange(hidden);
                setup.WorkflowJobs.Add(eligibleJob);
                await setup.SaveChangesAsync();
            }

            await using var db = fixture.CreateDbContext();
            var repository = new WorkflowJobRepository(db, fixture.DataSource);
            var lease = Assert.Single(await repository.LeaseRunnableAsync(
                new WorkflowJobLeaseRequest(
                    $"saturated-fairness-test-{suffix}",
                    MaxCount: 1,
                    MaxActivityCount: 1,
                    MaxPerInstance: 1,
                    LeaseDuration: TimeSpan.FromMinutes(1)),
                CancellationToken.None));

            Assert.Equal(eligibleInstanceId, lease.Job.InstanceId);
        }
        finally
        {
            await DeleteWorkflowAsync(suffix);
        }
    }

    [Fact]
    public async Task AcquisitionSelectsOneFairTimerStartCandidatePerWorkflowBeforeLimit()
    {
        var prefix = $"fair-timer-{Guid.NewGuid():N}";
        var workflowKeys = Enumerable.Range(0, 3)
            .Select(index => $"{prefix}-{index}")
            .ToArray();
        try
        {
            await using (var setup = fixture.CreateDbContext())
            {
                var definitions = workflowKeys.Select(NewDefinition).ToArray();
                setup.WorkflowDefinitions.AddRange(definitions);
                await setup.SaveChangesAsync();

                var jobs = Enumerable.Range(0, 12)
                    .Select(index => NewJob(
                        definitions[0],
                        instanceId: null,
                        workflowKeys[0],
                        index,
                        priority: 1_000_000,
                        kind: WorkflowJobKinds.TimerStart))
                    .Concat(
                        [
                            NewJob(
                                definitions[1],
                                instanceId: null,
                                workflowKeys[1],
                                100,
                                priority: 999_999,
                                kind: WorkflowJobKinds.TimerStart),
                            NewJob(
                                definitions[2],
                                instanceId: null,
                                workflowKeys[2],
                                101,
                                priority: 999_998,
                                kind: WorkflowJobKinds.TimerStart)
                        ])
                    .ToArray();
                setup.WorkflowJobs.AddRange(jobs);
                await setup.SaveChangesAsync();
            }

            await using var db = fixture.CreateDbContext();
            var repository = new WorkflowJobRepository(db, fixture.DataSource);
            var leases = await repository.LeaseRunnableAsync(
                new WorkflowJobLeaseRequest(
                    $"timer-fairness-test-{prefix}",
                    MaxCount: 3,
                    MaxActivityCount: 0,
                    MaxPerInstance: 4,
                    LeaseDuration: TimeSpan.FromMinutes(1)),
                CancellationToken.None);

            var matching = leases
                .Where(lease => lease.Job.WorkflowKey.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(3, matching.Length);
            Assert.Equal(3, matching.Select(lease => lease.Job.WorkflowKey).Distinct().Count());
        }
        finally
        {
            foreach (var workflowKey in workflowKeys)
            {
                await DeleteWorkflowAsync(workflowKey);
            }
        }
    }

    [Theory]
    [InlineData(WorkflowJobStatuses.Running)]
    [InlineData(WorkflowJobStatuses.ResultReady)]
    public async Task ExpiredLeaseAcquisitionKeepsOneFairCandidatePerInstance(string status)
    {
        var suffix = $"fair-expired-{status}-{Guid.NewGuid():N}";
        try
        {
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(suffix);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();

                var instances = Enumerable.Range(0, 3)
                    .Select(_ => NewInstance(definition, suffix))
                    .ToArray();
                setup.WorkflowInstances.AddRange(instances);
                await setup.SaveChangesAsync();

                var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-5);
                var jobs = Enumerable.Range(0, 12)
                    .Select(index => NewExpiredJob(
                        definition,
                        instances[0].Id,
                        suffix,
                        index,
                        status,
                        expiredAt))
                    .Concat(
                        [
                            NewExpiredJob(
                                definition,
                                instances[1].Id,
                                suffix,
                                100,
                                status,
                                expiredAt),
                            NewExpiredJob(
                                definition,
                                instances[2].Id,
                                suffix,
                                101,
                                status,
                                expiredAt)
                        ])
                    .ToArray();
                setup.WorkflowJobs.AddRange(jobs);
                await setup.SaveChangesAsync();
            }

            await using var db = fixture.CreateDbContext();
            var repository = new WorkflowJobRepository(db, fixture.DataSource);
            var leases = await repository.LeaseRunnableAsync(
                new WorkflowJobLeaseRequest(
                    $"expired-fairness-test-{suffix}",
                    MaxCount: 3,
                    MaxActivityCount: 0,
                    MaxPerInstance: 4,
                    LeaseDuration: TimeSpan.FromMinutes(1)),
                CancellationToken.None);

            var matching = leases
                .Where(lease => lease.Job.WorkflowKey == suffix)
                .ToArray();
            Assert.Equal(3, matching.Length);
            Assert.Equal(3, matching.Select(lease => lease.Job.InstanceId).Distinct().Count());
        }
        finally
        {
            await DeleteWorkflowAsync(suffix);
        }
    }

    [Fact]
    public async Task ActivityCapIsSerializedAcrossConcurrentReplicaAcquisitions()
    {
        var suffix = $"activity-cap-{Guid.NewGuid():N}";
        try
        {
            long instanceId;
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(suffix);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();
                var instance = NewInstance(definition, suffix);
                setup.WorkflowInstances.Add(instance);
                await setup.SaveChangesAsync();
                instanceId = instance.Id;

                setup.WorkflowJobs.AddRange(Enumerable.Range(0, 6).Select(index =>
                {
                    var job = NewJob(definition, instance.Id, suffix, index, priority: 1_000_000);
                    job.QueueClass = WorkflowJobClasses.Activity;
                    return job;
                }));
                await setup.SaveChangesAsync();
            }

            await using var firstDb = fixture.CreateDbContext();
            await using var secondDb = fixture.CreateDbContext();
            var first = new WorkflowJobRepository(firstDb, fixture.DataSource);
            var second = new WorkflowJobRepository(secondDb, fixture.DataSource);
            var requestOne = new WorkflowJobLeaseRequest(
                $"replica-one-{suffix}",
                MaxCount: 1,
                MaxActivityCount: 1,
                MaxPerInstance: 1,
                LeaseDuration: TimeSpan.FromMinutes(1));
            var requestTwo = requestOne with { WorkerId = $"replica-two-{suffix}" };

            var results = await Task.WhenAll(
                first.LeaseRunnableAsync(requestOne, CancellationToken.None),
                second.LeaseRunnableAsync(requestTwo, CancellationToken.None));

            Assert.Single(
                results.SelectMany(static result => result),
                lease => lease.Job.InstanceId == instanceId);
        }
        finally
        {
            await DeleteWorkflowAsync(suffix);
        }
    }

    [Fact]
    public async Task LeaseCheckPromptlyObservesCancellationWithoutRenewingTheLease()
    {
        var suffix = $"lease-check-{Guid.NewGuid():N}";
        var workerId = $"lease-worker-{suffix}";
        var leaseToken = Guid.NewGuid();
        const long leaseGeneration = 3;
        long instanceId;
        long jobId;
        var leaseExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
        try
        {
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(suffix);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();
                var instance = NewInstance(definition, suffix);
                setup.WorkflowInstances.Add(instance);
                await setup.SaveChangesAsync();
                instanceId = instance.Id;

                var job = NewJob(definition, instance.Id, suffix, 0, 100);
                job.Status = WorkflowJobStatuses.Running;
                job.AttemptCount = 1;
                job.WorkerId = workerId;
                job.LeaseToken = leaseToken;
                job.LeaseGeneration = leaseGeneration;
                job.LeaseExpiresAt = leaseExpiry;
                job.HeartbeatAt = DateTimeOffset.UtcNow;
                job.StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
                job.Attempts.Add(new WorkflowJobAttemptEntity
                {
                    AttemptNumber = 1,
                    Status = WorkflowJobAttemptStatuses.Running,
                    WorkerId = workerId,
                    LeaseGeneration = leaseGeneration,
                    StartedAt = job.StartedAt.Value
                });
                setup.WorkflowJobs.Add(job);
                await setup.SaveChangesAsync();
                jobId = job.Id;
            }

            var fence = new WorkflowJobFence(
                jobId,
                workerId,
                leaseToken,
                leaseGeneration);
            await using (var db = fixture.CreateDbContext())
            {
                var repository = new WorkflowJobRepository(db, fixture.DataSource);
                Assert.True(await repository.IsLeaseAliveAsync(
                    fence,
                    CancellationToken.None));
                Assert.False(await repository.IsLeaseAliveAsync(
                    fence with { LeaseGeneration = leaseGeneration + 1 },
                    CancellationToken.None));
                var beforeCancellation = await db.WorkflowJobs
                    .AsNoTracking()
                    .SingleAsync(job => job.Id == jobId);
                Assert.NotNull(beforeCancellation.LeaseExpiresAt);
                Assert.InRange(
                    (beforeCancellation.LeaseExpiresAt.Value - leaseExpiry).Duration(),
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(1));
                Assert.Equal(1, await repository.CancelByInstanceAsync(
                    instanceId,
                    "repository cancellation test",
                    CancellationToken.None));
                Assert.False(await repository.IsLeaseAliveAsync(
                    fence,
                    CancellationToken.None));
            }

            await using var verification = fixture.CreateDbContext();
            var stored = await verification.WorkflowJobs
                .AsNoTracking()
                .SingleAsync(job => job.Id == jobId);
            Assert.Equal(WorkflowJobStatuses.Cancelled, stored.Status);
            Assert.Null(stored.LeaseExpiresAt);
        }
        finally
        {
            await DeleteWorkflowAsync(suffix);
        }
    }

    [Fact]
    public async Task FinalAdministrativeBatchLeaseExhaustionReconcilesOnlyNonterminalWork()
    {
        var suffix = $"administrative-lease-exhaustion-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        long preparingBatchId = 0;
        long confirmedBatchId = 0;
        long cancelledBatchId = 0;
        try
        {
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(suffix);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();

                var instance = NewInstance(definition, suffix);
                setup.WorkflowInstances.Add(instance);
                await setup.SaveChangesAsync();

                var tokens = Enumerable.Range(0, 8)
                    .Select(index => new ExecutionTokenEntity
                    {
                        InstanceId = instance.Id,
                        NodeId = index + 1,
                        NodeName = $"Administrative task {index + 1}",
                        NodeType = BpmnFlowNodeTypes.UserTask,
                        Status = ExecutionTokenStatuses.Active,
                        CreatedAt = now,
                        UpdatedAt = now
                    })
                    .ToArray();
                setup.ExecutionTokens.AddRange(tokens);
                await setup.SaveChangesAsync();

                var tasks = tokens.Select((token, index) => new UserTaskEntity
                {
                    InstanceId = instance.Id,
                    TokenId = token.Id,
                    NodeId = index + 1,
                    NodeName = token.NodeName,
                    Status = UserTaskStatuses.Active,
                    CreatedAt = now,
                    UpdatedAt = now
                }).ToArray();
                setup.UserTasks.AddRange(tasks);
                await setup.SaveChangesAsync();

                var preparationJob = NewFinalExpiredAdministrativeJob(
                    definition,
                    suffix,
                    WorkflowJobKinds.AdministrativeBatchPrepare,
                    now.AddHours(-3));
                var completedPreparationJob = NewFinalExpiredAdministrativeJob(
                    definition,
                    suffix,
                    WorkflowJobKinds.AdministrativeBatchPrepare,
                    now.AddHours(-2));
                var executionJob = NewFinalExpiredAdministrativeJob(
                    definition,
                    suffix,
                    WorkflowJobKinds.AdministrativeBatchExecute,
                    now.AddHours(-1));
                setup.WorkflowJobs.AddRange(
                    preparationJob,
                    completedPreparationJob,
                    executionJob);
                await setup.SaveChangesAsync();

                var preparingBatch = NewAdministrativeBatch(
                    definition,
                    suffix,
                    AdministrativeActionBatchStatuses.Preparing,
                    now);
                preparingBatch.PreparationJobId = preparationJob.Id;
                preparingBatch.TotalItemCount = 3;
                preparingBatch.EligibleItemCount = 1;
                preparingBatch.IneligibleItemCount = 1;

                var confirmedBatch = NewAdministrativeBatch(
                    definition,
                    suffix,
                    AdministrativeActionBatchStatuses.Queued,
                    now);
                confirmedBatch.PreparationJobId = completedPreparationJob.Id;
                confirmedBatch.TotalItemCount = 1;
                confirmedBatch.QueuedItemCount = 1;
                confirmedBatch.PreparedAt = now;
                confirmedBatch.ConfirmedAt = now;

                var cancelledBatch = NewAdministrativeBatch(
                    definition,
                    suffix,
                    AdministrativeActionBatchStatuses.Cancelled,
                    now);
                cancelledBatch.ExecutionJobId = executionJob.Id;
                cancelledBatch.TotalItemCount = 4;
                cancelledBatch.QueuedItemCount = 1;
                cancelledBatch.SucceededItemCount = 1;
                cancelledBatch.SkippedItemCount = 1;
                cancelledBatch.CancelledItemCount = 1;
                cancelledBatch.CancelledBy = "repository-test";
                cancelledBatch.CancelledAt = now;
                setup.AdministrativeActionBatches.AddRange(
                    preparingBatch,
                    confirmedBatch,
                    cancelledBatch);
                await setup.SaveChangesAsync();
                preparingBatchId = preparingBatch.Id;
                confirmedBatchId = confirmedBatch.Id;
                cancelledBatchId = cancelledBatch.Id;

                setup.AdministrativeActionBatchItems.AddRange(
                    NewAdministrativeBatchItem(
                        preparingBatch,
                        definition,
                        instance,
                        tasks[0],
                        tokens[0],
                        AdministrativeActionBatchItemStatuses.Preparing,
                        now),
                    NewAdministrativeBatchItem(
                        preparingBatch,
                        definition,
                        instance,
                        tasks[1],
                        tokens[1],
                        AdministrativeActionBatchItemStatuses.Eligible,
                        now),
                    NewAdministrativeBatchItem(
                        preparingBatch,
                        definition,
                        instance,
                        tasks[2],
                        tokens[2],
                        AdministrativeActionBatchItemStatuses.Ineligible,
                        now,
                        issues: """[{"Code":"existing","Message":"Preserve this issue."}]"""),
                    NewAdministrativeBatchItem(
                        confirmedBatch,
                        definition,
                        instance,
                        tasks[3],
                        tokens[3],
                        AdministrativeActionBatchItemStatuses.Queued,
                        now),
                    NewAdministrativeBatchItem(
                        cancelledBatch,
                        definition,
                        instance,
                        tasks[4],
                        tokens[4],
                        AdministrativeActionBatchItemStatuses.Queued,
                        now,
                        startedAt: now),
                    NewAdministrativeBatchItem(
                        cancelledBatch,
                        definition,
                        instance,
                        tasks[5],
                        tokens[5],
                        AdministrativeActionBatchItemStatuses.Succeeded,
                        now,
                        result: """{"outcome":"preserved"}"""),
                    NewAdministrativeBatchItem(
                        cancelledBatch,
                        definition,
                        instance,
                        tasks[6],
                        tokens[6],
                        AdministrativeActionBatchItemStatuses.Skipped,
                        now),
                    NewAdministrativeBatchItem(
                        cancelledBatch,
                        definition,
                        instance,
                        tasks[7],
                        tokens[7],
                        AdministrativeActionBatchItemStatuses.Cancelled,
                        now));
                await setup.SaveChangesAsync();
            }

            await using (var leaseDb = fixture.CreateDbContext())
            {
                var repository = new WorkflowJobRepository(leaseDb, fixture.DataSource);
                await repository.LeaseRunnableAsync(
                    new WorkflowJobLeaseRequest(
                        $"administrative-exhaustion-sweeper-{suffix}",
                        MaxCount: 3,
                        MaxActivityCount: 0,
                        MaxPerInstance: 1,
                        LeaseDuration: TimeSpan.FromMinutes(1)),
                    CancellationToken.None);
            }

            await using var verification = fixture.CreateDbContext();
            var preparing = await verification.AdministrativeActionBatches
                .AsNoTracking()
                .SingleAsync(batch => batch.Id == preparingBatchId);
            Assert.Equal(AdministrativeActionBatchStatuses.Failed, preparing.Status);
            Assert.Equal(3, preparing.TotalItemCount);
            Assert.Equal(0, preparing.EligibleItemCount);
            Assert.Equal(1, preparing.IneligibleItemCount);
            Assert.Equal(2, preparing.FailedItemCount);
            Assert.NotNull(preparing.CompletedAt);
            Assert.Equal(
                "lease_exhausted",
                preparing.IssuesJson!.RootElement[0].GetProperty("Code").GetString());

            var preparingItems = await verification.AdministrativeActionBatchItems
                .AsNoTracking()
                .Where(item => item.BatchId == preparingBatchId)
                .OrderBy(item => item.Id)
                .ToArrayAsync();
            Assert.All(preparingItems.Take(2), item =>
            {
                Assert.Equal(AdministrativeActionBatchItemStatuses.Failed, item.Status);
                Assert.Equal("lease_exhausted", item.ErrorCode);
                Assert.Equal(
                    "The final permitted worker attempt lost its lease.",
                    item.ErrorDescription);
                Assert.NotNull(item.CompletedAt);
            });
            Assert.Equal(
                AdministrativeActionBatchItemStatuses.Ineligible,
                preparingItems[2].Status);
            Assert.Equal(
                "existing",
                preparingItems[2].IssuesJson!.RootElement[0].GetProperty("Code").GetString());
            Assert.Null(preparingItems[2].ErrorCode);

            var confirmed = await verification.AdministrativeActionBatches
                .AsNoTracking()
                .SingleAsync(batch => batch.Id == confirmedBatchId);
            Assert.Equal(AdministrativeActionBatchStatuses.Queued, confirmed.Status);
            Assert.Equal(1, confirmed.QueuedItemCount);
            Assert.Equal(0, confirmed.FailedItemCount);
            Assert.Null(confirmed.CompletedAt);
            var confirmedItem = await verification.AdministrativeActionBatchItems
                .AsNoTracking()
                .SingleAsync(item => item.BatchId == confirmedBatchId);
            Assert.Equal(AdministrativeActionBatchItemStatuses.Queued, confirmedItem.Status);
            Assert.Null(confirmedItem.ErrorCode);

            var cancelled = await verification.AdministrativeActionBatches
                .AsNoTracking()
                .SingleAsync(batch => batch.Id == cancelledBatchId);
            Assert.Equal(AdministrativeActionBatchStatuses.Cancelled, cancelled.Status);
            Assert.Equal(4, cancelled.TotalItemCount);
            Assert.Equal(0, cancelled.QueuedItemCount);
            Assert.Equal(1, cancelled.SucceededItemCount);
            Assert.Equal(1, cancelled.SkippedItemCount);
            Assert.Equal(1, cancelled.FailedItemCount);
            Assert.Equal(1, cancelled.CancelledItemCount);
            Assert.NotNull(cancelled.CompletedAt);

            var cancelledItems = await verification.AdministrativeActionBatchItems
                .AsNoTracking()
                .Where(item => item.BatchId == cancelledBatchId)
                .OrderBy(item => item.Id)
                .ToArrayAsync();
            Assert.Equal(AdministrativeActionBatchItemStatuses.Failed, cancelledItems[0].Status);
            Assert.Equal("lease_exhausted", cancelledItems[0].ErrorCode);
            Assert.Equal(AdministrativeActionBatchItemStatuses.Succeeded, cancelledItems[1].Status);
            Assert.Equal(
                "preserved",
                cancelledItems[1].ResultJson!.RootElement.GetProperty("outcome").GetString());
            Assert.Null(cancelledItems[1].ErrorCode);
            Assert.Equal(AdministrativeActionBatchItemStatuses.Skipped, cancelledItems[2].Status);
            Assert.Equal(AdministrativeActionBatchItemStatuses.Cancelled, cancelledItems[3].Status);

            var exhaustedJobs = await verification.WorkflowJobs
                .AsNoTracking()
                .Where(job => job.WorkflowKey == suffix)
                .OrderBy(job => job.Id)
                .ToArrayAsync();
            Assert.All(exhaustedJobs, job =>
            {
                Assert.Equal(WorkflowJobStatuses.Incident, job.Status);
                Assert.Equal("lease_exhausted", job.LastFailureCode);
            });
        }
        finally
        {
            await DeleteWorkflowAsync(suffix);
        }
    }

    [Fact]
    public async Task ReleaseResultReadyLeaseExpiresOnlyTheMatchingFenceAndPreservesResult()
    {
        var suffix = $"release-result-{Guid.NewGuid():N}";
        var workerId = $"result-worker-{suffix}";
        var leaseToken = Guid.NewGuid();
        const long leaseGeneration = 7;
        var originalExpiry = DateTimeOffset.UtcNow.AddMinutes(10);
        long jobId = 0;
        try
        {
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(suffix);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();

                var job = NewJob(definition, null, suffix, 0, priority: 100);
                job.Status = WorkflowJobStatuses.ResultReady;
                job.AttemptCount = 1;
                job.WorkerId = workerId;
                job.LeaseToken = leaseToken;
                job.LeaseGeneration = leaseGeneration;
                job.LeaseExpiresAt = originalExpiry;
                job.HeartbeatAt = DateTimeOffset.UtcNow;
                job.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                job.ResultReadyAt = DateTimeOffset.UtcNow;
                job.ResultJson = JsonDocument.Parse("""{"outcome":"captured"}""");
                setup.WorkflowJobs.Add(job);
                await setup.SaveChangesAsync();
                jobId = job.Id;

                setup.WorkflowJobAttempts.Add(new WorkflowJobAttemptEntity
                {
                    JobId = job.Id,
                    AttemptNumber = 1,
                    Status = WorkflowJobAttemptStatuses.ResultReady,
                    WorkerId = workerId,
                    LeaseGeneration = leaseGeneration,
                    StartedAt = job.StartedAt.Value
                });
                await setup.SaveChangesAsync();
            }

            await using (var db = fixture.CreateDbContext())
            {
                var repository = new WorkflowJobRepository(db, fixture.DataSource);
                Assert.False(await repository.ReleaseResultReadyLeaseAsync(
                    new WorkflowJobFence(
                        jobId,
                        workerId,
                        leaseToken,
                        leaseGeneration + 1),
                    CancellationToken.None));
                Assert.True(await repository.ReleaseResultReadyLeaseAsync(
                    new WorkflowJobFence(jobId, workerId, leaseToken, leaseGeneration),
                    CancellationToken.None));
                Assert.False(await repository.ReleaseResultReadyLeaseAsync(
                    new WorkflowJobFence(jobId, workerId, leaseToken, leaseGeneration),
                    CancellationToken.None));
            }

            await using var verification = fixture.CreateDbContext();
            var stored = await verification.WorkflowJobs
                .AsNoTracking()
                .SingleAsync(job => job.Id == jobId);
            Assert.Equal(WorkflowJobStatuses.ResultReady, stored.Status);
            Assert.Equal(1, stored.AttemptCount);
            Assert.Equal(workerId, stored.WorkerId);
            Assert.Equal(leaseToken, stored.LeaseToken);
            Assert.Equal(leaseGeneration, stored.LeaseGeneration);
            Assert.NotNull(stored.LeaseExpiresAt);
            Assert.True(stored.LeaseExpiresAt < originalExpiry);
            Assert.True(stored.LeaseExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(1));
            Assert.NotNull(stored.ResultReadyAt);
            Assert.Equal(
                "captured",
                stored.ResultJson!.RootElement.GetProperty("outcome").GetString());

            var attempt = await verification.WorkflowJobAttempts
                .AsNoTracking()
                .SingleAsync(item => item.JobId == jobId);
            Assert.Equal(WorkflowJobAttemptStatuses.ResultReady, attempt.Status);
            Assert.Equal(workerId, attempt.WorkerId);
            Assert.Equal(leaseGeneration, attempt.LeaseGeneration);
            Assert.Null(attempt.FinishedAt);
        }
        finally
        {
            await DeleteWorkflowAsync(suffix);
        }
    }

    [Fact]
    public async Task ManualOutputConflictRetryDropsSnapshotAndResumesPausedTimer()
    {
        var suffix = $"retry-output-conflict-{Guid.NewGuid():N}";
        var activationId = Guid.NewGuid();
        var workerId = $"conflict-worker-{suffix}";
        var leaseToken = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var retryDueAt = now.AddMinutes(1);
        long? snapshotId = null;
        long incidentId;
        long jobId;
        long subscriptionId;
        try
        {
            WorkflowJobFence fence;
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(suffix);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();

                var instance = NewInstance(definition, suffix);
                setup.WorkflowInstances.Add(instance);
                await setup.SaveChangesAsync();

                var token = new ExecutionTokenEntity
                {
                    InstanceId = instance.Id,
                    NodeId = 2,
                    NodeName = "Approval",
                    NodeType = BpmnFlowNodeTypes.UserTask,
                    ActivationId = activationId,
                    Status = ExecutionTokenStatuses.Active,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                setup.ExecutionTokens.Add(token);
                await setup.SaveChangesAsync();

                var snapshot = new WorkflowJobSnapshotEntity
                {
                    Kind = WorkflowJobKinds.AsyncBefore,
                    InvocationJson = JsonDocument.Parse("""{"url":"https://tests.local"}"""),
                    VariablesJson = JsonDocument.Parse("""{"amount":10}"""),
                    OutputVariableVersionsJson = JsonDocument.Parse("""{"result":42}"""),
                    EvaluationTime = now,
                    SizeBytes = 96,
                    CreatedAt = now
                };
                var subscription = new TimerSubscriptionEntity
                {
                    InstanceId = instance.Id,
                    WorkflowDefinitionId = definition.Id,
                    WorkflowKey = suffix,
                    TokenId = token.Id,
                    ActivationId = activationId,
                    TimerNodeId = 3,
                    TimerNodeName = "Reminder",
                    AttachedToNodeId = token.NodeId,
                    ScheduleKind = TimerScheduleKinds.Cycle,
                    ScheduleExpression = "R/PT1M",
                    CancelActivity = false,
                    Status = TimerSubscriptionStatuses.Paused,
                    NextDueAt = now.AddMinutes(5),
                    Occurrence = 4,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                setup.WorkflowJobSnapshots.Add(snapshot);
                setup.TimerSubscriptions.Add(subscription);
                await setup.SaveChangesAsync();
                snapshotId = snapshot.Id;
                subscriptionId = subscription.Id;

                var job = NewJob(definition, instance.Id, suffix, 2, priority: 100);
                job.TokenId = token.Id;
                job.TimerSubscriptionId = subscription.Id;
                job.ActivationId = activationId;
                job.NodeId = subscription.TimerNodeId;
                job.NodeName = subscription.TimerNodeName;
                job.NodeType = BpmnFlowNodeTypes.TimerBoundaryEvent;
                job.Kind = WorkflowJobKinds.TimerBoundary;
                job.Phase = WorkflowJobKinds.Timer;
                job.Status = WorkflowJobStatuses.ResultReady;
                job.AttemptCount = 4;
                job.MaxAttempts = 4;
                job.SnapshotId = snapshot.Id;
                job.WorkerId = workerId;
                job.LeaseToken = leaseToken;
                job.LeaseGeneration = 3;
                job.LeaseExpiresAt = now.AddMinutes(10);
                job.HeartbeatAt = now;
                job.StartedAt = now.AddMinutes(-1);
                job.ResultReadyAt = now;
                job.ResultJson = JsonDocument.Parse("""{"result":"stale"}""");
                job.ErrorJson = JsonDocument.Parse("""{"conflict":true}""");
                job.LastFailureCode = "output_version_conflict";
                job.LastFailureDescription = "Output row changed.";
                setup.WorkflowJobs.Add(job);
                await setup.SaveChangesAsync();
                jobId = job.Id;
                fence = new WorkflowJobFence(
                    job.Id,
                    workerId,
                    leaseToken,
                    job.LeaseGeneration);

                token.WaitState = ExecutionTokenWaitStates.TimerBoundary;
                token.WaitingJobId = job.Id;
                token.WaitingTimerSubscriptionId = subscription.Id;
                setup.WorkflowJobAttempts.Add(new WorkflowJobAttemptEntity
                {
                    JobId = job.Id,
                    AttemptNumber = job.AttemptCount,
                    Status = WorkflowJobAttemptStatuses.ResultReady,
                    WorkerId = workerId,
                    LeaseGeneration = job.LeaseGeneration,
                    StartedAt = job.StartedAt.Value
                });
                await setup.SaveChangesAsync();
            }

            await using (var incidentDb = fixture.CreateDbContext())
            {
                var repository = new WorkflowJobRepository(incidentDb, fixture.DataSource);
                var incident = await repository.OpenIncidentAsync(
                    fence,
                    "output_version_conflict",
                    "Async output conflict.",
                    "Output row changed after staging.",
                    CancellationToken.None);
                Assert.NotNull(incident);
                incidentId = incident.Id;
            }

            await using (var retryDb = fixture.CreateDbContext())
            {
                var repository = new WorkflowJobRepository(retryDb, fixture.DataSource);
                var retried = await repository.RetryIncidentAsync(
                    incidentId,
                    "operations-admin",
                    retryDueAt,
                    CancellationToken.None);
                Assert.NotNull(retried);
                Assert.Equal(jobId, retried.Id);
                Assert.Equal(WorkflowJobStatuses.Queued, retried.Status);
                Assert.Null(retried.SnapshotId);
                Assert.Equal(5, retried.MaxAttempts);
                Assert.Equal(retryDueAt, retried.DueAt);
            }

            await using var verification = fixture.CreateDbContext();
            var storedJob = await verification.WorkflowJobs
                .AsNoTracking()
                .SingleAsync(job => job.Id == jobId);
            Assert.Equal(WorkflowJobStatuses.Queued, storedJob.Status);
            Assert.Equal(4, storedJob.AttemptCount);
            Assert.Equal(5, storedJob.MaxAttempts);
            Assert.Null(storedJob.SnapshotId);
            Assert.Null(storedJob.WorkerId);
            Assert.Null(storedJob.LeaseToken);
            Assert.Null(storedJob.LeaseExpiresAt);
            Assert.Null(storedJob.HeartbeatAt);
            Assert.Null(storedJob.ResultReadyAt);
            Assert.Null(storedJob.ResultJson);
            Assert.Null(storedJob.ErrorJson);
            Assert.Null(storedJob.LastFailureCode);
            Assert.Null(storedJob.LastFailureDescription);

            var storedSubscription = await verification.TimerSubscriptions
                .AsNoTracking()
                .SingleAsync(subscription => subscription.Id == subscriptionId);
            Assert.Equal(TimerSubscriptionStatuses.Active, storedSubscription.Status);
            Assert.InRange(
                (storedSubscription.UpdatedAt - retryDueAt).Duration(),
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(1));

            var storedIncident = await verification.WorkflowIncidents
                .AsNoTracking()
                .SingleAsync(incident => incident.Id == incidentId);
            Assert.Equal(WorkflowIncidentStatuses.Resolved, storedIncident.Status);
            Assert.Equal("operations-admin", storedIncident.ResolvedBy);
            Assert.NotNull(storedIncident.ResolvedAt);
            Assert.True(await verification.WorkflowJobSnapshots
                .AnyAsync(snapshot => snapshot.Id == snapshotId.Value));

            var attempt = await verification.WorkflowJobAttempts
                .AsNoTracking()
                .SingleAsync(item => item.JobId == jobId && item.AttemptNumber == 4);
            Assert.Equal(WorkflowJobAttemptStatuses.Failed, attempt.Status);
            Assert.Equal("output_version_conflict", attempt.FailureCode);
            Assert.NotNull(attempt.FinishedAt);
        }
        finally
        {
            await DeleteWorkflowAsync(suffix);
            if (snapshotId is long id)
            {
                await using var cleanup = fixture.CreateDbContext();
                await cleanup.WorkflowJobSnapshots
                    .Where(snapshot => snapshot.Id == id)
                    .ExecuteDeleteAsync();
            }
        }
    }

    [Fact]
    public async Task CleanupDrainsRepeatedBatchesAndHonorsIncidentRetention()
    {
        var suffix = $"cleanup-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        try
        {
            long recentIncidentJobId;
            long openIncidentJobId;
            long leasedJobId;
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(suffix);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();

                var ordinaryTerminalJobs = Enumerable.Range(0, 5)
                    .Select(index => NewTerminalJob(definition, suffix, index, now.AddDays(-45)))
                    .ToArray();
                var oldIncidentJob = NewTerminalJob(definition, suffix, 100, now.AddDays(-45));
                var recentIncidentJob = NewTerminalJob(definition, suffix, 101, now.AddDays(-45));
                var openIncidentJob = NewJob(definition, null, suffix, 102, 0);
                openIncidentJob.Status = WorkflowJobStatuses.Incident;
                var leasedJob = NewJob(definition, null, suffix, 103, 0);
                leasedJob.Status = WorkflowJobStatuses.Running;
                leasedJob.AttemptCount = 1;
                leasedJob.WorkerId = $"worker-{suffix}";
                leasedJob.LeaseToken = Guid.NewGuid();
                leasedJob.LeaseGeneration = 1;
                leasedJob.LeaseExpiresAt = now.AddHours(1);
                leasedJob.HeartbeatAt = now;
                leasedJob.StartedAt = now.AddMinutes(-1);

                setup.WorkflowJobs.AddRange(ordinaryTerminalJobs);
                setup.WorkflowJobs.AddRange(
                    oldIncidentJob,
                    recentIncidentJob,
                    openIncidentJob,
                    leasedJob);
                await setup.SaveChangesAsync();
                recentIncidentJobId = recentIncidentJob.Id;
                openIncidentJobId = openIncidentJob.Id;
                leasedJobId = leasedJob.Id;

                setup.WorkflowIncidents.AddRange(
                    NewResolvedIncident(
                        oldIncidentJob,
                        definition,
                        suffix,
                        now.AddDays(-100)),
                    NewResolvedIncident(
                        recentIncidentJob,
                        definition,
                        suffix,
                        now.AddDays(-60)),
                    NewOpenIncident(openIncidentJob, definition, suffix, now.AddDays(-100)));
                await setup.SaveChangesAsync();
            }

            await using var db = fixture.CreateDbContext();
            var repository = new WorkflowJobRepository(db, fixture.DataSource);
            var result = await repository.CleanupAsync(
                now.AddDays(-30),
                now.AddDays(-90),
                batchSize: 2,
                CancellationToken.None);

            Assert.Equal(7, result.JobsDeleted);
            Assert.Equal(1, result.IncidentsDeleted);
            await using var verification = fixture.CreateDbContext();
            var remainingJobIds = await verification.WorkflowJobs
                .Where(job => job.WorkflowKey == suffix)
                .Select(job => job.Id)
                .ToArrayAsync();
            Assert.Equal(
                [openIncidentJobId, leasedJobId],
                remainingJobIds.Order().ToArray());
            Assert.Equal(2, await verification.WorkflowIncidents.CountAsync(
                incident => incident.WorkflowKey == suffix));
            var retainedResolvedIncident = await verification.WorkflowIncidents
                .SingleAsync(incident =>
                    incident.WorkflowKey == suffix
                    && incident.Status == WorkflowIncidentStatuses.Resolved);
            Assert.Null(retainedResolvedIncident.JobId);
            Assert.Equal(recentIncidentJobId, retainedResolvedIncident.OriginalJobId);
        }
        finally
        {
            await DeleteWorkflowAsync(suffix);
        }
    }

    [Fact]
    public async Task JobAndIncidentSearchUseOpaqueKeysetsFromTheFirstPage()
    {
        var suffix = $"operations-keyset-{Guid.NewGuid():N}";
        try
        {
            long[] expectedJobIds;
            long[] expectedIncidentIds;
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(suffix);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();

                var now = DateTimeOffset.UtcNow;
                var jobs = Enumerable.Range(0, 5)
                    .Select(index =>
                    {
                        var job = NewJob(
                            definition,
                            null,
                            suffix,
                            index,
                            priority: index);
                        job.UpdatedAt = now.AddMinutes(-(index / 2));
                        return job;
                    })
                    .ToArray();
                setup.WorkflowJobs.AddRange(jobs);
                await setup.SaveChangesAsync();

                var incidents = jobs.Take(3)
                    .Select((job, index) => new WorkflowIncidentEntity
                    {
                        JobId = job.Id,
                        OriginalJobId = job.Id,
                        WorkflowDefinitionId = definition.Id,
                        WorkflowKey = suffix,
                        NodeId = job.NodeId,
                        NodeName = job.NodeName,
                        Type = "keysetTest",
                        Status = WorkflowIncidentStatuses.Open,
                        Summary = $"Incident {index}",
                        CreatedAt = now.AddMinutes(-index),
                        UpdatedAt = now.AddMinutes(-(index / 2))
                    })
                    .ToArray();
                setup.WorkflowIncidents.AddRange(incidents);
                await setup.SaveChangesAsync();

                expectedJobIds = jobs
                    .OrderByDescending(job => job.UpdatedAt)
                    .ThenByDescending(job => job.Id)
                    .Select(job => job.Id)
                    .ToArray();
                expectedIncidentIds = incidents
                    .OrderByDescending(incident => incident.UpdatedAt)
                    .ThenByDescending(incident => incident.Id)
                    .Select(incident => incident.Id)
                    .ToArray();
            }

            await using var db = fixture.CreateDbContext();
            var repository = new WorkflowJobRepository(db, fixture.DataSource);
            var firstJobs = await repository.SearchJobsAsync(
                new WorkflowJobQuery
                {
                    WorkflowKey = suffix,
                    PageSize = 2
                },
                CancellationToken.None);
            var secondJobs = await repository.SearchJobsAsync(
                new WorkflowJobQuery
                {
                    WorkflowKey = suffix,
                    Cursor = firstJobs.NextCursor,
                    Page = 2,
                    PageSize = 2
                },
                CancellationToken.None);
            var thirdJobs = await repository.SearchJobsAsync(
                new WorkflowJobQuery
                {
                    WorkflowKey = suffix,
                    Cursor = secondJobs.NextCursor,
                    Page = 3,
                    PageSize = 2
                },
                CancellationToken.None);

            Assert.Equal(5, firstJobs.TotalCount);
            Assert.NotNull(firstJobs.NextCursor);
            Assert.NotNull(secondJobs.NextCursor);
            Assert.Null(thirdJobs.NextCursor);
            Assert.Equal(
                expectedJobIds,
                firstJobs.Items
                    .Concat(secondJobs.Items)
                    .Concat(thirdJobs.Items)
                    .Select(job => job.Id));

            var firstIncidents = await repository.SearchIncidentsAsync(
                new WorkflowIncidentQuery
                {
                    WorkflowKey = suffix,
                    PageSize = 2
                },
                CancellationToken.None);
            var secondIncidents = await repository.SearchIncidentsAsync(
                new WorkflowIncidentQuery
                {
                    WorkflowKey = suffix,
                    Cursor = firstIncidents.NextCursor,
                    Page = 2,
                    PageSize = 2
                },
                CancellationToken.None);

            Assert.Equal(3, firstIncidents.TotalCount);
            Assert.NotNull(firstIncidents.NextCursor);
            Assert.Null(secondIncidents.NextCursor);
            Assert.Equal(
                expectedIncidentIds,
                firstIncidents.Items
                    .Concat(secondIncidents.Items)
                    .Select(incident => incident.Id));
        }
        finally
        {
            await DeleteWorkflowAsync(suffix);
        }
    }

    private static WorkflowDefinitionEntity NewDefinition(string workflowKey) =>
        new()
        {
            Name = workflowKey,
            WorkflowKey = workflowKey,
            Version = 1,
            Definition = new WorkflowModel { Id = workflowKey, Name = workflowKey },
            IsPublished = true
        };

    private static WorkflowInstanceEntity NewInstance(
        WorkflowDefinitionEntity definition,
        string workflowKey) =>
        new()
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            Status = "running",
            StartedBy = "repository-test"
        };

    private static WorkflowJobEntity NewJob(
        WorkflowDefinitionEntity definition,
        long? instanceId,
        string workflowKey,
        int index,
        int priority,
        string kind = WorkflowJobKinds.AsyncBefore)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowJobEntity
        {
            InstanceId = instanceId,
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            ActivationId = Guid.NewGuid(),
            NodeId = index + 1,
            NodeName = $"job-{index}",
            NodeType = kind == WorkflowJobKinds.TimerStart ? "timerStartEvent" : "serviceTask",
            Kind = kind,
            QueueClass = WorkflowJobClasses.Control,
            Phase = "before",
            Status = WorkflowJobStatuses.Queued,
            Priority = priority,
            MaxAttempts = 4,
            FailureHandling = WorkflowJobFailureHandling.BoundaryFirst,
            RetryDelays =
            [
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(5)
            ],
            DueAt = now.AddDays(-10),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static WorkflowJobEntity NewExpiredJob(
        WorkflowDefinitionEntity definition,
        long instanceId,
        string workflowKey,
        int index,
        string status,
        DateTimeOffset expiredAt)
    {
        var job = NewJob(definition, instanceId, workflowKey, index, priority: 1_000_000);
        job.Status = status;
        job.AttemptCount = 1;
        job.WorkerId = $"expired-worker-{index}";
        job.LeaseToken = Guid.NewGuid();
        job.LeaseGeneration = 1;
        job.LeaseExpiresAt = expiredAt;
        job.HeartbeatAt = expiredAt.AddMinutes(-1);
        job.StartedAt = expiredAt.AddMinutes(-2);
        if (status == WorkflowJobStatuses.ResultReady)
        {
            job.ResultReadyAt = expiredAt.AddMinutes(-1);
        }

        job.Attempts.Add(new WorkflowJobAttemptEntity
        {
            AttemptNumber = 1,
            Status = status == WorkflowJobStatuses.ResultReady
                ? WorkflowJobAttemptStatuses.ResultReady
                : WorkflowJobAttemptStatuses.Running,
            WorkerId = job.WorkerId,
            LeaseGeneration = job.LeaseGeneration,
            StartedAt = job.StartedAt.Value
        });
        return job;
    }

    private static WorkflowJobEntity NewTerminalJob(
        WorkflowDefinitionEntity definition,
        string workflowKey,
        int index,
        DateTimeOffset completedAt)
    {
        var job = NewJob(definition, null, workflowKey, index, 0);
        job.Status = WorkflowJobStatuses.Completed;
        job.CompletedAt = completedAt;
        job.CreatedAt = completedAt.AddMinutes(-1);
        job.UpdatedAt = completedAt;
        return job;
    }

    private static WorkflowJobEntity NewFinalExpiredAdministrativeJob(
        WorkflowDefinitionEntity definition,
        string workflowKey,
        string kind,
        DateTimeOffset expiredAt)
    {
        var job = NewJob(definition, null, workflowKey, 1000, 100, kind);
        job.NodeType = BpmnFlowNodeTypes.UserTask;
        job.QueueClass = WorkflowJobClasses.Activity;
        job.Phase = kind == WorkflowJobKinds.AdministrativeBatchPrepare
            ? "prepare"
            : "execute";
        job.Status = WorkflowJobStatuses.Running;
        job.AttemptCount = 1;
        job.MaxAttempts = 1;
        job.RetryDelays = [];
        job.FailureHandling = WorkflowJobFailureHandling.RetryFirst;
        job.WorkerId = $"expired-administrative-worker-{Guid.NewGuid():N}";
        job.LeaseToken = Guid.NewGuid();
        job.LeaseGeneration = 1;
        job.LeaseExpiresAt = expiredAt;
        job.HeartbeatAt = expiredAt.AddMinutes(-1);
        job.StartedAt = expiredAt.AddMinutes(-2);
        job.Attempts.Add(new WorkflowJobAttemptEntity
        {
            AttemptNumber = 1,
            Status = WorkflowJobAttemptStatuses.Running,
            WorkerId = job.WorkerId,
            LeaseGeneration = job.LeaseGeneration,
            StartedAt = job.StartedAt.Value
        });
        return job;
    }

    private static AdministrativeActionBatchEntity NewAdministrativeBatch(
        WorkflowDefinitionEntity definition,
        string workflowKey,
        string status,
        DateTimeOffset now)
    {
        var action = new AdministrativeActionSnapshotRecord(
            definition.Id,
            definition.Version,
            "directFlow",
            1,
            "RETURN_FOR_REWORK",
            "Return for rework",
            1,
            "Administrative task",
            2,
            "Rework task",
            BpmnFlowNodeTypes.UserTask,
            null,
            ["admin"],
            [],
            null,
            null,
            null,
            null);
        return new()
        {
            WorkflowKey = workflowKey,
            WorkflowDefinitionId = definition.Id,
            SourceNodeId = 1,
            ActionKind = "directFlow",
            FlowId = 1,
            ActionSnapshotJson = JsonDocument.Parse(JsonSerializer.Serialize(
                action,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            Reason = "Repository lease-exhaustion test",
            CommonVariablesJson = JsonDocument.Parse("{}"),
            SelectionJson = JsonDocument.Parse("""{"mode":"explicit"}"""),
            Status = status,
            PreparedBy = "repository-test",
            PreparedByRolesJson = JsonDocument.Parse("""["admin","Ops"]"""),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static AdministrativeActionBatchItemEntity NewAdministrativeBatchItem(
        AdministrativeActionBatchEntity batch,
        WorkflowDefinitionEntity definition,
        WorkflowInstanceEntity instance,
        UserTaskEntity task,
        ExecutionTokenEntity token,
        string status,
        DateTimeOffset now,
        DateTimeOffset? startedAt = null,
        string? issues = null,
        string? result = null) =>
        new()
        {
            BatchId = batch.Id,
            PositionKind = "userTask",
            InstanceId = instance.Id,
            UserTaskId = task.Id,
            TokenId = token.Id,
            TokenActivationId = token.ActivationId,
            WorkflowDefinitionId = definition.Id,
            SourceNodeId = task.NodeId,
            FlowId = 1,
            CapturedPositionUpdatedAt = task.UpdatedAt,
            AffectedTaskCount = 1,
            Status = status,
            IssuesJson = issues is null ? null : JsonDocument.Parse(issues),
            ResultJson = result is null ? null : JsonDocument.Parse(result),
            CreatedAt = now,
            UpdatedAt = now,
            PreparedAt = status == AdministrativeActionBatchItemStatuses.Preparing
                ? null
                : now,
            StartedAt = startedAt,
            CompletedAt = status is AdministrativeActionBatchItemStatuses.Ineligible
                or AdministrativeActionBatchItemStatuses.Succeeded
                or AdministrativeActionBatchItemStatuses.Skipped
                or AdministrativeActionBatchItemStatuses.Cancelled
                ? now
                : null
        };

    private static WorkflowIncidentEntity NewResolvedIncident(
        WorkflowJobEntity job,
        WorkflowDefinitionEntity definition,
        string workflowKey,
        DateTimeOffset resolvedAt) =>
        new()
        {
            JobId = job.Id,
            OriginalJobId = job.Id,
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            NodeId = job.NodeId,
            NodeName = job.NodeName,
            Type = "repositoryTest",
            Status = WorkflowIncidentStatuses.Resolved,
            Summary = "Resolved repository test incident.",
            ResolvedBy = "repository-test",
            CreatedAt = resolvedAt.AddMinutes(-1),
            UpdatedAt = resolvedAt,
            ResolvedAt = resolvedAt
        };

    private static WorkflowIncidentEntity NewOpenIncident(
        WorkflowJobEntity job,
        WorkflowDefinitionEntity definition,
        string workflowKey,
        DateTimeOffset createdAt) =>
        new()
        {
            JobId = job.Id,
            OriginalJobId = job.Id,
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            NodeId = job.NodeId,
            NodeName = job.NodeName,
            Type = "repositoryTest",
            Status = WorkflowIncidentStatuses.Open,
            Summary = "Open repository test incident.",
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

    private async Task DeleteWorkflowAsync(string workflowKey)
    {
        await using var cleanup = fixture.CreateDbContext();
        var batchIds = await cleanup.AdministrativeActionBatches
            .Where(batch => batch.WorkflowKey == workflowKey)
            .Select(batch => batch.Id)
            .ToArrayAsync();
        if (batchIds.Length > 0)
        {
            await cleanup.AdministrativeActionBatchItems
                .Where(item => batchIds.Contains(item.BatchId))
                .ExecuteDeleteAsync();
            await cleanup.AdministrativeActionBatches
                .Where(batch => batchIds.Contains(batch.Id))
                .ExecuteDeleteAsync();
        }
        await cleanup.WorkflowIncidents
            .Where(incident => incident.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowJobs
            .Where(job => job.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowInstances
            .Where(instance => instance.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowDefinitions
            .Where(definition => definition.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
    }
}
