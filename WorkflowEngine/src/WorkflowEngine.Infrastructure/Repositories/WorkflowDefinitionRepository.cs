using Microsoft.EntityFrameworkCore;
using WorkflowEngine.Infrastructure.Data;
using WorkflowEngine.Infrastructure.Entities;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Models;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Infrastructure.Repositories;

public sealed class WorkflowDefinitionRepository(AppDbContext dbContext) : IWorkflowDefinitionRepository
{
    public async Task<IReadOnlyList<WorkflowDefinitionRecord>> ListLatestAsync(CancellationToken cancellationToken)
    {
        var definitions = await dbContext.WorkflowDefinitions.AsNoTracking()
            .OrderBy(w => w.Name)
            .ToListAsync(cancellationToken);

        return definitions
            .GroupBy(w => w.Name)
            .Select(group => group.OrderByDescending(w => w.Version).First())
            .Select(ToRecord)
            .ToList();
    }

    public async Task<WorkflowDefinitionRecord?> GetAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowDefinitions.AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<WorkflowDefinitionRecord?> GetLatestPublishedByWorkflowKeyAsync(string workflowKey, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.WorkflowKey == workflowKey && w.IsPublished)
            .OrderByDescending(w => w.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<int> GetLatestVersionAsync(string name, CancellationToken cancellationToken) =>
        await dbContext.WorkflowDefinitions
            .Where(w => w.Name == name)
            .Select(w => (int?)w.Version)
            .MaxAsync(cancellationToken) ?? 0;

    public async Task<WorkflowDefinitionRecord> AddAsync(
        string name,
        int version,
        WorkflowModel definition,
        bool isPublished,
        CancellationToken cancellationToken)
    {
        var entity = new WorkflowDefinitionEntity
        {
            Name = name,
            WorkflowKey = definition.Id,
            Version = version,
            Definition = definition,
            IsPublished = isPublished,
            CreatedAt = DateTimeOffset.UtcNow
        };

        dbContext.WorkflowDefinitions.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<bool> SetPublishedAsync(long id, bool isPublished, CancellationToken cancellationToken)
    {
        var affected = await dbContext.WorkflowDefinitions
            .Where(w => w.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(w => w.IsPublished, isPublished),
                cancellationToken);
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var affected = await dbContext.WorkflowDefinitions
            .Where(w => w.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
        return affected > 0;
    }

    private static WorkflowDefinitionRecord ToRecord(WorkflowDefinitionEntity entity)
    {
        WorkflowModelMigrator.Normalize(entity.Definition);
        return new(entity.Id, entity.Name, entity.WorkflowKey, entity.Version, entity.Definition, entity.IsPublished, entity.CreatedAt);
    }
}
