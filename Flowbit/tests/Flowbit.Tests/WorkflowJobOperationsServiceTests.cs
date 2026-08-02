using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowJobOperationsServiceTests
{
    [Fact]
    public async Task MissingSettingDefaultsToAdminRole()
    {
        var repository = new StubJobRepository();
        var service = new WorkflowJobOperationsService(
            repository,
            new StubEngineSettingsRepository(null),
            TimeProvider.System);

        await service.SearchJobsAsync(
            new WorkflowJobQuery(),
            new ActorContext("operator", ["ADMIN"], new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.Equal(1, repository.SearchCount);
    }

    [Fact]
    public async Task CallerWithoutConfiguredRoleIsRejectedBeforeQuery()
    {
        var repository = new StubJobRepository();
        var service = new WorkflowJobOperationsService(
            repository,
            new StubEngineSettingsRepository("ops, platform-admin"),
            TimeProvider.System);

        await Assert.ThrowsAsync<WorkflowForbiddenException>(() =>
            service.SearchJobsAsync(
                new WorkflowJobQuery(),
                new ActorContext("reader", ["auditor"], new Dictionary<string, string>()),
                CancellationToken.None));

        Assert.Equal(0, repository.SearchCount);
    }

    [Fact]
    public async Task ConfiguredRoleMatchingIsCaseInsensitive()
    {
        var repository = new StubJobRepository();
        var service = new WorkflowJobOperationsService(
            repository,
            new StubEngineSettingsRepository("ops, platform-admin"),
            TimeProvider.System);

        await service.SearchIncidentsAsync(
            new WorkflowIncidentQuery(),
            new ActorContext("operator", ["OPS"], new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.Equal(1, repository.IncidentSearchCount);
    }

    [Fact]
    public async Task InvalidJobCursorIsRejectedBeforeQuery()
    {
        var repository = new StubJobRepository();
        var service = new WorkflowJobOperationsService(
            repository,
            new StubEngineSettingsRepository(null),
            TimeProvider.System);

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.SearchJobsAsync(
                new WorkflowJobQuery { Cursor = "not-a-cursor" },
                new ActorContext("operator", ["admin"], new Dictionary<string, string>()),
                CancellationToken.None));

        Assert.Equal(0, repository.SearchCount);
    }

    [Fact]
    public async Task JobPageAfterFirstRequiresKeysetCursor()
    {
        var repository = new StubJobRepository();
        var service = new WorkflowJobOperationsService(
            repository,
            new StubEngineSettingsRepository(null),
            TimeProvider.System);

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.SearchJobsAsync(
                new WorkflowJobQuery { Page = 2 },
                new ActorContext("operator", ["admin"], new Dictionary<string, string>()),
                CancellationToken.None));

        Assert.Equal(0, repository.SearchCount);
    }

    [Fact]
    public async Task IncidentPageAfterFirstRequiresKeysetCursor()
    {
        var repository = new StubJobRepository();
        var service = new WorkflowJobOperationsService(
            repository,
            new StubEngineSettingsRepository(null),
            TimeProvider.System);

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.SearchIncidentsAsync(
                new WorkflowIncidentQuery { Page = 2 },
                new ActorContext("operator", ["admin"], new Dictionary<string, string>()),
                CancellationToken.None));

        Assert.Equal(0, repository.IncidentSearchCount);
    }

    [Fact]
    public async Task QueueStatisticsAreAuthorizedAndExposeDatabaseObservedLag()
    {
        var observedAt = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var repository = new StubJobRepository
        {
            QueueStatistics = new WorkflowJobQueueStatisticsRecord(
                RunnableDepth: 17,
                OldestRunnableDueAt: observedAt.AddSeconds(-75.25),
                TimerControlRunnableCount: 4,
                ActiveLeaseCount: 6,
                OpenIncidentCount: 2,
                ObservedAt: observedAt)
        };
        var service = new WorkflowJobOperationsService(
            repository,
            new StubEngineSettingsRepository(null),
            TimeProvider.System);

        var result = await service.GetQueueStatisticsAsync(
            new ActorContext("operator", ["ADMIN"], new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.Equal(1, repository.QueueStatisticsCount);
        Assert.Equal(17, result.RunnableDepth);
        Assert.Equal(observedAt.AddSeconds(-75.25), result.OldestRunnableDueAt);
        Assert.InRange(result.QueueLagSeconds, 75.249, 75.251);
        Assert.Equal(4, result.TimerControlRunnableCount);
        Assert.Equal(6, result.ActiveLeaseCount);
        Assert.Equal(2, result.OpenIncidentCount);
        Assert.Equal(observedAt, result.ObservedAt);
    }

    [Fact]
    public async Task QueueStatisticsRejectUnauthorizedCallerBeforeAggregateQuery()
    {
        var repository = new StubJobRepository();
        var service = new WorkflowJobOperationsService(
            repository,
            new StubEngineSettingsRepository("ops"),
            TimeProvider.System);

        await Assert.ThrowsAsync<WorkflowForbiddenException>(() =>
            service.GetQueueStatisticsAsync(
                new ActorContext("reader", ["auditor"], new Dictionary<string, string>()),
                CancellationToken.None));

        Assert.Equal(0, repository.QueueStatisticsCount);
    }

    [Fact]
    public async Task JobDetailUsesPersistedLastFailureFieldsWhenErrorPayloadIsAbsent()
    {
        var repository = new StubJobRepository
        {
            Job = CreateJob(
                lastFailureCode: "output_version_conflict",
                lastFailureDescription: "The output variable changed after staging.")
        };
        var service = new WorkflowJobOperationsService(
            repository,
            new StubEngineSettingsRepository(null),
            TimeProvider.System);

        var detail = await service.GetJobAsync(
            repository.Job.Id,
            new ActorContext("operator", ["admin"], new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("output_version_conflict", detail.LastFailureCode);
        Assert.Equal(
            "The output variable changed after staging.",
            detail.LastFailureDescription);
    }

    private sealed class StubEngineSettingsRepository(string? value) : IEngineSettingsRepository
    {
        public Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(value is null
                ? null
                : new EngineSettingRecord(
                    1,
                    null,
                    key,
                    value,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(
            string pattern,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EngineSettingRecord>>([]);

        public Task<EngineSettingRecord> SetAsync(
            string key,
            string settingValue,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubJobRepository : IWorkflowJobRepository
    {
        public int SearchCount { get; private set; }
        public int IncidentSearchCount { get; private set; }
        public int QueueStatisticsCount { get; private set; }
        public WorkflowJobQueueStatisticsRecord QueueStatistics { get; init; } = new(
            RunnableDepth: 0,
            OldestRunnableDueAt: null,
            TimerControlRunnableCount: 0,
            ActiveLeaseCount: 0,
            OpenIncidentCount: 0,
            ObservedAt: DateTimeOffset.UnixEpoch);
        public WorkflowJobRecord Job { get; init; } = CreateJob(null, null);

        public Task<WorkflowJobQueueStatisticsRecord> GetQueueStatisticsAsync(
            CancellationToken cancellationToken)
        {
            QueueStatisticsCount++;
            return Task.FromResult(QueueStatistics);
        }
        public Task<IReadOnlyDictionary<long, WorkflowInstanceJobSummaryRecord>>
            GetInstanceJobSummariesAsync(
                IReadOnlyCollection<long> instanceIds,
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<long, WorkflowInstanceJobSummaryRecord>>(
                new Dictionary<long, WorkflowInstanceJobSummaryRecord>());

        public Task<PagedResult<WorkflowJobRecord>> SearchJobsAsync(
            WorkflowJobQuery query,
            CancellationToken cancellationToken)
        {
            SearchCount++;
            return Task.FromResult(new PagedResult<WorkflowJobRecord>(
                [],
                query.Page,
                query.PageSize,
                0));
        }

        public Task<PagedResult<WorkflowIncidentRecord>> SearchIncidentsAsync(
            WorkflowIncidentQuery query,
            CancellationToken cancellationToken)
        {
            IncidentSearchCount++;
            return Task.FromResult(new PagedResult<WorkflowIncidentRecord>(
                [],
                query.Page,
                query.PageSize,
                0));
        }

        public Task<WorkflowJobRecord> EnqueueAsync(
            WorkflowJobCreateRecord create,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobRecord> EnqueueIncidentAsync(
            WorkflowJobCreateRecord create,
            string type,
            string summary,
            string? details,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobRecord?> GetAsync(long jobId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowJobRecord?>(jobId == Job.Id ? Job : null);
        public Task<WorkflowJobRecord?> GetForUpdateAsync(long jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowJobRecord>> ListOpenByInstanceAsync(
            long instanceId,
            bool forUpdate,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowIncidentRecord>> ListOpenIncidentsByInstanceAsync(
            long instanceId,
            bool forUpdate,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowJobLeaseRecord>> LeaseRunnableAsync(
            WorkflowJobLeaseRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> HeartbeatAsync(
            WorkflowJobFence fence,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> IsLeaseAliveAsync(
            WorkflowJobFence fence,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobSnapshotRecord?> SaveStageAsync(
            WorkflowJobFence fence,
            WorkflowJobStageRecord stage,
            int maxSnapshotBytes,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobSnapshotRecord?> GetSnapshotAsync(
            long snapshotId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SaveResultReadyAsync(
            WorkflowJobFence fence,
            WorkflowJobResultRecord result,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ReleaseResultReadyLeaseAsync(
            WorkflowJobFence fence,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CompleteAsync(
            WorkflowJobFence fence,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ScheduleRetryAsync(
            WorkflowJobFence fence,
            DateTimeOffset dueAt,
            WorkflowJobResultRecord failure,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowIncidentRecord?> OpenIncidentAsync(
            WorkflowJobFence fence,
            string type,
            string summary,
            string? details,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelByInstanceAsync(
            long instanceId,
            string reason,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelByTokenIdsAsync(
            long instanceId,
            IReadOnlyCollection<long> tokenIds,
            string reason,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelByTimerSubscriptionIdsAsync(
            IReadOnlyCollection<long> timerSubscriptionIds,
            string reason,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelOtherJobsByTokenIdsAsync(
            long instanceId,
            IReadOnlyCollection<long> tokenIds,
            long? exceptJobId,
            string reason,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelTimerJobsByTokenIdsAsync(
            long instanceId,
            IReadOnlyCollection<long> tokenIds,
            long? exceptJobId,
            string reason,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<long> CountOpenByInstanceAsync(
            long instanceId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DateTimeOffset?> GetNextWakeAtAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<DateTimeOffset?>(null);
        public Task<PagedResult<WorkflowJobAttemptRecord>> ListAttemptsAsync(
            long jobId,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowIncidentRecord?> GetIncidentAsync(
            long incidentId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobRecord?> RetryIncidentAsync(
            long incidentId,
            string resolvedBy,
            DateTimeOffset dueAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobCleanupResult> CleanupAsync(
            DateTimeOffset completedJobsBefore,
            DateTimeOffset resolvedIncidentsBefore,
            int batchSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static WorkflowJobRecord CreateJob(
        string? lastFailureCode,
        string? lastFailureDescription)
    {
        var now = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        return new WorkflowJobRecord(
            Id: 17,
            InstanceId: 5,
            WorkflowDefinitionId: 3,
            WorkflowKey: "operations-test",
            TokenId: 7,
            MultiInstanceExecutionId: null,
            UserTaskId: null,
            TimerSubscriptionId: null,
            ActivationId: Guid.NewGuid(),
            NodeId: 10,
            NodeName: "Send request",
            NodeType: "serviceTask",
            Kind: WorkflowJobKinds.AsyncBefore,
            QueueClass: WorkflowJobClasses.Activity,
            Phase: "execute",
            Status: WorkflowJobStatuses.Incident,
            Priority: 0,
            AttemptCount: 1,
            MaxAttempts: 4,
            FailureHandling: WorkflowJobFailureHandling.BoundaryFirst,
            RetryDelays: [TimeSpan.FromSeconds(10)],
            DueAt: now,
            ScheduledOccurrenceAt: null,
            Payload: null,
            SnapshotId: 11,
            WorkerId: null,
            LeaseToken: null,
            LeaseGeneration: 1,
            LeaseExpiresAt: null,
            HeartbeatAt: null,
            Result: null,
            Error: null,
            LastFailureCode: lastFailureCode,
            LastFailureDescription: lastFailureDescription,
            ResultReadyAt: now,
            IncidentId: 19,
            CreatedAt: now,
            UpdatedAt: now,
            StartedAt: now,
            CompletedAt: null);
    }
}
