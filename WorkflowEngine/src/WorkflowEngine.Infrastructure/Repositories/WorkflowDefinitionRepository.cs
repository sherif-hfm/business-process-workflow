using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WorkflowEngine.Infrastructure.Data;
using WorkflowEngine.Infrastructure.Entities;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Models;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Infrastructure.Repositories;

public sealed class WorkflowDefinitionRepository(AppDbContext dbContext, IMemoryCache cache) : IWorkflowDefinitionRepository
{
    private static readonly JsonSerializerOptions CloneOptions = new(JsonSerializerDefaults.Web);

    // Cache entry options: definitions are immutable once published, so a long
    // sliding expiration is safe. Compaction under memory pressure is handled by
    // the built-in MemoryCacheEntryOptions.CompactionPercentage.
    private static readonly MemoryCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(30),
        Size = 1
    };

    private const string KeyPrefix = "wf:def:";
    private const string KeyLatestPrefix = "wf:latest:";

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
        var cacheKey = KeyPrefix + id;
        if (cache.TryGetValue(cacheKey, out object? cached))
        {
            return cached is WorkflowDefinitionRecord record ? CloneRecord(record) : null;
        }

        var entity = await dbContext.WorkflowDefinitions.AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        var result = entity is null ? null : ToRecord(entity);
        cache.Set(cacheKey, result ?? NullSentinel, CacheOptions);
        return result is null ? null : CloneRecord(result);
    }

    public async Task<WorkflowDefinitionRecord?> GetLatestPublishedByWorkflowKeyAsync(string workflowKey, CancellationToken cancellationToken)
    {
        var cacheKey = KeyLatestPrefix + workflowKey;
        if (cache.TryGetValue(cacheKey, out object? cached))
        {
            return cached is WorkflowDefinitionRecord record ? CloneRecord(record) : null;
        }

        var entity = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.WorkflowKey == workflowKey && w.IsPublished)
            .OrderByDescending(w => w.Version)
            .FirstOrDefaultAsync(cancellationToken);
        var result = entity is null ? null : ToRecord(entity);
        cache.Set(cacheKey, result ?? NullSentinel, CacheOptions);
        return result is null ? null : CloneRecord(result);
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
        var record = ToRecord(entity);

        // A new version may become the latest published, invalidating the
        // previous key→latest mapping. Also cache the new id for direct lookups.
        InvalidateDefinition(entity.Id, entity.WorkflowKey);
        if (isPublished)
        {
            cache.Set(KeyPrefix + entity.Id, CloneRecord(record), CacheOptions);
            cache.Set(KeyLatestPrefix + entity.WorkflowKey, CloneRecord(record), CacheOptions);
        }

        return record;
    }

    public async Task<bool> SetPublishedAsync(long id, bool isPublished, CancellationToken cancellationToken)
    {
        var affected = await dbContext.WorkflowDefinitions
            .Where(w => w.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(w => w.IsPublished, isPublished),
                cancellationToken);

        if (affected > 0)
        {
            // Publishing or unpublishing changes the latest-published mapping; load
            // the row for its WorkflowKey so the key→latest cache can be invalidated.
            var entity = await dbContext.WorkflowDefinitions.AsNoTracking()
                .Where(w => w.Id == id)
                .Select(w => new { w.WorkflowKey })
                .SingleOrDefaultAsync(cancellationToken);
            if (entity is not null)
            {
                InvalidateDefinition(id, entity.WorkflowKey);
            }
        }

        return affected > 0;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.WorkflowKey })
            .SingleOrDefaultAsync(cancellationToken);

        var affected = await dbContext.WorkflowDefinitions
            .Where(w => w.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        if (affected > 0 && entity is not null)
        {
            InvalidateDefinition(id, entity.WorkflowKey);
        }

        return affected > 0;
    }

    private void InvalidateDefinition(long id, string workflowKey)
    {
        cache.Remove(KeyPrefix + id);
        cache.Remove(KeyLatestPrefix + workflowKey);
    }

    // A sentinel stored in the cache to represent a known-missing definition so
    // repeated lookups for a non-existent id don't hit the database.
    private static readonly object NullSentinel = new();

    // Deep-clones a record so callers can safely mutate the WorkflowModel
    // (WorkflowModelMigrator.Normalize runs on every ToRecord) without
    // corrupting the cached instance.
    private static WorkflowDefinitionRecord CloneRecord(WorkflowDefinitionRecord record)
    {
        var json = JsonSerializer.Serialize(record, CloneOptions);
        return JsonSerializer.Deserialize<WorkflowDefinitionRecord>(json, CloneOptions)!;
    }

    private static WorkflowDefinitionRecord ToRecord(WorkflowDefinitionEntity entity)
    {
        WorkflowModelMigrator.Normalize(entity.Definition);
        return new(entity.Id, entity.Name, entity.WorkflowKey, entity.Version, entity.Definition, entity.IsPublished, entity.CreatedAt);
    }
}
