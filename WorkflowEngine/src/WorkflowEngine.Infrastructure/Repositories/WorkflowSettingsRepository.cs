using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkflowEngine.Infrastructure.Data;
using WorkflowEngine.Service.Abstractions;

namespace WorkflowEngine.Infrastructure.Repositories;

public sealed class WorkflowSettingsRepository(AppDbContext dbContext) : IWorkflowSettingsRepository
{
    public async Task<IReadOnlyDictionary<string, JsonElement>> LoadAllAsync(CancellationToken cancellationToken)
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

        return result;
    }
}
