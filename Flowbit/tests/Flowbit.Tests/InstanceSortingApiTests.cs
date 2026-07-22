using System.Net;
using System.Net.Http.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceSortingApiTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task InstanceListSortsEveryAllowedFieldInBothDirectionsAndSupportsPriority()
    {
        var rows = await SeedAsync();

        foreach (var field in new[] { "id", "createdAt", "updatedAt" })
        {
            foreach (var direction in new[] { "asc", "desc" })
            {
                var page = await GetInstancesAsync(rows.WorkflowKey, $"{field}:{direction}");
                var key = InstanceKey(field);
                var expected = direction == "asc"
                    ? rows.Items.OrderBy(key).ThenBy(row => row.InstanceId)
                    : rows.Items.OrderByDescending(key).ThenByDescending(row => row.InstanceId);
                Assert.Equal(expected.Select(row => row.InstanceId), page.Items.Select(item => item.Id));
            }
        }

        var prioritized = await GetInstancesAsync(rows.WorkflowKey, "createdAt:asc", "updatedAt:desc");
        Assert.Equal(
            rows.Items.OrderBy(row => row.InstanceCreatedAt)
                .ThenByDescending(row => row.InstanceUpdatedAt)
                .ThenByDescending(row => row.InstanceId)
                .Select(row => row.InstanceId),
            prioritized.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task InboxSortsEveryAllowedFieldAndExposesTaskAndInstanceTimestamps()
    {
        var rows = await SeedAsync();

        foreach (var field in new[]
                 {
                     "userTaskId", "instanceId", "taskCreatedAt", "taskUpdatedAt",
                     "instanceCreatedAt", "instanceUpdatedAt"
                 })
        {
            foreach (var direction in new[] { "asc", "desc" })
            {
                var page = await GetInboxAsync(rows.WorkflowKey, $"{field}:{direction}");
                var key = InboxKey(field);
                var expected = direction == "asc"
                    ? rows.Items.OrderBy(key).ThenBy(row => row.UserTaskId)
                    : rows.Items.OrderByDescending(key).ThenByDescending(row => row.UserTaskId);
                Assert.Equal(expected.Select(row => row.UserTaskId), page.Items.Select(item => item.UserTaskId));
            }
        }

        var prioritized = await GetInboxAsync(rows.WorkflowKey, "taskCreatedAt:asc", "instanceUpdatedAt:desc");
        Assert.Equal(
            rows.Items.OrderBy(row => row.TaskCreatedAt)
                .ThenByDescending(row => row.InstanceUpdatedAt)
                .ThenByDescending(row => row.UserTaskId)
                .Select(row => row.UserTaskId),
            prioritized.Items.Select(item => item.UserTaskId));

        var byTaskId = (await GetInboxAsync(rows.WorkflowKey, "userTaskId:asc")).Items
            .ToDictionary(item => item.UserTaskId);
        foreach (var row in rows.Items)
        {
            var item = byTaskId[row.UserTaskId];
            Assert.Equal(row.TaskCreatedAt, item.TaskCreatedAt);
            Assert.Equal(row.TaskUpdatedAt, item.TaskUpdatedAt);
            Assert.Equal(row.InstanceCreatedAt, item.InstanceCreatedAt);
            Assert.Equal(row.InstanceUpdatedAt, item.InstanceUpdatedAt);
#pragma warning disable CS0618
            Assert.Equal(item.TaskCreatedAt, item.CreatedAt);
            Assert.Equal(item.TaskUpdatedAt, item.UpdatedAt);
#pragma warning restore CS0618
        }
    }

    [Theory]
    [InlineData("/api/instances?sort=unknown:asc")]
    [InlineData("/api/instances?sort=id:sideways")]
    [InlineData("/api/instances?sort=id:asc&sort=ID:desc")]
    [InlineData("/api/instances?sort=")]
    [InlineData("/api/instances/inbox?sort=unknown:asc")]
    [InlineData("/api/instances/inbox?sort=userTaskId:asc&sort=instanceId:asc&sort=taskCreatedAt:asc&sort=taskUpdatedAt:asc")]
    public async Task InvalidSortClausesReturnBadRequest(string path)
    {
        using var response = await GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<SeedResult> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"sorting-{suffix}";
        var rawBasis = DateTimeOffset.UtcNow.AddDays(-1);
        var basis = new DateTimeOffset(rawBasis.Ticks - rawBasis.Ticks % 10, TimeSpan.Zero);

        await using var setup = fixture.CreateDbContext();
        var definition = new WorkflowDefinitionEntity
        {
            Name = workflowKey,
            WorkflowKey = workflowKey,
            Version = 1,
            IsPublished = true,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = workflowKey,
                FlowNodes =
                [
                    new FlowNodeModel
                    {
                        Id = 2,
                        Name = "Review",
                        Type = BpmnFlowNodeTypes.UserTask
                    }
                ]
            }
        };
        setup.WorkflowDefinitions.Add(definition);
        await setup.SaveChangesAsync();

        var timestamps = new[]
        {
            new TimestampSet(basis, basis.AddMinutes(3), basis.AddMinutes(2), basis.AddMinutes(4)),
            new TimestampSet(basis.AddMinutes(1), basis.AddMinutes(2), basis.AddMinutes(1), basis.AddMinutes(3)),
            new TimestampSet(basis.AddMinutes(1), basis.AddMinutes(4), basis.AddMinutes(1), basis.AddMinutes(2))
        };
        var instances = timestamps.Select(times => new WorkflowInstanceEntity
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            Status = WorkflowInstanceStatuses.Running,
            StartedBy = "sort-test",
            CreatedAt = times.InstanceCreatedAt,
            UpdatedAt = times.InstanceUpdatedAt
        }).ToList();
        setup.WorkflowInstances.AddRange(instances);
        await setup.SaveChangesAsync();

        var tokens = instances.Select((instance, index) => new ExecutionTokenEntity
        {
            InstanceId = instance.Id,
            NodeId = 2,
            NodeName = "Review",
            NodeType = BpmnFlowNodeTypes.UserTask,
            Status = ExecutionTokenStatuses.Active,
            CreatedAt = timestamps[index].TaskCreatedAt,
            UpdatedAt = timestamps[index].TaskUpdatedAt
        }).ToList();
        setup.ExecutionTokens.AddRange(tokens);
        await setup.SaveChangesAsync();

        var tasks = instances.Select((instance, index) => new UserTaskEntity
        {
            InstanceId = instance.Id,
            TokenId = tokens[index].Id,
            NodeId = 2,
            NodeName = "Review",
            Roles = [],
            Status = UserTaskStatuses.Active,
            CreatedAt = timestamps[index].TaskCreatedAt,
            UpdatedAt = timestamps[index].TaskUpdatedAt
        }).ToList();
        setup.UserTasks.AddRange(tasks);
        await setup.SaveChangesAsync();

        return new SeedResult(
            workflowKey,
            instances.Select((instance, index) => new SeedRow(
                instance.Id,
                tasks[index].Id,
                timestamps[index].InstanceCreatedAt,
                timestamps[index].InstanceUpdatedAt,
                timestamps[index].TaskCreatedAt,
                timestamps[index].TaskUpdatedAt)).ToArray());
    }

    private async Task<PagedResult<InstanceSummaryDto>> GetInstancesAsync(string workflowKey, params string[] sort)
    {
        var query = string.Join(string.Empty, sort.Select(clause => $"&sort={Uri.EscapeDataString(clause)}"));
        using var response = await GetAsync($"/api/instances?workflowKey={workflowKey}&pageSize=20{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PagedResult<InstanceSummaryDto>>())!;
    }

    private async Task<PagedResult<InboxItemDto>> GetInboxAsync(string workflowKey, params string[] sort)
    {
        var query = string.Join(string.Empty, sort.Select(clause => $"&sort={Uri.EscapeDataString(clause)}"));
        using var response = await GetAsync($"/api/instances/inbox?workflowKey={workflowKey}&pageSize=20{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PagedResult<InboxItemDto>>())!;
    }

    private Task<HttpResponseMessage> GetAsync(string path)
    {
        var request = ApiTestAuth.Authorize(new HttpRequestMessage(HttpMethod.Get, path));
        return fixture.Client.SendAsync(request);
    }

    private static Func<SeedRow, IComparable> InstanceKey(string field) => field switch
    {
        "id" => row => row.InstanceId,
        "createdAt" => row => row.InstanceCreatedAt,
        "updatedAt" => row => row.InstanceUpdatedAt,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static Func<SeedRow, IComparable> InboxKey(string field) => field switch
    {
        "userTaskId" => row => row.UserTaskId,
        "instanceId" => row => row.InstanceId,
        "taskCreatedAt" => row => row.TaskCreatedAt,
        "taskUpdatedAt" => row => row.TaskUpdatedAt,
        "instanceCreatedAt" => row => row.InstanceCreatedAt,
        "instanceUpdatedAt" => row => row.InstanceUpdatedAt,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private sealed record TimestampSet(
        DateTimeOffset InstanceCreatedAt,
        DateTimeOffset InstanceUpdatedAt,
        DateTimeOffset TaskCreatedAt,
        DateTimeOffset TaskUpdatedAt);

    private sealed record SeedRow(
        long InstanceId,
        long UserTaskId,
        DateTimeOffset InstanceCreatedAt,
        DateTimeOffset InstanceUpdatedAt,
        DateTimeOffset TaskCreatedAt,
        DateTimeOffset TaskUpdatedAt);

    private sealed record SeedResult(string WorkflowKey, IReadOnlyList<SeedRow> Items);
}
