using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;

namespace Flowbit.Service.Services;

public sealed class EngineSettingsService(
    IEngineSettingsRepository repository,
    ILogger<EngineSettingsService> logger) : IEngineSettingsService
{
    public Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken) =>
        repository.GetByKeyAsync(key, cancellationToken);

    public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken cancellationToken) =>
        repository.SearchAsync(pattern, cancellationToken);

    public async Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        var record = await repository.SetAsync(key, value, cancellationToken);
        logger.LogInformation("Engine setting '{Key}' updated.", key);
        return record;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var deleted = await repository.DeleteAsync(key, cancellationToken);
        if (deleted)
        {
            logger.LogInformation("Engine setting '{Key}' deleted.", key);
        }
        else
        {
            logger.LogInformation("Delete engine setting '{Key}': not found.", key);
        }
        return deleted;
    }
}
