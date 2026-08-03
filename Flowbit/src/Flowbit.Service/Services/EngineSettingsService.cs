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

    public Task<IReadOnlyList<EngineSettingRecord>> ListAsync(CancellationToken cancellationToken) =>
        repository.ListAsync(cancellationToken);

    public async Task<EngineSettingRecord> CreateAsync(
        string? settingNamespace,
        string key,
        string value,
        string? description,
        CancellationToken cancellationToken)
    {
        var normalizedNamespace = SettingManagementValidation.NormalizeNamespace(settingNamespace);
        var normalizedKey = SettingManagementValidation.NormalizeIdentifier(key, "key");
        var normalizedValue = SettingManagementValidation.NormalizeEngineValue(value);
        var normalizedDescription = SettingManagementValidation.NormalizeDescription(description);

        var record = await repository.CreateAsync(
            normalizedNamespace,
            normalizedKey,
            normalizedValue,
            normalizedDescription,
            cancellationToken);
        logger.LogInformation(
            "Engine setting '{Namespace}.{Key}' created with id {SettingId}.",
            normalizedNamespace,
            normalizedKey,
            record.Id);
        return record;
    }

    public async Task<EngineSettingRecord?> UpdateAsync(
        long id,
        string value,
        string? description,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken)
    {
        SettingManagementValidation.ValidateMutation(id, expectedUpdatedAt);
        var normalizedValue = SettingManagementValidation.NormalizeEngineValue(value);
        var normalizedDescription = SettingManagementValidation.NormalizeDescription(description);

        var record = await repository.UpdateAsync(
            id,
            normalizedValue,
            normalizedDescription,
            expectedUpdatedAt,
            cancellationToken);
        if (record is not null)
        {
            logger.LogInformation("Engine setting {SettingId} updated.", id);
        }

        return record;
    }

    public async Task<bool> DeleteByIdAsync(
        long id,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken)
    {
        SettingManagementValidation.ValidateMutation(id, expectedUpdatedAt);
        var deleted = await repository.DeleteByIdAsync(id, expectedUpdatedAt, cancellationToken);
        if (deleted)
        {
            logger.LogInformation("Engine setting {SettingId} deleted.", id);
        }

        return deleted;
    }
}
