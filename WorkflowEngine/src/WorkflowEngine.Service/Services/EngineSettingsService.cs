using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Models;

namespace WorkflowEngine.Service.Services;

public sealed class EngineSettingsService(IEngineSettingsRepository repository) : IEngineSettingsService
{
    public Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken) =>
        repository.GetByKeyAsync(key, cancellationToken);

    public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken cancellationToken) =>
        repository.SearchAsync(pattern, cancellationToken);

    public Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken cancellationToken) =>
        repository.SetAsync(key, value, cancellationToken);

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
        repository.DeleteAsync(key, cancellationToken);
}
