using System.Globalization;

namespace Flowbit.Service.Services;

/// <summary>
/// Evaluates the per-token-lineage bound for consecutive durable automatic
/// activity activations. The caller owns persistence and must invoke
/// <see cref="EvaluateNext"/> exactly once when a new automatic task activation
/// is about to be scheduled; async phases and retries of that activation do not
/// call it again.
/// </summary>
public static class WorkflowAutomaticActivationGuard
{
    public const string SettingKey =
        "Workflow.Async.MaxConsecutiveAutomaticActivations";
    public const int DefaultLimit = 1000;
    public const int ResetCount = 0;
    public const int FirstActivationCount = 1;

    /// <summary>
    /// Resolves the dynamic engine setting. Missing, malformed, overflowing,
    /// and non-positive values deliberately fall back to the safe default.
    /// </summary>
    public static int ResolveLimit(string? configuredLimit) =>
        int.TryParse(
            configuredLimit,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
        && parsed > 0
            ? parsed
            : DefaultLimit;

    /// <summary>
    /// Evaluates one new automatic activity activation. A rejected activation
    /// reports the attempted count but retains the previously persisted count,
    /// because the blocked task body has not run.
    /// </summary>
    public static WorkflowAutomaticActivationDecision EvaluateNext(
        int currentCount,
        string? configuredLimit)
    {
        ValidateCount(currentCount, nameof(currentCount));

        var limit = ResolveLimit(configuredLimit);
        var attemptedCount = currentCount == int.MaxValue
            ? int.MaxValue
            : currentCount + 1;
        var shouldOpenIncident = currentCount >= limit;

        return new WorkflowAutomaticActivationDecision(
            currentCount,
            attemptedCount,
            shouldOpenIncident ? currentCount : attemptedCount,
            limit,
            shouldOpenIncident);
    }

    /// <summary>
    /// Resets the consecutive chain after an intentional external wait or
    /// trigger such as a user action, message delivery, or timer firing.
    /// </summary>
    public static int ResetAfterExternalWaitOrTrigger() => ResetCount;

    /// <summary>
    /// Starts a fresh allowance when an operator retries the job blocked by
    /// this guard. The blocked activation is the first activation in the new
    /// allowance; the existing durable job identity is unaffected.
    /// </summary>
    public static int RestartBlockedActivation() => FirstActivationCount;

    /// <summary>
    /// Copies the lineage count to every child created by a gateway fork.
    /// </summary>
    public static int InheritForFork(int parentCount)
    {
        ValidateCount(parentCount, nameof(parentCount));
        return parentCount;
    }

    /// <summary>
    /// Continues a joined lineage at the greatest contributing count. Counts
    /// are intentionally not summed, because the guard bounds automatic depth
    /// rather than aggregate parallel work. An empty join starts at zero.
    /// </summary>
    public static int MergeAtJoin(IEnumerable<int> incomingCounts)
    {
        ArgumentNullException.ThrowIfNull(incomingCounts);

        var maximum = ResetCount;
        foreach (var count in incomingCounts)
        {
            ValidateCount(count, nameof(incomingCounts));
            maximum = Math.Max(maximum, count);
        }

        return maximum;
    }

    private static void ValidateCount(int count, string parameterName)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                count,
                "An automatic activation count cannot be negative.");
        }
    }
}

/// <summary>
/// Result of attempting to schedule one new automatic activity activation.
/// </summary>
public readonly record struct WorkflowAutomaticActivationDecision(
    int PreviousCount,
    int AttemptedCount,
    int PersistedCount,
    int Limit,
    bool ShouldOpenIncident)
{
    public bool CanSchedule => !ShouldOpenIncident;
}
