using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkflowEngine.Infrastructure.Entities;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Infrastructure.Data;

public sealed class DatabaseInitializer(AppDbContext dbContext, ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Applying EF Core database migrations...");
        await dbContext.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Database migrations applied successfully.");
        await BackfillCurrentNodeAsync(cancellationToken);
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

        logger.LogInformation("Backfilling denormalized current-node columns for {Count} stale instance(s)...", stale.Count);

        var definitionIds = stale.Select(i => i.WorkflowDefinitionId).Distinct().ToList();
        var definitions = await dbContext.WorkflowDefinitions
            .Where(d => definitionIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, cancellationToken);

        var backfilled = 0;
        foreach (var instance in stale)
        {
            if (!definitions.TryGetValue(instance.WorkflowDefinitionId, out var definition))
            {
                logger.LogWarning("Backfill: instance {InstanceId} references missing definition {DefinitionId}; skipping.", instance.Id, instance.WorkflowDefinitionId);
                continue;
            }

            WorkflowModelMigrator.Normalize(definition.Definition);
            var node = definition.Definition.FlowNodes.FirstOrDefault(n => n.Id == instance.CurrentStepId);
            if (node is null)
            {
                logger.LogWarning("Backfill: instance {InstanceId} current node #{NodeId} not found in definition; skipping.", instance.Id, instance.CurrentStepId);
                continue;
            }

            instance.CurrentNodeName = node.Name;
            instance.CurrentNodeExternalId = node.ExternalId;
            instance.CurrentNodeType = node.Type;
            instance.CurrentNodeRoles = node.Roles.ToList();
            instance.CurrentRequiresClaim = node.RequiresClaim;
            backfilled++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Backfilled {Backfilled}/{Total} stale instance(s).", backfilled, stale.Count);
    }
}
