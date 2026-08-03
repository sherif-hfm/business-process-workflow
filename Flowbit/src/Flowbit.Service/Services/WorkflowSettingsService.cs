using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

public sealed class WorkflowSettingsService(
    IWorkflowSettingsRepository repository,
    ILogger<WorkflowSettingsService> logger) : IWorkflowSettingsService
{
    public Task<IReadOnlyList<WorkflowSettingRecord>> ListAsync(
        CancellationToken cancellationToken) =>
        repository.ListAsync(cancellationToken);

    public async Task<WorkflowSettingRecord> CreateAsync(
        string? settingNamespace,
        string name,
        JsonElement value,
        string? description,
        CancellationToken cancellationToken)
    {
        var normalizedNamespace = SettingManagementValidation.NormalizeNamespace(settingNamespace);
        var normalizedName = SettingManagementValidation.NormalizeIdentifier(name, "name");
        var normalizedValue = SettingManagementValidation.NormalizeWorkflowValue(value);
        var normalizedDescription = SettingManagementValidation.NormalizeDescription(description);

        var record = await repository.CreateAsync(
            normalizedNamespace,
            normalizedName,
            normalizedValue,
            normalizedDescription,
            cancellationToken);
        logger.LogInformation(
            "Workflow setting '{Namespace}.{Name}' created with id {SettingId}.",
            normalizedNamespace,
            normalizedName,
            record.Id);
        return record;
    }

    public async Task<WorkflowSettingRecord?> UpdateAsync(
        long id,
        JsonElement value,
        string? description,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken)
    {
        SettingManagementValidation.ValidateMutation(id, expectedUpdatedAt);
        var normalizedValue = SettingManagementValidation.NormalizeWorkflowValue(value);
        var normalizedDescription = SettingManagementValidation.NormalizeDescription(description);

        var record = await repository.UpdateAsync(
            id,
            normalizedValue,
            normalizedDescription,
            expectedUpdatedAt,
            cancellationToken);
        if (record is not null)
        {
            logger.LogInformation("Workflow setting {SettingId} updated.", id);
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
            logger.LogInformation("Workflow setting {SettingId} deleted.", id);
        }

        return deleted;
    }
}
