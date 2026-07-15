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

    public async Task<WorkflowDetailDto?> CreateNewVersionAsync(
        long sourceWorkflowId,
        UpdateWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PutAsJsonAsync($"/api/workflows/{sourceWorkflowId}", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WorkflowDetailDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowSummaryDto>> GetWorkflowVersionsAsync(
        string workflowKey,
        CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<WorkflowSummaryDto>>(
            $"/api/workflows/{Uri.EscapeDataString(workflowKey)}/versions",
            cancellationToken) ?? [];

    public async Task PublishWorkflowAsync(long id, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsync($"/api/workflows/{id}/publish", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task UnpublishWorkflowAsync(long id, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsync($"/api/workflows/{id}/unpublish", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetDefaultWorkflowAsync(long id, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsync($"/api/workflows/{id}/set-default", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DeleteWorkflowAsync(long id, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.DeleteAsync($"/api/workflows/{id}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<PagedResult<InstanceSummaryDto>> GetInstancesAsync(
        string? status = null,
        int page = 1,
        int pageSize = 50,
        IEnumerable<string>? variables = null,
        string? nodeExternalId = null,
        int? nodeId = null,
        long? instanceId = null,
        long? workflowId = null,
        string? workflowKey = null,
        string? businessKey = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"/api/instances?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(status))
        {
            url += $"&status={Uri.EscapeDataString(status)}";
        }

        if (instanceId is not null)
        {
            url += $"&instanceId={instanceId.Value}";
        }

        if (workflowId is not null)
        {
            url += $"&workflowId={workflowId.Value}";
        }

        if (!string.IsNullOrEmpty(workflowKey))
        {
            url += $"&workflowKey={Uri.EscapeDataString(workflowKey)}";
        }

        if (!string.IsNullOrWhiteSpace(businessKey))
        {
            url += $"&businessKey={Uri.EscapeDataString(businessKey.Trim())}";
        }

        if (nodeId is not null)
        {
            url += $"&nodeId={nodeId.Value}";
        }

        if (!string.IsNullOrWhiteSpace(nodeExternalId))
        {
            url += $"&nodeExternalId={Uri.EscapeDataString(nodeExternalId)}";
        }

        url += BuildVariableQuery(variables);

        return await httpClient.GetFromJsonAsync<PagedResult<InstanceSummaryDto>>(url, cancellationToken)
            ?? new PagedResult<InstanceSummaryDto>([], page, pageSize, 0);
    }

    public async Task<PagedResult<InboxItemDto>> GetInboxAsync(
        int page = 1,
        int pageSize = 50,
        IEnumerable<string>? variables = null,
        string? nodeExternalId = null,
        int? nodeId = null,
        long? instanceId = null,
        long? workflowId = null,
        string? workflowKey = null,
        string? businessKey = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"/api/instances/inbox?page={page}&pageSize={pageSize}";
        if (instanceId is not null)
        {
            url += $"&instanceId={instanceId.Value}";
        }

        if (workflowId is not null)
        {
            url += $"&workflowId={workflowId.Value}";
        }

        if (!string.IsNullOrEmpty(workflowKey))
        {
            url += $"&workflowKey={Uri.EscapeDataString(workflowKey)}";
        }

        if (!string.IsNullOrWhiteSpace(businessKey))
        {
            url += $"&businessKey={Uri.EscapeDataString(businessKey.Trim())}";
        }

        if (nodeId is not null)
        {
            url += $"&nodeId={nodeId.Value}";
        }

        if (!string.IsNullOrWhiteSpace(nodeExternalId))
        {
            url += $"&nodeExternalId={Uri.EscapeDataString(nodeExternalId)}";
        }

        url += BuildVariableQuery(variables);

        return await httpClient.GetFromJsonAsync<PagedResult<InboxItemDto>>(url, cancellationToken)
            ?? new PagedResult<InboxItemDto>([], page, pageSize, 0);
    }

    private static string BuildVariableQuery(IEnumerable<string>? variables)
    {
        if (variables is null)
        {
            return string.Empty;
        }

        var query = string.Empty;
        foreach (var variable in variables.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            query += $"&var={Uri.EscapeDataString(variable)}";
        }

        return query;
    }

    public Task<InstanceDetailDto?> GetInstanceAsync(long id, CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<InstanceDetailDto>($"/api/instances/{id}", cancellationToken);

    public async Task<IReadOnlyList<SequenceFlowModel>> GetAvailableFlowsAsync(
        long id,
        CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<SequenceFlowModel>>(
            $"/api/instances/{id}/flows",
            cancellationToken) ?? [];

    public async Task<StartInstanceResultDto?> StartInstanceAsync(
        StartInstanceRequest request,
        string? idempotencyHeaderName = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/instances")
        {
            Content = JsonContent.Create(request)
        };
        if (!string.IsNullOrWhiteSpace(idempotencyHeaderName)
            && idempotencyKey is not null)
        {
            message.Headers.TryAddWithoutValidation(idempotencyHeaderName, idempotencyKey);
        }

        var response = await httpClient.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<StartInstanceResultDto>(cancellationToken);
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

    public Task<UserTaskDto?> GetUserTaskAsync(long taskId, CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<UserTaskDto>($"/api/user-tasks/{taskId}", cancellationToken);

    public async Task<IReadOnlyList<SequenceFlowModel>> GetUserTaskFlowsAsync(
        long taskId, CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<SequenceFlowModel>>(
            $"/api/user-tasks/{taskId}/flows", cancellationToken) ?? [];

    public async Task<UserTaskDto?> ClaimUserTaskAsync(long taskId, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsync($"/api/user-tasks/{taskId}/claim", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<UserTaskDto>(cancellationToken);
    }

    public async Task<UserTaskDto?> UnclaimUserTaskAsync(long taskId, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsync($"/api/user-tasks/{taskId}/unclaim", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<UserTaskDto>(cancellationToken);
    }

    public async Task<UserTaskActionAckDto?> TakeUserTaskFlowAsync(
        long taskId, int flowId, TakeFlowRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"/api/user-tasks/{taskId}/flows/{flowId}", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<UserTaskActionAckDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<SequenceFlowModel>> GetMultiInstanceInterruptFlowsAsync(
        long executionId,
        CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<SequenceFlowModel>>(
            $"/api/multi-instance-executions/{executionId}/flows", cancellationToken) ?? [];

    public async Task<InstanceDetailDto?> TakeMultiInstanceInterruptFlowAsync(
        long executionId,
        int flowId,
        TakeFlowRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"/api/multi-instance-executions/{executionId}/flows/{flowId}", request, cancellationToken);
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
