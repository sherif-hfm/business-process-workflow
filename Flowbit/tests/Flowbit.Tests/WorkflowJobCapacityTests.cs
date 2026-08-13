using Flowbit.Service.Services;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowJobCapacityTests
{
    [Fact]
    public void Missing_or_invalid_setting_uses_default_limit()
    {
        Assert.Equal(2000, WorkflowJobCapacity.ResolveOpenJobLimit(null));
        Assert.Equal(2000, WorkflowJobCapacity.ResolveOpenJobLimit("invalid"));
        Assert.Equal(2000, WorkflowJobCapacity.ResolveOpenJobLimit("0"));
    }

    [Fact]
    public void Configured_multi_instance_limit_is_doubled_without_int_overflow()
    {
        Assert.Equal(42, WorkflowJobCapacity.ResolveOpenJobLimit("21"));
        Assert.Equal(
            (long)int.MaxValue * 2,
            WorkflowJobCapacity.ResolveOpenJobLimit(int.MaxValue.ToString()));
    }

    [Theory]
    [InlineData(1999, 2000, 0, false)]
    [InlineData(2000, 2000, 0, true)]
    [InlineData(2000, 2000, 1, false)]
    [InlineData(2001, 2000, 1, true)]
    public void Completing_job_credit_is_counted_once(
        long current,
        long limit,
        int credit,
        bool expected)
    {
        Assert.Equal(expected, WorkflowJobCapacity.WouldExceed(current, limit, credit));
    }

    [Theory]
    [InlineData(1997, 2000, 0, 3, false)]
    [InlineData(1998, 2000, 0, 3, true)]
    [InlineData(2000, 2000, 1, 1, false)]
    [InlineData(2000, 2000, 1, 2, true)]
    public void Conditional_wake_wave_is_capacity_checked_as_one_batch(
        long current,
        long limit,
        int credit,
        int additional,
        bool expected)
    {
        Assert.Equal(
            expected,
            WorkflowJobCapacity.WouldExceed(current, limit, credit, additional));
    }
}
