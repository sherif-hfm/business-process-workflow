using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowbit.Infrastructure.Repositories;

public sealed class WorkflowJobRepository(
    AppDbContext dbContext,
    NpgsqlDataSource dataSource) : IWorkflowJobRepository
{
    private const int FailureDescriptionLimit = 1000;
    private const int CleanupMaxBatchesPerRun = 20;
    private static readonly TimeSpan CleanupTimeBudget = TimeSpan.FromSeconds(30);

    public async Task<WorkflowJobRecord> EnqueueAsync(
        WorkflowJobCreateRecord create,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new WorkflowJobEntity
        {
            InstanceId = create.InstanceId,
            WorkflowDefinitionId = create.WorkflowDefinitionId,
            WorkflowKey = create.WorkflowKey,
            TokenId = create.TokenId,
            MultiInstanceExecutionId = create.MultiInstanceExecutionId,
            UserTaskId = create.UserTaskId,
            TimerSubscriptionId = create.TimerSubscriptionId,
            ActivationId = create.ActivationId,
            NodeId = create.NodeId,
            NodeName = create.NodeName,
            NodeType = create.NodeType,
            Kind = create.Kind,
            QueueClass = create.QueueClass,
            Phase = create.Phase,
            Status = WorkflowJobStatuses.Queued,
            Priority = create.Priority,
            MaxAttempts = create.MaxAttempts,
            FailureHandling = create.FailureHandling,
            RetryDelays = create.RetryDelays.ToArray(),
            DueAt = create.DueAt,
            ScheduledOccurrenceAt = create.ScheduledOccurrenceAt,
            PayloadJson = CloneDocument(create.Payload),
            SnapshotId = create.SnapshotId,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.WorkflowJobs.Add(entity);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (create.TimerSubscriptionId is not null
                  && create.ScheduledOccurrenceAt is not null
                  && exception.InnerException is PostgresException
                  {
                      SqlState: PostgresErrorCodes.UniqueViolation
                  })
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            var existing = await dbContext.WorkflowJobs
                .AsNoTracking()
                .SingleAsync(job =>
                    job.TimerSubscriptionId == create.TimerSubscriptionId
                    && job.ScheduledOccurrenceAt == create.ScheduledOccurrenceAt,
                    cancellationToken);
            return MapJob(existing);
        }
        await dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_notify('flowbit_jobs', '')",
            cancellationToken);
        return MapJob(entity);
    }

    public async Task<WorkflowJobRecord?> GetAsync(
        long jobId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowJobs
            .AsNoTracking()
            .Include(job => job.Incidents.Where(incident =>
                incident.Status == WorkflowIncidentStatuses.Open))
            .SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken);
        return entity is null ? null : MapJob(entity);
    }

    public async Task<WorkflowJobRecord?> GetForUpdateAsync(
        long jobId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowJobs
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM flowbit.workflow_jobs
                WHERE "Id" = {jobId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

        if (entity is null)
        {
            return null;
        }

        return MapJob(entity);
    }

    public async Task<IReadOnlyList<WorkflowJobLeaseRecord>> LeaseRunnableAsync(
        WorkflowJobLeaseRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MaxCount <= 0)
        {
            return [];
        }

        var selected = new List<LeasedIdentity>(request.MaxCount);
        var selectedFairnessKeys = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await OpenExhaustedLeaseIncidentsAsync(
            connection,
            transaction,
            request.MaxCount,
            cancellationToken);

        await SelectAndLeaseClassAsync(
            connection,
            transaction,
            request,
            WorkflowJobClasses.Control,
            request.MaxCount,
            selected,
            selectedFairnessKeys,
            cancellationToken);

        var remaining = request.MaxCount - selected.Count;
        if (remaining > 0 && request.MaxActivityCount > 0)
        {
            await SelectAndLeaseClassAsync(
                connection,
                transaction,
                request,
                WorkflowJobClasses.Activity,
                Math.Min(remaining, request.MaxActivityCount),
                selected,
                selectedFairnessKeys,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (selected.Count == 0)
        {
            return [];
        }

        var ids = selected.Select(item => item.JobId).ToArray();
        var entities = await dbContext.WorkflowJobs
            .AsNoTracking()
            .Include(job => job.Incidents.Where(incident =>
                incident.Status == WorkflowIncidentStatuses.Open))
            .Where(job => ids.Contains(job.Id))
            .ToListAsync(cancellationToken);
        var byId = entities.ToDictionary(job => job.Id);

        return selected
            .Where(item => byId.ContainsKey(item.JobId))
            .Select(item => new WorkflowJobLeaseRecord(
                MapJob(byId[item.JobId]),
                item.LeaseToken,
                item.LeaseGeneration,
                item.AttemptNumber))
            .ToArray();
    }

    private static async Task OpenExhaustedLeaseIncidentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int limit,
        CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        await using (var select = new NpgsqlCommand(
            """
            SELECT "Id"
            FROM flowbit.workflow_jobs
            WHERE "Status" = 'running'
              AND "LeaseExpiresAt" <= clock_timestamp()
              AND "AttemptCount" >= "MaxAttempts"
            ORDER BY "LeaseExpiresAt", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT @limit
            """,
            connection,
            transaction))
        {
            select.Parameters.AddWithValue("limit", Math.Max(1, limit));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetInt64(0));
            }
        }
        if (ids.Count == 0)
        {
            return;
        }

        await using (var attempts = new NpgsqlCommand(
            """
            UPDATE flowbit.workflow_job_attempts
            SET "Status" = 'leaseLost',
                "FinishedAt" = clock_timestamp(),
                "FailureCode" = 'leaseExpired',
                "FailureDescription" = 'The final permitted worker attempt lost its lease.'
            WHERE "JobId" = ANY(@ids)
              AND "Status" IN ('running', 'resultReady')
            """,
            connection,
            transaction))
        {
            attempts.Parameters.AddWithValue("ids", ids.ToArray());
            await attempts.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var incidents = new NpgsqlCommand(
            """
            INSERT INTO flowbit.workflow_incidents
                ("JobId", "OriginalJobId", "InstanceId", "WorkflowDefinitionId", "WorkflowKey",
                 "NodeId", "NodeName", "Type", "Status", "Summary", "Details",
                 "CreatedAt", "UpdatedAt")
            SELECT j."Id", j."Id", j."InstanceId", j."WorkflowDefinitionId", j."WorkflowKey",
                   j."NodeId", j."NodeName", 'lease_exhausted', 'open',
                   'Workflow job exhausted its attempts after a worker lease expired.',
                   'The external operation may have occurred. Review downstream idempotency before retrying.',
                   clock_timestamp(), clock_timestamp()
            FROM flowbit.workflow_jobs AS j
            WHERE j."Id" = ANY(@ids)
            ON CONFLICT ("JobId", "Status") WHERE "Status" = 'open' DO NOTHING
            """,
            connection,
            transaction))
        {
            incidents.Parameters.AddWithValue("ids", ids.ToArray());
            await incidents.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var jobs = new NpgsqlCommand(
            """
            UPDATE flowbit.workflow_jobs
            SET "Status" = 'incident',
                "WorkerId" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL,
                "HeartbeatAt" = NULL,
                "LastFailureCode" = 'lease_exhausted',
                "LastFailureDescription" = 'The final permitted worker attempt lost its lease.',
                "UpdatedAt" = clock_timestamp()
            WHERE "Id" = ANY(@ids)
            """,
            connection,
            transaction);
        jobs.Parameters.AddWithValue("ids", ids.ToArray());
        await jobs.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> HeartbeatAsync(
        WorkflowJobFence fence,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE flowbit.workflow_jobs
            SET "HeartbeatAt" = clock_timestamp(),
                "LeaseExpiresAt" = clock_timestamp() + {leaseDuration},
                "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {fence.JobId}
              AND "WorkerId" = {fence.WorkerId}
              AND "LeaseToken" = {fence.LeaseToken}
              AND "LeaseGeneration" = {fence.LeaseGeneration}
              AND "Status" IN ('running', 'resultReady')
              AND "LeaseExpiresAt" > clock_timestamp()
            """,
            cancellationToken);
        return affected == 1;
    }

    public async Task<bool> IsLeaseAliveAsync(
        WorkflowJobFence fence,
        CancellationToken cancellationToken)
    {
        // A plain MVCC read never waits for a concurrent finalizer's row lock.
        // It observes the old live fence until that transaction commits and
        // then promptly observes completion/cancellation on the next check.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS
            (
                SELECT 1
                FROM flowbit.workflow_jobs
                WHERE "Id" = @job_id
                  AND "WorkerId" = @worker_id
                  AND "LeaseToken" = @lease_token
                  AND "LeaseGeneration" = @lease_generation
                  AND "Status" IN ('running', 'resultReady')
                  AND "LeaseExpiresAt" > clock_timestamp()
            )
            """,
            connection);
        command.Parameters.AddWithValue("job_id", fence.JobId);
        command.Parameters.AddWithValue("worker_id", fence.WorkerId);
        command.Parameters.AddWithValue("lease_token", fence.LeaseToken);
        command.Parameters.AddWithValue("lease_generation", fence.LeaseGeneration);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<WorkflowJobSnapshotRecord?> SaveStageAsync(
        WorkflowJobFence fence,
        WorkflowJobStageRecord stage,
        int maxSnapshotBytes,
        CancellationToken cancellationToken)
    {
        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var job = await FindFencedTrackedAsync(fence, WorkflowJobStatuses.Running, cancellationToken);
        if (job is null)
        {
            return null;
        }

        if (job.SnapshotId is not null)
        {
            return await GetSnapshotAsync(job.SnapshotId.Value, cancellationToken);
        }

        using var variables = ToDocument(stage.Variables);
        using var outputVersions = ToDocument(stage.OutputVariableVersions);
        using var invocation = CloneDocument(stage.Invocation);
        using var flowInfo = CloneDocument(stage.FlowInfo);
        var size = Utf8Size(invocation)
            + Utf8Size(variables)
            + Utf8Size(outputVersions)
            + Utf8Size(flowInfo);
        if (size > maxSnapshotBytes)
        {
            throw new InvalidOperationException(
                $"Workflow job snapshot is {size} bytes; the configured maximum is {maxSnapshotBytes} bytes.");
        }

        var entity = new WorkflowJobSnapshotEntity
        {
            Kind = job.Kind,
            InvocationJson = CloneDocument(invocation),
            VariablesJson = CloneDocument(variables)!,
            OutputVariableVersionsJson = CloneDocument(outputVersions)!,
            FlowInfoJson = CloneDocument(flowInfo),
            EvaluationTime = stage.EvaluationTime,
            SizeBytes = size,
            CreatedAt = DateTimeOffset.UtcNow
        };
        dbContext.WorkflowJobSnapshots.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        job.SnapshotId = entity.Id;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return MapSnapshot(entity);
    }

    public async Task<WorkflowJobSnapshotRecord?> GetSnapshotAsync(
        long snapshotId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowJobSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(snapshot => snapshot.Id == snapshotId, cancellationToken);
        return entity is null ? null : MapSnapshot(entity);
    }

    public async Task<bool> SaveResultReadyAsync(
        WorkflowJobFence fence,
        WorkflowJobResultRecord result,
        CancellationToken cancellationToken)
    {
        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var job = await FindFencedTrackedAsync(fence, WorkflowJobStatuses.Running, cancellationToken);
        if (job is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        job.Status = WorkflowJobStatuses.ResultReady;
        job.ResultJson = CloneDocument(result.Result);
        job.ErrorJson = CloneDocument(result.Error);
        job.LastFailureCode = Truncate(result.FailureCode, 100);
        job.LastFailureDescription = Truncate(result.FailureDescription, FailureDescriptionLimit);
        job.ResultReadyAt = now;
        job.UpdatedAt = now;

        var attempt = await GetCurrentAttemptAsync(job, cancellationToken);
        if (attempt is not null)
        {
            attempt.Status = WorkflowJobAttemptStatuses.ResultReady;
            attempt.FailureCode = job.LastFailureCode;
            attempt.FailureDescription = job.LastFailureDescription;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return true;
    }

    public async Task<bool> ReleaseResultReadyLeaseAsync(
        WorkflowJobFence fence,
        CancellationToken cancellationToken)
    {
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE flowbit.workflow_jobs
            SET "LeaseExpiresAt" = clock_timestamp(),
                "HeartbeatAt" = clock_timestamp(),
                "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {fence.JobId}
              AND "WorkerId" = {fence.WorkerId}
              AND "LeaseToken" = {fence.LeaseToken}
              AND "LeaseGeneration" = {fence.LeaseGeneration}
              AND "Status" = 'resultReady'
              AND "LeaseExpiresAt" > clock_timestamp()
            """,
            cancellationToken);
        if (affected == 1)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "SELECT pg_notify('flowbit_jobs', '')",
                cancellationToken);
        }
        return affected == 1;
    }

    public async Task<bool> CompleteAsync(
        WorkflowJobFence fence,
        CancellationToken cancellationToken)
    {
        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var job = await FindFencedTrackedAsync(
            fence,
            [WorkflowJobStatuses.Running, WorkflowJobStatuses.ResultReady],
            cancellationToken);
        if (job is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        job.Status = WorkflowJobStatuses.Completed;
        job.CompletedAt = now;
        job.UpdatedAt = now;
        ClearLease(job);

        var attempt = await GetCurrentAttemptAsync(job, cancellationToken);
        if (attempt is not null)
        {
            attempt.Status = WorkflowJobAttemptStatuses.Completed;
            attempt.FinishedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return true;
    }

    public async Task<bool> ScheduleRetryAsync(
        WorkflowJobFence fence,
        DateTimeOffset dueAt,
        WorkflowJobResultRecord failure,
        CancellationToken cancellationToken)
    {
        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var job = await FindFencedTrackedAsync(
            fence,
            [WorkflowJobStatuses.Running, WorkflowJobStatuses.ResultReady],
            cancellationToken);
        if (job is null || job.AttemptCount >= job.MaxAttempts)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        job.Status = WorkflowJobStatuses.Retry;
        job.DueAt = dueAt;
        job.ResultJson = null;
        job.ErrorJson = CloneDocument(failure.Error);
        job.ResultReadyAt = null;
        job.LastFailureCode = Truncate(failure.FailureCode, 100);
        job.LastFailureDescription = Truncate(failure.FailureDescription, FailureDescriptionLimit);
        job.UpdatedAt = now;
        ClearLease(job);

        var attempt = await GetCurrentAttemptAsync(job, cancellationToken);
        if (attempt is not null)
        {
            attempt.Status = WorkflowJobAttemptStatuses.Failed;
            attempt.FinishedAt = now;
            attempt.FailureCode = job.LastFailureCode;
            attempt.FailureDescription = job.LastFailureDescription;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_notify('flowbit_jobs', '')",
            cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return true;
    }

    public async Task<WorkflowIncidentRecord?> OpenIncidentAsync(
        WorkflowJobFence fence,
        string type,
        string summary,
        string? details,
        CancellationToken cancellationToken)
    {
        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var job = await FindFencedTrackedAsync(
            fence,
            [WorkflowJobStatuses.Running, WorkflowJobStatuses.ResultReady],
            cancellationToken);
        if (job is null)
        {
            return null;
        }

        await dbContext.Entry(job)
            .Collection(item => item.Incidents)
            .Query()
            .Where(incident => incident.Status == WorkflowIncidentStatuses.Open)
            .LoadAsync(cancellationToken);
        var existingIncident = job.Incidents.SingleOrDefault(incident =>
            incident.Status == WorkflowIncidentStatuses.Open);
        if (existingIncident is not null)
        {
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return MapIncident(existingIncident);
        }

        var now = DateTimeOffset.UtcNow;
        var incident = new WorkflowIncidentEntity
        {
            JobId = job.Id,
            OriginalJobId = job.Id,
            InstanceId = job.InstanceId,
            WorkflowDefinitionId = job.WorkflowDefinitionId,
            WorkflowKey = job.WorkflowKey,
            NodeId = job.NodeId,
            NodeName = job.NodeName,
            Type = Truncate(type, 100) ?? "jobFailure",
            Status = WorkflowIncidentStatuses.Open,
            Summary = Truncate(summary, 500) ?? "Workflow job failed.",
            Details = Truncate(details, 4000),
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.WorkflowIncidents.Add(incident);

        job.Status = WorkflowJobStatuses.Incident;
        job.UpdatedAt = now;
        ClearLease(job);

        var attempt = await GetCurrentAttemptAsync(job, cancellationToken);
        if (attempt is not null)
        {
            attempt.Status = WorkflowJobAttemptStatuses.Failed;
            attempt.FinishedAt = now;
            attempt.FailureCode = incident.Type;
            attempt.FailureDescription = Truncate(details ?? summary, FailureDescriptionLimit);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return MapIncident(incident);
    }

    public Task<int> CancelByInstanceAsync(
        long instanceId,
        string reason,
        CancellationToken cancellationToken) =>
        CancelAsync(
            dbContext.WorkflowJobs.Where(job => job.InstanceId == instanceId),
            reason,
            cancellationToken);

    public Task<int> CancelByTokenIdsAsync(
        long instanceId,
        IReadOnlyCollection<long> tokenIds,
        string reason,
        CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0)
        {
            return Task.FromResult(0);
        }

        return CancelAsync(
            dbContext.WorkflowJobs.Where(job =>
                job.InstanceId == instanceId
                && job.TokenId != null
                && tokenIds.Contains(job.TokenId.Value)),
            reason,
            cancellationToken);
    }

    public Task<int> CancelByTimerSubscriptionIdsAsync(
        IReadOnlyCollection<long> timerSubscriptionIds,
        string reason,
        CancellationToken cancellationToken)
    {
        if (timerSubscriptionIds.Count == 0)
        {
            return Task.FromResult(0);
        }

        return CancelAsync(
            dbContext.WorkflowJobs.Where(job =>
                job.TimerSubscriptionId != null
                && timerSubscriptionIds.Contains(job.TimerSubscriptionId.Value)),
            reason,
            cancellationToken);
    }

    public Task<int> CancelOtherJobsByTokenIdsAsync(
        long instanceId,
        IReadOnlyCollection<long> tokenIds,
        long? exceptJobId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0)
        {
            return Task.FromResult(0);
        }

        return CancelAsync(
            dbContext.WorkflowJobs.Where(job =>
                job.InstanceId == instanceId
                && job.TokenId != null
                && tokenIds.Contains(job.TokenId.Value)
                && (exceptJobId == null || job.Id != exceptJobId.Value)),
            reason,
            cancellationToken);
    }

    public Task<int> CancelTimerJobsByTokenIdsAsync(
        long instanceId,
        IReadOnlyCollection<long> tokenIds,
        long? exceptJobId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0)
        {
            return Task.FromResult(0);
        }

        return CancelAsync(
            dbContext.WorkflowJobs.Where(job =>
                job.InstanceId == instanceId
                && job.TokenId != null
                && tokenIds.Contains(job.TokenId.Value)
                && job.TimerSubscriptionId != null
                && (exceptJobId == null || job.Id != exceptJobId.Value)),
            reason,
            cancellationToken);
    }

    public Task<long> CountOpenByInstanceAsync(
        long instanceId,
        CancellationToken cancellationToken) =>
        dbContext.WorkflowJobs
            .AsNoTracking()
            .LongCountAsync(job =>
                job.InstanceId == instanceId
                && job.Status != WorkflowJobStatuses.Completed
                && job.Status != WorkflowJobStatuses.Cancelled
                && job.Status != WorkflowJobStatuses.Skipped,
                cancellationToken);

    public async Task<WorkflowJobQueueStatisticsRecord> GetQueueStatisticsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            WITH observed AS MATERIALIZED
            (
                SELECT clock_timestamp() AS observed_at
            ),
            runnable AS MATERIALIZED
            (
                SELECT
                    j."QueueClass",
                    j."DueAt"
                FROM flowbit.workflow_jobs AS j
                CROSS JOIN observed AS o
                WHERE j."Status" IN ('queued', 'retry')
                  AND j."DueAt" <= o.observed_at
                  AND j."AttemptCount" < j."MaxAttempts"

                UNION ALL

                SELECT
                    j."QueueClass",
                    j."DueAt"
                FROM flowbit.workflow_jobs AS j
                CROSS JOIN observed AS o
                WHERE j."Status" = 'running'
                  AND j."LeaseExpiresAt" <= o.observed_at
                  AND j."AttemptCount" < j."MaxAttempts"

                UNION ALL

                SELECT
                    j."QueueClass",
                    j."DueAt"
                FROM flowbit.workflow_jobs AS j
                CROSS JOIN observed AS o
                WHERE j."Status" = 'resultReady'
                  AND j."LeaseExpiresAt" <= o.observed_at
            ),
            job_statistics AS
            (
                SELECT
                    count(*) AS runnable_depth,
                    min("DueAt") AS oldest_runnable_due_at,
                    count(*) FILTER (
                        WHERE "QueueClass" = 'control'
                    ) AS timer_control_runnable_count
                FROM runnable
            )
            SELECT
                s.runnable_depth,
                s.oldest_runnable_due_at,
                s.timer_control_runnable_count,
                (
                    SELECT count(*)
                    FROM flowbit.workflow_jobs AS leased
                    CROSS JOIN observed AS lease_observed
                    WHERE leased."Status" IN ('running', 'resultReady')
                      AND leased."LeaseExpiresAt" > lease_observed.observed_at
                ) AS active_lease_count,
                (
                    SELECT count(*)
                    FROM flowbit.workflow_incidents
                    WHERE "Status" = 'open'
                ) AS open_incident_count,
                o.observed_at
            FROM job_statistics AS s
            CROSS JOIN observed AS o
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The workflow queue statistics query returned no row.");
        }

        static DateTimeOffset ReadUtc(NpgsqlDataReader dataReader, int ordinal) =>
            new(DateTime.SpecifyKind(dataReader.GetDateTime(ordinal), DateTimeKind.Utc));

        return new WorkflowJobQueueStatisticsRecord(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : ReadUtc(reader, 1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            ReadUtc(reader, 5));
    }

    public async Task<DateTimeOffset?> GetNextWakeAtAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT min(wake_at)
            FROM
            (
                SELECT min(j."DueAt") AS wake_at
                FROM flowbit.workflow_jobs AS j
                WHERE j."Status" IN ('queued', 'retry')
                  AND j."AttemptCount" < j."MaxAttempts"

                UNION ALL

                SELECT min(j."LeaseExpiresAt") AS wake_at
                FROM flowbit.workflow_jobs AS j
                WHERE j."Status" = 'running'
                  AND j."AttemptCount" < j."MaxAttempts"
                  AND j."LeaseExpiresAt" IS NOT NULL

                UNION ALL

                SELECT min(j."LeaseExpiresAt") AS wake_at
                FROM flowbit.workflow_jobs AS j
                WHERE j."Status" = 'resultReady'
                  AND j."LeaseExpiresAt" IS NOT NULL
            ) AS durable_wakeups
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        return new DateTimeOffset(
            DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc));
    }

    public async Task<IReadOnlyDictionary<long, WorkflowInstanceJobSummaryRecord>>
        GetInstanceJobSummariesAsync(
            IReadOnlyCollection<long> instanceIds,
            CancellationToken cancellationToken)
    {
        if (instanceIds.Count == 0)
        {
            return new Dictionary<long, WorkflowInstanceJobSummaryRecord>();
        }

        var ids = instanceIds.Distinct().ToArray();
        var rows = await dbContext.WorkflowJobs
            .AsNoTracking()
            .Where(job =>
                job.InstanceId != null
                && ids.Contains(job.InstanceId.Value)
                && job.Status != WorkflowJobStatuses.Completed
                && job.Status != WorkflowJobStatuses.Cancelled
                && job.Status != WorkflowJobStatuses.Skipped)
            .GroupBy(job => job.InstanceId!.Value)
            .Select(group => new
            {
                InstanceId = group.Key,
                OpenCount = group.LongCount(),
                QueuedCount = group.LongCount(job =>
                    job.Status == WorkflowJobStatuses.Queued
                    || job.Status == WorkflowJobStatuses.Retry),
                RunningCount = group.LongCount(job =>
                    job.Status == WorkflowJobStatuses.Running
                    || job.Status == WorkflowJobStatuses.ResultReady),
                IncidentCount = group.LongCount(job =>
                    job.Status == WorkflowJobStatuses.Incident),
                NearestDueAt = group.Min(job =>
                    job.Status == WorkflowJobStatuses.Queued
                    || job.Status == WorkflowJobStatuses.Retry
                        ? (DateTimeOffset?)job.DueAt
                        : null)
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.InstanceId,
            row => new WorkflowInstanceJobSummaryRecord(
                row.InstanceId,
                row.OpenCount,
                row.QueuedCount,
                row.RunningCount,
                row.IncidentCount,
                row.NearestDueAt));
    }

    public async Task<PagedResult<WorkflowJobRecord>> SearchJobsAsync(
        WorkflowJobQuery query,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var source = dbContext.WorkflowJobs.AsNoTracking();
        if (query.InstanceId is not null)
        {
            source = source.Where(job => job.InstanceId == query.InstanceId);
        }
        if (query.WorkflowDefinitionId is not null)
        {
            source = source.Where(job => job.WorkflowDefinitionId == query.WorkflowDefinitionId);
        }
        if (!string.IsNullOrWhiteSpace(query.WorkflowKey))
        {
            source = source.Where(job => job.WorkflowKey == query.WorkflowKey);
        }
        if (query.TokenId is not null)
        {
            source = source.Where(job => job.TokenId == query.TokenId);
        }
        if (query.Statuses.Count > 0)
        {
            source = source.Where(job => query.Statuses.Contains(job.Status));
        }
        if (query.Kinds.Count > 0)
        {
            source = source.Where(job => query.Kinds.Contains(job.Kind));
        }
        if (query.DueFrom is not null)
        {
            source = source.Where(job => job.DueAt >= query.DueFrom);
        }
        if (query.DueTo is not null)
        {
            source = source.Where(job => job.DueAt < query.DueTo);
        }

        var total = await source.LongCountAsync(cancellationToken);
        var cursorPaging = !string.IsNullOrWhiteSpace(query.Cursor);
        if (cursorPaging)
        {
            _ = WorkflowJobCursor.TryDecodeJob(
                query.Cursor,
                out var cursorUpdatedAt,
                out var cursorId);
            source = source.Where(job =>
                job.UpdatedAt < cursorUpdatedAt
                || job.UpdatedAt == cursorUpdatedAt && job.Id < cursorId);
        }
        var rows = await source
            .OrderByDescending(job => job.UpdatedAt)
            .ThenByDescending(job => job.Id)
            .Take(pageSize + 1)
            .Select(job => new
            {
                job.Id,
                job.InstanceId,
                job.WorkflowDefinitionId,
                job.WorkflowKey,
                job.TokenId,
                job.MultiInstanceExecutionId,
                job.UserTaskId,
                job.TimerSubscriptionId,
                job.ActivationId,
                job.NodeId,
                job.NodeName,
                job.NodeType,
                job.Kind,
                job.QueueClass,
                job.Phase,
                job.Status,
                job.Priority,
                job.AttemptCount,
                job.MaxAttempts,
                job.FailureHandling,
                job.RetryDelays,
                job.DueAt,
                job.ScheduledOccurrenceAt,
                job.SnapshotId,
                job.WorkerId,
                job.LeaseToken,
                job.LeaseGeneration,
                job.LeaseExpiresAt,
                job.HeartbeatAt,
                job.LastFailureCode,
                job.LastFailureDescription,
                job.ResultReadyAt,
                IncidentId = job.Incidents
                    .Where(incident => incident.Status == WorkflowIncidentStatuses.Open)
                    .Select(incident => (long?)incident.Id)
                    .FirstOrDefault(),
                job.CreatedAt,
                job.UpdatedAt,
                job.StartedAt,
                job.CompletedAt
            })
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > pageSize;
        if (rows.Count > pageSize)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var nextCursor = hasMore && rows.Count > 0
            ? WorkflowJobCursor.EncodeJob(rows[^1].UpdatedAt, rows[^1].Id)
            : null;
        return new PagedResult<WorkflowJobRecord>(
            rows.Select(row => new WorkflowJobRecord(
                row.Id,
                row.InstanceId,
                row.WorkflowDefinitionId,
                row.WorkflowKey,
                row.TokenId,
                row.MultiInstanceExecutionId,
                row.UserTaskId,
                row.TimerSubscriptionId,
                row.ActivationId,
                row.NodeId,
                row.NodeName,
                row.NodeType,
                row.Kind,
                row.QueueClass,
                row.Phase,
                row.Status,
                row.Priority,
                row.AttemptCount,
                row.MaxAttempts,
                row.FailureHandling,
                row.RetryDelays,
                row.DueAt,
                row.ScheduledOccurrenceAt,
                null,
                row.SnapshotId,
                row.WorkerId,
                row.LeaseToken,
                row.LeaseGeneration,
                row.LeaseExpiresAt,
                row.HeartbeatAt,
                null,
                null,
                row.LastFailureCode,
                row.LastFailureDescription,
                row.ResultReadyAt,
                row.IncidentId,
                row.CreatedAt,
                row.UpdatedAt,
                row.StartedAt,
                row.CompletedAt)).ToArray(),
            page,
            pageSize,
            total)
        {
            NextCursor = nextCursor
        };
    }

    public async Task<PagedResult<WorkflowJobAttemptRecord>> ListAttemptsAsync(
        long jobId,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        var source = dbContext.WorkflowJobAttempts
            .AsNoTracking()
            .Where(attempt => attempt.JobId == jobId);
        var total = await source.LongCountAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            _ = WorkflowJobCursor.TryDecodeAttempt(
                cursor,
                out var cursorAttempt,
                out var cursorId);
            source = source.Where(attempt =>
                attempt.AttemptNumber < cursorAttempt
                || attempt.AttemptNumber == cursorAttempt && attempt.Id < cursorId);
        }
        var entities = await source
            .OrderByDescending(attempt => attempt.AttemptNumber)
            .ThenByDescending(attempt => attempt.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);
        var hasMore = entities.Count > pageSize;
        if (hasMore)
        {
            entities.RemoveAt(entities.Count - 1);
        }
        var nextCursor = hasMore && entities.Count > 0
            ? WorkflowJobCursor.EncodeAttempt(
                entities[^1].AttemptNumber,
                entities[^1].Id)
            : null;
        return new PagedResult<WorkflowJobAttemptRecord>(
            entities.Select(MapAttempt).ToArray(),
            1,
            pageSize,
            total)
        {
            NextCursor = nextCursor
        };
    }

    public async Task<PagedResult<WorkflowIncidentRecord>> SearchIncidentsAsync(
        WorkflowIncidentQuery query,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var source = dbContext.WorkflowIncidents.AsNoTracking();
        if (query.InstanceId is not null)
        {
            source = source.Where(incident => incident.InstanceId == query.InstanceId);
        }
        if (query.WorkflowDefinitionId is not null)
        {
            source = source.Where(incident =>
                incident.WorkflowDefinitionId == query.WorkflowDefinitionId);
        }
        if (!string.IsNullOrWhiteSpace(query.WorkflowKey))
        {
            source = source.Where(incident => incident.WorkflowKey == query.WorkflowKey);
        }
        if (query.Statuses.Count > 0)
        {
            source = source.Where(incident => query.Statuses.Contains(incident.Status));
        }
        if (query.Types.Count > 0)
        {
            source = source.Where(incident => query.Types.Contains(incident.Type));
        }

        var total = await source.LongCountAsync(cancellationToken);
        var cursorPaging = !string.IsNullOrWhiteSpace(query.Cursor);
        if (cursorPaging)
        {
            _ = WorkflowJobCursor.TryDecodeIncident(
                query.Cursor,
                out var cursorUpdatedAt,
                out var cursorId);
            source = source.Where(incident =>
                incident.UpdatedAt < cursorUpdatedAt
                || incident.UpdatedAt == cursorUpdatedAt && incident.Id < cursorId);
        }
        var rows = await source
            .OrderByDescending(incident => incident.UpdatedAt)
            .ThenByDescending(incident => incident.Id)
            .Take(pageSize + 1)
            .Select(incident => new
            {
                incident.Id,
                JobId = incident.OriginalJobId,
                incident.InstanceId,
                incident.WorkflowDefinitionId,
                incident.WorkflowKey,
                incident.NodeId,
                incident.NodeName,
                incident.Type,
                incident.Status,
                incident.Summary,
                incident.ResolvedBy,
                incident.CreatedAt,
                incident.UpdatedAt,
                incident.ResolvedAt
            })
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > pageSize;
        if (rows.Count > pageSize)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var nextCursor = hasMore && rows.Count > 0
            ? WorkflowJobCursor.EncodeIncident(rows[^1].UpdatedAt, rows[^1].Id)
            : null;
        return new PagedResult<WorkflowIncidentRecord>(
            rows.Select(incident => new WorkflowIncidentRecord(
                incident.Id,
                incident.JobId,
                incident.InstanceId,
                incident.WorkflowDefinitionId,
                incident.WorkflowKey,
                incident.NodeId,
                incident.NodeName,
                incident.Type,
                incident.Status,
                incident.Summary,
                Details: null,
                incident.ResolvedBy,
                incident.CreatedAt,
                incident.UpdatedAt,
                incident.ResolvedAt)).ToArray(),
            page,
            pageSize,
            total)
        {
            NextCursor = nextCursor
        };
    }

    public async Task<WorkflowIncidentRecord?> GetIncidentAsync(
        long incidentId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowIncidents
            .AsNoTracking()
            .SingleOrDefaultAsync(incident => incident.Id == incidentId, cancellationToken);
        return entity is null ? null : MapIncident(entity);
    }

    public async Task<WorkflowJobRecord?> RetryIncidentAsync(
        long incidentId,
        string resolvedBy,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken)
    {
        var incidentLookup = await dbContext.WorkflowIncidents
            .AsNoTracking()
            .Where(incident => incident.Id == incidentId)
            .Select(incident => new
            {
                incident.Id,
                incident.JobId,
                incident.InstanceId,
                TokenId = incident.Job == null ? null : incident.Job.TokenId,
                ActivationId = incident.Job == null
                    ? (Guid?)null
                    : incident.Job.ActivationId
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (incidentLookup is null)
        {
            return null;
        }
        if (incidentLookup.JobId is null)
        {
            throw new WorkflowConflictException(
                "The incident is resolved and its retained job is no longer retryable.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Instance-owned retries follow the runtime lock order before touching
        // the job or a paused timer subscription. This prevents a retry from
        // racing host completion/cancellation and mutating timer state outside
        // the owning instance fence.
        WorkflowInstanceEntity? lockedInstance = null;
        if (incidentLookup.InstanceId is long instanceId)
        {
            lockedInstance = await dbContext.WorkflowInstances
                .FromSqlInterpolated(
                    $"""
                    SELECT *
                    FROM flowbit.workflow_instances
                    WHERE "Id" = {instanceId}
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The incident's workflow instance no longer exists.");
        }
        ExecutionTokenEntity? lockedToken = null;
        if (incidentLookup.TokenId is long tokenId)
        {
            lockedToken = await dbContext.ExecutionTokens
                .FromSqlInterpolated(
                    $"""
                    SELECT *
                    FROM flowbit.execution_tokens
                    WHERE "Id" = {tokenId}
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The incident's execution token no longer exists.");
        }
        var job = await dbContext.WorkflowJobs
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM flowbit.workflow_jobs
                WHERE "Id" = {incidentLookup.JobId.Value}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new WorkflowConflictException(
                "The incident's durable job no longer exists.");
        if (lockedInstance is not null
            && (lockedInstance.Status != WorkflowInstanceStatuses.Running
                || job.InstanceId != lockedInstance.Id)
            || lockedToken is not null
            && (lockedToken.InstanceId != job.InstanceId
                || lockedToken.Status != ExecutionTokenStatuses.Active
                || lockedToken.ActivationId != job.ActivationId
                || incidentLookup.ActivationId != job.ActivationId))
        {
            throw new WorkflowConflictException(
                "The incident's workflow activation is no longer current.");
        }
        var incident = await dbContext.WorkflowIncidents
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM flowbit.workflow_incidents
                WHERE "Id" = {incidentLookup.Id}
                FOR UPDATE
                """)
            .SingleAsync(cancellationToken);
        if (incident.Status != WorkflowIncidentStatuses.Open
            || job.Status != WorkflowJobStatuses.Incident)
        {
            throw new WorkflowConflictException(
                "The incident was already resolved or its job is no longer awaiting retry.");
        }

        TimerSubscriptionEntity? pausedSubscription = null;
        if (job.TimerSubscriptionId is long subscriptionId)
        {
            pausedSubscription = await dbContext.TimerSubscriptions
                .FromSqlInterpolated(
                    $"""
                    SELECT *
                    FROM flowbit.timer_subscriptions
                    WHERE "Id" = {subscriptionId}
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(cancellationToken);
            if (pausedSubscription is null)
            {
                throw new WorkflowConflictException(
                    "The incident's timer subscription no longer exists.");
            }
            if (pausedSubscription.Status == TimerSubscriptionStatuses.Paused)
            {
                pausedSubscription.Status = TimerSubscriptionStatuses.Active;
                pausedSubscription.UpdatedAt = dueAt;
            }
            else if (pausedSubscription.Status != TimerSubscriptionStatuses.Active)
            {
                throw new WorkflowConflictException(
                    "The incident's timer subscription is no longer active or resumable.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        incident.Status = WorkflowIncidentStatuses.Resolved;
        incident.ResolvedBy = Truncate(resolvedBy, 300) ?? "system";
        incident.ResolvedAt = now;
        incident.UpdatedAt = now;
        job.Status = WorkflowJobStatuses.Queued;
        job.MaxAttempts = Math.Max(job.MaxAttempts, checked(job.AttemptCount + 1));
        job.DueAt = dueAt;
        job.UpdatedAt = now;
        job.ResultReadyAt = null;
        job.ResultJson = null;
        job.ErrorJson = null;
        if (string.Equals(
                incident.Type,
                "output_version_conflict",
                StringComparison.Ordinal))
        {
            // The immutable snapshot contains the conflicting output row ids.
            // Manual retry explicitly requests a fresh stage/invocation under
            // the same stable job id; the orphaned snapshot is retained until
            // normal cleanup.
            job.SnapshotId = null;
        }
        job.LastFailureCode = null;
        job.LastFailureDescription = null;
        ClearLease(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_notify('flowbit_jobs', '')",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MapJob(job);
    }

    public async Task<WorkflowJobCleanupResult> CleanupAsync(
        DateTimeOffset completedJobsBefore,
        DateTimeOffset resolvedIncidentsBefore,
        int batchSize,
        CancellationToken cancellationToken)
    {
        batchSize = Math.Clamp(batchSize, 1, 1000);
        var elapsed = Stopwatch.StartNew();
        var jobsDeleted = 0;
        var attemptsDeleted = 0;
        var snapshotsDeleted = 0;
        var incidentsDeleted = 0;
        for (var batch = 0;
             batch < CleanupMaxBatchesPerRun && elapsed.Elapsed < CleanupTimeBudget;
             batch++)
        {
            var result = await CleanupBatchAsync(
                completedJobsBefore,
                resolvedIncidentsBefore,
                batchSize,
                cancellationToken);
            jobsDeleted += result.JobsDeleted;
            attemptsDeleted += result.AttemptsDeleted;
            snapshotsDeleted += result.SnapshotsDeleted;
            incidentsDeleted += result.IncidentsDeleted;
            if (result.JobsDeleted == 0
                && result.AttemptsDeleted == 0
                && result.SnapshotsDeleted == 0
                && result.IncidentsDeleted == 0)
            {
                break;
            }
        }

        return new WorkflowJobCleanupResult(
            jobsDeleted,
            attemptsDeleted,
            snapshotsDeleted,
            incidentsDeleted);
    }

    private async Task<WorkflowJobCleanupResult> CleanupBatchAsync(
        DateTimeOffset completedJobsBefore,
        DateTimeOffset resolvedIncidentsBefore,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var incidentIds = await dbContext.WorkflowIncidents
            .Where(incident =>
                incident.Status == WorkflowIncidentStatuses.Resolved
                && incident.ResolvedAt < resolvedIncidentsBefore)
            .OrderBy(incident => incident.ResolvedAt)
            .ThenBy(incident => incident.Id)
            .Select(incident => incident.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        var incidentsDeleted = incidentIds.Count == 0
            ? 0
            : await dbContext.WorkflowIncidents
                .Where(incident => incidentIds.Contains(incident.Id))
                .ExecuteDeleteAsync(cancellationToken);

        var jobIds = await dbContext.WorkflowJobs
            .Where(job =>
                (job.Status == WorkflowJobStatuses.Completed
                 || job.Status == WorkflowJobStatuses.Cancelled
                 || job.Status == WorkflowJobStatuses.Skipped)
                && job.CompletedAt < completedJobsBefore
                && job.WorkerId == null
                && job.LeaseToken == null
                && job.LeaseExpiresAt == null
                && !dbContext.WorkflowIncidents.Any(incident =>
                    incident.JobId == job.Id
                    && incident.Status == WorkflowIncidentStatuses.Open))
            .OrderBy(job => job.CompletedAt)
            .ThenBy(job => job.Id)
            .Select(job => job.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        var attemptsDeleted = jobIds.Count == 0
            ? 0
            : await dbContext.WorkflowJobAttempts
                .Where(attempt => jobIds.Contains(attempt.JobId))
                .CountAsync(cancellationToken);
        var jobsDeleted = jobIds.Count == 0
            ? 0
            : await dbContext.WorkflowJobs
                .Where(job => jobIds.Contains(job.Id))
                .ExecuteDeleteAsync(cancellationToken);

        var orphanSnapshotIds = await dbContext.WorkflowJobSnapshots
            .Where(snapshot => !dbContext.WorkflowJobs.Any(job => job.SnapshotId == snapshot.Id))
            .OrderBy(snapshot => snapshot.Id)
            .Select(snapshot => snapshot.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        var snapshotsDeleted = orphanSnapshotIds.Count == 0
            ? 0
            : await dbContext.WorkflowJobSnapshots
                .Where(snapshot => orphanSnapshotIds.Contains(snapshot.Id))
                .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new WorkflowJobCleanupResult(
            jobsDeleted,
            attemptsDeleted,
            snapshotsDeleted,
            incidentsDeleted);
    }

    private async Task SelectAndLeaseClassAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorkflowJobLeaseRequest request,
        string queueClass,
        int limit,
        List<LeasedIdentity> selected,
        HashSet<string> selectedFairnessKeys,
        CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return;
        }

        // Keep available work and expired leases in separate statements so each
        // query can use its small partial index. Result-ready recovery is first
        // because it never repeats the external invocation. Fairness is ranked
        // before the candidate limit: limiting an ordered frontier first lets a
        // single busy instance hide every other runnable instance indefinitely.
        var candidates = new List<Candidate>(limit * 3);
        string[] acquisitionQueries =
        [
            """
            WITH ranked AS MATERIALIZED
            (
                SELECT
                    j."Id",
                    j."InstanceId",
                    j."WorkflowKey",
                    j."LeaseExpiresAt",
                    row_number() OVER
                    (
                        PARTITION BY
                            (j."InstanceId" IS NULL),
                            COALESCE(j."InstanceId"::text, j."WorkflowKey")
                        ORDER BY j."LeaseExpiresAt", j."Id"
                    ) AS fairness_rank
                FROM flowbit.workflow_jobs AS j
                WHERE j."QueueClass" = @queue_class
                  AND j."Status" = 'resultReady'
                  AND j."LeaseExpiresAt" <= clock_timestamp()
                  AND
                  (
                      @enforce_instance_cap = FALSE
                      OR j."InstanceId" IS NULL
                      OR NOT EXISTS
                      (
                          SELECT 1
                          FROM flowbit.workflow_jobs AS active
                          WHERE active."InstanceId" = j."InstanceId"
                            AND active."QueueClass" = 'activity'
                            AND active."Status" IN ('running', 'resultReady')
                            AND active."LeaseExpiresAt" > clock_timestamp()
                          GROUP BY active."InstanceId"
                          HAVING count(*) >= @max_per_instance
                      )
                  )
            ),
            fair AS MATERIALIZED
            (
                SELECT "Id", "LeaseExpiresAt"
                FROM ranked
                WHERE fairness_rank = 1
                ORDER BY "LeaseExpiresAt", "Id"
                LIMIT @candidate_limit
            )
            SELECT
                j."Id",
                j."InstanceId",
                j."WorkflowKey",
                j."Status",
                j."AttemptCount",
                j."MaxAttempts"
            FROM fair AS f
            INNER JOIN flowbit.workflow_jobs AS j ON j."Id" = f."Id"
            ORDER BY f."LeaseExpiresAt", f."Id"
            FOR UPDATE OF j SKIP LOCKED
            """,
            """
            WITH ranked AS MATERIALIZED
            (
                SELECT
                    j."Id",
                    j."InstanceId",
                    j."WorkflowKey",
                    j."LeaseExpiresAt",
                    row_number() OVER
                    (
                        PARTITION BY
                            (j."InstanceId" IS NULL),
                            COALESCE(j."InstanceId"::text, j."WorkflowKey")
                        ORDER BY j."LeaseExpiresAt", j."Id"
                    ) AS fairness_rank
                FROM flowbit.workflow_jobs AS j
                WHERE j."QueueClass" = @queue_class
                  AND j."Status" = 'running'
                  AND j."LeaseExpiresAt" <= clock_timestamp()
                  AND j."AttemptCount" < j."MaxAttempts"
                  AND
                  (
                      @enforce_instance_cap = FALSE
                      OR j."InstanceId" IS NULL
                      OR NOT EXISTS
                      (
                          SELECT 1
                          FROM flowbit.workflow_jobs AS active
                          WHERE active."InstanceId" = j."InstanceId"
                            AND active."QueueClass" = 'activity'
                            AND active."Status" IN ('running', 'resultReady')
                            AND active."LeaseExpiresAt" > clock_timestamp()
                          GROUP BY active."InstanceId"
                          HAVING count(*) >= @max_per_instance
                      )
                  )
            ),
            fair AS MATERIALIZED
            (
                SELECT "Id", "LeaseExpiresAt"
                FROM ranked
                WHERE fairness_rank = 1
                ORDER BY "LeaseExpiresAt", "Id"
                LIMIT @candidate_limit
            )
            SELECT
                j."Id",
                j."InstanceId",
                j."WorkflowKey",
                j."Status",
                j."AttemptCount",
                j."MaxAttempts"
            FROM fair AS f
            INNER JOIN flowbit.workflow_jobs AS j ON j."Id" = f."Id"
            ORDER BY f."LeaseExpiresAt", f."Id"
            FOR UPDATE OF j SKIP LOCKED
            """,
            """
            WITH ranked AS MATERIALIZED
            (
                SELECT
                    j."Id",
                    j."InstanceId",
                    j."WorkflowKey",
                    j."Priority",
                    j."DueAt",
                    row_number() OVER
                    (
                        PARTITION BY
                            (j."InstanceId" IS NULL),
                            COALESCE(j."InstanceId"::text, j."WorkflowKey")
                        ORDER BY j."Priority" DESC, j."DueAt", j."Id"
                    ) AS fairness_rank
                FROM flowbit.workflow_jobs AS j
                WHERE j."QueueClass" = @queue_class
                  AND j."Status" IN ('queued', 'retry')
                  AND j."DueAt" <= clock_timestamp()
                  AND j."AttemptCount" < j."MaxAttempts"
                  AND
                  (
                      @enforce_instance_cap = FALSE
                      OR j."InstanceId" IS NULL
                      OR NOT EXISTS
                      (
                          SELECT 1
                          FROM flowbit.workflow_jobs AS active
                          WHERE active."InstanceId" = j."InstanceId"
                            AND active."QueueClass" = 'activity'
                            AND active."Status" IN ('running', 'resultReady')
                            AND active."LeaseExpiresAt" > clock_timestamp()
                          GROUP BY active."InstanceId"
                          HAVING count(*) >= @max_per_instance
                      )
                  )
            ),
            fair AS MATERIALIZED
            (
                SELECT "Id", "Priority", "DueAt"
                FROM ranked
                WHERE fairness_rank = 1
                ORDER BY "Priority" DESC, "DueAt", "Id"
                LIMIT @candidate_limit
            )
            SELECT
                j."Id",
                j."InstanceId",
                j."WorkflowKey",
                j."Status",
                j."AttemptCount",
                j."MaxAttempts"
            FROM fair AS f
            INNER JOIN flowbit.workflow_jobs AS j ON j."Id" = f."Id"
            ORDER BY f."Priority" DESC, f."DueAt", f."Id"
            FOR UPDATE OF j SKIP LOCKED
            """
        ];
        foreach (var sql in acquisitionQueries)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("queue_class", queueClass);
            // Each query may surface one row for a fairness key already selected
            // by an earlier status class. Oversample only by that bounded set.
            command.Parameters.AddWithValue(
                "candidate_limit",
                limit + selectedFairnessKeys.Count);
            command.Parameters.AddWithValue(
                "enforce_instance_cap",
                queueClass == WorkflowJobClasses.Activity);
            command.Parameters.AddWithValue("max_per_instance", request.MaxPerInstance);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(new Candidate(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5)));
            }
        }

        var distinctCandidates = candidates
            .Where(candidate => !selectedFairnessKeys.Contains(candidate.FairnessKey))
            .GroupBy(candidate => candidate.FairnessKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(limit)
            .ToList();
        var instanceIds = queueClass == WorkflowJobClasses.Activity
            ? distinctCandidates
            .Where(candidate => candidate.InstanceId is not null)
            .Select(candidate => candidate.InstanceId!.Value)
            .Distinct()
            .Order()
            .ToArray()
            : [];

        foreach (var instanceId in instanceIds)
        {
            await using var advisory = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(@instance_id)",
                connection,
                transaction);
            advisory.Parameters.AddWithValue("instance_id", instanceId);
            await advisory.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var candidate in distinctCandidates)
        {
            if (queueClass == WorkflowJobClasses.Activity
                && candidate.InstanceId is not null)
            {
                await using var countCommand = new NpgsqlCommand(
                    """
                    SELECT count(*)
                    FROM flowbit.workflow_jobs
                    WHERE "InstanceId" = @instance_id
                      AND "QueueClass" = 'activity'
                      AND "Status" IN ('running', 'resultReady')
                      AND "LeaseExpiresAt" > clock_timestamp()
                    """,
                    connection,
                    transaction);
                countCommand.Parameters.AddWithValue("instance_id", candidate.InstanceId.Value);
                var active = Convert.ToInt32(
                    await countCommand.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (active >= request.MaxPerInstance)
                {
                    continue;
                }
            }

            if (candidate.Status == WorkflowJobStatuses.Running)
            {
                await using var expire = new NpgsqlCommand(
                    """
                    UPDATE flowbit.workflow_job_attempts
                    SET "Status" = 'leaseLost',
                        "FinishedAt" = clock_timestamp(),
                        "FailureCode" = 'leaseExpired',
                        "FailureDescription" = 'Worker lease expired before the attempt finalized.'
                    WHERE "JobId" = @job_id
                      AND "AttemptNumber" = @attempt_number
                      AND "Status" IN ('running', 'resultReady')
                    """,
                    connection,
                    transaction);
                expire.Parameters.AddWithValue("job_id", candidate.JobId);
                expire.Parameters.AddWithValue("attempt_number", candidate.AttemptCount);
                await expire.ExecuteNonQueryAsync(cancellationToken);
            }

            var leaseToken = Guid.NewGuid();
            var lease = await LeaseOneAsync(
                connection,
                transaction,
                candidate,
                request,
                leaseToken,
                cancellationToken);
            if (lease is null)
            {
                continue;
            }

            selected.Add(new LeasedIdentity(
                candidate.JobId,
                leaseToken,
                lease.LeaseGeneration,
                lease.AttemptNumber));
            selectedFairnessKeys.Add(candidate.FairnessKey);
            if (selected.Count >= request.MaxCount)
            {
                break;
            }
        }
    }

    private static async Task<LeaseOutcome?> LeaseOneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Candidate candidate,
        WorkflowJobLeaseRequest request,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE flowbit.workflow_jobs
            SET "Status" = CASE WHEN "Status" = 'resultReady' THEN 'resultReady' ELSE 'running' END,
                "AttemptCount" = CASE
                    WHEN "Status" = 'resultReady' THEN "AttemptCount"
                    ELSE "AttemptCount" + 1
                END,
                "WorkerId" = @worker_id,
                "LeaseToken" = @lease_token,
                "LeaseGeneration" = "LeaseGeneration" + 1,
                "LeaseExpiresAt" = clock_timestamp() + @lease_duration,
                "HeartbeatAt" = clock_timestamp(),
                "StartedAt" = COALESCE("StartedAt", clock_timestamp()),
                "UpdatedAt" = clock_timestamp()
            WHERE "Id" = @job_id
              AND (
                    ("Status" IN ('queued', 'retry') AND "AttemptCount" < "MaxAttempts")
                 OR ("Status" = 'running' AND "LeaseExpiresAt" <= clock_timestamp()
                     AND "AttemptCount" < "MaxAttempts")
                 OR ("Status" = 'resultReady' AND "LeaseExpiresAt" <= clock_timestamp())
              )
            RETURNING "LeaseGeneration", "Status", "AttemptCount"
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("worker_id", request.WorkerId);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("lease_duration", request.LeaseDuration);
        command.Parameters.AddWithValue("job_id", candidate.JobId);

        long generation;
        string status;
        int attemptNumber;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            generation = reader.GetInt64(0);
            status = reader.GetString(1);
            attemptNumber = reader.GetInt32(2);
        }

        var isResultRecovery = candidate.Status == WorkflowJobStatuses.ResultReady;
        await using var attempt = new NpgsqlCommand(
            isResultRecovery
                ? """
                  UPDATE flowbit.workflow_job_attempts
                  SET "Status" = 'resultReady',
                      "WorkerId" = @worker_id,
                      "LeaseGeneration" = @lease_generation
                  WHERE "JobId" = @job_id
                    AND "AttemptNumber" = @attempt_number
                  """
                : """
                  INSERT INTO flowbit.workflow_job_attempts
                      ("JobId", "AttemptNumber", "Status", "WorkerId", "LeaseGeneration", "StartedAt")
                  VALUES
                      (@job_id, @attempt_number, @status, @worker_id, @lease_generation, clock_timestamp())
                  """,
            connection,
            transaction);
        attempt.Parameters.AddWithValue("job_id", candidate.JobId);
        attempt.Parameters.AddWithValue("attempt_number", attemptNumber);
        attempt.Parameters.AddWithValue(
            "status",
            status == WorkflowJobStatuses.ResultReady
                ? WorkflowJobAttemptStatuses.ResultReady
                : WorkflowJobAttemptStatuses.Running);
        attempt.Parameters.AddWithValue("worker_id", request.WorkerId);
        attempt.Parameters.AddWithValue("lease_generation", generation);
        await attempt.ExecuteNonQueryAsync(cancellationToken);
        return new LeaseOutcome(generation, attemptNumber);
    }

    private async Task<WorkflowJobEntity?> FindFencedTrackedAsync(
        WorkflowJobFence fence,
        string status,
        CancellationToken cancellationToken) =>
        await FindFencedTrackedAsync(fence, [status], cancellationToken);

    private async Task<WorkflowJobEntity?> FindFencedTrackedAsync(
        WorkflowJobFence fence,
        IReadOnlyCollection<string> statuses,
        CancellationToken cancellationToken)
    {
        var job = await dbContext.WorkflowJobs
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM flowbit.workflow_jobs
                WHERE "Id" = {fence.JobId}
                  AND "WorkerId" = {fence.WorkerId}
                  AND "LeaseToken" = {fence.LeaseToken}
                  AND "LeaseGeneration" = {fence.LeaseGeneration}
                  AND "LeaseExpiresAt" > clock_timestamp()
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        return job is not null && statuses.Contains(job.Status) ? job : null;
    }

    private Task<WorkflowJobAttemptEntity?> GetCurrentAttemptAsync(
        WorkflowJobEntity job,
        CancellationToken cancellationToken) =>
        dbContext.WorkflowJobAttempts.SingleOrDefaultAsync(
            attempt =>
                attempt.JobId == job.Id
                && attempt.AttemptNumber == job.AttemptCount
                && attempt.LeaseGeneration == job.LeaseGeneration,
            cancellationToken);

    private async Task<int> CancelAsync(
        IQueryable<WorkflowJobEntity> source,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var boundedReason = Truncate(reason, FailureDescriptionLimit);
        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        // Fence the complete affected job set in one database update. Any
        // concurrent finalizer then fails its status/generation fence before it
        // can write workflow state; no job ids are enumerated in application
        // memory, so cancellation cost is bounded by set-based statements.
        var affected = await source
            .Where(job =>
                job.Status == WorkflowJobStatuses.Queued
                || job.Status == WorkflowJobStatuses.Retry
                || job.Status == WorkflowJobStatuses.Running
                || job.Status == WorkflowJobStatuses.ResultReady
                || job.Status == WorkflowJobStatuses.Incident)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, WorkflowJobStatuses.Cancelled)
                .SetProperty(job => job.CompletedAt, now)
                .SetProperty(job => job.UpdatedAt, now)
                .SetProperty(job => job.LastFailureCode, "cancelled")
                .SetProperty(job => job.LastFailureDescription, boundedReason)
                .SetProperty(job => job.WorkerId, (string?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(job => job.HeartbeatAt, (DateTimeOffset?)null),
                cancellationToken);
        if (affected == 0)
        {
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return 0;
        }

        var scopedJobIds = source.Select(job => job.Id);
        await dbContext.WorkflowJobAttempts
            .Where(attempt =>
                scopedJobIds.Contains(attempt.JobId)
                && (attempt.Status == WorkflowJobAttemptStatuses.Running
                    || attempt.Status == WorkflowJobAttemptStatuses.ResultReady))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(attempt => attempt.Status, WorkflowJobAttemptStatuses.Cancelled)
                .SetProperty(attempt => attempt.FinishedAt, now)
                .SetProperty(attempt => attempt.FailureCode, "cancelled")
                .SetProperty(attempt => attempt.FailureDescription, boundedReason),
                cancellationToken);
        await dbContext.WorkflowIncidents
            .Where(incident =>
                incident.JobId != null
                && scopedJobIds.Contains(incident.JobId.Value)
                && incident.Status == WorkflowIncidentStatuses.Open)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(incident => incident.Status, WorkflowIncidentStatuses.Resolved)
                .SetProperty(incident => incident.ResolvedAt, now)
                .SetProperty(incident => incident.ResolvedBy, "system:cancellation")
                .SetProperty(incident => incident.UpdatedAt, now),
                cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return affected;
    }

    private static void ClearLease(WorkflowJobEntity job)
    {
        job.WorkerId = null;
        job.LeaseToken = null;
        job.LeaseExpiresAt = null;
        job.HeartbeatAt = null;
    }

    private static WorkflowJobRecord MapJob(WorkflowJobEntity entity) =>
        new(
            entity.Id,
            entity.InstanceId,
            entity.WorkflowDefinitionId,
            entity.WorkflowKey,
            entity.TokenId,
            entity.MultiInstanceExecutionId,
            entity.UserTaskId,
            entity.TimerSubscriptionId,
            entity.ActivationId,
            entity.NodeId,
            entity.NodeName,
            entity.NodeType,
            entity.Kind,
            entity.QueueClass,
            entity.Phase,
            entity.Status,
            entity.Priority,
            entity.AttemptCount,
            entity.MaxAttempts,
            entity.FailureHandling,
            entity.RetryDelays,
            entity.DueAt,
            entity.ScheduledOccurrenceAt,
            CloneElement(entity.PayloadJson),
            entity.SnapshotId,
            entity.WorkerId,
            entity.LeaseToken,
            entity.LeaseGeneration,
            entity.LeaseExpiresAt,
            entity.HeartbeatAt,
            CloneElement(entity.ResultJson),
            CloneElement(entity.ErrorJson),
            entity.LastFailureCode,
            entity.LastFailureDescription,
            entity.ResultReadyAt,
            entity.Incidents.SingleOrDefault(incident =>
                incident.Status == WorkflowIncidentStatuses.Open)?.Id,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.StartedAt,
            entity.CompletedAt);

    private static WorkflowJobSnapshotRecord MapSnapshot(WorkflowJobSnapshotEntity entity) =>
        new(
            entity.Id,
            entity.Kind,
            CloneElement(entity.InvocationJson),
            ToDictionary(entity.VariablesJson),
            ToLongDictionary(entity.OutputVariableVersionsJson),
            CloneElement(entity.FlowInfoJson),
            entity.EvaluationTime,
            entity.SizeBytes,
            entity.CreatedAt);

    private static WorkflowJobAttemptRecord MapAttempt(WorkflowJobAttemptEntity entity) =>
        new(
            entity.Id,
            entity.JobId,
            entity.AttemptNumber,
            entity.Status,
            entity.WorkerId,
            entity.LeaseGeneration,
            entity.StartedAt,
            entity.FinishedAt,
            entity.FailureCode,
            entity.FailureDescription);

    private static WorkflowIncidentRecord MapIncident(WorkflowIncidentEntity entity) =>
        new(
            entity.Id,
            entity.OriginalJobId,
            entity.InstanceId,
            entity.WorkflowDefinitionId,
            entity.WorkflowKey,
            entity.NodeId,
            entity.NodeName,
            entity.Type,
            entity.Status,
            entity.Summary,
            entity.Details,
            entity.ResolvedBy,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.ResolvedAt);

    private static JsonDocument? CloneDocument(JsonElement? element) =>
        element is null ? null : JsonDocument.Parse(element.Value.GetRawText());

    private static JsonDocument? CloneDocument(JsonDocument? document) =>
        document is null ? null : JsonDocument.Parse(document.RootElement.GetRawText());

    private static JsonElement? CloneElement(JsonDocument? document) =>
        document?.RootElement.Clone();

    private static JsonDocument ToDocument<T>(T value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value));

    private static IReadOnlyDictionary<string, JsonElement> ToDictionary(JsonDocument document) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            document.RootElement.GetRawText())?
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase)
        ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, long> ToLongDictionary(JsonDocument document) =>
        JsonSerializer.Deserialize<Dictionary<string, long>>(
            document.RootElement.GetRawText())?
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
        ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    private static int Utf8Size(JsonDocument? document) =>
        document is null ? 0 : Encoding.UTF8.GetByteCount(document.RootElement.GetRawText());

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value)
            ? value
            : value.Length <= maxLength
                ? value
                : value[..maxLength];

    private sealed record Candidate(
        long JobId,
        long? InstanceId,
        string WorkflowKey,
        string Status,
        int AttemptCount,
        int MaxAttempts)
    {
        public string FairnessKey => InstanceId is long instanceId
            ? $"instance:{instanceId}"
            : $"workflow:{WorkflowKey}";
    }

    private sealed record LeasedIdentity(
        long JobId,
        Guid LeaseToken,
        long LeaseGeneration,
        int AttemptNumber);

    private sealed record LeaseOutcome(long LeaseGeneration, int AttemptNumber);
}
