using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowbit.Infrastructure.Repositories;

public sealed class EngineSettingsRepository(AppDbContext dbContext) : IEngineSettingsRepository
{
    private const string ProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken)
    {
        var entity = await QueryByLogicalKey(key)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null ? null : MapToRecord(entity);
    }

    public async Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(
        string pattern,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.EngineSettings
            .AsNoTracking()
            .Where(s =>
                (s.Namespace != null && s.Namespace.Trim() != "" &&
                    EF.Functions.ILike(s.Namespace.Trim() + "." + s.Key.Trim(), pattern)) ||
                ((s.Namespace == null || s.Namespace.Trim() == "") &&
                    EF.Functions.ILike(s.Key.Trim(), pattern)))
            .ToListAsync(cancellationToken);

        return entities.Select(MapToRecord).ToList();
    }

    public async Task<EngineSettingRecord> SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        var (settingNamespace, name) = SplitCanonicalKey(key);
        var entity = await QueryByLogicalKey(key)
            .FirstOrDefaultAsync(cancellationToken);

        var now = UtcNow();
        if (entity is null)
        {
            entity = new EngineSettingEntity
            {
                Namespace = settingNamespace,
                Key = name,
                Value = value,
                CreatedAt = now,
                UpdatedAt = now
            };
            dbContext.EngineSettings.Add(entity);
        }
        else
        {
            entity.Value = value;
            entity.UpdatedAt = NextTimestamp(entity.UpdatedAt);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return MapToRecord(entity);
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var entity = await QueryByLogicalKey(key)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity is null)
        {
            return false;
        }

        dbContext.EngineSettings.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<EngineSettingRecord>> ListAsync(
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.EngineSettings
            .AsNoTracking()
            .OrderBy(setting => setting.Namespace ?? string.Empty)
            .ThenBy(setting => setting.Key)
            .ThenBy(setting => setting.Id)
            .ToListAsync(cancellationToken);

        return entities.Select(MapToRecord).ToList();
    }

    public async Task<EngineSettingRecord> CreateAsync(
        string? settingNamespace,
        string key,
        string value,
        string? description,
        CancellationToken cancellationToken)
    {
        var ownsTransaction = IsNpgsql() && dbContext.Database.CurrentTransaction is null;
        await using var ownedTransaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        if (IsNpgsql())
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "LOCK TABLE \"flowbit\".\"engine_settings\" IN SHARE ROW EXCLUSIVE MODE",
                cancellationToken);
        }

        var logicalKey = BuildLogicalKey(settingNamespace, key);
        var existingIdentifiers = await dbContext.EngineSettings
            .AsNoTracking()
            .Select(setting => new { setting.Namespace, setting.Key })
            .ToListAsync(cancellationToken);
        var duplicate = existingIdentifiers.Any(setting =>
            string.Equals(
                BuildLogicalKey(setting.Namespace, setting.Key),
                logicalKey,
                StringComparison.Ordinal));
        if (duplicate)
        {
            throw DuplicateConflict(logicalKey);
        }

        var now = UtcNow();
        var entity = new EngineSettingEntity
        {
            Namespace = settingNamespace,
            Key = key,
            Value = value,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.EngineSettings.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw DuplicateConflict(logicalKey);
        }

        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken);
        }

        return MapToRecord(entity);
    }

    public async Task<EngineSettingRecord?> UpdateAsync(
        long id,
        string value,
        string? description,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken)
    {
        var ownsTransaction = IsNpgsql() && dbContext.Database.CurrentTransaction is null;
        await using var ownedTransaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var entity = await GetByIdForUpdateAsync(id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        if (entity.UpdatedAt != expectedUpdatedAt)
        {
            throw StaleConflict(id);
        }

        var updatedAt = NextTimestamp(entity.UpdatedAt);
        var updated = await dbContext.EngineSettings
            .Where(setting => setting.Id == id && setting.UpdatedAt == entity.UpdatedAt)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(setting => setting.Value, value)
                    .SetProperty(setting => setting.Description, description)
                    .SetProperty(setting => setting.UpdatedAt, updatedAt),
                cancellationToken);

        if (updated == 0)
        {
            throw StaleConflict(id);
        }

        entity.Value = value;
        entity.Description = description;
        entity.UpdatedAt = updatedAt;
        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken);
        }

        return MapToRecord(entity);
    }

    public async Task<bool> DeleteByIdAsync(
        long id,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken)
    {
        var ownsTransaction = IsNpgsql() && dbContext.Database.CurrentTransaction is null;
        await using var ownedTransaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var entity = await GetByIdForUpdateAsync(id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        if (entity.UpdatedAt != expectedUpdatedAt)
        {
            throw StaleConflict(id);
        }

        var deleted = await dbContext.EngineSettings
            .Where(setting => setting.Id == id && setting.UpdatedAt == entity.UpdatedAt)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            throw StaleConflict(id);
        }

        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken);
        }

        return true;
    }

    private async Task<EngineSettingEntity?> GetByIdForUpdateAsync(
        long id,
        CancellationToken cancellationToken)
    {
        if (IsNpgsql())
        {
            return await dbContext.EngineSettings
                .FromSqlInterpolated(
                    $"""SELECT * FROM "flowbit"."engine_settings" WHERE "Id" = {id} FOR UPDATE""")
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
        }

        return await dbContext.EngineSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(setting => setting.Id == id, cancellationToken);
    }

    private IOrderedQueryable<EngineSettingEntity> QueryByLogicalKey(string key)
    {
        var (canonicalNamespace, canonicalKey) = SplitCanonicalKey(key);
        var matches = dbContext.EngineSettings.Where(setting =>
            (setting.Namespace != null && setting.Namespace.Trim() != "" &&
             setting.Namespace.Trim() + "." + setting.Key.Trim() == key) ||
            ((setting.Namespace == null || setting.Namespace.Trim() == "") &&
             setting.Key.Trim() == key));

        return canonicalNamespace is null
            ? matches
                .OrderBy(setting =>
                    (setting.Namespace == null || setting.Namespace == "") &&
                    setting.Key == canonicalKey
                        ? 0
                        : (setting.Namespace == null || setting.Namespace.Trim() == "") &&
                          setting.Key.Trim() == canonicalKey
                            ? 1
                            : 2)
                .ThenBy(setting => setting.Id)
            : matches
                .OrderBy(setting =>
                    setting.Namespace == canonicalNamespace && setting.Key == canonicalKey
                        ? 0
                        : setting.Namespace != null &&
                          setting.Namespace.Trim() == canonicalNamespace &&
                          setting.Key.Trim() == canonicalKey
                            ? 1
                            : setting.Namespace != null && setting.Namespace.Trim() != ""
                                ? 2
                                : 3)
                .ThenBy(setting => setting.Id);
    }

    private static (string? Namespace, string Key) SplitCanonicalKey(string key)
    {
        var lastDot = key.LastIndexOf('.');
        return lastDot < 0
            ? (null, key)
            : (key[..lastDot], key[(lastDot + 1)..]);
    }

    private bool IsNpgsql() =>
        string.Equals(dbContext.Database.ProviderName, ProviderName, StringComparison.Ordinal);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        };

    private static string BuildLogicalKey(string? settingNamespace, string key)
    {
        var normalizedNamespace = settingNamespace?.Trim();
        var normalizedKey = key.Trim();
        return string.IsNullOrEmpty(normalizedNamespace)
            ? normalizedKey
            : $"{normalizedNamespace}.{normalizedKey}";
    }

    private static WorkflowConflictException DuplicateConflict(string logicalKey) =>
        new($"Engine setting '{logicalKey}' already exists.");

    private static WorkflowConflictException StaleConflict(long id) =>
        new($"Engine setting {id} was changed by another request; reload and try again.");

    private static DateTimeOffset UtcNow()
    {
        var value = DateTimeOffset.UtcNow;
        return new DateTimeOffset(value.Ticks - value.Ticks % 10, TimeSpan.Zero);
    }

    private static DateTimeOffset NextTimestamp(DateTimeOffset previous)
    {
        var now = UtcNow();
        var previousUtc = previous.ToUniversalTime();
        return now > previousUtc ? now : previousUtc.AddTicks(10);
    }

    private static EngineSettingRecord MapToRecord(EngineSettingEntity entity) =>
        new(
            entity.Id,
            entity.Namespace,
            entity.Key,
            entity.Value,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.Description);
}
