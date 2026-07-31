using Flowbit.Service.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowInstanceCursorTests
{
    [Fact]
    public void NormalizeSortDefaultsAndAppendsMatchingIdTieBreaker()
    {
        Assert.Equal(
            [
                new InstanceSortCriterion(
                    InstanceSortField.UpdatedAt,
                    SortDirection.Descending),
                new InstanceSortCriterion(
                    InstanceSortField.Id,
                    SortDirection.Descending)
            ],
            WorkflowInstanceCursor.NormalizeSort([]));

        Assert.Equal(
            [
                new InstanceSortCriterion(
                    InstanceSortField.CreatedAt,
                    SortDirection.Descending),
                new InstanceSortCriterion(
                    InstanceSortField.UpdatedAt,
                    SortDirection.Ascending),
                new InstanceSortCriterion(
                    InstanceSortField.Id,
                    SortDirection.Ascending)
            ],
            WorkflowInstanceCursor.NormalizeSort(
            [
                new InstanceSortCriterion(
                    InstanceSortField.CreatedAt,
                    SortDirection.Descending),
                new InstanceSortCriterion(
                    InstanceSortField.UpdatedAt,
                    SortDirection.Ascending)
            ]));
    }

    [Fact]
    public void NormalizeSortDoesNotDuplicateAnAuthoredIdCriterion()
    {
        InstanceSortCriterion[] requested =
        [
            new(InstanceSortField.Id, SortDirection.Ascending),
            new(InstanceSortField.CreatedAt, SortDirection.Descending)
        ];

        Assert.Equal(requested, WorkflowInstanceCursor.NormalizeSort(requested));
    }

    [Fact]
    public void CursorRoundTripsAllPredicateValuesAsUtc()
    {
        var sort = WorkflowInstanceCursor.NormalizeSort(
        [
            new InstanceSortCriterion(
                InstanceSortField.CreatedAt,
                SortDirection.Ascending),
            new InstanceSortCriterion(
                InstanceSortField.UpdatedAt,
                SortDirection.Descending)
        ]);
        var createdAt = new DateTimeOffset(
            2026,
            7,
            30,
            11,
            22,
            33,
            TimeSpan.FromHours(3)).AddTicks(4567);
        var updatedAt = new DateTimeOffset(
            2026,
            7,
            31,
            8,
            9,
            10,
            TimeSpan.FromHours(-4)).AddTicks(7654);

        var cursor = WorkflowInstanceCursor.Encode(
            sort,
            9182,
            createdAt,
            updatedAt);

        Assert.DoesNotContain("=", cursor);
        Assert.True(WorkflowInstanceCursor.TryDecode(cursor, sort, out var values));
        Assert.Equal(9182, values.Id);
        Assert.Equal(createdAt.ToUniversalTime(), values.CreatedAt);
        Assert.Equal(updatedAt.ToUniversalTime(), values.UpdatedAt);
    }

    [Fact]
    public void CursorIsBoundToExactNormalizedSort()
    {
        var encodedSort = WorkflowInstanceCursor.NormalizeSort(
        [
            new InstanceSortCriterion(
                InstanceSortField.CreatedAt,
                SortDirection.Ascending)
        ]);
        var cursor = WorkflowInstanceCursor.Encode(
            encodedSort,
            73,
            DateTimeOffset.UtcNow.AddHours(-2),
            DateTimeOffset.UtcNow);

        var wrongDirection = WorkflowInstanceCursor.NormalizeSort(
        [
            new InstanceSortCriterion(
                InstanceSortField.CreatedAt,
                SortDirection.Descending)
        ]);
        var wrongField = WorkflowInstanceCursor.NormalizeSort(
        [
            new InstanceSortCriterion(
                InstanceSortField.UpdatedAt,
                SortDirection.Ascending)
        ]);
        var wrongPriority = WorkflowInstanceCursor.NormalizeSort(
        [
            new InstanceSortCriterion(
                InstanceSortField.Id,
                SortDirection.Ascending),
            new InstanceSortCriterion(
                InstanceSortField.CreatedAt,
                SortDirection.Ascending)
        ]);

        Assert.False(WorkflowInstanceCursor.TryDecode(
            cursor,
            wrongDirection,
            out _));
        Assert.False(WorkflowInstanceCursor.TryDecode(cursor, wrongField, out _));
        Assert.False(WorkflowInstanceCursor.TryDecode(
            cursor,
            wrongPriority,
            out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("AA")]
    [InlineData("_______________________________________________")]
    public void MalformedCursorIsRejected(string? cursor)
    {
        var sort = WorkflowInstanceCursor.NormalizeSort([]);

        Assert.False(WorkflowInstanceCursor.TryDecode(cursor, sort, out _));
    }

    [Fact]
    public void TamperedCursorIsRejected()
    {
        var sort = WorkflowInstanceCursor.NormalizeSort([]);
        var cursor = WorkflowInstanceCursor.Encode(
            sort,
            91,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow);
        var replacement = cursor[0] == 'A' ? 'B' : 'A';
        var tampered = replacement + cursor[1..];

        Assert.False(WorkflowInstanceCursor.TryDecode(tampered, sort, out _));
    }

    [Fact]
    public void NormalizeSortRejectsDuplicateOrUnknownCriteria()
    {
        Assert.Throws<ArgumentException>(() => WorkflowInstanceCursor.NormalizeSort(
        [
            new InstanceSortCriterion(
                InstanceSortField.CreatedAt,
                SortDirection.Ascending),
            new InstanceSortCriterion(
                InstanceSortField.CreatedAt,
                SortDirection.Descending)
        ]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WorkflowInstanceCursor.NormalizeSort(
            [
                new InstanceSortCriterion(
                    (InstanceSortField)99,
                    SortDirection.Ascending)
            ]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WorkflowInstanceCursor.NormalizeSort(
            [
                new InstanceSortCriterion(
                    InstanceSortField.Id,
                    (SortDirection)99)
            ]));
    }

    [Fact]
    public void EncodeRejectsNonPositiveId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WorkflowInstanceCursor.Encode(
                WorkflowInstanceCursor.NormalizeSort([]),
                0,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
    }
}
