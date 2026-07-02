using System.Net.Http.Json;
using WorkflowEngine.Shared.Dtos;

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

    public async Task<IReadOnlyList<InstanceSummaryDto>> GetInstancesAsync(
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(status)
            ? "/api/instances"
            : $"/api/instances?status={Uri.EscapeDataString(status)}";
        return await httpClient.GetFromJsonAsync<IReadOnlyList<InstanceSummaryDto>>(url, cancellationToken) ?? [];
    }

    public Task<InstanceDetailDto?> GetInstanceAsync(long id, CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<InstanceDetailDto>($"/api/instances/{id}", cancellationToken);

    public async Task<InstanceDetailDto?> StartInstanceAsync(
        StartInstanceRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("/api/instances", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceDetailDto>(cancellationToken);
    }

    public async Task<InstanceDetailDto?> ClaimAsync(
        long id,
        ClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync($"/api/instances/{id}/claim", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceDetailDto>(cancellationToken);
    }

    public async Task<InstanceDetailDto?> UnclaimAsync(long id, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsync($"/api/instances/{id}/unclaim", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceDetailDto>(cancellationToken);
    }

    public async Task<InstanceDetailDto?> TakeActionAsync(
        long id,
        int actionId,
        TakeActionRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync($"/api/instances/{id}/actions/{actionId}", request, cancellationToken);
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
