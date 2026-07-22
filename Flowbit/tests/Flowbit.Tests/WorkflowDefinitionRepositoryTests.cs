using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class WorkflowDefinitionRepositoryTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task GetManyAsync_queries_distinct_cache_misses_once_and_returns_safe_clones()
    {
        var suffix = Guid.NewGuid().ToString("N");
        long firstId;
        long secondId;

        await using (var setup = fixture.CreateDbContext())
        {
            var definitions = new[]
            {
                new WorkflowDefinitionEntity
                {
                    Name = $"first-{suffix}",
                    WorkflowKey = $"first-{suffix}",
                    Version = 1,
                    IsPublished = true,
                    Definition = new WorkflowModel { Id = $"first-{suffix}", Name = "First" }
                },
                new WorkflowDefinitionEntity
                {
                    Name = $"second-{suffix}",
                    WorkflowKey = $"second-{suffix}",
                    Version = 1,
                    IsPublished = true,
                    Definition = new WorkflowModel { Id = $"second-{suffix}", Name = "Second" }
                }
            };
            setup.WorkflowDefinitions.AddRange(definitions);
            await setup.SaveChangesAsync();
            firstId = definitions[0].Id;
            secondId = definitions[1].Id;
        }

        var counter = new ReaderCommandCounter();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.DataSource, FlowbitDatabase.ConfigureProvider)
            .AddInterceptors(counter)
            .Options;
        await using var measured = new AppDbContext(options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = new WorkflowDefinitionRepository(measured, cache);
        const long missingId = long.MaxValue;

        var firstRead = await repository.GetManyAsync(
            [firstId, firstId, secondId, missingId],
            CancellationToken.None);

        Assert.Equal(1, counter.ReaderCommands);
        Assert.Equal(2, firstRead.Count);
        Assert.DoesNotContain(missingId, firstRead.Keys);

        firstRead[firstId].Definition.Name = "Mutated by caller";
        var secondRead = await repository.GetManyAsync(
            [missingId, secondId, firstId],
            CancellationToken.None);
        var missing = await repository.GetAsync(missingId, CancellationToken.None);

        Assert.Equal(1, counter.ReaderCommands);
        Assert.Equal("First", secondRead[firstId].Definition.Name);
        Assert.Null(missing);
    }

    private sealed class ReaderCommandCounter : DbCommandInterceptor
    {
        public int ReaderCommands { get; private set; }

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
