using Flowbit.Service.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowJobCursorTests
{
    [Fact]
    public void JobCursorRoundTripsAsUtc()
    {
        var updatedAt = new DateTimeOffset(2026, 7, 30, 12, 34, 56, TimeSpan.FromHours(3));

        var cursor = WorkflowJobCursor.EncodeJob(updatedAt, 8123);

        Assert.True(WorkflowJobCursor.TryDecodeJob(cursor, out var decodedAt, out var decodedId));
        Assert.Equal(updatedAt.ToUniversalTime(), decodedAt);
        Assert.Equal(8123, decodedId);
    }

    [Fact]
    public void IncidentCursorCannotBeUsedForJobPaging()
    {
        var cursor = WorkflowJobCursor.EncodeIncident(DateTimeOffset.UtcNow, 19);

        Assert.False(WorkflowJobCursor.TryDecodeJob(cursor, out _, out _));
    }

    [Fact]
    public void AttemptCursorRoundTripsItsStableTieBreaker()
    {
        var cursor = WorkflowJobCursor.EncodeAttempt(7, 91);

        Assert.True(WorkflowJobCursor.TryDecodeAttempt(cursor, out var attemptNumber, out var id));
        Assert.Equal(7, attemptNumber);
        Assert.Equal(91, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("djF8am9ifDEyfDA")]
    public void InvalidCursorIsRejected(string cursor)
    {
        Assert.False(WorkflowJobCursor.TryDecodeJob(cursor, out _, out _));
    }
}
