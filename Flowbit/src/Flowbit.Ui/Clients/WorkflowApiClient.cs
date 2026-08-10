using System.Net.Http.Json;
using System.Net;
using System.Globalization;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Ui.Clients;

public sealed class WorkflowApiClient(HttpClient httpClient)
{
    public async Task<ActorContextDto> GetActorContextAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetAsync("/api/auth/context", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ActorContextDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty actor context.");
    }

    public async Task<IReadOnlyList<EngineSettingDto>> GetEngineSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/engine-settings", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<EngineSettingDto>>(cancellationToken)
            ?? [];
    }

    public async Task<EngineSettingDto> CreateEngineSettingAsync(
        CreateEngineSettingRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/engine-settings", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<EngineSettingDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty engine setting.");
    }

    public async Task<EngineSettingDto> UpdateEngineSettingAsync(
        long id,
        UpdateEngineSettingRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PutAsJsonAsync(
            $"/api/engine-settings/{id}", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<EngineSettingDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty engine setting.");
    }

    public async Task DeleteEngineSettingAsync(
        long id,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        var expected = Uri.EscapeDataString(
            expectedUpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        using var response = await httpClient.DeleteAsync(
            $"/api/engine-settings/{id}?expectedUpdatedAt={expected}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowSettingDto>> GetWorkflowSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/workflow-settings", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<WorkflowSettingDto>>(cancellationToken)
            ?? [];
    }

    public async Task<WorkflowSettingDto> CreateWorkflowSettingAsync(
        CreateWorkflowSettingRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/workflow-settings", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WorkflowSettingDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty workflow setting.");
    }

    public async Task<WorkflowSettingDto> UpdateWorkflowSettingAsync(
        long id,
        UpdateWorkflowSettingRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PutAsJsonAsync(
            $"/api/workflow-settings/{id}", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WorkflowSettingDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty workflow setting.");
    }

    public async Task DeleteWorkflowSettingAsync(
        long id,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        var expected = Uri.EscapeDataString(
            expectedUpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        using var response = await httpClient.DeleteAsync(
            $"/api/workflow-settings/{id}?expectedUpdatedAt={expected}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

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
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/workflows/{Uri.EscapeDataString(workflowKey)}/versions",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<WorkflowSummaryDto>>(cancellationToken)
            ?? [];
    }

    public async Task<IReadOnlyList<WorkflowSummaryDto>> GetAdministrativeActionWorkflowCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            "/api/administrative-actions/workflows",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<WorkflowSummaryDto>>(
                   cancellationToken)
               ?? [];
    }

    public async Task<IReadOnlyList<AdministrativeActionSourceNodeDto>> GetWorkflowAdministrativeActionNodesAsync(
        long workflowDefinitionId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/workflows/{workflowDefinitionId}/administrative-actions/nodes",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<AdministrativeActionSourceNodeDto>>(cancellationToken)
            ?? [];
    }

    public async Task<IReadOnlyList<AdministrativeActionSummaryDto>> GetWorkflowAdministrativeActionsAsync(
        long workflowDefinitionId,
        int sourceNodeId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/workflows/{workflowDefinitionId}/nodes/{sourceNodeId}/administrative-actions",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<AdministrativeActionSummaryDto>>(cancellationToken)
            ?? [];
    }

    public async Task<PagedResult<AdministrativeActionCandidateDto>> SearchAdministrativeActionCandidatesAsync(
        AdministrativeActionCandidateSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            "/api/administrative-actions/candidates/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<AdministrativeActionCandidateDto>>(cancellationToken)
            ?? new PagedResult<AdministrativeActionCandidateDto>([], request.Page ?? 1, request.PageSize ?? 50, 0);
    }

    public async Task<AdministrativeActionBatchDetailDto> CreateAdministrativeActionBatchAsync(
        CreateAdministrativeActionBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            "/api/administrative-action-batches", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AdministrativeActionBatchDetailDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty administrative action batch.");
    }

    public async Task<PagedResult<AdministrativeActionBatchSummaryDto>> GetAdministrativeActionBatchesAsync(
        AdministrativeActionBatchSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = new List<string>
        {
            $"page={request.Page ?? 1}",
            $"pageSize={request.PageSize ?? 50}"
        };
        AddQueryValue(parameters, "workflowKey", request.WorkflowKey);
        if (request.WorkflowDefinitionId is long workflowDefinitionId)
        {
            parameters.Add($"workflowDefinitionId={workflowDefinitionId}");
        }
        AddQueryValue(parameters, "status", request.Status);
        AddQueryValue(parameters, "preparedBy", request.PreparedBy);

        using var response = await httpClient.GetAsync(
            $"/api/administrative-action-batches?{string.Join("&", parameters)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<AdministrativeActionBatchSummaryDto>>(cancellationToken)
            ?? new PagedResult<AdministrativeActionBatchSummaryDto>([], request.Page ?? 1, request.PageSize ?? 50, 0);
    }

    public async Task<AdministrativeActionBatchDetailDto?> GetAdministrativeActionBatchAsync(
        long batchId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/administrative-action-batches/{batchId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AdministrativeActionBatchDetailDto>(cancellationToken);
    }

    public async Task<PagedResult<AdministrativeActionBatchItemDto>> GetAdministrativeActionBatchItemsAsync(
        long batchId,
        string? status = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        AddQueryValue(parameters, "status", status);
        using var response = await httpClient.GetAsync(
            $"/api/administrative-action-batches/{batchId}/items?{string.Join("&", parameters)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<AdministrativeActionBatchItemDto>>(cancellationToken)
            ?? new PagedResult<AdministrativeActionBatchItemDto>([], page, pageSize, 0);
    }

    public async Task<AdministrativeActionBatchDetailDto> ConfirmAdministrativeActionBatchAsync(
        long batchId,
        ConfirmAdministrativeActionBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/administrative-action-batches/{batchId}/confirm", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AdministrativeActionBatchDetailDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty administrative action batch.");
    }

    public async Task<AdministrativeActionBatchDetailDto> CancelAdministrativeActionBatchAsync(
        long batchId,
        CancelAdministrativeActionBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/administrative-action-batches/{batchId}/cancel", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AdministrativeActionBatchDetailDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty administrative action batch.");
    }

    public async Task<PagedResult<InstanceVersionChangeCandidateDto>> SearchInstanceVersionChangeCandidatesAsync(
        InstanceVersionChangeCandidateSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            "/api/instance-version-change-batches/candidates/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<InstanceVersionChangeCandidateDto>>(cancellationToken)
            ?? new PagedResult<InstanceVersionChangeCandidateDto>([], request.Page ?? 1, request.PageSize ?? 50, 0);
    }

    public async Task<InstanceVersionChangeBatchDetailDto> CreateInstanceVersionChangeBatchAsync(
        CreateInstanceVersionChangeBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            "/api/instance-version-change-batches", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceVersionChangeBatchDetailDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty instance version-change batch.");
    }

    public async Task<PagedResult<InstanceVersionChangeBatchSummaryDto>> GetInstanceVersionChangeBatchesAsync(
        InstanceVersionChangeBatchSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = new List<string>
        {
            $"page={request.Page ?? 1}",
            $"pageSize={request.PageSize ?? 50}"
        };
        AddQueryValue(parameters, "workflowKey", request.WorkflowKey);
        AddQueryValue(parameters, "sourceWorkflowId", request.SourceWorkflowId);
        AddQueryValue(parameters, "targetWorkflowId", request.TargetWorkflowId);
        AddQueryValue(parameters, "status", request.Status);
        AddQueryValue(parameters, "preparedBy", request.PreparedBy);

        using var response = await httpClient.GetAsync(
            $"/api/instance-version-change-batches?{string.Join("&", parameters)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<InstanceVersionChangeBatchSummaryDto>>(cancellationToken)
            ?? new PagedResult<InstanceVersionChangeBatchSummaryDto>([], request.Page ?? 1, request.PageSize ?? 50, 0);
    }

    public async Task<InstanceVersionChangeBatchDetailDto?> GetInstanceVersionChangeBatchAsync(
        long batchId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/instance-version-change-batches/{batchId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceVersionChangeBatchDetailDto>(cancellationToken);
    }

    public async Task<PagedResult<InstanceVersionChangeBatchItemDto>> GetInstanceVersionChangeBatchItemsAsync(
        long batchId,
        string? status = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        AddQueryValue(parameters, "status", status);
        using var response = await httpClient.GetAsync(
            $"/api/instance-version-change-batches/{batchId}/items?{string.Join("&", parameters)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<InstanceVersionChangeBatchItemDto>>(cancellationToken)
            ?? new PagedResult<InstanceVersionChangeBatchItemDto>([], page, pageSize, 0);
    }

    public async Task<InstanceVersionChangeBatchDetailDto> ConfirmInstanceVersionChangeBatchAsync(
        long batchId,
        ConfirmInstanceVersionChangeBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/instance-version-change-batches/{batchId}/confirm", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceVersionChangeBatchDetailDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty instance version-change batch.");
    }

    public async Task<InstanceVersionChangeBatchDetailDto> CancelInstanceVersionChangeBatchAsync(
        long batchId,
        CancelInstanceVersionChangeBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/instance-version-change-batches/{batchId}/cancel", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceVersionChangeBatchDetailDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty instance version-change batch.");
    }

    public async Task<UpdateInstanceVariablesResultDto> UpdateInstanceVariablesAsync(
        long instanceId,
        UpdateInstanceVariablesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/instances/{instanceId}/variables")
        {
            Content = JsonContent.Create(request)
        };
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<UpdateInstanceVariablesResultDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty instance-variable update result.");
    }

    public async Task<PagedResult<InstanceVariableUpdateCandidateDto>> SearchInstanceVariableUpdateCandidatesAsync(
        InstanceVariableUpdateCandidateSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            "/api/instance-variable-update-batches/candidates/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<InstanceVariableUpdateCandidateDto>>(cancellationToken)
            ?? new PagedResult<InstanceVariableUpdateCandidateDto>([], request.Page ?? 1, request.PageSize ?? 50, 0);
    }

    public async Task<InstanceVariableUpdateBatchDetailDto> CreateInstanceVariableUpdateBatchAsync(
        CreateInstanceVariableUpdateBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            "/api/instance-variable-update-batches", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceVariableUpdateBatchDetailDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty instance-variable update batch.");
    }

    public async Task<PagedResult<InstanceVariableUpdateBatchSummaryDto>> GetInstanceVariableUpdateBatchesAsync(
        InstanceVariableUpdateBatchSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = new List<string>
        {
            $"page={request.Page ?? 1}",
            $"pageSize={request.PageSize ?? 50}"
        };
        AddQueryValue(parameters, "workflowKey", request.WorkflowKey);
        AddQueryValue(parameters, "status", request.Status);
        AddQueryValue(parameters, "preparedBy", request.PreparedBy);

        using var response = await httpClient.GetAsync(
            $"/api/instance-variable-update-batches?{string.Join("&", parameters)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<InstanceVariableUpdateBatchSummaryDto>>(cancellationToken)
            ?? new PagedResult<InstanceVariableUpdateBatchSummaryDto>([], request.Page ?? 1, request.PageSize ?? 50, 0);
    }

    public async Task<InstanceVariableUpdateBatchDetailDto?> GetInstanceVariableUpdateBatchAsync(
        long batchId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/instance-variable-update-batches/{batchId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceVariableUpdateBatchDetailDto>(cancellationToken);
    }

    public async Task<PagedResult<InstanceVariableUpdateBatchItemDto>> GetInstanceVariableUpdateBatchItemsAsync(
        long batchId,
        string? status = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        AddQueryValue(parameters, "status", status);
        using var response = await httpClient.GetAsync(
            $"/api/instance-variable-update-batches/{batchId}/items?{string.Join("&", parameters)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<InstanceVariableUpdateBatchItemDto>>(cancellationToken)
            ?? new PagedResult<InstanceVariableUpdateBatchItemDto>([], page, pageSize, 0);
    }

    public async Task<InstanceVariableUpdateBatchDetailDto> ConfirmInstanceVariableUpdateBatchAsync(
        long batchId,
        ConfirmInstanceVariableUpdateBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/instance-variable-update-batches/{batchId}/confirm", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceVariableUpdateBatchDetailDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty instance-variable update batch.");
    }

    public async Task<InstanceVariableUpdateBatchDetailDto> CancelInstanceVariableUpdateBatchAsync(
        long batchId,
        CancelInstanceVariableUpdateBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/instance-variable-update-batches/{batchId}/cancel", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceVariableUpdateBatchDetailDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty instance-variable update batch.");
    }

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
        bool includeVariables = false,
        IReadOnlyList<string>? sort = null,
        string? cursor = null,
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

        if (includeVariables)
        {
            url += "&includeVariables=true";
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
        url += BuildSortQuery(sort);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            url += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

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
        bool includeVariables = false,
        IReadOnlyList<string>? sort = null,
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

        if (includeVariables)
        {
            url += "&includeVariables=true";
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
        url += BuildSortQuery(sort);

        return await httpClient.GetFromJsonAsync<PagedResult<InboxItemDto>>(url, cancellationToken)
            ?? new PagedResult<InboxItemDto>([], page, pageSize, 0);
    }

    public async Task<PagedResult<ManagedUserTaskDto>> GetManagedUserTasksAsync(
        int page = 1,
        int pageSize = 50,
        long? taskId = null,
        long? instanceId = null,
        long? workflowId = null,
        string? workflowKey = null,
        string? businessKey = null,
        int? nodeId = null,
        string? nodeExternalId = null,
        string? owner = null,
        string? ownership = null,
        IEnumerable<string>? variables = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"/api/user-tasks/manage?page={page}&pageSize={pageSize}";
        if (taskId is not null) url += $"&taskId={taskId.Value}";
        if (instanceId is not null) url += $"&instanceId={instanceId.Value}";
        if (workflowId is not null) url += $"&workflowId={workflowId.Value}";
        if (!string.IsNullOrWhiteSpace(workflowKey))
            url += $"&workflowKey={Uri.EscapeDataString(workflowKey.Trim())}";
        if (!string.IsNullOrWhiteSpace(businessKey))
            url += $"&businessKey={Uri.EscapeDataString(businessKey.Trim())}";
        if (nodeId is not null) url += $"&nodeId={nodeId.Value}";
        if (!string.IsNullOrWhiteSpace(nodeExternalId))
            url += $"&nodeExternalId={Uri.EscapeDataString(nodeExternalId.Trim())}";
        if (!string.IsNullOrWhiteSpace(owner))
            url += $"&owner={Uri.EscapeDataString(owner.Trim())}";
        if (!string.IsNullOrWhiteSpace(ownership))
            url += $"&ownership={Uri.EscapeDataString(ownership.Trim())}";
        url += BuildVariableQuery(variables);

        return await httpClient.GetFromJsonAsync<PagedResult<ManagedUserTaskDto>>(url, cancellationToken)
            ?? new PagedResult<ManagedUserTaskDto>([], page, pageSize, 0);
    }

    public async Task<PagedResult<InstanceSummaryDto>> SearchInstancesAsync(
        InstanceSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            "/api/instances/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<InstanceSummaryDto>>(cancellationToken)
            ?? new PagedResult<InstanceSummaryDto>([], request.Page ?? 1, request.PageSize ?? 50, 0);
    }

    public async Task<PagedResult<InboxItemDto>> SearchInboxAsync(
        InboxSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            "/api/instances/inbox/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<InboxItemDto>>(cancellationToken)
            ?? new PagedResult<InboxItemDto>([], request.Page ?? 1, request.PageSize ?? 50, 0);
    }

    public async Task<PagedResult<ManagedUserTaskDto>> SearchManagedUserTasksAsync(
        ManageableUserTaskSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            "/api/user-tasks/manage/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<ManagedUserTaskDto>>(cancellationToken)
            ?? new PagedResult<ManagedUserTaskDto>([], request.Page ?? 1, request.PageSize ?? 50, 0);
    }

    public async Task<PagedResult<ManagedUserTaskDto>> SearchDistributableUserTasksAsync(
        string workflowKey,
        string? clientId,
        string? clientSecret,
        DistributableUserTaskSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowKey);
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/task-distribution/workflows/{Uri.EscapeDataString(workflowKey.Trim())}/tasks/search")
        {
            Content = JsonContent.Create(request)
        };
        if (clientId is not null)
        {
            message.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
        }
        if (clientSecret is not null)
        {
            message.Headers.TryAddWithoutValidation("X-Client-Secret", clientSecret);
        }

        using var response = await httpClient.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<ManagedUserTaskDto>>(cancellationToken)
            ?? new PagedResult<ManagedUserTaskDto>([], request.Page ?? 1, request.PageSize ?? 50, 0);
    }

    public async Task<PagedResult<UserDelegationDto>> GetUserDelegationsAsync(
        string direction = "outgoing",
        string? workflowKey = null,
        string? state = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>
        {
            $"direction={Uri.EscapeDataString(direction)}",
            $"page={page}",
            $"pageSize={pageSize}"
        };
        AddQueryValue(parameters, "workflowKey", workflowKey);
        AddQueryValue(parameters, "state", state);

        using var response = await httpClient.GetAsync(
            $"/api/user-delegations?{string.Join("&", parameters)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<UserDelegationDto>>(cancellationToken)
            ?? new PagedResult<UserDelegationDto>([], page, pageSize, 0);
    }

    public async Task<IReadOnlyList<UserDelegationDto>> CreateUserDelegationsAsync(
        CreateUserDelegationRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/user-delegations", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<UserDelegationDto>>(cancellationToken)
            ?? [];
    }

    public Task<UserDelegationDto?> AcceptUserDelegationAsync(
        long id,
        UserDelegationLifecycleRequest request,
        CancellationToken cancellationToken = default) =>
        ChangeUserDelegationAsync(id, "accept", request, managed: false, cancellationToken);

    public Task<UserDelegationDto?> RejectUserDelegationAsync(
        long id,
        UserDelegationLifecycleRequest request,
        CancellationToken cancellationToken = default) =>
        ChangeUserDelegationAsync(id, "reject", request, managed: false, cancellationToken);

    public Task<UserDelegationDto?> RevokeUserDelegationAsync(
        long id,
        UserDelegationLifecycleRequest request,
        CancellationToken cancellationToken = default) =>
        ChangeUserDelegationAsync(id, "revoke", request, managed: false, cancellationToken);

    public async Task<PagedResult<UserDelegationDto>> GetManagedUserDelegationsAsync(
        string? delegator = null,
        string? @delegate = null,
        string? workflowKey = null,
        string? state = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        AddQueryValue(parameters, "delegator", delegator);
        AddQueryValue(parameters, "delegate", @delegate);
        AddQueryValue(parameters, "workflowKey", workflowKey);
        AddQueryValue(parameters, "state", state);

        using var response = await httpClient.GetAsync(
            $"/api/user-delegations/manage?{string.Join("&", parameters)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<UserDelegationDto>>(cancellationToken)
            ?? new PagedResult<UserDelegationDto>([], page, pageSize, 0);
    }

    public async Task<IReadOnlyList<UserDelegationDto>> CreateManagedUserDelegationsAsync(
        CreateManagedUserDelegationRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/user-delegations/manage", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<UserDelegationDto>>(cancellationToken)
            ?? [];
    }

    public Task<UserDelegationDto?> RevokeManagedUserDelegationAsync(
        long id,
        UserDelegationLifecycleRequest request,
        CancellationToken cancellationToken = default) =>
        ChangeUserDelegationAsync(id, "revoke", request, managed: true, cancellationToken);

    public async Task<WorkflowDelegationPolicyDto?> GetWorkflowDelegationPolicyAsync(
        string workflowKey,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/user-delegation-policies/{Uri.EscapeDataString(workflowKey)}",
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WorkflowDelegationPolicyDto>(cancellationToken);
    }

    public async Task<WorkflowDelegationPolicyDto?> UpdateWorkflowDelegationPolicyAsync(
        string workflowKey,
        UpdateWorkflowDelegationPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PutAsJsonAsync(
            $"/api/user-delegation-policies/{Uri.EscapeDataString(workflowKey)}",
            request,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WorkflowDelegationPolicyDto>(cancellationToken);
    }

    private async Task<UserDelegationDto?> ChangeUserDelegationAsync(
        long id,
        string operation,
        UserDelegationLifecycleRequest request,
        bool managed,
        CancellationToken cancellationToken)
    {
        var route = managed
            ? $"/api/user-delegations/manage/{id}/{operation}"
            : $"/api/user-delegations/{id}/{operation}";
        using var response = await httpClient.PostAsJsonAsync(route, request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<UserDelegationDto>(cancellationToken);
    }

    public async Task<PagedResult<NodeExecutionSummaryDto>> GetNodeExecutionsAsync(
        NodeExecutionSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>
        {
            $"page={query.Page}",
            $"pageSize={query.PageSize}"
        };

        AddQueryValue(parameters, "executionId", query.ExecutionId);
        AddQueryValue(parameters, "instanceId", query.InstanceId);
        AddQueryValue(parameters, "workflowId", query.WorkflowId);
        AddQueryValue(parameters, "workflowKey", query.WorkflowKey);
        AddQueryValue(parameters, "workflowVersion", query.WorkflowVersion);
        AddQueryValue(parameters, "businessKey", query.BusinessKey);
        AddQueryValue(parameters, "tokenId", query.TokenId);
        AddQueryValue(parameters, "userTaskId", query.UserTaskId);
        AddQueryValue(parameters, "multiInstanceExecutionId", query.MultiInstanceExecutionId);
        AddQueryValue(parameters, "gatewayBranchId", query.GatewayBranchId);
        AddQueryValue(parameters, "itemIndex", query.ItemIndex);
        AddQueryValue(parameters, "executionKind", query.ExecutionKind);
        AddQueryValue(parameters, "nodeId", query.NodeId);
        AddQueryValue(parameters, "nodeName", query.NodeName);
        AddQueryValue(parameters, "nodeExternalId", query.NodeExternalId);
        AddQueryValues(parameters, "nodeType", query.NodeTypes);
        AddQueryValues(parameters, "status", query.Statuses);
        AddQueryValues(parameters, "instanceStatus", query.InstanceStatuses);
        AddQueryValues(parameters, "completionReason", query.CompletionReasons);
        AddQueryValue(parameters, "isMultiInstance", query.IsMultiInstance);
        AddQueryValue(parameters, "isCutoverSeeded", query.IsCutoverSeeded);
        AddQueryValue(parameters, "owner", query.Owner);
        AddQueryValue(parameters, "startedBy", query.StartedBy);
        AddQueryValue(parameters, "completedBy", query.CompletedBy);
        AddQueryValue(parameters, "enteredViaFlowId", query.EnteredViaFlowId);
        AddQueryValue(parameters, "selectedFlowId", query.SelectedFlowId);
        AddQueryValue(parameters, "exitedViaFlowId", query.ExitedViaFlowId);
        AddQueryValue(parameters, "aggregateFlowId", query.AggregateFlowId);
        AddQueryValue(parameters, "createdFrom", query.CreatedFrom);
        AddQueryValue(parameters, "createdTo", query.CreatedTo);
        AddQueryValue(parameters, "startedFrom", query.StartedFrom);
        AddQueryValue(parameters, "startedTo", query.StartedTo);
        AddQueryValue(parameters, "updatedFrom", query.UpdatedFrom);
        AddQueryValue(parameters, "updatedTo", query.UpdatedTo);
        AddQueryValue(parameters, "completedFrom", query.CompletedFrom);
        AddQueryValue(parameters, "completedTo", query.CompletedTo);
        AddQueryValue(parameters, "minDurationMilliseconds", query.MinimumDurationMilliseconds);
        AddQueryValue(parameters, "maxDurationMilliseconds", query.MaximumDurationMilliseconds);
        AddQueryValues(parameters, "var", query.Variables);
        AddQueryValues(parameters, "sort", query.Sort);

        var url = $"/api/node-executions?{string.Join("&", parameters)}";
        using var response = await httpClient.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<NodeExecutionSummaryDto>>(cancellationToken)
            ?? new PagedResult<NodeExecutionSummaryDto>([], query.Page, query.PageSize, 0);
    }

    public async Task<PagedResult<NodeExecutionSummaryDto>> SearchNodeExecutionsAsync(
        NodeExecutionSearchBodyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.PostAsJsonAsync(
            "/api/node-executions/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<NodeExecutionSummaryDto>>(cancellationToken)
            ?? new PagedResult<NodeExecutionSummaryDto>([], request.Page ?? 1, request.PageSize ?? 50, 0);
    }

    public async Task<NodeExecutionDetailDto?> GetNodeExecutionAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"/api/node-executions/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<NodeExecutionDetailDto>(cancellationToken);
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

    private static string BuildSortQuery(IReadOnlyList<string>? sort)
    {
        if (sort is null || sort.Count == 0)
        {
            return string.Empty;
        }

        return string.Concat(sort.Select(clause => $"&sort={Uri.EscapeDataString(clause)}"));
    }

    private static void AddQueryValues(
        ICollection<string> parameters,
        string name,
        IEnumerable<string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            parameters.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
        }
    }

    private static void AddQueryValue(
        ICollection<string> parameters,
        string name,
        object? value)
    {
        var text = value switch
        {
            null => null,
            string stringValue => stringValue.Trim(),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O"),
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString()
        };

        if (!string.IsNullOrWhiteSpace(text))
        {
            parameters.Add($"{name}={Uri.EscapeDataString(text)}");
        }
    }

    public Task<InstanceDetailDto?> GetInstanceAsync(long id, CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<InstanceDetailDto>($"/api/instances/{id}", cancellationToken);

    public async Task<InstanceVersionChangePreviewDto?> PreviewInstanceVersionChangeAsync(
        long id,
        PreviewInstanceVersionChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/instances/{id}/version-change/preview",
            request,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceVersionChangePreviewDto>(cancellationToken);
    }

    public async Task<ChangeInstanceVersionResultDto?> ChangeInstanceVersionAsync(
        long id,
        ChangeInstanceVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/instances/{id}/version-change",
            request,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ChangeInstanceVersionResultDto>(cancellationToken);
    }

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

    public async Task<UserTaskDto?> GetUserTaskAsync(
        long taskId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/user-tasks/{taskId}",
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<UserTaskDto>(cancellationToken);
    }

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

    public async Task<UserTaskAssignmentAckDto?> AssignUserTaskAsync(
        long taskId,
        AssignUserTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"/api/user-tasks/{taskId}/assign", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<UserTaskAssignmentAckDto>(cancellationToken);
    }

    public async Task<UserTaskAssignmentAckDto?> UnassignUserTaskAsync(
        long taskId,
        UnassignUserTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"/api/user-tasks/{taskId}/unassign", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<UserTaskAssignmentAckDto>(cancellationToken);
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

    public async Task<JobQueueStatisticsDto> GetJobQueueStatisticsAsync(
        CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<JobQueueStatisticsDto>(
            "/api/jobs/statistics",
            cancellationToken)
        ?? throw new InvalidOperationException(
            "The workflow queue statistics response was empty.");

    public async Task<PagedResult<JobSummaryDto>> GetJobsAsync(
        string? status = null,
        string? cursor = null,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var url = $"/api/jobs?pageSize={Math.Clamp(pageSize, 1, 200)}";
        if (!string.IsNullOrWhiteSpace(status))
        {
            url += $"&status={Uri.EscapeDataString(status.Trim())}";
        }
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            url += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        return await httpClient.GetFromJsonAsync<PagedResult<JobSummaryDto>>(url, cancellationToken)
            ?? new PagedResult<JobSummaryDto>([], 1, pageSize, 0);
    }

    public Task<JobDetailDto?> GetJobAsync(
        long id,
        CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<JobDetailDto>($"/api/jobs/{id}", cancellationToken);

    public async Task<PagedResult<JobAttemptDto>> GetJobAttemptsAsync(
        long id,
        string? cursor = null,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var url = $"/api/jobs/{id}/attempts?pageSize={Math.Clamp(pageSize, 1, 200)}";
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            url += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        return await httpClient.GetFromJsonAsync<PagedResult<JobAttemptDto>>(url, cancellationToken)
            ?? new PagedResult<JobAttemptDto>([], 1, pageSize, 0);
    }

    public async Task<PagedResult<IncidentSummaryDto>> GetIncidentsAsync(
        string? status = "open",
        string? cursor = null,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var url = $"/api/incidents?pageSize={Math.Clamp(pageSize, 1, 200)}";
        if (!string.IsNullOrWhiteSpace(status))
        {
            url += $"&status={Uri.EscapeDataString(status.Trim())}";
        }
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            url += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        return await httpClient.GetFromJsonAsync<PagedResult<IncidentSummaryDto>>(url, cancellationToken)
            ?? new PagedResult<IncidentSummaryDto>([], 1, pageSize, 0);
    }

    public Task<IncidentDetailDto?> GetIncidentAsync(
        long id,
        CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<IncidentDetailDto>($"/api/incidents/{id}", cancellationToken);

    public async Task<RetryIncidentResultDto?> RetryIncidentAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsync($"/api/incidents/{id}/retry", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<RetryIncidentResultDto>(cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = string.IsNullOrWhiteSpace(text)
            ? response.ReasonPhrase
            : TryReadErrorMessage(text) ?? text;
        throw new WorkflowApiException(response.StatusCode, message);
    }

    private static string? TryReadErrorMessage(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                return NonEmptyString(root);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // RFC 7807/9457 problem details use detail/title. Flowbit's domain
            // responses use error, while some upstream APIs use message.
            foreach (var propertyName in new[] { "detail", "error", "message" })
            {
                if (TryGetPropertyIgnoreCase(root, propertyName, out var property))
                {
                    var value = ReadMessageValue(property);
                    if (value is not null)
                    {
                        return value;
                    }
                }
            }

            if (TryGetPropertyIgnoreCase(root, "errors", out var errors))
            {
                var validationMessage = ReadValidationErrors(errors);
                if (validationMessage is not null)
                {
                    return validationMessage;
                }
            }

            return TryGetPropertyIgnoreCase(root, "title", out var title)
                ? NonEmptyString(title)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadMessageValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return NonEmptyString(value);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[] { "detail", "message", "description" })
        {
            if (TryGetPropertyIgnoreCase(value, propertyName, out var nested))
            {
                var message = NonEmptyString(nested);
                if (message is not null)
                {
                    return message;
                }
            }
        }

        return null;
    }

    private static string? ReadValidationErrors(JsonElement errors)
    {
        var messages = new List<string>();
        if (errors.ValueKind == JsonValueKind.Object)
        {
            foreach (var error in errors.EnumerateObject())
            {
                AddValidationMessages(error.Value, messages);
            }
        }
        else
        {
            AddValidationMessages(errors, messages);
        }

        return messages.Count == 0 ? null : string.Join(" ", messages.Distinct(StringComparer.Ordinal));
    }

    private static void AddValidationMessages(JsonElement value, List<string> messages)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var message = NonEmptyString(value);
            if (message is not null)
            {
                messages.Add(message);
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in value.EnumerateArray())
        {
            var message = NonEmptyString(item);
            if (message is not null)
            {
                messages.Add(message);
            }
        }
    }

    private static string? NonEmptyString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

public sealed class WorkflowApiException(HttpStatusCode statusCode, string? message)
    : InvalidOperationException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class NodeExecutionSearchQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public long? ExecutionId { get; init; }
    public long? InstanceId { get; init; }
    public long? WorkflowId { get; init; }
    public string? WorkflowKey { get; init; }
    public int? WorkflowVersion { get; init; }
    public string? BusinessKey { get; init; }
    public long? TokenId { get; init; }
    public long? UserTaskId { get; init; }
    public long? MultiInstanceExecutionId { get; init; }
    public long? GatewayBranchId { get; init; }
    public int? ItemIndex { get; init; }
    public string? ExecutionKind { get; init; }
    public int? NodeId { get; init; }
    public string? NodeName { get; init; }
    public string? NodeExternalId { get; init; }
    public IReadOnlyList<string> NodeTypes { get; init; } = [];
    public IReadOnlyList<string> Statuses { get; init; } = [];
    public IReadOnlyList<string> InstanceStatuses { get; init; } = [];
    public IReadOnlyList<string> CompletionReasons { get; init; } = [];
    public bool? IsMultiInstance { get; init; }
    public bool? IsCutoverSeeded { get; init; }
    public string? Owner { get; init; }
    public string? StartedBy { get; init; }
    public string? CompletedBy { get; init; }
    public int? EnteredViaFlowId { get; init; }
    public int? SelectedFlowId { get; init; }
    public int? ExitedViaFlowId { get; init; }
    public int? AggregateFlowId { get; init; }
    public DateTimeOffset? CreatedFrom { get; init; }
    public DateTimeOffset? CreatedTo { get; init; }
    public DateTimeOffset? StartedFrom { get; init; }
    public DateTimeOffset? StartedTo { get; init; }
    public DateTimeOffset? UpdatedFrom { get; init; }
    public DateTimeOffset? UpdatedTo { get; init; }
    public DateTimeOffset? CompletedFrom { get; init; }
    public DateTimeOffset? CompletedTo { get; init; }
    public long? MinimumDurationMilliseconds { get; init; }
    public long? MaximumDurationMilliseconds { get; init; }
    public IReadOnlyList<string> Variables { get; init; } = [];
    public IReadOnlyList<string> Sort { get; init; } = [];
}
