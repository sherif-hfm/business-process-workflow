using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkflowEngine.Infrastructure.Entities;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Infrastructure.Data;

public sealed class DatabaseInitializer(AppDbContext dbContext)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task ApplyMigrationsAndSeedAsync(
        string seedWorkflowPath,
        CancellationToken cancellationToken = default)
    {
        await dbContext.Database.MigrateAsync(cancellationToken);

        if (await dbContext.WorkflowDefinitions.AnyAsync(cancellationToken))
        {
            return;
        }

        if (!File.Exists(seedWorkflowPath))
        {
            return;
        }

        await using var stream = File.OpenRead(seedWorkflowPath);
        var workflow = await JsonSerializer.DeserializeAsync<WorkflowModel>(
            stream,
            JsonOptions,
            cancellationToken);

        if (workflow is null || string.IsNullOrWhiteSpace(workflow.Name))
        {
            return;
        }

        dbContext.WorkflowDefinitions.Add(new WorkflowDefinitionEntity
        {
            Name = workflow.Name,
            Version = 1,
            Definition = workflow,
            IsPublished = true,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
