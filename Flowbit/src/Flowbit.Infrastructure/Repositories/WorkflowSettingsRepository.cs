using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowbit.Infrastructure.Repositories;

public sealed class WorkflowSettingsRepository(AppDbContext dbContext) : IWorkflowSettingsRepository
{
    private const string ProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public Task<IReadOnlyDictionary<string, JsonElement>> LoadAllAsync(
        CancellationToken cancellationToken) =>
        LoadFromDatabaseAsync(cancellationToken);

    public Task<IReadOnlyDictionary<string, JsonElement>> LoadAllFreshAsync(
        CancellationToken cancellationToken) =>
        LoadFromDatabaseAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkflowSettingRecord>> ListAsync(
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.WorkflowSettings
            .AsNoTracking()
            .OrderBy(setting => setting.Namespace ?? string.Empty)
            .ThenBy(setting => setting.Name)
            .ThenBy(setting => setting.Id)
            .ToListAsync(cancellationToken);

        return entities.Select(MapToRecord).ToList();
    }

    public async Task<WorkflowSettingRecord> CreateAsync(
        string? settingNamespace,
        string name,
        JsonElement value,
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
                "LOCK TABLE \"flowbit\".\"workflow_settings\" IN SHARE ROW EXCLUSIVE MODE",
                cancellationToken);
        }

        var logicalName = BuildLogicalName(settingNamespace, name);
        var existingIdentifiers = await dbContext.WorkflowSettings
            .AsNoTracking()
            .Select(setting => new { setting.Namespace, setting.Name })
            .ToListAsync(cancellationToken);
        var duplicate = existingIdentifiers.Any(setting =>
            string.Equals(
                BuildLogicalName(setting.Namespace, setting.Name),
                logicalName,
                StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            throw DuplicateConflict(logicalName);
        }

        var now = UtcNow();
        var entity = new WorkflowSettingEntity
        {
            Namespace = settingNamespace,
            Name = name,
            Value = value.Clone(),
            Description = description,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.WorkflowSettings.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw DuplicateConflict(logicalName);
        }

        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken);
        }

        return MapToRecord(entity);
    }

    public async Task<WorkflowSettingRecord?> UpdateAsync(
        long id,
        JsonElement value,
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
        var clonedValue = value.Clone();
        var updated = await dbContext.WorkflowSettings
            .Where(setting => setting.Id == id && setting.UpdatedAt == entity.UpdatedAt)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(setting => setting.Value, clonedValue)
                    .SetProperty(setting => setting.Description, description)
                    .SetProperty(setting => setting.UpdatedAt, updatedAt),
                cancellationToken);

        if (updated == 0)
        {
            throw StaleConflict(id);
        }

        entity.Value = clonedValue;
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

        var deleted = await dbContext.WorkflowSettings
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

    private async Task<WorkflowSettingEntity?> GetByIdForUpdateAsync(
        long id,
        CancellationToken cancellationToken)
    {
        if (IsNpgsql())
        {
            return await dbContext.WorkflowSettings
                .FromSqlInterpolated(
                    $"""SELECT * FROM "flowbit"."workflow_settings" WHERE "Id" = {id} FOR UPDATE""")
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
        }

        return await dbContext.WorkflowSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(setting => setting.Id == id, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, JsonElement>> LoadFromDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var settings = await dbContext.WorkflowSettings
            .AsNoTracking()
            .OrderBy(setting => setting.Id)
            .Select(setting => new { setting.Namespace, setting.Name, setting.Value })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, JsonElement>(settings.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            var key = string.IsNullOrWhiteSpace(setting.Namespace)
                ? $"setting.{setting.Name}"
                : $"setting.{setting.Namespace.Trim()}.{setting.Name}";
            result[key] = setting.Value.Clone();
        }

        return result;
    }

    private bool IsNpgsql() =>
        string.Equals(dbContext.Database.ProviderName, ProviderName, StringComparison.Ordinal);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        };

    private static string BuildLogicalName(string? settingNamespace, string name)
    {
        var normalizedNamespace = settingNamespace?.Trim();
        var normalizedName = name.Trim();
        return string.IsNullOrEmpty(normalizedNamespace)
            ? normalizedName
            : $"{normalizedNamespace}.{normalizedName}";
    }

    private static WorkflowConflictException DuplicateConflict(string logicalName) =>
        new($"Workflow setting '{logicalName}' already exists.");

    private static WorkflowConflictException StaleConflict(long id) =>
        new($"Workflow setting {id} was changed by another request; reload and try again.");

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

    private static WorkflowSettingRecord MapToRecord(WorkflowSettingEntity entity) =>
        new(
            entity.Id,
            entity.Namespace,
            entity.Name,
            entity.Value.Clone(),
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.Description);
}
