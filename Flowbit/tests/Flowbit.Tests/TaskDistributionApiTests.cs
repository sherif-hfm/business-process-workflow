using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class TaskDistributionApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ClientId = "workforce-service";
    private const string ClientSecret = "workforce-secret";

    [Fact]
    public async Task RequiredAssignmentTaskIsHiddenUntilAssignedAndHiddenAgainAfterUnassign()
    {
        var model = CreateSimpleModel("required-assignment", ClientId, ClientSecret);
        model.FlowNodes.Single(node => node.Id == 2).RequiresAssignment = true;
        var workflow = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflow.Id);
        Assert.Equal("[redacted]", instance.Workflow.Definition.TaskDistribution!.ClientSecret);

        Assert.Empty((await GetInboxAsync(instance.Id, "alice", "Worker")).Items);
        Assert.Empty((await GetInboxAsync(instance.Id, "bob", "Worker")).Items);
        Assert.Empty((await GetActorTasksAsync(instance.Id, "bob", "Worker")).Items);

        var distributed = await GetSingleTaskAsync(workflow.WorkflowKey, instance.Id);
        Assert.True(distributed.RequiresAssignment);
        Assert.Equal(UserTaskOwnershipKinds.Unassigned, distributed.Ownership);
        Assert.Null(distributed.Owner);

        using var claimWhileHidden = await SendJwtAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{distributed.UserTaskId}/claim",
            null,
            "bob",
            "Worker");
        Assert.Equal(HttpStatusCode.BadRequest, claimWhileHidden.StatusCode);

        var assigned = await AssignAsync(workflow.WorkflowKey, distributed, "bob", null);
        Assert.True(assigned.RequiresAssignment);
        Assert.Empty((await GetInboxAsync(instance.Id, "alice", "Worker")).Items);
        var bobItem = Assert.Single((await GetInboxAsync(instance.Id, "bob", "Worker")).Items);
        Assert.True(bobItem.RequiresAssignment);
        Assert.Equal("bob", bobItem.Assignee);
        Assert.Single((await GetActorTasksAsync(instance.Id, "bob", "Worker")).Items);

        var assignedTask = await GetSingleTaskAsync(workflow.WorkflowKey, instance.Id);
        using var unassignResponse = await SendDistributorAsync(
            HttpMethod.Post,
            TasksPath(workflow.WorkflowKey, $"/{assignedTask.UserTaskId}/unassign"),
            new UnassignUserTaskRequest(assignedTask.UpdatedAt, null));
        Assert.Equal(HttpStatusCode.OK, unassignResponse.StatusCode);
        var unassigned = await ReadAsync<UserTaskAssignmentAckDto>(unassignResponse);
        Assert.True(unassigned.RequiresAssignment);

        Assert.Empty((await GetInboxAsync(instance.Id, "bob", "Worker")).Items);
        Assert.Empty((await GetInboxAsync(instance.Id, "carol", "Worker")).Items);
        Assert.Null((await GetSingleTaskAsync(workflow.WorkflowKey, instance.Id)).Owner);
    }

    [Fact]
    public async Task FromNodeAssignmentInheritsRecordedAssigneeInsteadOfMostRecentActor()
    {
        var workflow = await CreateWorkflowAsync(CreateAssignmentInheritanceModel());
        var instance = await StartAsync(workflow.Id);

        using var first = await SendJwtAsync(
            HttpMethod.Post,
            $"/api/instances/{instance.Id}/flows/201",
            new TakeFlowRequest(null),
            "alice",
            "Worker");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await SendJwtAsync(
            HttpMethod.Post,
            $"/api/instances/{instance.Id}/flows/301",
            new TakeFlowRequest(null),
            "bob",
            "Worker");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var inherited = await GetSingleTaskAsync(workflow.WorkflowKey, instance.Id);
        Assert.True(inherited.RequiresAssignment);
        Assert.Equal("alice", inherited.Owner);
        Assert.Single((await GetInboxAsync(instance.Id, "alice", "Worker")).Items);
        Assert.Empty((await GetInboxAsync(instance.Id, "bob", "Worker")).Items);

        var detail = await GetInstanceAsync(instance.Id);
        var audit = Assert.Single(detail.History, row =>
            row.Note == "taskAssignment"
            && row.Payload is not null
            && row.Payload.TryGetValue("authority", out var authority)
            && authority.GetString() == "assignmentInheritance");
        Assert.Equal("system", audit.PerformedBy);
        Assert.Equal(AssignmentModes.FromNode, audit.Payload!["assignmentMode"].GetString());
        Assert.Equal(2, audit.Payload["sourceNodeId"].GetInt32());
        Assert.Equal("assignee", audit.Payload["candidateField"].GetString());
    }

    [Fact]
    public async Task PreviousAssignmentFallsBackToCompletingActorForSharedPoolSource()
    {
        var workflow = await CreateWorkflowAsync(CreatePreviousAssignmentInheritanceModel());
        var instance = await StartAsync(workflow.Id);

        using var completeSource = await SendJwtAsync(
            HttpMethod.Post,
            $"/api/instances/{instance.Id}/flows/201",
            new TakeFlowRequest(null),
            "casey",
            "Worker");
        Assert.Equal(HttpStatusCode.OK, completeSource.StatusCode);

        var inherited = await GetSingleTaskAsync(workflow.WorkflowKey, instance.Id);
        Assert.True(inherited.RequiresAssignment);
        Assert.Equal("casey", inherited.Owner);
        Assert.Single((await GetInboxAsync(instance.Id, "casey", "Worker")).Items);

        var detail = await GetInstanceAsync(instance.Id);
        var audit = Assert.Single(detail.History, row =>
            row.Note == "taskAssignment"
            && row.Payload is not null
            && row.Payload.TryGetValue("authority", out var authority)
            && authority.GetString() == "assignmentInheritance");
        Assert.Equal(AssignmentModes.Previous, audit.Payload!["assignmentMode"].GetString());
        Assert.Equal("completedBy", audit.Payload["candidateField"].GetString());
    }

    [Fact]
    public async Task DefaultAndStartGuardsKeepDistributorCredentialsAvailable()
    {
        var model = CreateSimpleModel("required-assignment-family", ClientId, ClientSecret);
        model.FlowNodes.Single(node => node.Id == 2).RequiresAssignment = true;
        var requiredVersion = await CreateWorkflowAsync(model);

        model.FlowNodes.Single(node => node.Id == 2).RequiresAssignment = false;
        model.TaskDistribution = null;
        var versionWithoutDistributor = await CreateVersionAsync(requiredVersion.Id, model);

        var running = await StartAsync(requiredVersion.Id);
        using var blockedDefault = await SendJwtAsync(
            HttpMethod.Post,
            $"/api/workflows/{versionWithoutDistributor.Id}/set-default");
        Assert.Equal(HttpStatusCode.BadRequest, blockedDefault.StatusCode);

        var hiddenTask = await GetSingleTaskAsync(requiredVersion.WorkflowKey, running.Id);
        await AssignAsync(requiredVersion.WorkflowKey, hiddenTask, "bob", null);
        using var complete = await SendJwtAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{hiddenTask.UserTaskId}/flows/201",
            new TakeFlowRequest(null),
            "bob",
            "Worker");
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        await SetDefaultAsync(versionWithoutDistributor.Id);
        using var blockedStart = await SendJwtAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(requiredVersion.Id, null, null, null),
            "starter",
            "Worker");
        Assert.Equal(HttpStatusCode.BadRequest, blockedStart.StatusCode);
    }

    [Fact]
    public async Task DistributorListsVariablesMutatesAndAuditsWithoutJwtRoles()
    {
        var workflow = await CreateWorkflowAsync(CreateSimpleModel(
            "external-lifecycle", ClientId, ClientSecret));
        var instance = await StartAsync(workflow.Id, new Dictionary<string, JsonElement>
        {
            ["region"] = JsonSerializer.SerializeToElement("north")
        });

        using var missingCredentials = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(workflow.WorkflowKey, $"?instanceId={instance.Id}"),
            clientId: null,
            clientSecret: null);
        Assert.Equal(HttpStatusCode.Unauthorized, missingCredentials.StatusCode);

        using var wrongCredentials = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(workflow.WorkflowKey, $"?instanceId={instance.Id}"),
            clientId: ClientId,
            clientSecret: "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongCredentials.StatusCode);
        using var wrongClientCasing = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(workflow.WorkflowKey, $"?instanceId={instance.Id}"),
            clientId: ClientId.ToUpperInvariant(),
            clientSecret: ClientSecret);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongClientCasing.StatusCode);

        using var minimalResponse = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(workflow.WorkflowKey, $"?instanceId={instance.Id}"));
        Assert.Equal(HttpStatusCode.OK, minimalResponse.StatusCode);
        var minimalJson = await minimalResponse.Content.ReadAsStringAsync();
        using (var document = JsonDocument.Parse(minimalJson))
        {
            var item = document.RootElement.GetProperty("items")[0];
            Assert.False(item.TryGetProperty("variables", out _));
        }
        var initial = Assert.Single(
            JsonSerializer.Deserialize<PagedResult<ManagedUserTaskDto>>(minimalJson, JsonOptions)!.Items);

        using var variablesResponse = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(workflow.WorkflowKey, $"?instanceId={instance.Id}&includeVariables=true"));
        var withVariables = Assert.Single((await ReadAsync<PagedResult<ManagedUserTaskDto>>(variablesResponse)).Items);
        Assert.Equal("north", withVariables.Variables!["region"].GetString());
        using var variableFilterResponse = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(workflow.WorkflowKey, $"?instanceId={instance.Id}&var=region:south"));
        Assert.Empty((await ReadAsync<PagedResult<ManagedUserTaskDto>>(variableFilterResponse)).Items);

        var otherWorkflow = await CreateWorkflowAsync(CreateSimpleModel(
            "external-other-family", "other-client", "other-secret"));
        var otherInstance = await StartAsync(otherWorkflow.Id);
        using var otherListResponse = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(otherWorkflow.WorkflowKey, $"?instanceId={otherInstance.Id}"),
            clientId: "other-client",
            clientSecret: "other-secret");
        var otherTask = Assert.Single((await ReadAsync<PagedResult<ManagedUserTaskDto>>(otherListResponse)).Items);
        using var familyMismatch = await SendDistributorAsync(
            HttpMethod.Post,
            TasksPath(workflow.WorkflowKey, $"/{otherTask.UserTaskId}/assign"),
            new AssignUserTaskRequest("intruder", otherTask.UpdatedAt, null));
        Assert.Equal(HttpStatusCode.NotFound, familyMismatch.StatusCode);

        var assigned = await AssignAsync(workflow.WorkflowKey, initial, "bob", "Initial distribution");
        Assert.Equal(UserTaskAssignmentOperations.Assigned, assigned.Operation);

        using var exactRetryResponse = await SendDistributorAsync(
            HttpMethod.Post,
            TasksPath(workflow.WorkflowKey, $"/{initial.UserTaskId}/assign"),
            new AssignUserTaskRequest("BOB", initial.UpdatedAt, "retry"));
        Assert.Equal(HttpStatusCode.OK, exactRetryResponse.StatusCode);
        Assert.False((await ReadAsync<UserTaskAssignmentAckDto>(exactRetryResponse)).Changed);

        using var staleResponse = await SendDistributorAsync(
            HttpMethod.Post,
            TasksPath(workflow.WorkflowKey, $"/{initial.UserTaskId}/assign"),
            new AssignUserTaskRequest("carol", initial.UpdatedAt, null));
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);

        var bobTask = await GetSingleTaskAsync(workflow.WorkflowKey, instance.Id);
        var reassigned = await AssignAsync(workflow.WorkflowKey, bobTask, "carol", "Load balancing");
        Assert.Equal(UserTaskAssignmentOperations.Reassigned, reassigned.Operation);

        var carolTask = await GetSingleTaskAsync(workflow.WorkflowKey, instance.Id);
        using var unassignResponse = await SendDistributorAsync(
            HttpMethod.Post,
            TasksPath(workflow.WorkflowKey, $"/{carolTask.UserTaskId}/unassign"),
            new UnassignUserTaskRequest(carolTask.UpdatedAt, "Return to pool"));
        Assert.Equal(HttpStatusCode.OK, unassignResponse.StatusCode);
        var unassigned = await ReadAsync<UserTaskAssignmentAckDto>(unassignResponse);
        Assert.Equal(UserTaskAssignmentOperations.Unassigned, unassigned.Operation);

        var detail = await GetInstanceAsync(instance.Id);
        var history = detail.History.Where(row => row.Note == "taskAssignment").ToList();
        Assert.Equal(3, history.Count);
        Assert.All(history, row =>
        {
            Assert.Equal(ClientId, row.PerformedBy);
            Assert.Equal("taskDistribution", row.Payload!["authority"].GetString());
        });
        Assert.Equal("Load balancing", history[1].Payload!["reason"].GetString());

        using var completeResponse = await SendJwtAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{initial.UserTaskId}/flows/201",
            new TakeFlowRequest(null),
            "worker",
            "Worker");
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

        using var completedMutation = await SendDistributorAsync(
            HttpMethod.Post,
            TasksPath(workflow.WorkflowKey, $"/{initial.UserTaskId}/assign"),
            new AssignUserTaskRequest("dana", unassigned.UpdatedAt, null));
        Assert.Equal(HttpStatusCode.Conflict, completedMutation.StatusCode);
    }

    [Fact]
    public async Task CurrentDefaultCredentialsControlTasksAcrossWorkflowVersions()
    {
        var model = CreateSimpleModel("external-versions", "version-one", "secret-one");
        var versionOne = await CreateWorkflowAsync(model);
        var firstInstance = await StartAsync(versionOne.Id);

        model.TaskDistribution = new TaskDistributionModel
        {
            ClientId = "version-two",
            ClientSecret = "secret-two"
        };
        var versionTwo = await CreateVersionAsync(versionOne.Id, model);
        var secondInstance = await StartAsync(versionTwo.Id);
        await SetDefaultAsync(versionTwo.Id);

        using var oldCredentials = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(versionOne.WorkflowKey),
            clientId: "version-one",
            clientSecret: "secret-one");
        Assert.Equal(HttpStatusCode.Unauthorized, oldCredentials.StatusCode);

        using var newCredentials = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(versionOne.WorkflowKey),
            clientId: "version-two",
            clientSecret: "secret-two");
        var tasks = await ReadAsync<PagedResult<ManagedUserTaskDto>>(newCredentials);
        Assert.Equal(2, tasks.TotalCount);
        Assert.Equal(
            new[] { firstInstance.Id, secondInstance.Id }.OrderBy(id => id),
            tasks.Items.Select(item => item.InstanceId).OrderBy(id => id));
        Assert.Equal(new[] { 1, 2 }, tasks.Items.Select(item => item.WorkflowVersion).OrderBy(value => value));

        await SetDefaultAsync(versionOne.Id);
        using var rotatedBack = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(versionOne.WorkflowKey),
            clientId: "version-one",
            clientSecret: "secret-one");
        Assert.Equal(2, (await ReadAsync<PagedResult<ManagedUserTaskDto>>(rotatedBack)).TotalCount);
    }

    [Fact]
    public async Task SettingAndConfigCredentialTemplatesResolveAndRotate()
    {
        await SetWorkflowSettingAsync("taskDistribution", "clientId", "settings-client-one");
        await SetWorkflowSettingAsync("taskDistribution", "clientSecret", "settings-secret-one");
        var settingsModel = CreateSimpleModel(
            "external-settings",
            "${setting.taskDistribution.clientId}",
            "${setting.taskDistribution.clientSecret}");
        var settingsWorkflow = await CreateWorkflowAsync(settingsModel);
        await StartAsync(settingsWorkflow.Id);

        using var initialSettings = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(settingsWorkflow.WorkflowKey),
            clientId: "settings-client-one",
            clientSecret: "settings-secret-one");
        Assert.Equal(HttpStatusCode.OK, initialSettings.StatusCode);

        await SetWorkflowSettingAsync("taskDistribution", "clientId", "settings-client-two");
        await SetWorkflowSettingAsync("taskDistribution", "clientSecret", "settings-secret-two");
        using var staleSettings = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(settingsWorkflow.WorkflowKey),
            clientId: "settings-client-one",
            clientSecret: "settings-secret-one");
        Assert.Equal(HttpStatusCode.Unauthorized, staleSettings.StatusCode);
        using var rotatedSettings = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(settingsWorkflow.WorkflowKey),
            clientId: "settings-client-two",
            clientSecret: "settings-secret-two");
        Assert.Equal(HttpStatusCode.OK, rotatedSettings.StatusCode);

        var configModel = CreateSimpleModel(
            "external-config",
            "${config.taskDistributionClientId}",
            "${config.taskDistributionClientSecret}");
        var contextOptions = fixture.Factory.Services.GetRequiredService<WorkflowContextOptions>();
        Assert.Equal("config-distributor", contextOptions.Config["taskDistributionClientId"]);
        Assert.Equal("config-distributor-secret", contextOptions.Config["taskDistributionClientSecret"]);
        var configWorkflow = await CreateWorkflowAsync(configModel);
        await StartAsync(configWorkflow.Id);
        using var configResponse = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(configWorkflow.WorkflowKey),
            clientId: "config-distributor",
            clientSecret: "config-distributor-secret");
        Assert.Equal(HttpStatusCode.OK, configResponse.StatusCode);
    }

    [Fact]
    public async Task DistributorListsMultiInstanceChildrenAndPreservesOnePerActorRule()
    {
        var model = DefinitionValidationTests.LoadModel("votes-cardinality-approve-reject.json");
        MakeUnique(model, "external-one-per-actor");
        model.TaskDistribution = new TaskDistributionModel
        {
            ClientId = ClientId,
            ClientSecret = ClientSecret
        };
        var node = model.FlowNodes.Single(flowNode => flowNode.Id == 2);
        node.RequiresClaim = true;
        node.MultiInstance!.OnePerActor = true;
        var workflow = await CreateWorkflowAsync(model);
        var review = await StartAsync(workflow.Id, new Dictionary<string, JsonElement>
        {
            ["voters"] = JsonSerializer.SerializeToElement(2)
        });
        using var enterResponse = await SendJwtAsync(
            HttpMethod.Post,
            $"/api/instances/{review.Id}/flows/204",
            new TakeFlowRequest(null),
            "reviewer",
            "Manager",
            "User");
        Assert.Equal(HttpStatusCode.OK, enterResponse.StatusCode);

        using var listResponse = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(workflow.WorkflowKey, $"?instanceId={review.Id}"));
        var tasks = (await ReadAsync<PagedResult<ManagedUserTaskDto>>(listResponse))
            .Items.OrderBy(item => item.ItemIndex).ToList();
        Assert.Equal(2, tasks.Count);
        await AssignAsync(workflow.WorkflowKey, tasks[0], "voter", null);
        using var duplicate = await SendDistributorAsync(
            HttpMethod.Post,
            TasksPath(workflow.WorkflowKey, $"/{tasks[1].UserTaskId}/assign"),
            new AssignUserTaskRequest("VOTER", tasks[1].UpdatedAt, null));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task DefinitionRejectsIncompleteTaskDistributionCredentials()
    {
        var model = CreateSimpleModel("external-invalid", ClientId, ClientSecret);
        model.TaskDistribution!.ClientSecret = "";
        using var response = await SendJwtAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MissingConfigurationDisablesDistributionAndUnknownFamilyIsNotFound()
    {
        var model = CreateSimpleModel("external-disabled", ClientId, ClientSecret);
        model.TaskDistribution = null;
        var workflow = await CreateWorkflowAsync(model);
        await StartAsync(workflow.Id);

        using var disabled = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(workflow.WorkflowKey));
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);

        using var missing = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath("missing-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task<UserTaskAssignmentAckDto> AssignAsync(
        string workflowKey,
        ManagedUserTaskDto task,
        string actorId,
        string? reason)
    {
        using var response = await SendDistributorAsync(
            HttpMethod.Post,
            TasksPath(workflowKey, $"/{task.UserTaskId}/assign"),
            new AssignUserTaskRequest(actorId, task.UpdatedAt, reason));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<UserTaskAssignmentAckDto>(response);
    }

    private async Task<ManagedUserTaskDto> GetSingleTaskAsync(string workflowKey, long instanceId)
    {
        using var response = await SendDistributorAsync(
            HttpMethod.Get,
            TasksPath(workflowKey, $"?instanceId={instanceId}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single((await ReadAsync<PagedResult<ManagedUserTaskDto>>(response)).Items);
    }

    private async Task<WorkflowDetailDto> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendJwtAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<WorkflowDetailDto>(response);
    }

    private async Task<WorkflowDetailDto> CreateVersionAsync(long sourceId, WorkflowModel model)
    {
        using var response = await SendJwtAsync(
            HttpMethod.Put,
            $"/api/workflows/{sourceId}",
            new UpdateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<WorkflowDetailDto>(response);
    }

    private async Task SetDefaultAsync(long workflowId)
    {
        using var response = await SendJwtAsync(
            HttpMethod.Post,
            $"/api/workflows/{workflowId}/set-default");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<InstanceDetailDto> StartAsync(
        long workflowId,
        Dictionary<string, JsonElement>? variables = null)
    {
        using var response = await SendJwtAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, variables),
            "starter",
            "Worker",
            "User");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<InstanceDetailDto> GetInstanceAsync(long instanceId)
    {
        using var response = await SendJwtAsync(HttpMethod.Get, $"/api/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<PagedResult<InboxItemDto>> GetInboxAsync(
        long instanceId,
        string user,
        params string[] roles)
    {
        using var response = await SendJwtAsync(
            HttpMethod.Get,
            $"/api/instances/inbox?instanceId={instanceId}&pageSize=200",
            null,
            user,
            roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<InboxItemDto>>(response);
    }

    private async Task<PagedResult<UserTaskDto>> GetActorTasksAsync(
        long instanceId,
        string user,
        params string[] roles)
    {
        using var response = await SendJwtAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}/user-tasks?status=active&pageSize=200",
            null,
            user,
            roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<UserTaskDto>>(response);
    }

    private async Task SetWorkflowSettingAsync(string? settingNamespace, string name, string value)
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.WorkflowSettings.SingleOrDefaultAsync(item =>
            item.Namespace == settingNamespace && item.Name == name);
        if (setting is null)
        {
            db.WorkflowSettings.Add(new WorkflowSettingEntity
            {
                Namespace = settingNamespace,
                Name = name,
                Value = JsonSerializer.SerializeToElement(value)
            });
        }
        else
        {
            setting.Value = JsonSerializer.SerializeToElement(value);
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> SendDistributorAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string? clientId = ClientId,
        string? clientSecret = ClientSecret)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        if (clientId is not null) request.Headers.Add("X-Client-Id", clientId);
        if (clientSecret is not null) request.Headers.Add("X-Client-Secret", clientSecret);
        return await fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendJwtAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "admin",
        params string[] roles)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        ApiTestAuth.Authorize(request, user, roles.Length == 0 ? ["admin"] : roles);
        return await fixture.Client.SendAsync(request);
    }

    private static string TasksPath(string workflowKey, string suffix = "") =>
        $"/api/task-distribution/workflows/{Uri.EscapeDataString(workflowKey)}/tasks{suffix}";

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel CreateSimpleModel(
        string label,
        string clientId,
        string clientSecret)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"tests-{label}-{suffix}",
            Name = $"Tests {label} {suffix}",
            InitialEventId = 1,
            TaskDistribution = new TaskDistributionModel
            {
                ClientId = clientId,
                ClientSecret = clientSecret
            },
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent,
                    Variables =
                    [
                        new VariableModel
                        {
                            Id = 1,
                            Name = "region",
                            DataType = WorkflowVariableTypes.String,
                            Required = false
                        }
                    ]
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    ExternalId = "TASK_REVIEW",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"]
                },
                new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "Complete", SourceRef = 2, TargetRef = 3 }
            ]
        };
    }

    private static WorkflowModel CreateAssignmentInheritanceModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"tests-assignment-inheritance-{suffix}",
            Name = $"Tests assignment inheritance {suffix}",
            InitialEventId = 1,
            TaskDistribution = new TaskDistributionModel
            {
                ClientId = ClientId,
                ClientSecret = ClientSecret
            },
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Named owner",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"],
                    AssigneeExpression = "'alice'"
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Intervening review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"]
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Return to named owner",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"],
                    RequiresAssignment = true,
                    AssignmentMode = AssignmentModes.FromNode,
                    InheritAssignmentFromNodeId = 2
                },
                new FlowNodeModel { Id = 5, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "Continue", SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, Name = "Continue", SourceRef = 3, TargetRef = 4 },
                new SequenceFlowModel { Id = 401, Name = "Complete", SourceRef = 4, TargetRef = 5 }
            ]
        };
    }

    private static WorkflowModel CreatePreviousAssignmentInheritanceModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"tests-previous-assignment-{suffix}",
            Name = $"Tests previous assignment {suffix}",
            InitialEventId = 1,
            TaskDistribution = new TaskDistributionModel
            {
                ClientId = ClientId,
                ClientSecret = ClientSecret
            },
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Shared review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"]
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Continue with actor",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"],
                    RequiresAssignment = true,
                    AssignmentMode = AssignmentModes.Previous
                },
                new FlowNodeModel { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "Continue", SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, Name = "Complete", SourceRef = 3, TargetRef = 4 }
            ]
        };
    }

    private static void MakeUnique(WorkflowModel model, string label)
    {
        var suffix = Guid.NewGuid().ToString("N");
        model.Id = $"tests-{label}-{suffix}";
        model.Name = $"Tests {label} {suffix}";
    }
}
