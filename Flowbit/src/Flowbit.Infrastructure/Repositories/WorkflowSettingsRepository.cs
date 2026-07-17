using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;

namespace Flowbit.Infrastructure.Repositories;

public sealed class WorkflowSettingsRepository(AppDbContext dbContext, IMemoryCache cache) : IWorkflowSettingsRepository
{
    private static readonly MemoryCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(30),
        Size = 1
    };

    private const string CacheKey = "wf:settings";

    public async Task<IReadOnlyDictionary<string, JsonElement>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out Dictionary<string, JsonElement>? cached) && cached is not null)
        {
            return cached;
        }

        return await LoadFreshAndCacheAsync(cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, JsonElement>> LoadAllFreshAsync(CancellationToken cancellationToken) =>
        LoadFreshAndCacheAsync(cancellationToken);

    private async Task<IReadOnlyDictionary<string, JsonElement>> LoadFreshAndCacheAsync(
        CancellationToken cancellationToken)
    {
        var settings = await dbContext.WorkflowSettings
            .AsNoTracking()
            .Select(s => new { s.Namespace, s.Name, s.Value })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, JsonElement>(settings.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            var key = string.IsNullOrEmpty(setting.Namespace)
                ? $"setting.{setting.Name}"
                : $"setting.{setting.Namespace}.{setting.Name}";
            result[key] = setting.Value.Clone();
        }

        cache.Set(CacheKey, result, CacheOptions);
        return result;
    }
}
