using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdvancedVariableFilterPostgresTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task LatestProjectionNestedPathsAndTypedStringEqualityStayExact()
    {
        var workflowKey = NewWorkflowKey("advanced-equality");
        var ids = await SeedInstancesAsync(
            workflowKey,
            new Dictionary<string, object?>
            {
                ["state"] = "old",
                ["request"] = new
                {
                    medicalCenter = new { id = "MC-1042" },
                    code = "Alpha"
                },
                ["typed"] = "42"
            },
            new Dictionary<string, object?>
            {
                ["state"] = "current",
                ["request"] = new
                {
                    medicalCenter = new { id = "mc-1042" },
                    code = "alpha"
                },
                ["typed"] = 42
            },
            new Dictionary<string, object?>
            {
                ["state"] = "other",
                ["request"] = new
                {
                    medicalCenter = new { id = "MC-9999" },
                    code = "Beta"
                },
                ["typed"] = true
            });

        // A later history row must replace the projection used by search.
        await AddVariablesAsync(ids[0], ("state", "Current"));

        await AssertMatchesAsync(
            workflowKey,
            """{ "state": { "$eq": "old" } }""",
            []);
        await AssertMatchesAsync(
            workflowKey,
            """{ "state": { "$eq": "Current" } }""",
            [ids[0]]);
        await AssertMatchesAsync(
            workflowKey,
            """{ "request.medicalCenter.id": { "$eq": "MC-1042" } }""",
            [ids[0]]);

        // $eq is both typed and case-sensitive; $eqIgnoreCase is explicitly broader.
        await AssertMatchesAsync(
            workflowKey,
            """{ "state": { "$eq": "current" } }""",
            [ids[1]]);
        await AssertMatchesAsync(
            workflowKey,
            """{ "state": { "$eqIgnoreCase": "current" } }""",
            [ids[0], ids[1]]);
        await AssertMatchesAsync(
            workflowKey,
            """{ "typed": { "$eq": 42 } }""",
            [ids[1]]);
        await AssertMatchesAsync(
            workflowKey,
            """{ "typed": { "$eq": "42" } }""",
            [ids[0]]);
    }

    [Fact]
    public async Task MissingAndJsonNullRemainDistinctThroughExistsNeNinAndNot()
    {
        var workflowKey = NewWorkflowKey("advanced-missing-null");
        var ids = await SeedInstancesAsync(
            workflowKey,
            new Dictionary<string, object?> { ["nullable"] = null },
            new Dictionary<string, object?> { ["nullable"] = "x" },
            new Dictionary<string, object?>(),
            new Dictionary<string, object?> { ["nullable"] = "y" });

        await AssertMatchesAsync(
            workflowKey,
            """{ "nullable": { "$eq": null } }""",
            [ids[0]]);
        await AssertMatchesAsync(
            workflowKey,
            """{ "nullable": { "$exists": true } }""",
            [ids[0], ids[1], ids[3]]);
        await AssertMatchesAsync(
            workflowKey,
            """{ "nullable": { "$exists": false } }""",
            [ids[2]]);
        await AssertMatchesAsync(
            workflowKey,
            """{ "nullable": { "$ne": "x" } }""",
            [ids[0], ids[3]]);
        await AssertMatchesAsync(
            workflowKey,
            """{ "nullable": { "$nin": ["x", "y"] } }""",
            [ids[0]]);
        await AssertMatchesAsync(
            workflowKey,
            """{ "$not": { "nullable": { "$eq": "x" } } }""",
            [ids[0], ids[3]]);
    }

    [Fact]
    public async Task NumericRangesAndLogicalNodesProduceExactKeysetCountsAndPages()
    {
        var workflowKey = NewWorkflowKey("advanced-range-page");
        var instances = Enumerable.Range(1, 9)
            .Select(score => new Dictionary<string, object?>
            {
                ["score"] = score,
                ["region"] = score % 2 == 1 ? "east" : "west"
            })
            .Concat(
            [
                new Dictionary<string, object?>
                {
                    ["score"] = "7",
                    ["region"] = "east"
                },
                new Dictionary<string, object?>
                {
                    ["score"] = new { raw = 7 },
                    ["region"] = "east"
                },
                new Dictionary<string, object?>
                {
                    ["score"] = null,
                    ["region"] = "east"
                }
            ])
            .ToArray();
        var ids = await SeedInstancesAsync(workflowKey, instances);
        var filter = ParseFilter(
            """
            {
              "$and": [
                { "score": { "$gte": 2, "$lte": 8 } },
                {
                  "$or": [
                    { "region": { "$eq": "east" } },
                    { "score": { "$in": [4, 6, 8] } }
                  ]
                },
                { "$not": { "score": { "$eq": 5 } } }
              ]
            }
            """);
        var expected = new[] { ids[2], ids[3], ids[5], ids[6], ids[7] };
        var sort = new[]
        {
            new InstanceSortCriterion(InstanceSortField.Id, SortDirection.Ascending)
        };

        var first = await SearchAsync(
            workflowKey, filter, sort, cursor: null, page: 1, pageSize: 2);
        Assert.Equal(5, first.TotalCount);
        Assert.Equal(expected.Take(2), first.Items.Select(item => item.Id));
        Assert.NotNull(first.NextCursor);

        var second = await SearchAsync(
            workflowKey, filter, sort, first.NextCursor, page: 2, pageSize: 2);
        Assert.Equal(5, second.TotalCount);
        Assert.Equal(expected.Skip(2).Take(2), second.Items.Select(item => item.Id));
        Assert.NotNull(second.NextCursor);

        var third = await SearchAsync(
            workflowKey, filter, sort, second.NextCursor, page: 3, pageSize: 2);
        Assert.Equal(5, third.TotalCount);
        Assert.Equal(expected.Skip(4), third.Items.Select(item => item.Id));
        Assert.Null(third.NextCursor);

        var allPageIds = first.Items
            .Concat(second.Items)
            .Concat(third.Items)
            .Select(item => item.Id)
            .ToArray();
        Assert.Equal(expected, allPageIds);
        Assert.Equal(expected.Length, allPageIds.Distinct().Count());
    }

    [Fact]
    public async Task ContainmentAndElemMatchUseJsonbAndOneSharedArrayElement()
    {
        var workflowKey = NewWorkflowKey("advanced-containment");
        var ids = await SeedInstancesAsync(
            workflowKey,
            new Dictionary<string, object?>
            {
                ["tags"] = new[] { "health-certificate", "urgent", "arabic" },
                ["metadata"] = new { center = new { id = "MC-1" }, tier = "gold" },
                ["services"] = new object[]
                {
                    new { code = "health-certificate", active = true },
                    new { code = "other", active = false }
                },
                ["serviceMatrix"] = new object[]
                {
                    new[] { "health-certificate", "urgent" },
                    new[] { "other" }
                }
            },
            new Dictionary<string, object?>
            {
                ["tags"] = new[] { "health-certificate", "routine" },
                ["metadata"] = new { center = new { id = "MC-2" }, tier = "silver" },
                // Each condition exists, but on different elements. This must not match.
                ["services"] = new object[]
                {
                    new { code = "health-certificate", active = false },
                    new { code = "other", active = true }
                },
                ["serviceMatrix"] = new object[]
                {
                    new[] { "health-certificate", "routine" },
                    new[] { "other" }
                }
            },
            new Dictionary<string, object?>
            {
                ["tags"] = new[] { "other", "urgent" },
                ["metadata"] = new { center = new { id = "MC-3" }, tier = "bronze" },
                ["services"] = new object[]
                {
                    new { code = "other", active = true }
                },
                ["serviceMatrix"] = new object[]
                {
                    new[] { "urgent" },
                    new[] { "other" }
                }
            });

        await AssertMatchesAsync(
            workflowKey,
            """{ "tags": { "$contains": "health-certificate" } }""",
            [ids[0], ids[1]]);
        await AssertMatchesAsync(
            workflowKey,
            """{ "tags": { "$containsAny": ["arabic", "routine"] } }""",
            [ids[0], ids[1]]);
        await AssertMatchesAsync(
            workflowKey,
            """{ "tags": { "$containsAll": ["health-certificate", "urgent"] } }""",
            [ids[0]]);
        await AssertMatchesAsync(
            workflowKey,
            """{ "metadata": { "$contains": { "center": { "id": "MC-1" } } } }""",
            [ids[0]]);
        await AssertMatchesAsync(
            workflowKey,
            """{ "services": { "$contains": { "code": "health-certificate", "active": true } } }""",
            [ids[0]]);
        await AssertMatchesAsync(
            workflowKey,
            """
            {
              "services": {
                "$containsAny": [
                  { "code": "missing" },
                  { "code": "other", "active": false }
                ]
              }
            }
            """,
            [ids[0]]);
        await AssertMatchesAsync(
            workflowKey,
            """
            {
              "services": {
                "$containsAll": [
                  { "code": "health-certificate" },
                  { "code": "other", "active": false }
                ]
              }
            }
            """,
            [ids[0]]);
        await AssertMatchesAsync(
            workflowKey,
            """
            {
              "serviceMatrix": {
                "$contains": [["health-certificate", "urgent"]]
              }
            }
            """,
            [ids[0]]);
        await AssertMatchesAsync(
            workflowKey,
            """
            {
              "serviceMatrix": {
                "$containsAny": [
                  ["health-certificate", "urgent"],
                  ["health-certificate", "routine"]
                ]
              }
            }
            """,
            [ids[0], ids[1]]);
        await AssertMatchesAsync(
            workflowKey,
            """
            {
              "serviceMatrix": {
                "$containsAll": [
                  ["health-certificate", "urgent"],
                  ["other"]
                ]
              }
            }
            """,
            [ids[0]]);
        await AssertMatchesAsync(
            workflowKey,
            """
            {
              "services": {
                "$elemMatch": {
                  "code": { "$eq": "health-certificate" },
                  "active": { "$eq": true }
                }
              }
            }
            """,
            [ids[0]]);
    }

    [Fact]
    public async Task DottedFieldEscapeAndInjectionShapedInputsAreAlwaysBoundParameters()
    {
        var workflowKey = NewWorkflowKey("advanced-parameterization");
        const string variableName = "request.medicalCenter'); DROP TABLE flowbit.workflow_instances; --";
        const string path = "id') OR TRUE; --";
        const string value = "MC-1042' OR '1'='1";
        var ids = await SeedInstancesAsync(
            workflowKey,
            new Dictionary<string, object?>
            {
                [variableName] = new Dictionary<string, object?> { [path] = value }
            },
            new Dictionary<string, object?>
            {
                [variableName] = new Dictionary<string, object?> { [path] = "different" }
            });
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["$field"] = new Dictionary<string, object?>
            {
                ["$var"] = variableName,
                ["$path"] = new[] { path },
                ["$eq"] = value
            }
        });
        var filter = ParseFilter(json);
        var capture = new CommandCaptureInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.DataSource, FlowbitDatabase.ConfigureProvider)
            .AddInterceptors(capture)
            .Options;

        await using var context = new AppDbContext(options);
        var result = await SearchAsync(
            context,
            workflowKey,
            filter,
            [],
            cursor: null,
            page: 1,
            pageSize: 20);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(ids[0], Assert.Single(result.Items).Id);

        var filterCommands = capture.Commands
            .Where(command => command.CommandText.Contains(
                "advancedVariableFilter",
                StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(filterCommands);
        Assert.All(filterCommands, command =>
        {
            Assert.DoesNotContain(variableName, command.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain(path, command.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain(value, command.CommandText, StringComparison.Ordinal);
        });

        var boundText = string.Join(
            "\n",
            filterCommands.SelectMany(command => command.ParameterText));
        Assert.Contains(variableName, boundText, StringComparison.Ordinal);
        Assert.Contains(path, boundText, StringComparison.Ordinal);
        Assert.Contains(JsonSerializer.Serialize(value), boundText, StringComparison.Ordinal);

        // The query did not broaden itself, and the target table still exists.
        var unfiltered = await SearchAsync(
            context,
            workflowKey,
            variableFilter: null,
            sort: [],
            cursor: null,
            page: 1,
            pageSize: 20);
        Assert.Equal(2, unfiltered.TotalCount);
    }

    private async Task AssertMatchesAsync(
        string workflowKey,
        string json,
        IReadOnlyCollection<long> expectedIds)
    {
        var result = await SearchAsync(
            workflowKey,
            ParseFilter(json),
            [new InstanceSortCriterion(InstanceSortField.Id, SortDirection.Ascending)],
            cursor: null,
            page: 1,
            pageSize: 200);

        Assert.Equal(expectedIds.Count, result.TotalCount);
        Assert.Equal(
            expectedIds.OrderBy(id => id),
            result.Items.Select(item => item.Id));
    }

    private async Task<PagedResult<InstanceListItem>> SearchAsync(
        string workflowKey,
        VariableFilterExpression? variableFilter,
        IReadOnlyList<InstanceSortCriterion> sort,
        string? cursor,
        int page,
        int pageSize)
    {
        await using var context = fixture.CreateDbContext();
        return await SearchAsync(
            context,
            workflowKey,
            variableFilter,
            sort,
            cursor,
            page,
            pageSize);
    }

    private static Task<PagedResult<InstanceListItem>> SearchAsync(
        AppDbContext context,
        string workflowKey,
        VariableFilterExpression? variableFilter,
        IReadOnlyList<InstanceSortCriterion> sort,
        string? cursor,
        int page,
        int pageSize) =>
        new WorkflowRuntimeRepository(context).ListInstancesAsync(
            status: null,
            instanceId: null,
            workflowId: null,
            workflowKey,
            businessKey: null,
            nodeId: null,
            nodeExternalId: null,
            variableFilter,
            sort,
            InstanceListAuthorization.Global,
            cursor,
            includeVariables: false,
            page,
            pageSize,
            CancellationToken.None);

    private async Task<long[]> SeedInstancesAsync(
        string workflowKey,
        params Dictionary<string, object?>[] instanceVariables)
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
        var instances = instanceVariables
            .Select((_, index) => new WorkflowInstanceEntity
            {
                WorkflowDefinitionId = definition.Id,
                WorkflowKey = workflowKey,
                Status = WorkflowInstanceStatuses.Running,
                StartedBy = "advanced-filter-test",
                CreatedAt = now.AddSeconds(index),
                UpdatedAt = now.AddSeconds(index)
            })
            .ToArray();
        setup.WorkflowInstances.AddRange(instances);
        await setup.SaveChangesAsync();

        setup.ExecutionTokens.AddRange(instances.Select(instance => new ExecutionTokenEntity
        {
            InstanceId = instance.Id,
            NodeId = 2,
            NodeName = "Review",
            NodeType = BpmnFlowNodeTypes.UserTask,
            Status = ExecutionTokenStatuses.Active
        }));
        await setup.SaveChangesAsync();

        var variables = instanceVariables.SelectMany((values, index) =>
            values.Select(pair => Variable(instances[index].Id, pair.Key, pair.Value)));
        setup.InstanceVariables.AddRange(variables);
        await setup.SaveChangesAsync();
        return instances.Select(instance => instance.Id).ToArray();
    }

    private async Task AddVariablesAsync(
        long instanceId,
        params (string Name, object? Value)[] variables)
    {
        await using var setup = fixture.CreateDbContext();
        setup.InstanceVariables.AddRange(
            variables.Select(variable => Variable(instanceId, variable.Name, variable.Value)));
        await setup.SaveChangesAsync();
    }

    private static InstanceVariableEntity Variable(
        long instanceId,
        string name,
        object? value) => new()
        {
            InstanceId = instanceId,
            VariableName = name,
            ValueJson = JsonDocument.Parse(JsonSerializer.Serialize(value)),
            SetBy = "advanced-filter-test"
        };

    private static VariableFilterExpression ParseFilter(string json)
    {
        using var document = JsonDocument.Parse(json);
        var expression = VariableFilterParser.Parse(document.RootElement);
        Assert.NotNull(expression);
        return expression!;
    }

    private static string NewWorkflowKey(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}";

    private sealed class CommandCaptureInterceptor : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }

        private void Capture(DbCommand command)
        {
            var parameterText = command.Parameters
                .Cast<DbParameter>()
                .SelectMany(parameter => parameter.Value switch
                {
                    string[] values => values,
                    null or DBNull => [string.Empty],
                    _ => [Convert.ToString(parameter.Value) ?? string.Empty]
                })
                .ToArray();
            Commands.Add(new CapturedCommand(command.CommandText, parameterText));
        }
    }

    private sealed record CapturedCommand(
        string CommandText,
        IReadOnlyList<string> ParameterText);
}
