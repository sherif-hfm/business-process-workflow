using Flowbit.Service.Services;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowAutomaticActivationGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    public void Missing_invalid_or_nonpositive_setting_uses_default(string? value)
    {
        Assert.Equal(
            WorkflowAutomaticActivationGuard.DefaultLimit,
            WorkflowAutomaticActivationGuard.ResolveLimit(value));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("42", 42)]
    [InlineData(" 42 ", 42)]
    [InlineData("2147483647", int.MaxValue)]
    public void Positive_integer_setting_is_used(string value, int expected)
    {
        Assert.Equal(
            expected,
            WorkflowAutomaticActivationGuard.ResolveLimit(value));
    }

    [Fact]
    public void Activation_at_limit_is_allowed_and_persisted()
    {
        var decision = WorkflowAutomaticActivationGuard.EvaluateNext(999, null);

        Assert.Equal(999, decision.PreviousCount);
        Assert.Equal(1000, decision.AttemptedCount);
        Assert.Equal(1000, decision.PersistedCount);
        Assert.Equal(1000, decision.Limit);
        Assert.True(decision.CanSchedule);
        Assert.False(decision.ShouldOpenIncident);
    }

    [Fact]
    public void Activation_after_limit_is_blocked_without_advancing_persisted_count()
    {
        var decision = WorkflowAutomaticActivationGuard.EvaluateNext(1000, null);

        Assert.Equal(1000, decision.PreviousCount);
        Assert.Equal(1001, decision.AttemptedCount);
        Assert.Equal(1000, decision.PersistedCount);
        Assert.Equal(1000, decision.Limit);
        Assert.False(decision.CanSchedule);
        Assert.True(decision.ShouldOpenIncident);
    }

    [Fact]
    public void Configured_limit_is_applied_to_the_next_activation()
    {
        Assert.True(
            WorkflowAutomaticActivationGuard.EvaluateNext(1, "2").CanSchedule);
        Assert.True(
            WorkflowAutomaticActivationGuard.EvaluateNext(2, "2").ShouldOpenIncident);
    }

    [Fact]
    public void Counter_overflow_is_saturated_and_blocked()
    {
        var decision = WorkflowAutomaticActivationGuard.EvaluateNext(
            int.MaxValue,
            int.MaxValue.ToString());

        Assert.Equal(int.MaxValue, decision.AttemptedCount);
        Assert.Equal(int.MaxValue, decision.PersistedCount);
        Assert.True(decision.ShouldOpenIncident);
    }

    [Fact]
    public void External_trigger_resets_and_manual_retry_starts_a_new_allowance()
    {
        Assert.Equal(
            0,
            WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger());
        Assert.Equal(
            1,
            WorkflowAutomaticActivationGuard.RestartBlockedActivation());
    }

    [Fact]
    public void Fork_inherits_and_join_uses_maximum_instead_of_sum()
    {
        Assert.Equal(37, WorkflowAutomaticActivationGuard.InheritForFork(37));
        Assert.Equal(
            37,
            WorkflowAutomaticActivationGuard.MergeAtJoin([8, 37, 14]));
        Assert.Equal(0, WorkflowAutomaticActivationGuard.MergeAtJoin([]));
    }

    [Fact]
    public void Negative_persisted_counts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WorkflowAutomaticActivationGuard.EvaluateNext(-1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WorkflowAutomaticActivationGuard.InheritForFork(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WorkflowAutomaticActivationGuard.MergeAtJoin([0, -1]));
    }
}
