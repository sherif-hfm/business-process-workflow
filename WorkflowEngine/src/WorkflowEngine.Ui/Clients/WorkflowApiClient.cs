using System.Net.Http.Json;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Ui.Clients;

public sealed class WorkflowApiClient(HttpClient httpClient)
{
    public async Task<IReadOnlyList<WorkflowSummaryDto>> GetWorkflowsAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<WorkflowSummaryDto>>(
            "/api/workflows",
            cancellationToken) ?? [];

    public Task<WorkflowDetailDto?> GetWorkflowAsync(long id, CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<WorkflowDetailDto>($"/api/workflows/{id}", cancellationToken);

    public async Task<WorkflowDetailDto?> CreateWorkflowAsync(
        CreateWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("/api/workflows", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WorkflowDetailDto>(cancellationToken);
    }

    public async Task<PagedResult<InstanceSummaryDto>> GetInstancesAsync(
        string? status = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var url = $"/api/instances?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(status))
        {
            url += $"&status={Uri.EscapeDataString(status)}";
        }

        return await httpClient.GetFromJsonAsync<PagedResult<InstanceSummaryDto>>(url, cancellationToken)
            ?? new PagedResult<InstanceSummaryDto>([], page, pageSize, 0);
    }

    public async Task<PagedResult<InboxItemDto>> GetInboxAsync(
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<PagedResult<InboxItemDto>>(
            $"/api/instances/inbox?page={page}&pageSize={pageSize}",
            cancellationToken) ?? new PagedResult<InboxItemDto>([], page, pageSize, 0);

    public Task<InstanceDetailDto?> GetInstanceAsync(long id, CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<InstanceDetailDto>($"/api/instances/{id}", cancellationToken);

    public async Task<IReadOnlyList<SequenceFlowModel>> GetAvailableFlowsAsync(
        long id,
        CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<SequenceFlowModel>>(
            $"/api/instances/{id}/flows",
            cancellationToken) ?? [];

    public async Task<InstanceDetailDto?> StartInstanceAsync(
        StartInstanceRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("/api/instances", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceDetailDto>(cancellationToken);
    }

    public async Task<InstanceDetailDto?> ClaimAsync(long id, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsync($"/api/instances/{id}/claim", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceDetailDto>(cancellationToken);
    }

    public async Task<InstanceDetailDto?> UnclaimAsync(long id, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsync($"/api/instances/{id}/unclaim", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceDetailDto>(cancellationToken);
    }

    public async Task<InstanceDetailDto?> TakeFlowAsync(
        long id,
        int flowId,
        TakeFlowRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync($"/api/instances/{id}/flows/{flowId}", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceDetailDto>(cancellationToken);
    }

    public async Task CancelAsync(long id, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsync($"/api/instances/{id}/cancel", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(text)
            ? response.ReasonPhrase
            : text);
    }
}
