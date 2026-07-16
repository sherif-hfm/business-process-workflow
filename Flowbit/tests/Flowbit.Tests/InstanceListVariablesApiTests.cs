using System.Net;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceListVariablesApiTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task GetInstancesIncludesOnlyLatestVariablesWhenRequested()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"list-variables-{suffix}";
        var (withVariables, withoutVariables) = await SeedTwoInstancesAsync(workflowKey);

        await using (var setup = fixture.CreateDbContext())
        {
            setup.InstanceVariables.AddRange(
                Variable(withVariables, "requestAmount", 100),
                Variable(withVariables, "department", "Finance"));
            await setup.SaveChangesAsync();

            setup.InstanceVariables.AddRange(
                Variable(withVariables, "requestAmount", 5000),
                Variable(withVariables, "department", "IT"),
                Variable(withVariables, "approved", true),
                Variable(withVariables, "optional", null),
                Variable(withVariables, "tags", new[] { "urgent", "internal" }),
                Variable(withVariables, "metadata", new { priority = "high" }));
            await setup.SaveChangesAsync();
        }

        using (var omitted = await GetAsync($"/api/instances?workflowKey={workflowKey}&pageSize=20"))
        {
            Assert.Equal(HttpStatusCode.OK, omitted.StatusCode);
            using var json = JsonDocument.Parse(await omitted.Content.ReadAsStringAsync());
            Assert.All(json.RootElement.GetProperty("items").EnumerateArray(), item =>
                Assert.False(item.TryGetProperty("variables", out _)));
        }

        using (var explicitlyFalse = await GetAsync($"/api/instances?workflowKey={workflowKey}&includeVariables=false&pageSize=20"))
        {
            Assert.Equal(HttpStatusCode.OK, explicitlyFalse.StatusCode);
            using var json = JsonDocument.Parse(await explicitlyFalse.Content.ReadAsStringAsync());
            Assert.All(json.RootElement.GetProperty("items").EnumerateArray(), item =>
                Assert.False(item.TryGetProperty("variables", out _)));
        }

        using var included = await GetAsync($"/api/instances?workflowKey={workflowKey}&includeVariables=true&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, included.StatusCode);
        using var includedJson = JsonDocument.Parse(await included.Content.ReadAsStringAsync());
        var items = includedJson.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var populated = items.Single(item => item.GetProperty("id").GetInt64() == withVariables);
        var variables = populated.GetProperty("variables");
        Assert.Equal(JsonValueKind.Object, variables.ValueKind);
        Assert.Equal(5000, variables.GetProperty("requestAmount").GetInt32());
        Assert.Equal("IT", variables.GetProperty("department").GetString());
        Assert.True(variables.GetProperty("approved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, variables.GetProperty("optional").ValueKind);
        Assert.Equal(
            new[] { "urgent", "internal" },
            variables.GetProperty("tags").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal("high", variables.GetProperty("metadata").GetProperty("priority").GetString());

        var empty = items.Single(item => item.GetProperty("id").GetInt64() == withoutVariables)
            .GetProperty("variables");
        Assert.Equal(JsonValueKind.Object, empty.ValueKind);
        Assert.Empty(empty.EnumerateObject());
    }

    [Fact]
    public async Task VariableFiltersMatchOnlyTheLatestScalarValue()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"latest-filter-{suffix}";
        var (instanceId, _) = await SeedTwoInstancesAsync(workflowKey);

        await using (var setup = fixture.CreateDbContext())
        {
            setup.InstanceVariables.AddRange(
                Variable(instanceId, "state", "old"),
                Variable(instanceId, "department", "IT"),
                Variable(instanceId, "shape", "previous-scalar"));
            await setup.SaveChangesAsync();

            setup.InstanceVariables.AddRange(
                Variable(instanceId, "state", "current"),
                Variable(instanceId, "shape", new { current = true }));
            await setup.SaveChangesAsync();
        }

        Assert.Equal(0, await GetTotalCountAsync(workflowKey, "var=state:old"));
        Assert.Equal(1, await GetTotalCountAsync(workflowKey, "var=state:current&var=department:it"));
        Assert.Equal(0, await GetTotalCountAsync(workflowKey, "var=shape:previous-scalar"));
    }

    private async Task<(long First, long Second)> SeedTwoInstancesAsync(string workflowKey)
    {
        await using var setup = fixture.CreateDbContext();
        var definition = new WorkflowDefinitionEntity
        {
            Name = workflowKey,
            WorkflowKey = workflowKey,
            Version = 1,
            IsPublished = true,
            Definition = new WorkflowModel { Id = workflowKey, Name = workflowKey }
        };
        setup.WorkflowDefinitions.Add(definition);
        await setup.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var instances = new[]
        {
            new WorkflowInstanceEntity
            {
                WorkflowDefinitionId = definition.Id,
                WorkflowKey = workflowKey,
                Status = "running",
                StartedBy = "test-admin",
                CreatedAt = now,
                UpdatedAt = now
            },
            new WorkflowInstanceEntity
            {
                WorkflowDefinitionId = definition.Id,
                WorkflowKey = workflowKey,
                Status = "running",
                StartedBy = "test-admin",
                CreatedAt = now.AddSeconds(1),
                UpdatedAt = now.AddSeconds(1)
            }
        };
        setup.WorkflowInstances.AddRange(instances);
        await setup.SaveChangesAsync();

        setup.ExecutionTokens.AddRange(instances.Select(instance => new ExecutionTokenEntity
        {
            InstanceId = instance.Id,
            NodeId = 2,
            NodeName = "Review",
            NodeType = "userTask",
            Status = ExecutionTokenStatuses.Active
        }));
        await setup.SaveChangesAsync();
        return (instances[0].Id, instances[1].Id);
    }

    private async Task<long> GetTotalCountAsync(string workflowKey, string query)
    {
        using var response = await GetAsync($"/api/instances?workflowKey={workflowKey}&{query}&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("totalCount").GetInt64();
    }

    private Task<HttpResponseMessage> GetAsync(string path)
    {
        var request = ApiTestAuth.Authorize(new HttpRequestMessage(HttpMethod.Get, path));
        return fixture.Client.SendAsync(request);
    }

    private static InstanceVariableEntity Variable(long instanceId, string name, object? value) => new()
    {
        InstanceId = instanceId,
        VariableName = name,
        ValueJson = JsonDocument.Parse(JsonSerializer.Serialize(value)),
        SetBy = "test-admin"
    };
}
