using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;

namespace Flowbit.Infrastructure.Repositories;

public sealed class EngineSettingsRepository(AppDbContext dbContext) : IEngineSettingsRepository
{
    public async Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken)
    {
        string? ns = null;
        string name = key;
        int lastDot = key.LastIndexOf('.');
        if (lastDot >= 0)
        {
            ns = key.Substring(0, lastDot);
            name = key.Substring(lastDot + 1);
        }

        var entity = await dbContext.EngineSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => 
                (s.Namespace == ns && s.Key == name) ||
                (s.Namespace == null && s.Key == key) ||
                (s.Namespace == "" && s.Key == key), cancellationToken);

        return entity is null ? null : MapToRecord(entity);
    }

    public async Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken cancellationToken)
    {
        var entities = await dbContext.EngineSettings
            .AsNoTracking()
            .Where(s => 
                (s.Namespace != null && s.Namespace != "" && EF.Functions.ILike(s.Namespace + "." + s.Key, pattern)) ||
                ((s.Namespace == null || s.Namespace == "") && EF.Functions.ILike(s.Key, pattern))
            )
            .ToListAsync(cancellationToken);

        return entities.Select(MapToRecord).ToList();
    }

    public async Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        string? ns = null;
        string name = key;
        int lastDot = key.LastIndexOf('.');
        if (lastDot >= 0)
        {
            ns = key.Substring(0, lastDot);
            name = key.Substring(lastDot + 1);
        }

        var entity = await dbContext.EngineSettings
            .FirstOrDefaultAsync(s => 
                (s.Namespace == ns && s.Key == name) ||
                (s.Namespace == null && s.Key == key) ||
                (s.Namespace == "" && s.Key == key), cancellationToken);

        if (entity is null)
        {
            entity = new EngineSettingEntity
            {
                Namespace = ns,
                Key = name,
                Value = value,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            dbContext.EngineSettings.Add(entity);
        }
        else
        {
            entity.Value = value;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return MapToRecord(entity);
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        string? ns = null;
        string name = key;
        int lastDot = key.LastIndexOf('.');
        if (lastDot >= 0)
        {
            ns = key.Substring(0, lastDot);
            name = key.Substring(lastDot + 1);
        }

        var entity = await dbContext.EngineSettings
            .FirstOrDefaultAsync(s => 
                (s.Namespace == ns && s.Key == name) ||
                (s.Namespace == null && s.Key == key) ||
                (s.Namespace == "" && s.Key == key), cancellationToken);

        if (entity is null)
        {
            return false;
        }

        dbContext.EngineSettings.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static EngineSettingRecord MapToRecord(EngineSettingEntity entity)
    {
        return new EngineSettingRecord(
            entity.Id,
            entity.Namespace,
            entity.Key,
            entity.Value,
            entity.CreatedAt,
            entity.UpdatedAt);
    }
}
