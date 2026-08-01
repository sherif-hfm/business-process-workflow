using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Flowbit.Service.Models;

namespace Flowbit.Worker;

public sealed class WorkerTelemetry : IDisposable
{
    public const string MeterName = "Flowbit.Worker";

    private readonly Meter _meter = new(MeterName);
    private readonly MeterListener _runtimeListener = new();
    private readonly Counter<long> _acquired;
    private readonly Counter<long> _processed;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _leaseLost;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _cleanupJobs;
    private readonly Counter<long> _cleanupAttempts;
    private readonly Counter<long> _cleanupSnapshots;
    private readonly Counter<long> _cleanupIncidents;
    private readonly Counter<long> _timerStarts;
    private readonly Histogram<double> _acquisitionLatency;
    private readonly Histogram<double> _processingDuration;
    private readonly Histogram<double> _queueLag;
    private readonly Histogram<double> _timerLateness;
    private int _active;
    private int _activeActivities;
    private int _ready;
    private long _runnableDepth;
    private long _openIncidents;
    private double _oldestRunnableAgeMilliseconds;
    private long _acquiredTotal;
    private long _processedTotal;
    private long _failedTotal;
    private long _leaseLostTotal;
    private long _retriesTotal;
    private long _cleanupJobsTotal;
    private long _cleanupAttemptsTotal;
    private long _cleanupSnapshotsTotal;
    private long _cleanupIncidentsTotal;
    private long _timerStartsTotal;
    private long _acquisitionSamples;
    private long _acquisitionMicroseconds;
    private long _processingSamples;
    private long _processingMicroseconds;
    private long _queueLagSamples;
    private long _queueLagMicroseconds;
    private long _timerLatenessSamples;
    private long _timerLatenessMicroseconds;
    private long _runtimeRetriesTotal;
    private long _runtimeConflictsTotal;
    private long _runtimeIncidentsTotal;
    private long _runtimeAutomaticLoopLimitsTotal;
    private long _instanceLockWaitSamples;
    private long _instanceLockWaitMicroseconds;

    public WorkerTelemetry()
    {
        _runtimeListener.InstrumentPublished = static (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Flowbit.Runtime.Jobs")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _runtimeListener.SetMeasurementEventCallback<long>(OnRuntimeLongMeasurement);
        _runtimeListener.SetMeasurementEventCallback<double>(OnRuntimeDoubleMeasurement);
        _runtimeListener.Start();

        _acquired = _meter.CreateCounter<long>(
            "flowbit.worker.jobs.acquired",
            description: "Jobs leased by this worker replica.");
        _processed = _meter.CreateCounter<long>(
            "flowbit.worker.jobs.processed",
            description: "Leased jobs whose processor returned normally.");
        _failed = _meter.CreateCounter<long>(
            "flowbit.worker.jobs.failed",
            description: "Leased jobs whose processor escaped an exception.");
        _leaseLost = _meter.CreateCounter<long>(
            "flowbit.worker.leases.lost",
            description: "Fenced leases rejected by heartbeat.");
        _retries = _meter.CreateCounter<long>(
            "flowbit.worker.retries.scheduled",
            description: "Unhandled worker failures scheduled for retry.");
        _cleanupJobs = _meter.CreateCounter<long>(
            "flowbit.worker.cleanup.jobs",
            description: "Terminal jobs removed by retention cleanup.");
        _cleanupAttempts = _meter.CreateCounter<long>(
            "flowbit.worker.cleanup.attempts",
            description: "Job attempts removed by retention cleanup.");
        _cleanupSnapshots = _meter.CreateCounter<long>(
            "flowbit.worker.cleanup.snapshots",
            description: "Immutable job snapshots removed by retention cleanup.");
        _cleanupIncidents = _meter.CreateCounter<long>(
            "flowbit.worker.cleanup.incidents",
            description: "Resolved incidents removed by retention cleanup.");
        _timerStarts = _meter.CreateCounter<long>(
            "flowbit.worker.timer_start.subscriptions",
            description: "Timer-start subscriptions created or repaired.");
        _acquisitionLatency = _meter.CreateHistogram<double>(
            "flowbit.worker.acquisition.duration",
            "ms",
            "Job lease acquisition latency.");
        _processingDuration = _meter.CreateHistogram<double>(
            "flowbit.worker.processing.duration",
            "ms",
            "End-to-end processing duration for a leased job.");
        _queueLag = _meter.CreateHistogram<double>(
            "flowbit.worker.queue.lag",
            "ms",
            "Elapsed time from a job's due time until lease acquisition.");
        _timerLateness = _meter.CreateHistogram<double>(
            "flowbit.worker.timer.lateness",
            "ms",
            "Elapsed time from a timer occurrence until lease acquisition.");
        _meter.CreateObservableGauge(
            "flowbit.worker.jobs.active",
            () => Volatile.Read(ref _active),
            description: "Jobs currently executing in this worker replica.");
        _meter.CreateObservableGauge(
            "flowbit.worker.jobs.activity.active",
            () => Volatile.Read(ref _activeActivities),
            description: "External activity jobs currently executing in this worker replica.");
        _meter.CreateObservableGauge(
            "flowbit.worker.ready",
            () => Volatile.Read(ref _ready),
            description: "One after this worker has successfully queried the durable queue.");
        _meter.CreateObservableGauge(
            "flowbit.worker.queue.runnable.depth",
            () => Interlocked.Read(ref _runnableDepth),
            description: "Current database-observed runnable job depth.");
        _meter.CreateObservableGauge(
            "flowbit.worker.queue.oldest.age",
            () => Volatile.Read(ref _oldestRunnableAgeMilliseconds),
            "ms",
            "Current age of the oldest runnable job.");
        _meter.CreateObservableGauge(
            "flowbit.worker.incidents.open",
            () => Interlocked.Read(ref _openIncidents),
            description: "Current database-observed open incident count.");
    }

    public void RecordAcquisition(
        IReadOnlyList<WorkflowJobLeaseRecord> leases,
        TimeSpan elapsed,
        DateTimeOffset now)
    {
        _acquisitionLatency.Record(elapsed.TotalMilliseconds);
        RecordSample(
            ref _acquisitionSamples,
            ref _acquisitionMicroseconds,
            elapsed.TotalMilliseconds);
        Volatile.Write(ref _ready, 1);
        if (leases.Count == 0)
        {
            return;
        }

        _acquired.Add(leases.Count);
        Interlocked.Add(ref _acquiredTotal, leases.Count);
        foreach (var lease in leases)
        {
            var queueLag = Math.Max(0, (now - lease.Job.DueAt).TotalMilliseconds);
            _queueLag.Record(queueLag);
            RecordSample(ref _queueLagSamples, ref _queueLagMicroseconds, queueLag);
            if (lease.Job.ScheduledOccurrenceAt is DateTimeOffset occurrence)
            {
                var lateness = Math.Max(0, (now - occurrence).TotalMilliseconds);
                _timerLateness.Record(lateness);
                RecordSample(
                    ref _timerLatenessSamples,
                    ref _timerLatenessMicroseconds,
                    lateness);
            }
        }
    }

    public void JobStarted(bool activity)
    {
        Interlocked.Increment(ref _active);
        if (activity)
        {
            Interlocked.Increment(ref _activeActivities);
        }
    }

    public void JobFinished(bool activity, bool succeeded, TimeSpan elapsed)
    {
        Interlocked.Decrement(ref _active);
        if (activity)
        {
            Interlocked.Decrement(ref _activeActivities);
        }
        _processingDuration.Record(elapsed.TotalMilliseconds);
        RecordSample(
            ref _processingSamples,
            ref _processingMicroseconds,
            elapsed.TotalMilliseconds);
        if (succeeded)
        {
            _processed.Add(1);
            Interlocked.Increment(ref _processedTotal);
        }
        else
        {
            _failed.Add(1);
            Interlocked.Increment(ref _failedTotal);
        }
    }

    public void RecordLeaseLost()
    {
        _leaseLost.Add(1);
        Interlocked.Increment(ref _leaseLostTotal);
    }

    public void RecordRetry()
    {
        _retries.Add(1);
        Interlocked.Increment(ref _retriesTotal);
    }

    public void RecordCleanup(
        int jobs,
        int incidents,
        int attempts = 0,
        int snapshots = 0)
    {
        if (jobs > 0)
        {
            _cleanupJobs.Add(jobs);
            Interlocked.Add(ref _cleanupJobsTotal, jobs);
        }
        if (incidents > 0)
        {
            _cleanupIncidents.Add(incidents);
            Interlocked.Add(ref _cleanupIncidentsTotal, incidents);
        }
        if (attempts > 0)
        {
            _cleanupAttempts.Add(attempts);
            Interlocked.Add(ref _cleanupAttemptsTotal, attempts);
        }
        if (snapshots > 0)
        {
            _cleanupSnapshots.Add(snapshots);
            Interlocked.Add(ref _cleanupSnapshotsTotal, snapshots);
        }
    }

    public void RecordQueueSnapshot(WorkflowJobQueueStatisticsRecord statistics)
    {
        Interlocked.Exchange(ref _runnableDepth, statistics.RunnableDepth);
        Interlocked.Exchange(ref _openIncidents, statistics.OpenIncidentCount);
        Volatile.Write(
            ref _oldestRunnableAgeMilliseconds,
            statistics.OldestRunnableDueAt is { } dueAt
                ? Math.Max(0, (statistics.ObservedAt - dueAt).TotalMilliseconds)
                : 0);
    }

    public void RecordTimerStart()
    {
        _timerStarts.Add(1);
        Interlocked.Increment(ref _timerStartsTotal);
    }

    public bool IsReady => Volatile.Read(ref _ready) == 1;

    public string ExportPrometheus()
    {
        var output = new StringBuilder(2048);
        WriteGauge(output, "flowbit_worker_ready", Volatile.Read(ref _ready));
        WriteGauge(output, "flowbit_worker_jobs_active", Volatile.Read(ref _active));
        WriteGauge(
            output,
            "flowbit_worker_jobs_activity_active",
            Volatile.Read(ref _activeActivities));
        WriteGauge(
            output,
            "flowbit_worker_queue_runnable_depth",
            Interlocked.Read(ref _runnableDepth));
        WriteGauge(
            output,
            "flowbit_worker_queue_oldest_age_milliseconds",
            Volatile.Read(ref _oldestRunnableAgeMilliseconds));
        WriteGauge(
            output,
            "flowbit_worker_incidents_open",
            Interlocked.Read(ref _openIncidents));

        WriteCounter(output, "flowbit_worker_jobs_acquired_total", _acquiredTotal);
        WriteCounter(output, "flowbit_worker_jobs_processed_total", _processedTotal);
        WriteCounter(output, "flowbit_worker_jobs_failed_total", _failedTotal);
        WriteCounter(output, "flowbit_worker_leases_lost_total", _leaseLostTotal);
        WriteCounter(output, "flowbit_worker_retries_scheduled_total", _retriesTotal);
        WriteCounter(output, "flowbit_worker_cleanup_jobs_total", _cleanupJobsTotal);
        WriteCounter(output, "flowbit_worker_cleanup_attempts_total", _cleanupAttemptsTotal);
        WriteCounter(output, "flowbit_worker_cleanup_snapshots_total", _cleanupSnapshotsTotal);
        WriteCounter(output, "flowbit_worker_cleanup_incidents_total", _cleanupIncidentsTotal);
        WriteCounter(output, "flowbit_worker_timer_start_subscriptions_total", _timerStartsTotal);
        WriteCounter(output, "flowbit_jobs_retries_total", _runtimeRetriesTotal);
        WriteCounter(output, "flowbit_jobs_output_conflicts_total", _runtimeConflictsTotal);
        WriteCounter(output, "flowbit_jobs_incidents_opened_total", _runtimeIncidentsTotal);
        WriteCounter(
            output,
            "flowbit_jobs_automatic_loop_limit_total",
            _runtimeAutomaticLoopLimitsTotal);

        WriteSummary(
            output,
            "flowbit_worker_acquisition_duration_seconds",
            _acquisitionSamples,
            _acquisitionMicroseconds);
        WriteSummary(
            output,
            "flowbit_worker_processing_duration_seconds",
            _processingSamples,
            _processingMicroseconds);
        WriteSummary(
            output,
            "flowbit_worker_queue_lag_seconds",
            _queueLagSamples,
            _queueLagMicroseconds);
        WriteSummary(
            output,
            "flowbit_worker_timer_lateness_seconds",
            _timerLatenessSamples,
            _timerLatenessMicroseconds);
        WriteSummary(
            output,
            "flowbit_jobs_instance_lock_wait_seconds",
            _instanceLockWaitSamples,
            _instanceLockWaitMicroseconds);
        return output.ToString();
    }

    private void OnRuntimeLongMeasurement(
        Instrument instrument,
        long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        switch (instrument.Name)
        {
            case "flowbit.jobs.retries":
                Interlocked.Add(ref _runtimeRetriesTotal, measurement);
                break;
            case "flowbit.jobs.output_conflicts":
                Interlocked.Add(ref _runtimeConflictsTotal, measurement);
                break;
            case "flowbit.jobs.incidents.opened":
                Interlocked.Add(ref _runtimeIncidentsTotal, measurement);
                break;
            case "flowbit.jobs.automatic_loop_limit":
                Interlocked.Add(ref _runtimeAutomaticLoopLimitsTotal, measurement);
                break;
        }
    }

    private void OnRuntimeDoubleMeasurement(
        Instrument instrument,
        double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        if (instrument.Name == "flowbit.jobs.instance_lock.wait")
        {
            RecordSample(
                ref _instanceLockWaitSamples,
                ref _instanceLockWaitMicroseconds,
                measurement);
        }
    }

    private static void RecordSample(
        ref long count,
        ref long microseconds,
        double milliseconds)
    {
        Interlocked.Increment(ref count);
        Interlocked.Add(
            ref microseconds,
            Math.Max(0, checked((long)Math.Round(milliseconds * 1000d))));
    }

    private static void WriteGauge(StringBuilder output, string name, long value)
    {
        output.Append("# TYPE ").Append(name).AppendLine(" gauge");
        output.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).AppendLine();
    }

    private static void WriteGauge(StringBuilder output, string name, double value)
    {
        output.Append("# TYPE ").Append(name).AppendLine(" gauge");
        output.Append(name).Append(' ').Append(value.ToString("R", CultureInfo.InvariantCulture)).AppendLine();
    }

    private static void WriteCounter(StringBuilder output, string name, long value)
    {
        output.Append("# TYPE ").Append(name).AppendLine(" counter");
        output.Append(name).Append(' ')
            .Append(Interlocked.Read(ref value).ToString(CultureInfo.InvariantCulture))
            .AppendLine();
    }

    private static void WriteSummary(
        StringBuilder output,
        string name,
        long count,
        long microseconds)
    {
        output.Append("# TYPE ").Append(name).AppendLine(" summary");
        output.Append(name).Append("_count ")
            .Append(Interlocked.Read(ref count).ToString(CultureInfo.InvariantCulture))
            .AppendLine();
        output.Append(name).Append("_sum ")
            .Append((Interlocked.Read(ref microseconds) / 1_000_000d)
                .ToString("R", CultureInfo.InvariantCulture))
            .AppendLine();
    }

    public void Dispose()
    {
        _runtimeListener.Dispose();
        _meter.Dispose();
    }
}
