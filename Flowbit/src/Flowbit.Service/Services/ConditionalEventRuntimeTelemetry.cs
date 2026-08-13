using System.Diagnostics.Metrics;

namespace Flowbit.Service.Services;

internal static class ConditionalEventRuntimeTelemetry
{
    public const string MeterName = "Flowbit.Runtime.ConditionalEvents";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Evaluations = Meter.CreateCounter<long>(
        "flowbit.conditional.evaluations",
        description: "Conditional-event expressions evaluated.");
    private static readonly Counter<long> Triggers = Meter.CreateCounter<long>(
        "flowbit.conditional.triggers",
        description: "Conditional token activations triggered or latched.");
    private static readonly Histogram<long> Candidates = Meter.CreateHistogram<long>(
        "flowbit.conditional.candidates",
        description: "Conditional nodes selected by one variable-write batch.");
    private static readonly Histogram<double> EvaluationDuration = Meter.CreateHistogram<double>(
        "flowbit.conditional.evaluation.duration",
        "ms",
        "Time spent selecting, evaluating, and latching one conditional wave.");

    public static void RecordEvaluation(bool matched, string source) =>
        Evaluations.Add(1,
            new KeyValuePair<string, object?>("outcome", matched ? "true" : "false"),
            new KeyValuePair<string, object?>("source", source));

    public static void RecordTrigger(string deliveryMode, string source) =>
        Triggers.Add(1,
            new KeyValuePair<string, object?>("delivery_mode", deliveryMode),
            new KeyValuePair<string, object?>("source", source));

    public static void RecordWave(int candidateCount, TimeSpan duration)
    {
        Candidates.Record(candidateCount);
        EvaluationDuration.Record(duration.TotalMilliseconds);
    }
}
