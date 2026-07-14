using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WorkflowEngine.Infrastructure.Data;
using WorkflowEngine.Infrastructure.Entities;
using WorkflowEngine.Infrastructure.Repositories;
using WorkflowEngine.Shared.Models;
using Xunit;

namespace WorkflowEngine.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class RepositoryProjectionTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task Aggregate_projection_query_counts_do_not_grow_with_execution_or_item_count()
    {
        const int executionCount = 12;
        const int itemsPerExecution = 40;
        var suffix = Guid.NewGuid().ToString("N");

        await using (var setup = fixture.CreateDbContext())
        {
            var definition = new WorkflowDefinitionEntity
            {
                Name = $"projection-{suffix}",
                WorkflowKey = $"projection-{suffix}",
                Version = 1,
                IsPublished = true,
                Definition = new WorkflowModel
                {
                    Id = $"projection-{suffix}",
                    Name = $"projection-{suffix}"
                }
            };
            setup.WorkflowDefinitions.Add(definition);
            await setup.SaveChangesAsync();

            var instances = Enumerable.Range(0, executionCount)
                .Select(_ => new WorkflowInstanceEntity
                {
                    WorkflowDefinitionId = definition.Id,
                    Status = "running"
                })
                .ToList();
            setup.WorkflowInstances.AddRange(instances);
            await setup.SaveChangesAsync();

            var tokens = instances.Select(instance => new ExecutionTokenEntity
            {
                InstanceId = instance.Id,
                NodeId = 2,
                NodeName = "Review",
                NodeType = "userTask",
                Status = ExecutionTokenStatuses.Active
            }).ToList();
            setup.ExecutionTokens.AddRange(tokens);
            await setup.SaveChangesAsync();

            var executions = tokens.Select((token, index) => new MultiInstanceExecutionEntity
            {
                InstanceId = instances[index].Id,
                TokenId = token.Id,
                NodeId = token.NodeId,
                Mode = "sequential",
                Source = "cardinality",
                ResultVariable = "results",
                Status = MultiInstanceExecutionStatuses.Active,
                TotalCount = itemsPerExecution
            }).ToList();
            setup.MultiInstanceExecutions.AddRange(executions);
            await setup.SaveChangesAsync();

            foreach (var execution in executions)
            {
                for (var itemIndex = 0; itemIndex < itemsPerExecution; itemIndex++)
                {
                    setup.UserTasks.Add(new UserTaskEntity
                    {
                        InstanceId = execution.InstanceId,
                        TokenId = execution.TokenId,
                        NodeId = execution.NodeId,
                        NodeName = "Review",
                        Status = itemIndex == 0 ? UserTaskStatuses.Active : UserTaskStatuses.Pending,
                        RequiresClaim = true,
                        MultiInstanceExecutionId = execution.Id,
                        ItemIndex = itemIndex
                    });
                }

                setup.MultiInstanceFlowCounts.Add(new MultiInstanceFlowCountEntity
                {
                    ExecutionId = execution.Id,
                    FlowId = 10,
                    CompletedCount = 0
                });
            }
            await setup.SaveChangesAsync();
        }

        await using var idsContext = fixture.CreateDbContext();
        var executionIds = await idsContext.MultiInstanceExecutions.AsNoTracking()
            .Where(execution => execution.Instance!.WorkflowDefinition!.WorkflowKey == $"projection-{suffix}")
            .Select(execution => execution.Id)
            .ToListAsync();
        var instanceIds = await idsContext.WorkflowInstances.AsNoTracking()
            .Where(instance => instance.WorkflowDefinition!.WorkflowKey == $"projection-{suffix}")
            .Select(instance => instance.Id)
            .ToListAsync();

        var counter = new ReaderCommandCounter();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.DataSource)
            .AddInterceptors(counter)
            .Options;
        await using var measured = new AppDbContext(options);
        var repository = new WorkflowRuntimeRepository(measured);

        var progress = await repository.GetMultiInstanceProgressAsync(executionIds, CancellationToken.None);
        Assert.Equal(executionCount, progress.Count);
        Assert.All(progress.Values, item =>
        {
            Assert.Equal(1, item.ActiveCount);
            Assert.Equal(itemsPerExecution - 1, item.PendingCount);
        });
        Assert.Equal(2, counter.ReaderCommands);

        counter.Reset();
        var summaries = await repository.GetUserTaskWorkSummariesAsync(instanceIds, CancellationToken.None);
        Assert.Equal(executionCount, summaries.Count);
        Assert.All(summaries.Values, item =>
        {
            Assert.True(item.IsMultiInstance);
            Assert.Equal(1, item.ActiveCount);
            Assert.Equal(itemsPerExecution - 1, item.PendingCount);
        });
        Assert.Equal(2, counter.ReaderCommands);
    }

    private sealed class ReaderCommandCounter : DbCommandInterceptor
    {
        public int ReaderCommands { get; private set; }

        public void Reset() => ReaderCommands = 0;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommands++;
            return ValueTask.FromResult(result);
        }
    }
}
