namespace Flowbit.Worker;

public sealed class WorkerOptions
{
    public const string SectionName = "FlowbitWorker";

    public int MaxConcurrency { get; set; } = 8;
    public int ActivityConcurrency { get; set; } = 6;
    public int BatchSize { get; set; } = 32;
    public int MaxPerInstance { get; set; } = 4;
    public int LeaseSeconds { get; set; } = 60;
    public int HeartbeatSeconds { get; set; } = 20;
    public int LeaseCheckMilliseconds { get; set; } = 1000;
    public int HeartbeatCommandTimeoutMilliseconds { get; set; } = 2000;
    public int PollMilliseconds { get; set; } = 1000;
    public int IdleBackoffMilliseconds { get; set; } = 5000;
    public int CleanupBatchSize { get; set; } = 1000;
    public int CompletedJobRetentionDays { get; set; } = 30;
    public int ResolvedIncidentRetentionDays { get; set; } = 90;
    public int TimerStartReconcileSeconds { get; set; } = 1;
    public int TimerStartReconcileBatchSize { get; set; } = 100;
    public int ShutdownDrainSeconds { get; set; } = 30;
    public string HealthListenUrl { get; set; } = "http://0.0.0.0:8081";

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseSeconds);
    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(HeartbeatSeconds);
    public TimeSpan LeaseCheckInterval => TimeSpan.FromMilliseconds(LeaseCheckMilliseconds);
    public TimeSpan HeartbeatCommandTimeout =>
        TimeSpan.FromMilliseconds(HeartbeatCommandTimeoutMilliseconds);

    public void Validate()
    {
        if (MaxConcurrency is < 1 or > 1024)
            throw new InvalidOperationException($"{SectionName}:MaxConcurrency must be between 1 and 1024.");
        if (ActivityConcurrency < 0 || ActivityConcurrency > MaxConcurrency)
            throw new InvalidOperationException($"{SectionName}:ActivityConcurrency must be between 0 and MaxConcurrency.");
        if (BatchSize is < 1 or > 1000)
            throw new InvalidOperationException($"{SectionName}:BatchSize must be between 1 and 1000.");
        if (MaxPerInstance is < 1 or > 1000)
            throw new InvalidOperationException($"{SectionName}:MaxPerInstance must be between 1 and 1000.");
        if (LeaseSeconds < 15)
            throw new InvalidOperationException($"{SectionName}:LeaseSeconds must be at least 15.");
        if (HeartbeatSeconds < 1 || HeartbeatSeconds * 2 >= LeaseSeconds)
            throw new InvalidOperationException($"{SectionName}:HeartbeatSeconds must be less than half LeaseSeconds.");
        if (LeaseCheckMilliseconds is < 100 or > 60_000)
            throw new InvalidOperationException($"{SectionName}:LeaseCheckMilliseconds must be between 100 and 60000.");
        if (HeartbeatCommandTimeoutMilliseconds is < 100 or > 30_000)
            throw new InvalidOperationException($"{SectionName}:HeartbeatCommandTimeoutMilliseconds must be between 100 and 30000.");
        if (HeartbeatCommandTimeout >= LeaseDuration - HeartbeatInterval)
            throw new InvalidOperationException($"{SectionName}:HeartbeatCommandTimeoutMilliseconds must leave time to retry before the lease expires.");
        if (PollMilliseconds is < 50 or > 60_000)
            throw new InvalidOperationException($"{SectionName}:PollMilliseconds must be between 50 and 60000.");
        if (IdleBackoffMilliseconds < PollMilliseconds || IdleBackoffMilliseconds > 300_000)
            throw new InvalidOperationException($"{SectionName}:IdleBackoffMilliseconds must be between PollMilliseconds and 300000.");
        if (CleanupBatchSize is < 1 or > 1000)
            throw new InvalidOperationException($"{SectionName}:CleanupBatchSize must be between 1 and 1000.");
        if (CompletedJobRetentionDays < 1 || ResolvedIncidentRetentionDays < 1)
            throw new InvalidOperationException($"{SectionName}: retention days must be positive.");
        if (TimerStartReconcileSeconds is < 1 or > 3600)
            throw new InvalidOperationException($"{SectionName}:TimerStartReconcileSeconds must be between 1 and 3600.");
        if (TimerStartReconcileBatchSize is < 1 or > 1000)
            throw new InvalidOperationException($"{SectionName}:TimerStartReconcileBatchSize must be between 1 and 1000.");
        if (ShutdownDrainSeconds is < 1 or > 300)
            throw new InvalidOperationException($"{SectionName}:ShutdownDrainSeconds must be between 1 and 300.");
        if (!Uri.TryCreate(HealthListenUrl, UriKind.Absolute, out var healthUri)
            || (healthUri.Scheme != Uri.UriSchemeHttp
                && healthUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{SectionName}:HealthListenUrl must be an absolute HTTP or HTTPS URL.");
        }
    }
}
