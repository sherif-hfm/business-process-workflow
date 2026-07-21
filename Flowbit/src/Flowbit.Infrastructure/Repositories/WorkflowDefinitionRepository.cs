using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;

namespace Flowbit.Infrastructure.Repositories;

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
    public async Task<IReadOnlyList<WorkflowDefinitionRecord>> ListLatestAsync(CancellationToken cancellationToken)
    {
        var definitions = await dbContext.WorkflowDefinitions.AsNoTracking()
            .OrderBy(w => w.Name)
            .ToListAsync(cancellationToken);

        return definitions
            .GroupBy(w => w.WorkflowKey)
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

    public async Task<IReadOnlyDictionary<long, WorkflowDefinitionRecord>> GetManyAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<long, WorkflowDefinitionRecord>();
        }

        var distinctIds = ids.Distinct().ToArray();
        var results = new Dictionary<long, WorkflowDefinitionRecord>(distinctIds.Length);
        var missingIds = new List<long>(distinctIds.Length);

        foreach (var id in distinctIds)
        {
            if (!cache.TryGetValue(KeyPrefix + id, out object? cached))
            {
                missingIds.Add(id);
                continue;
            }

            if (cached is WorkflowDefinitionRecord record)
            {
                results.Add(id, CloneRecord(record));
            }
        }

        if (missingIds.Count == 0)
        {
            return results;
        }

        var entities = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(definition => missingIds.Contains(definition.Id))
            .ToListAsync(cancellationToken);
        var recordsById = entities
            .Select(ToRecord)
            .ToDictionary(record => record.Id);

        foreach (var id in missingIds)
        {
            if (!recordsById.TryGetValue(id, out var record))
            {
                cache.Set(KeyPrefix + id, NullSentinel, CacheOptions);
                continue;
            }

            cache.Set(KeyPrefix + id, record, CacheOptions);
            results.Add(id, CloneRecord(record));
        }

        return results;
    }

    public async Task<WorkflowDefinitionRecord?> GetPublishedAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowDefinitions.AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == id && w.IsPublished, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<WorkflowDefinitionRecord?> GetDefaultByWorkflowKeyAsync(string workflowKey, CancellationToken cancellationToken)
    {
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
        return entity is null ? null : ToRecord(entity);
    }

    public Task LockFamilyForStartAsync(string workflowKey, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock_shared(hashtext('workflow-family'), hashtext({workflowKey}))",
            cancellationToken);

    public Task<bool> IsBusinessKeyScopeActiveAsync(string workflowKey, CancellationToken cancellationToken) =>
        dbContext.WorkflowBusinessKeyScopes.AsNoTracking()
            .AnyAsync(scope => scope.WorkflowKey == workflowKey, cancellationToken);

    public async Task<WorkflowDefinitionRecord> AddAsync(
        string name,
        WorkflowModel definition,
        bool isPublished,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await LockWorkflowFamilyAsync(definition.Id, cancellationToken);

        var latestVersion = await dbContext.WorkflowDefinitions
            .Where(w => w.WorkflowKey == definition.Id)
            .Select(w => (int?)w.Version)
            .MaxAsync(cancellationToken) ?? 0;
        var version = latestVersion + 1;

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

        // Cache the immutable definition snapshot for direct id lookups only.
        InvalidateDefinition(entity.Id, entity.WorkflowKey);
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
            await LockWorkflowFamilyAsync(target.WorkflowKey, cancellationToken);
            target = await dbContext.WorkflowDefinitions.AsNoTracking()
                .SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
            if (target is null)
            {
                await publishTransaction.CommitAsync(cancellationToken);
                return false;
            }
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

        var unpublishTarget = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.WorkflowKey })
            .SingleOrDefaultAsync(cancellationToken);
        if (unpublishTarget is null)
        {
            return false;
        }

        await using var unpublishTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await LockWorkflowFamilyAsync(unpublishTarget.WorkflowKey, cancellationToken);
        var lockedTarget = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.WorkflowKey, w.IsDefault })
            .SingleOrDefaultAsync(cancellationToken);
        if (lockedTarget is null)
        {
            await unpublishTransaction.CommitAsync(cancellationToken);
            return false;
        }
        if (lockedTarget.IsDefault)
        {
            throw new WorkflowDomainException(
                "Cannot unpublish the default version. Set a different version as default first.");
        }

        var affected = await dbContext.WorkflowDefinitions
            .Where(w => w.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(w => w.IsPublished, false),
                cancellationToken);
        await unpublishTransaction.CommitAsync(cancellationToken);
        if (affected > 0)
        {
            InvalidateDefinition(id, lockedTarget.WorkflowKey);
        }

        return affected > 0;
    }

    public async Task<bool> SetDefaultAsync(long id, bool isDefault, CancellationToken cancellationToken)
    {
        // Load the workflow key first so we can clear any existing default for
        // the same key before setting the new one (at most one default per key).
        var targetKey = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => w.WorkflowKey)
            .SingleOrDefaultAsync(cancellationToken);
        if (targetKey is null)
        {
            return false;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await LockWorkflowFamilyAsync(targetKey, cancellationToken);

        var entity = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.WorkflowKey, w.IsPublished, w.Definition })
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        if (isDefault && !entity.IsPublished)
        {
            throw new WorkflowDomainException(
                "Cannot set an unpublished version as default. Publish it first.");
        }

        var previousDefault = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.WorkflowKey == entity.WorkflowKey && w.IsDefault && w.Id != id)
            .Select(w => new { w.Id })
            .SingleOrDefaultAsync(cancellationToken);

        if (isDefault)
        {
            if (entity.Definition.TaskDistribution is null
                && await HasRunningRequiredAssignmentInstancesAsync(entity.WorkflowKey, cancellationToken))
            {
                throw new WorkflowDomainException(
                    "Cannot set this version as default while running instances contain required-assignment tasks because it has no taskDistribution credentials.");
            }

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

        await transaction.CommitAsync(cancellationToken);

        if (affected > 0)
        {
            InvalidateDefinition(id, entity.WorkflowKey);
            if (previousDefault is not null)
            {
                cache.Remove(KeyPrefix + previousDefault.Id);
            }
        }

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
        await LockWorkflowFamilyAsync(entity.WorkflowKey, cancellationToken);
        entity = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.WorkflowKey, w.IsDefault })
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        var workflowKey = entity.WorkflowKey;
        var scopeActive = await dbContext.WorkflowBusinessKeyScopes.AsNoTracking()
            .AnyAsync(scope => scope.WorkflowKey == workflowKey, cancellationToken);
        var requiresDistributor = entity.IsDefault
            && await HasRunningRequiredAssignmentInstancesAsync(workflowKey, cancellationToken);
        long? successorId = null;

        List<WorkflowDefinitionEntity>? successorCandidates = null;
        if (entity.IsDefault)
        {
            successorCandidates = await dbContext.WorkflowDefinitions.AsNoTracking()
                .Where(w => w.WorkflowKey == entity.WorkflowKey && w.Id != id)
                .OrderByDescending(w => w.Version)
                .ToListAsync(cancellationToken);
            if (scopeActive)
            {
                successorCandidates = successorCandidates
                    .Where(candidate => HasBusinessKeys(candidate.Definition))
                    .ToList();
            }
            if (requiresDistributor)
            {
                successorCandidates = successorCandidates
                    .Where(candidate => candidate.Definition.TaskDistribution is not null)
                    .ToList();
            }

            if (requiresDistributor && successorCandidates.All(candidate => !candidate.IsPublished))
            {
                throw new WorkflowDomainException(
                    "Cannot delete the default version while running instances contain required-assignment tasks unless another published version configures taskDistribution credentials.");
            }
        }

        var affected = await dbContext.WorkflowDefinitions
            .Where(w => w.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        if (affected > 0)
        {
            // If the deleted row was the default, auto-assign the default to the
            // highest-version remaining published row for the same key. If none
            // are published the family intentionally has no effective default.
            if (entity.IsDefault)
            {
                var successor = successorCandidates!.FirstOrDefault(candidate => candidate.IsPublished);

                if (successor is not null)
                {
                    successorId = successor.Id;
                    await dbContext.WorkflowDefinitions
                        .Where(w => w.Id == successor.Id)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(w => w.IsDefault, true),
                            cancellationToken);

                }
            }
        }

        await transaction.CommitAsync(cancellationToken);

        if (affected > 0)
        {
            InvalidateDefinition(id, workflowKey);
            if (successorId is not null)
            {
                cache.Remove(KeyPrefix + successorId.Value);
            }
        }

        return affected > 0;
    }

    private async Task<bool> HasRunningRequiredAssignmentInstancesAsync(
        string workflowKey,
        CancellationToken cancellationToken)
    {
        var definitionIds = await dbContext.WorkflowInstances.AsNoTracking()
            .Where(instance => instance.WorkflowKey == workflowKey
                && instance.Status == WorkflowInstanceStatuses.Running)
            .Select(instance => instance.WorkflowDefinitionId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (definitionIds.Count == 0)
        {
            return false;
        }

        var activeDefinitions = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(definition => definitionIds.Contains(definition.Id))
            .Select(definition => definition.Definition)
            .ToListAsync(cancellationToken);
        return activeDefinitions.Any(definition => definition.FlowNodes.Any(node =>
            BpmnFlowNodeTypes.IsUserTask(node.Type) && node.RequiresAssignment));
    }

    private void InvalidateDefinition(long id, string _)
    {
        cache.Remove(KeyPrefix + id);
    }

    // A sentinel stored in the cache to represent a known-missing definition so
    // repeated lookups for a non-existent id don't hit the database.
    private static readonly object NullSentinel = new();

    private Task LockWorkflowFamilyAsync(string workflowKey, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('workflow-family'), hashtext({workflowKey}))",
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
