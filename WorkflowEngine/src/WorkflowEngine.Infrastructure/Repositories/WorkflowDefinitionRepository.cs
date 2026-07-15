using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WorkflowEngine.Infrastructure.Data;
using WorkflowEngine.Infrastructure.Entities;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Models;
using WorkflowEngine.Service.Services;
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
    private const string KeyDefaultPrefix = "wf:default:";

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

    public async Task<IReadOnlyList<WorkflowDefinitionRecord>> ListVersionsByKeyAsync(string workflowKey, CancellationToken cancellationToken)
    {
        var definitions = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.WorkflowKey == workflowKey)
            .OrderByDescending(w => w.Version)
            .ToListAsync(cancellationToken);

        return definitions.Select(ToRecord).ToList();
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

    public async Task<WorkflowDefinitionRecord?> GetDefaultByWorkflowKeyAsync(string workflowKey, CancellationToken cancellationToken)
    {
        var cacheKey = KeyDefaultPrefix + workflowKey;
        if (cache.TryGetValue(cacheKey, out object? cached))
        {
            return cached is WorkflowDefinitionRecord record ? CloneRecord(record) : null;
        }

        // The default version must also be published so that starting by
        // WorkflowKey always resolves to a startable version. If the default
        // row is not published (e.g. it was unpublished after being set as
        // default), it is treated as "no default" and the caller gets a 404/
        // 400 from the engine. This keeps the "default" and "published"
        // contracts consistent without auto-clearing the flag.
        var entity = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.WorkflowKey == workflowKey && w.IsDefault && w.IsPublished)
            .OrderByDescending(w => w.Version)
            .FirstOrDefaultAsync(cancellationToken);
        var result = entity is null ? null : ToRecord(entity);
        cache.Set(cacheKey, result ?? NullSentinel, CacheOptions);
        return result is null ? null : CloneRecord(result);
    }

    public Task<bool> IsBusinessKeyScopeActiveAsync(string workflowKey, CancellationToken cancellationToken) =>
        dbContext.WorkflowBusinessKeyScopes.AsNoTracking()
            .AnyAsync(scope => scope.WorkflowKey == workflowKey, cancellationToken);

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
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await LockBusinessKeyFamilyAsync(definition.Id, cancellationToken);

        // The first version of a workflow key becomes the default automatically;
        // subsequent versions are not default until explicitly set.
        var hasExisting = await dbContext.WorkflowDefinitions
            .AnyAsync(w => w.WorkflowKey == definition.Id, cancellationToken);
        var isDefault = !hasExisting;
        var hasBusinessKeys = HasBusinessKeys(definition);
        var scopeActive = await dbContext.WorkflowBusinessKeyScopes
            .AnyAsync(scope => scope.WorkflowKey == definition.Id, cancellationToken);
        if (scopeActive && !hasBusinessKeys)
        {
            throw new WorkflowDomainException(
                $"Workflow key '{definition.Id}' has business keys enabled; new versions must configure businessKey on every entry event.");
        }

        var entity = new WorkflowDefinitionEntity
        {
            Name = name,
            WorkflowKey = definition.Id,
            Version = version,
            Definition = definition,
            IsPublished = isPublished,
            IsDefault = isDefault,
            CreatedAt = DateTimeOffset.UtcNow
        };

        dbContext.WorkflowDefinitions.Add(entity);
        if (isDefault && isPublished && hasBusinessKeys && !scopeActive)
        {
            dbContext.WorkflowBusinessKeyScopes.Add(new WorkflowBusinessKeyScopeEntity
            {
                WorkflowKey = definition.Id,
                ActivatedAt = DateTimeOffset.UtcNow
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var record = ToRecord(entity);

        // A new version may become the default, invalidating the
        // previous key→default mapping. Also cache the new id for direct lookups.
        InvalidateDefinition(entity.Id, entity.WorkflowKey);
        if (isDefault)
        {
            cache.Set(KeyDefaultPrefix + entity.WorkflowKey, CloneRecord(record), CacheOptions);
        }
        cache.Set(KeyPrefix + entity.Id, CloneRecord(record), CacheOptions);

        return record;
    }

    public async Task<bool> SetPublishedAsync(long id, bool isPublished, CancellationToken cancellationToken)
    {
        if (isPublished)
        {
            var target = await dbContext.WorkflowDefinitions.AsNoTracking()
                .SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
            if (target is null)
            {
                return false;
            }

            await using var publishTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await LockBusinessKeyFamilyAsync(target.WorkflowKey, cancellationToken);
            var scopeActive = await dbContext.WorkflowBusinessKeyScopes
                .AnyAsync(scope => scope.WorkflowKey == target.WorkflowKey, cancellationToken);
            var hasBusinessKeys = HasBusinessKeys(target.Definition);
            if (scopeActive && !hasBusinessKeys)
            {
                throw new WorkflowDomainException(
                    $"Workflow key '{target.WorkflowKey}' has business keys enabled; an unkeyed version cannot be published.");
            }

            if (target.IsDefault && hasBusinessKeys && !scopeActive)
            {
                dbContext.WorkflowBusinessKeyScopes.Add(new WorkflowBusinessKeyScopeEntity
                {
                    WorkflowKey = target.WorkflowKey,
                    ActivatedAt = DateTimeOffset.UtcNow
                });
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            var publishAffected = await dbContext.WorkflowDefinitions
                .Where(w => w.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(w => w.IsPublished, true), cancellationToken);
            await publishTransaction.CommitAsync(cancellationToken);
            if (publishAffected > 0)
            {
                InvalidateDefinition(id, target.WorkflowKey);
            }
            return publishAffected > 0;
        }

        // Reject unpublishing the current default version: the default must
        // remain published so WorkflowKey resolution always yields a startable
        // version. The caller should set a different default first.
        if (!isPublished)
        {
            var target = await dbContext.WorkflowDefinitions.AsNoTracking()
                .Where(w => w.Id == id)
                .Select(w => new { w.WorkflowKey, w.IsDefault })
                .SingleOrDefaultAsync(cancellationToken);
            if (target is null)
            {
                return false;
            }
            if (target.IsDefault)
            {
                throw new WorkflowDomainException(
                    "Cannot unpublish the default version. Set a different version as default first.");
            }
        }

        var affected = await dbContext.WorkflowDefinitions
            .Where(w => w.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(w => w.IsPublished, isPublished),
                cancellationToken);

        if (affected > 0)
        {
            // Publishing or unpublishing may affect which version is the effective
            // default for starting; load the row for its WorkflowKey so caches can
            // be invalidated.
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

    public async Task<bool> SetDefaultAsync(long id, bool isDefault, CancellationToken cancellationToken)
    {
        // Load the workflow key first so we can clear any existing default for
        // the same key before setting the new one (at most one default per key).
        var entity = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.WorkflowKey, w.IsPublished, w.Definition })
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            return false;
        }

        // The default version must be published so WorkflowKey resolution
        // always yields a startable version. Reject setting an unpublished
        // version as the default.
        if (isDefault && !entity.IsPublished)
        {
            throw new WorkflowDomainException(
                "Cannot set an unpublished version as default. Publish it first.");
        }

        // Find the existing default id (if any) so its per-id cache can be
        // invalidated after the swap (the cached record still says IsDefault=true).
        var previousDefault = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.WorkflowKey == entity.WorkflowKey && w.IsDefault && w.Id != id)
            .Select(w => new { w.Id })
            .SingleOrDefaultAsync(cancellationToken);

        // Wrap the clear-then-set in a transaction so concurrent requests cannot
        // leave multiple default rows for the same WorkflowKey.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await LockBusinessKeyFamilyAsync(entity.WorkflowKey, cancellationToken);

        if (isDefault)
        {
            var scopeActive = await dbContext.WorkflowBusinessKeyScopes
                .AnyAsync(scope => scope.WorkflowKey == entity.WorkflowKey, cancellationToken);
            var hasBusinessKeys = HasBusinessKeys(entity.Definition);
            if (scopeActive && !hasBusinessKeys)
            {
                throw new WorkflowDomainException(
                    $"Workflow key '{entity.WorkflowKey}' has business keys enabled; an unkeyed version cannot become default.");
            }

            if (hasBusinessKeys && !scopeActive)
            {
                dbContext.WorkflowBusinessKeyScopes.Add(new WorkflowBusinessKeyScopeEntity
                {
                    WorkflowKey = entity.WorkflowKey,
                    ActivatedAt = DateTimeOffset.UtcNow
                });
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        if (isDefault && previousDefault is not null)
        {
            await dbContext.WorkflowDefinitions
                .Where(w => w.WorkflowKey == entity.WorkflowKey && w.IsDefault && w.Id != id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(w => w.IsDefault, false),
                    cancellationToken);
        }

        var affected = await dbContext.WorkflowDefinitions
            .Where(w => w.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(w => w.IsDefault, isDefault),
                cancellationToken);

        if (affected > 0)
        {
            InvalidateDefinition(id, entity.WorkflowKey);
            if (previousDefault is not null)
            {
                // The old default's per-id cache still reports IsDefault=true;
                // drop it so the next GetAsync reloads the updated row.
                cache.Remove(KeyPrefix + previousDefault.Id);
            }
            if (isDefault)
            {
                // Pre-seed the default cache with the newly-defaulted row (reloaded
                // from the database so IsDefault is current).
                var updated = await dbContext.WorkflowDefinitions.AsNoTracking()
                    .SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
                if (updated is not null)
                {
                    cache.Set(KeyDefaultPrefix + entity.WorkflowKey, CloneRecord(ToRecord(updated)), CacheOptions);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return affected > 0;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.WorkflowKey, w.IsDefault })
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            return false;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await LockBusinessKeyFamilyAsync(entity.WorkflowKey, cancellationToken);
        entity = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.WorkflowKey, w.IsDefault })
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        var scopeActive = await dbContext.WorkflowBusinessKeyScopes.AsNoTracking()
            .AnyAsync(scope => scope.WorkflowKey == entity.WorkflowKey, cancellationToken);

        var affected = await dbContext.WorkflowDefinitions
            .Where(w => w.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        if (affected > 0 && entity is not null)
        {
            InvalidateDefinition(id, entity.WorkflowKey);

            // If the deleted row was the default, auto-assign the default to the
            // highest-version remaining published row for the same key. Prefer
            // published so the successor is immediately startable; fall back to
            // the highest-version row if none are published.
            if (entity.IsDefault)
            {
                var candidates = await dbContext.WorkflowDefinitions.AsNoTracking()
                    .Where(w => w.WorkflowKey == entity.WorkflowKey)
                    .OrderByDescending(w => w.Version)
                    .ToListAsync(cancellationToken);
                if (scopeActive)
                {
                    candidates = candidates.Where(candidate => HasBusinessKeys(candidate.Definition)).ToList();
                }
                var successor = candidates.FirstOrDefault(candidate => candidate.IsPublished)
                    ?? candidates.FirstOrDefault();

                if (successor is not null)
                {
                    await dbContext.WorkflowDefinitions
                        .Where(w => w.Id == successor.Id)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(w => w.IsDefault, true),
                            cancellationToken);

                    // Reload the successor so the cached record reflects the
                    // updated IsDefault=true (the snapshot above predates the update).
                    var reloaded = await dbContext.WorkflowDefinitions.AsNoTracking()
                        .SingleOrDefaultAsync(w => w.Id == successor.Id, cancellationToken);
                    if (reloaded is not null)
                    {
                        cache.Set(KeyDefaultPrefix + entity.WorkflowKey, CloneRecord(ToRecord(reloaded)), CacheOptions);
                        cache.Set(KeyPrefix + successor.Id, CloneRecord(ToRecord(reloaded)), CacheOptions);
                    }
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return affected > 0;
    }

    private void InvalidateDefinition(long id, string workflowKey)
    {
        cache.Remove(KeyPrefix + id);
        cache.Remove(KeyDefaultPrefix + workflowKey);
    }

    // A sentinel stored in the cache to represent a known-missing definition so
    // repeated lookups for a non-existent id don't hit the database.
    private static readonly object NullSentinel = new();

    private Task LockBusinessKeyFamilyAsync(string workflowKey, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('workflow-business-key-scope'), hashtext({workflowKey}))",
            cancellationToken);

    private static bool HasBusinessKeys(WorkflowModel definition) =>
        definition.FlowNodes.Any(node => BpmnFlowNodeTypes.IsEntry(node.Type) && node.BusinessKey is not null);

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
        return new(entity.Id, entity.Name, entity.WorkflowKey, entity.Version, entity.Definition, entity.IsPublished, entity.IsDefault, entity.CreatedAt);
    }
}
