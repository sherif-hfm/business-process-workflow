using System.Net.Http.Json;
using System.Net;
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
        throw new WorkflowApiException(response.StatusCode, string.IsNullOrWhiteSpace(text)
            ? response.ReasonPhrase
            : text);
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
