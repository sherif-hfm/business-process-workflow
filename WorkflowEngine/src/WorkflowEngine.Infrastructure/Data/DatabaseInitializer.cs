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
        await BackfillCurrentNodeAsync(cancellationToken);

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

        WorkflowModelMigrator.Normalize(workflow);

        dbContext.WorkflowDefinitions.Add(new WorkflowDefinitionEntity
        {
            Name = workflow.Name,
            WorkflowKey = workflow.Id,
            Version = 1,
            Definition = workflow,
            IsPublished = true,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Stamp the denormalized current-node columns onto instances created before
    // the columns existed (identified by an empty CurrentNodeType). Idempotent.
    private async Task BackfillCurrentNodeAsync(CancellationToken cancellationToken)
    {
        var stale = await dbContext.WorkflowInstances
            .Where(i => i.CurrentNodeType == string.Empty)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return;
        }

        var definitionIds = stale.Select(i => i.WorkflowDefinitionId).Distinct().ToList();
        var definitions = await dbContext.WorkflowDefinitions
            .Where(d => definitionIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, cancellationToken);

        foreach (var instance in stale)
        {
            if (!definitions.TryGetValue(instance.WorkflowDefinitionId, out var definition))
            {
                continue;
            }

            WorkflowModelMigrator.Normalize(definition.Definition);
            var node = definition.Definition.FlowNodes.FirstOrDefault(n => n.Id == instance.CurrentStepId);
            if (node is null)
            {
                continue;
            }

            instance.CurrentNodeName = node.Name;
            instance.CurrentNodeExternalId = node.ExternalId;
            instance.CurrentNodeType = node.Type;
            instance.CurrentNodeRoles = node.Roles.ToList();
            instance.CurrentRequiresClaim = node.RequiresClaim;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
