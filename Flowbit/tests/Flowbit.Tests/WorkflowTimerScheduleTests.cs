using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowTimerScheduleTests
{
    private static readonly DateTimeOffset ActivatedAt =
        new(2026, 7, 30, 10, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void DurationResolvesOnceFromActivation()
    {
        var schedule = WorkflowTimerSchedule.Resolve(
            new TimerDefinitionModel { TimeDuration = "P2DT3H4M5.25S" },
            ActivatedAt);

        Assert.Equal(
            ActivatedAt.ToUniversalTime() + new TimeSpan(2, 3, 4, 5, 250),
            schedule.FirstOccurrenceAt);
        Assert.Null(schedule.Interval);
        Assert.Equal(1, schedule.TotalOccurrences);
    }

    [Fact]
    public void InfiniteCycleUsesNominalOccurrencesWithoutCompletionDrift()
    {
        var schedule = WorkflowTimerSchedule.Resolve(
            new TimerDefinitionModel { TimeCycle = "R/PT15M" },
            ActivatedAt);

        Assert.Null(schedule.TotalOccurrences);
        Assert.Equal(TimeSpan.FromMinutes(15), schedule.Interval);
        Assert.Equal(
            ActivatedAt.ToUniversalTime() + TimeSpan.FromMinutes(45),
            schedule.GetOccurrence(2));
    }

    [Fact]
    public void FiniteCycleStopsAfterConfiguredOccurrenceCount()
    {
        var schedule = WorkflowTimerSchedule.Resolve(
            new TimerDefinitionModel { TimeCycle = "R3/PT1H" },
            ActivatedAt);

        Assert.Equal(3, schedule.TotalOccurrences);
        Assert.NotNull(schedule.GetOccurrence(2));
        Assert.Null(schedule.GetOccurrence(3));
    }

    [Theory]
    [InlineData("P1M")]
    [InlineData("P1Y")]
    [InlineData("PT0S")]
    [InlineData("R0/PT1M")]
    [InlineData("R3/")]
    public void UnsupportedOrNonPositiveSchedulesAreRejected(string value)
    {
        var timer = value.StartsWith('R')
            ? new TimerDefinitionModel { TimeCycle = value }
            : new TimerDefinitionModel { TimeDuration = value };

        Assert.Throws<WorkflowDomainException>(() =>
            WorkflowTimerSchedule.Resolve(timer, ActivatedAt));
    }

    [Fact]
    public void CoalescesManyMissedCycleOccurrencesInConstantTime()
    {
        var currentDue = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var threshold = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var next = WorkflowTimerSchedule.ResolveNextCycleOccurrence(
            new TimerDefinitionModel { TimeCycle = "R/PT1S" },
            0,
            currentDue,
            threshold);

        Assert.NotNull(next);
        Assert.Equal(threshold, next!.DueAt);
        Assert.Equal(
            (threshold - currentDue).Ticks / TimeSpan.TicksPerSecond,
            next.Occurrence);
    }

    [Fact]
    public void KeepsImmediatelyNextOccurrenceWhenItIsWithinGrace()
    {
        var currentDue = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var next = WorkflowTimerSchedule.ResolveNextCycleOccurrence(
            new TimerDefinitionModel { TimeCycle = "R/PT30S" },
            4,
            currentDue,
            currentDue - TimeSpan.FromSeconds(10));

        Assert.Equal(5, next!.Occurrence);
        Assert.Equal(currentDue + TimeSpan.FromSeconds(30), next.DueAt);
    }

    [Fact]
    public void RoundsUpToFirstOccurrenceAtOrAfterThreshold()
    {
        var currentDue = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var next = WorkflowTimerSchedule.ResolveNextCycleOccurrence(
            new TimerDefinitionModel { TimeCycle = "R/PT10S" },
            2,
            currentDue,
            currentDue + TimeSpan.FromSeconds(21));

        Assert.Equal(5, next!.Occurrence);
        Assert.Equal(currentDue + TimeSpan.FromSeconds(30), next.DueAt);
    }

    [Fact]
    public void StaleBacklogCoalescesToFirstFutureOccurrence()
    {
        var currentDue = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = currentDue + TimeSpan.FromMinutes(5);

        var next = WorkflowTimerSchedule.ResolveNextCycleOccurrence(
            new TimerDefinitionModel { TimeCycle = "R/PT30S" },
            0,
            currentDue,
            now.AddTicks(1));

        Assert.Equal(11, next!.Occurrence);
        Assert.Equal(now + TimeSpan.FromSeconds(30), next.DueAt);
    }

    [Fact]
    public void FiniteCycleReturnsNoneWhenEveryRemainingOccurrenceWasMissed()
    {
        var currentDue = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var next = WorkflowTimerSchedule.ResolveNextCycleOccurrence(
            new TimerDefinitionModel { TimeCycle = "R3/PT10S" },
            0,
            currentDue,
            currentDue + TimeSpan.FromMinutes(1));

        Assert.Null(next);
    }

    [Theory]
    [InlineData(TimerScheduleKinds.Date)]
    [InlineData(TimerScheduleKinds.Duration)]
    public void OneShotOccurrenceIsNeverTreatedAsRecurringMisfire(string scheduleKind)
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 10, 0, TimeSpan.Zero);

        Assert.False(WorkflowTimerSchedule.IsRecurringMisfire(
            scheduleKind,
            now - TimeSpan.FromDays(1),
            now,
            TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void RecurringOccurrenceIsSkippedOnlyWhenStrictlyOlderThanGrace()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 10, 0, TimeSpan.Zero);
        var grace = TimeSpan.FromMinutes(1);

        Assert.False(WorkflowTimerSchedule.IsRecurringMisfire(
            TimerScheduleKinds.Cycle,
            now - grace,
            now,
            grace));
        Assert.True(WorkflowTimerSchedule.IsRecurringMisfire(
            TimerScheduleKinds.Cycle,
            now - grace - TimeSpan.FromTicks(1),
            now,
            grace));
    }
}
