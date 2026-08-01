using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Flowbit.Service.Services;

/// <summary>
/// Process-wide runtime instruments shared by API and worker hosts. Export is
/// intentionally host-configurable through the standard .NET Meter listener.
/// </summary>
internal static class WorkflowJobRuntimeTelemetry
{
    public const string MeterName = "Flowbit.Runtime.Jobs";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        "flowbit.jobs.retries",
        description: "Durable workflow retries successfully scheduled.");
    private static readonly Counter<long> Conflicts = Meter.CreateCounter<long>(
        "flowbit.jobs.output_conflicts",
        description: "Async output variable-version conflicts.");
    private static readonly Counter<long> Incidents = Meter.CreateCounter<long>(
        "flowbit.jobs.incidents.opened",
        description: "Durable workflow incidents opened.");
    private static readonly Counter<long> AutomaticLoopLimits = Meter.CreateCounter<long>(
        "flowbit.jobs.automatic_loop_limit",
        description: "Automatic workflow activations blocked by the durable loop guard.");
    private static readonly Histogram<double> InstanceLockWait = Meter.CreateHistogram<double>(
        "flowbit.jobs.instance_lock.wait",
        "ms",
        "Time spent waiting to acquire an instance row lock while processing a durable job.");

    public static void RecordRetry() => Retries.Add(1);

    public static void RecordConflict() => Conflicts.Add(1);

    public static void RecordIncident() => Incidents.Add(1);

    public static void RecordAutomaticLoopLimit(
        int observedCount,
        int configuredLimit,
        long tokenId,
        int nodeId,
        Guid activationId)
    {
        var tags = new TagList
        {
            { "observed_count", observedCount },
            { "configured_limit", configuredLimit },
            { "token_id", tokenId },
            { "node_id", nodeId },
            { "activation_id", activationId.ToString("D") }
        };
        AutomaticLoopLimits.Add(1, tags);
    }

    public static void RecordInstanceLockWait(TimeSpan elapsed) =>
        InstanceLockWait.Record(elapsed.TotalMilliseconds);
}
