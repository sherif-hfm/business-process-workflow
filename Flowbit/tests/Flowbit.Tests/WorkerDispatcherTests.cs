extern alias FlowbitWorker;

using System.Collections.Concurrent;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using JobDispatcher = FlowbitWorker::Flowbit.Worker.JobDispatcher;
using IJobWakeupSignal = FlowbitWorker::Flowbit.Worker.IJobWakeupSignal;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;
using WorkerTelemetry = FlowbitWorker::Flowbit.Worker.WorkerTelemetry;

namespace Flowbit.Tests;

public sealed class WorkerDispatcherTests
{
    [Fact]
    public async Task RepeatsFairAcquisitionRoundsImmediatelyWhileSlotsRemain()
    {
        var leases = Enumerable.Range(1, 4).Select(NewLease).ToArray();
        var repository = new DispatcherJobRepository(leases);
        var processor = new BlockingProcessor();
        await using var services = new ServiceCollection()
            .AddSingleton<IWorkflowJobRepository>(repository)
            .AddSingleton<IWorkflowJobProcessor>(processor)
            .BuildServiceProvider();
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = new JobDispatcher(
            services.GetRequiredService<IServiceScopeFactory>(),
            new DelayWakeupSignal(),
            NewOptions(maxConcurrency: 4),
            TimeProvider.System,
            telemetry,
            NullLogger<JobDispatcher>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        await repository.FirstAcquisition.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var started = DateTimeOffset.UtcNow;
        await processor.FourthStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromMilliseconds(500));
        Assert.Equal(4, processor.StartedCount);
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancelsLocalInvocationOnShortFenceCheckCadence()
    {
        var repository = new DispatcherJobRepository([NewLease(1)])
        {
            LeaseAlive = false
        };
        var processor = new BlockingProcessor();
        await using var services = new ServiceCollection()
            .AddSingleton<IWorkflowJobRepository>(repository)
            .AddSingleton<IWorkflowJobProcessor>(processor)
            .BuildServiceProvider();
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = new JobDispatcher(
            services.GetRequiredService<IServiceScopeFactory>(),
            new DelayWakeupSignal(),
            NewOptions(maxConcurrency: 1),
            TimeProvider.System,
            telemetry,
            NullLogger<JobDispatcher>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        await repository.FirstAcquisition.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var started = DateTimeOffset.UtcNow;
        await processor.Cancelled.Task.WaitAsync(TimeSpan.FromMilliseconds(750));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromMilliseconds(750));
        Assert.True(repository.LeaseCheckCount > 0);
        Assert.Equal(0, repository.HeartbeatCount);
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RetriesAHeartbeatThatBlocksPastTheCommandTimeout()
    {
        var repository = new DispatcherJobRepository([NewLease(1)])
        {
            BlockHeartbeats = true
        };
        var processor = new BlockingProcessor();
        await using var services = new ServiceCollection()
            .AddSingleton<IWorkflowJobRepository>(repository)
            .AddSingleton<IWorkflowJobProcessor>(processor)
            .BuildServiceProvider();
        using var telemetry = new WorkerTelemetry();
        var options = NewOptions(maxConcurrency: 1);
        options.HeartbeatSeconds = 1;
        using var dispatcher = new JobDispatcher(
            services.GetRequiredService<IServiceScopeFactory>(),
            new DelayWakeupSignal(),
            options,
            TimeProvider.System,
            telemetry,
            NullLogger<JobDispatcher>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        await repository.FirstAcquisition.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await repository.FirstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await repository.SecondHeartbeat.Task.WaitAsync(TimeSpan.FromMilliseconds(750));

        Assert.True(repository.HeartbeatCount >= 2);
        await dispatcher.StopAsync(CancellationToken.None);
    }

    private static WorkerOptions NewOptions(int maxConcurrency) => new()
    {
        MaxConcurrency = maxConcurrency,
        ActivityConcurrency = maxConcurrency,
        BatchSize = maxConcurrency,
        MaxPerInstance = maxConcurrency,
        LeaseSeconds = 15,
        HeartbeatSeconds = 5,
        LeaseCheckMilliseconds = 100,
        HeartbeatCommandTimeoutMilliseconds = 100,
        PollMilliseconds = 1000,
        IdleBackoffMilliseconds = 5000,
        ShutdownDrainSeconds = 2
    };

    private static WorkflowJobLeaseRecord NewLease(int id)
    {
        var now = DateTimeOffset.UtcNow;
        var token = Guid.NewGuid();
        return new WorkflowJobLeaseRecord(
            new WorkflowJobRecord(
                id,
                1,
                1,
                "dispatcher-test",
                id,
                null,
                null,
                null,
                Guid.NewGuid(),
                id,
                $"job-{id}",
                "serviceTask",
                WorkflowJobKinds.AsyncBefore,
                WorkflowJobClasses.Activity,
                "before",
                WorkflowJobStatuses.Running,
                0,
                1,
                4,
                WorkflowJobFailureHandling.BoundaryFirst,
                [TimeSpan.FromSeconds(1)],
                now,
                null,
                null,
                null,
                "dispatcher-worker",
                token,
                1,
                now.AddSeconds(15),
                now,
                null,
                null,
                null,
                null,
                null,
                null,
                now,
                now,
                now,
                null),
            token,
            1,
            1);
    }

    private sealed class BlockingProcessor : IWorkflowJobProcessor
    {
        private int _startedCount;

        public int StartedCount => Volatile.Read(ref _startedCount);
        public TaskCompletionSource FourthStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ProcessAsync(
            WorkflowJobLeaseRecord lease,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _startedCount) == 4)
            {
                FourthStarted.TrySetResult();
            }
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class DelayWakeupSignal : IJobWakeupSignal
    {
        public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            await Task.Delay(timeout, cancellationToken);
        }
    }

    private sealed class DispatcherJobRepository(
        IEnumerable<WorkflowJobLeaseRecord> leases) : IWorkflowJobRepository
    {
        private readonly ConcurrentQueue<WorkflowJobLeaseRecord> _leases = new(leases);
        private int _acquisitionCount;
        private int _leaseCheckCount;
        private int _heartbeatCount;

        public bool LeaseAlive { get; init; } = true;
        public bool BlockHeartbeats { get; init; }
        public int LeaseCheckCount => Volatile.Read(ref _leaseCheckCount);
        public int HeartbeatCount => Volatile.Read(ref _heartbeatCount);
        public TaskCompletionSource FirstAcquisition { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstHeartbeat { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondHeartbeat { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<WorkflowJobLeaseRecord>> LeaseRunnableAsync(
            WorkflowJobLeaseRequest request,
            CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _acquisitionCount);
            if (count == 1)
            {
                FirstAcquisition.TrySetResult();
            }
            IReadOnlyList<WorkflowJobLeaseRecord> result =
                _leases.TryDequeue(out var lease) ? [lease] : [];
            return Task.FromResult(result);
        }

        public async Task<bool> HeartbeatAsync(
            WorkflowJobFence fence,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _heartbeatCount);
            if (count == 1)
            {
                FirstHeartbeat.TrySetResult();
            }
            if (count == 2)
            {
                SecondHeartbeat.TrySetResult();
            }
            if (BlockHeartbeats)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return LeaseAlive;
        }

        public Task<bool> IsLeaseAliveAsync(
            WorkflowJobFence fence,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _leaseCheckCount);
            return Task.FromResult(LeaseAlive);
        }

        public Task<DateTimeOffset?> GetNextWakeAtAsync(CancellationToken cancellationToken) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task<bool> ReleaseResultReadyLeaseAsync(
            WorkflowJobFence fence,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> ScheduleRetryAsync(
            WorkflowJobFence fence,
            DateTimeOffset dueAt,
            WorkflowJobResultRecord failure,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<WorkflowIncidentRecord?> OpenIncidentAsync(
            WorkflowJobFence fence,
            string type,
            string summary,
            string? details,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowIncidentRecord?>(null);

        public Task<WorkflowJobRecord> EnqueueAsync(WorkflowJobCreateRecord create, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobRecord> EnqueueIncidentAsync(WorkflowJobCreateRecord create, string type, string summary, string? details, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobRecord?> GetAsync(long jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobRecord?> GetForUpdateAsync(long jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowJobRecord>> ListOpenByInstanceAsync(long instanceId, bool forUpdate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowIncidentRecord>> ListOpenIncidentsByInstanceAsync(long instanceId, bool forUpdate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobSnapshotRecord?> SaveStageAsync(WorkflowJobFence fence, WorkflowJobStageRecord stage, int maxSnapshotBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobSnapshotRecord?> GetSnapshotAsync(long snapshotId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SaveResultReadyAsync(WorkflowJobFence fence, WorkflowJobResultRecord result, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CompleteAsync(WorkflowJobFence fence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelByInstanceAsync(long instanceId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelByTokenIdsAsync(long instanceId, IReadOnlyCollection<long> tokenIds, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelByTimerSubscriptionIdsAsync(IReadOnlyCollection<long> timerSubscriptionIds, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelOtherJobsByTokenIdsAsync(long instanceId, IReadOnlyCollection<long> tokenIds, long? exceptJobId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelTimerJobsByTokenIdsAsync(long instanceId, IReadOnlyCollection<long> tokenIds, long? exceptJobId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<long> CountOpenByInstanceAsync(long instanceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobQueueStatisticsRecord> GetQueueStatisticsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<long, WorkflowInstanceJobSummaryRecord>> GetInstanceJobSummariesAsync(IReadOnlyCollection<long> instanceIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<WorkflowJobRecord>> SearchJobsAsync(WorkflowJobQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<WorkflowJobAttemptRecord>> ListAttemptsAsync(long jobId, string? cursor, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<WorkflowIncidentRecord>> SearchIncidentsAsync(WorkflowIncidentQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowIncidentRecord?> GetIncidentAsync(long incidentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobRecord?> RetryIncidentAsync(long incidentId, string resolvedBy, DateTimeOffset dueAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobCleanupResult> CleanupAsync(DateTimeOffset completedJobsBefore, DateTimeOffset resolvedIncidentsBefore, int batchSize, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
