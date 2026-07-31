namespace Flowbit.Service.Services;

/// <summary>
/// Resolves and evaluates the per-instance durable-job safety bound. The bound
/// intentionally follows the multi-instance fan-out setting so operators have
/// one scale control for both sources of durable work.
/// </summary>
public static class WorkflowJobCapacity
{
    public const int DefaultMultiInstanceMaximum = 1000;
    public const int OpenJobMultiplier = 2;

    public static long ResolveOpenJobLimit(string? configuredMultiInstanceMaximum)
    {
        var maximum = int.TryParse(configuredMultiInstanceMaximum, out var parsed)
                      && parsed > 0
            ? parsed
            : DefaultMultiInstanceMaximum;
        return (long)maximum * OpenJobMultiplier;
    }

    public static bool WouldExceed(
        long currentOpenJobs,
        long openJobLimit,
        int completingJobCredits = 0)
    {
        if (currentOpenJobs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentOpenJobs));
        }
        if (openJobLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(openJobLimit));
        }
        if (completingJobCredits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completingJobCredits));
        }

        var effectiveOpenJobs = Math.Max(0, currentOpenJobs - completingJobCredits);
        return effectiveOpenJobs >= openJobLimit;
    }
}
