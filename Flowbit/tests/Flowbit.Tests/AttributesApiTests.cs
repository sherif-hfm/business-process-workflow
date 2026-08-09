using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AttributesApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TaskSurfacesReturnCurrentNodeAttributesAndFlowSurfaceReturnsFlowAttributes()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var clientId = $"attributes-distributor-{suffix}";
        var clientSecret = $"attributes-secret-{suffix}";
        var model = BuildAttributedModel(suffix, clientId, clientSecret);

        var workflow = await CreateWorkflowAsync(model);
        AssertDefinitionAttributes(workflow);
        AssertDefinitionAttributes(await GetAsync<WorkflowDetailDto>(
            $"/api/workflows/{workflow.Id}",
            "definition-reader",
            "admin"));
        var instance = await StartAsync(workflow.Id);
        var instanceDetail = await GetAsync<InstanceDetailDto>(
            $"/api/instances/{instance.Id}",
            "instance-reader",
            "Requester");
        AssertDefinitionAttributes(instanceDetail.Workflow);

        var inbox = await GetAsync<PagedResult<InboxItemDto>>(
            $"/api/instances/inbox?instanceId={instance.Id}",
            "reviewer",
            "Reviewer");
        var inboxTask = Assert.Single(inbox.Items);
        AssertTaskAttributes(inboxTask.Attributes);

        using (var inboxSearchResponse = await SendJwtAsync(
                   HttpMethod.Post,
                   "/api/instances/inbox/search",
                   new InboxSearchRequest { InstanceId = instance.Id },
                   "reviewer",
                   ["Reviewer"]))
        {
            Assert.Equal(HttpStatusCode.OK, inboxSearchResponse.StatusCode);
            var searchedInbox = await ReadAsync<PagedResult<InboxItemDto>>(inboxSearchResponse);
            AssertTaskAttributes(Assert.Single(searchedInbox.Items).Attributes);
        }

        var taskPage = await GetAsync<PagedResult<UserTaskDto>>(
            $"/api/instances/{instance.Id}/user-tasks?status=active",
            "reviewer",
            "Reviewer");
        var task = Assert.Single(taskPage.Items);
        Assert.Equal(inboxTask.UserTaskId, task.Id);
        AssertTaskAttributes(task.Attributes);

        var detail = await GetAsync<UserTaskDto>(
            $"/api/user-tasks/{task.Id}",
            "reviewer",
            "Reviewer");
        AssertTaskAttributes(detail.Attributes);

        using (var claimResponse = await SendJwtAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/claim",
                   user: "reviewer",
                   roles: ["Reviewer"]))
        {
            Assert.Equal(HttpStatusCode.OK, claimResponse.StatusCode);
            var claimed = await ReadAsync<UserTaskDto>(claimResponse);
            AssertTaskAttributes(claimed.Attributes);
        }

        var flows = await GetAsync<List<SequenceFlowModel>>(
            $"/api/user-tasks/{task.Id}/flows",
            "reviewer",
            "Reviewer");
        var flow = Assert.Single(flows);
        var flowAttribute = Assert.Single(flow.Attributes);
        Assert.Equal("command", flowAttribute.Key);
        Assert.Equal("approve-purchase", flowAttribute.Value);

        var instanceFlows = await GetAsync<List<SequenceFlowModel>>(
            $"/api/instances/{instance.Id}/flows",
            "reviewer",
            "Reviewer");
        AssertFlowAttributes(Assert.Single(instanceFlows).Attributes);

        var managed = await GetAsync<PagedResult<ManagedUserTaskDto>>(
            $"/api/user-tasks/manage?taskId={task.Id}",
            "manager",
            "TaskManager");
        AssertTaskAttributes(Assert.Single(managed.Items).Attributes);

        using (var manageSearchResponse = await SendJwtAsync(
                   HttpMethod.Post,
                   "/api/user-tasks/manage/search",
                   new ManageableUserTaskSearchRequest { TaskId = task.Id },
                   "manager",
                   ["TaskManager"]))
        {
            Assert.Equal(HttpStatusCode.OK, manageSearchResponse.StatusCode);
            var searchedTasks = await ReadAsync<PagedResult<ManagedUserTaskDto>>(manageSearchResponse);
            AssertTaskAttributes(Assert.Single(searchedTasks.Items).Attributes);
        }

        using (var distributionResponse = await SendDistributorAsync(
                   HttpMethod.Get,
                   $"/api/task-distribution/workflows/{workflow.WorkflowKey}/tasks?taskId={task.Id}",
                   clientId,
                   clientSecret))
        {
            Assert.Equal(HttpStatusCode.OK, distributionResponse.StatusCode);
            var distributed = await ReadAsync<PagedResult<ManagedUserTaskDto>>(distributionResponse);
            AssertTaskAttributes(Assert.Single(distributed.Items).Attributes);
        }

        using (var distributionSearchResponse = await SendDistributorAsync(
                   HttpMethod.Post,
                   $"/api/task-distribution/workflows/{workflow.WorkflowKey}/tasks/search",
                   clientId,
                   clientSecret,
                   new DistributableUserTaskSearchRequest { TaskId = task.Id }))
        {
            Assert.Equal(HttpStatusCode.OK, distributionSearchResponse.StatusCode);
            var searchedTasks = await ReadAsync<PagedResult<ManagedUserTaskDto>>(distributionSearchResponse);
            AssertTaskAttributes(Assert.Single(searchedTasks.Items).Attributes);
        }

        using var unclaimResponse = await SendJwtAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/unclaim",
            user: "reviewer",
            roles: ["Reviewer"]);
        Assert.Equal(HttpStatusCode.OK, unclaimResponse.StatusCode);
        AssertTaskAttributes((await ReadAsync<UserTaskDto>(unclaimResponse)).Attributes);

        model.FlowNodes.Single(node => node.Id == 2).Attributes[0].Value = "purchase-review-v2";
        var newVersion = await CreateWorkflowVersionAsync(workflow.Id, model);
        AssertDefinitionAttributes(newVersion, "purchase-review-v2");
        var beforeVersionChange = await GetAsync<InstanceDetailDto>(
            $"/api/instances/{instance.Id}",
            "version-manager",
            "admin");
        using (var versionChangeResponse = await SendJwtAsync(
                   HttpMethod.Post,
                   $"/api/instances/{instance.Id}/version-change",
                   new ChangeInstanceVersionRequest(
                       newVersion.Id,
                       workflow.Id,
                       beforeVersionChange.UpdatedAt,
                       "Verify current-definition attributes"),
                   "version-manager",
                   ["admin"]))
        {
            Assert.Equal(HttpStatusCode.OK, versionChangeResponse.StatusCode);
            var changed = await ReadAsync<ChangeInstanceVersionResultDto>(versionChangeResponse);
            AssertDefinitionAttributes(changed.Instance.Workflow, "purchase-review-v2");
        }

        var taskAfterVersionChange = await GetAsync<UserTaskDto>(
            $"/api/user-tasks/{task.Id}",
            "reviewer",
            "Reviewer");
        AssertTaskAttributes(taskAfterVersionChange.Attributes, "purchase-review-v2");

        var inboxAfterVersionChange = await GetAsync<PagedResult<InboxItemDto>>(
            $"/api/instances/inbox?instanceId={instance.Id}",
            "reviewer",
            "Reviewer");
        AssertTaskAttributes(
            Assert.Single(inboxAfterVersionChange.Items).Attributes,
            "purchase-review-v2");

        var managedAfterVersionChange = await GetAsync<PagedResult<ManagedUserTaskDto>>(
            $"/api/user-tasks/manage?taskId={task.Id}",
            "manager",
            "TaskManager");
        AssertTaskAttributes(
            Assert.Single(managedAfterVersionChange.Items).Attributes,
            "purchase-review-v2");

        using var distributionAfterVersionChangeResponse = await SendDistributorAsync(
            HttpMethod.Get,
            $"/api/task-distribution/workflows/{workflow.WorkflowKey}/tasks?taskId={task.Id}",
            clientId,
            clientSecret);
        Assert.Equal(HttpStatusCode.OK, distributionAfterVersionChangeResponse.StatusCode);
        var distributionAfterVersionChange =
            await ReadAsync<PagedResult<ManagedUserTaskDto>>(distributionAfterVersionChangeResponse);
        AssertTaskAttributes(
            Assert.Single(distributionAfterVersionChange.Items).Attributes,
            "purchase-review-v2");
    }

    [Fact]
    public async Task MultiInstanceExecutionFlowSurfaceReturnsFlowAttributes()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        model.Id = $"mi-attributes-api-{suffix}";
        model.Name = $"MI attributes API {suffix}";
        model.SequenceFlows.Single(flow => flow.Id == 203).Attributes =
        [
            new WorkflowAttributeModel { Key = "command", Value = "cancel-vote" }
        ];

        var workflow = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflow.Id);
        using var enterResponse = await SendJwtAsync(
            HttpMethod.Post,
            $"/api/instances/{instance.Id}/flows/204",
            new TakeFlowRequest(new Dictionary<string, JsonElement>
            {
                ["voters"] = JsonSerializer.SerializeToElement(new[] { "alice", "bob" })
            }),
            "manager",
            ["Manager"]);
        Assert.Equal(HttpStatusCode.OK, enterResponse.StatusCode);
        var entered = await ReadAsync<InstanceDetailDto>(enterResponse);
        var execution = Assert.IsType<MultiInstanceProgressDto>(entered.MultiInstance);

        var flows = await GetAsync<List<SequenceFlowModel>>(
            $"/api/multi-instance-executions/{execution.ExecutionId}/flows",
            "manager",
            "Manager");
        var interrupt = Assert.Single(flows);
        Assert.Equal(203, interrupt.Id);
        var attribute = Assert.Single(interrupt.Attributes);
        Assert.Equal("command", attribute.Key);
        Assert.Equal("cancel-vote", attribute.Value);
    }

    [Fact]
    public async Task WorkflowHttpRoundTripNormalizesMissingAndExplicitNullAttributes()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var model = BuildAttributedModel(
            $"normalization-{suffix}",
            $"normalization-client-{suffix}",
            $"normalization-secret-{suffix}");
        var body = Assert.IsType<JsonObject>(
            JsonSerializer.SerializeToNode(new CreateWorkflowRequest(model, true), JsonOptions));
        var definition = Assert.IsType<JsonObject>(body["definition"]);
        var nodes = Assert.IsType<JsonArray>(definition["flowNodes"]);
        Assert.IsType<JsonObject>(nodes[0]).Remove("attributes");
        Assert.IsType<JsonObject>(nodes[1])["attributes"] = null;
        var flows = Assert.IsType<JsonArray>(definition["sequenceFlows"]);
        Assert.IsType<JsonObject>(flows[0]).Remove("attributes");
        Assert.IsType<JsonObject>(flows[1])["attributes"] = null;

        using var createResponse = await SendJwtAsync(HttpMethod.Post, "/api/workflows", body);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await ReadAsync<WorkflowDetailDto>(createResponse);
        AssertNormalizedAttributes(created);

        var fetched = await GetAsync<WorkflowDetailDto>(
            $"/api/workflows/{created.Id}",
            "definition-reader",
            "admin");
        AssertNormalizedAttributes(fetched);
    }

    [Fact]
    public async Task ManageAndDistributionAttributeEnrichmentQueryCountIsPageBounded()
    {
        const int definitionCount = 6;
        var suffix = Guid.NewGuid().ToString("N");
        var clientId = $"bounded-distributor-{suffix}";
        var clientSecret = $"bounded-secret-{suffix}";
        var model = BuildAttributedModel($"bounded-{suffix}", clientId, clientSecret);
        var first = await CreateWorkflowAsync(model);
        await StartAsync(first.Id);

        for (var version = 2; version <= definitionCount; version++)
        {
            model.FlowNodes.Single(node => node.Id == 2).Attributes[0].Value =
                $"purchase-review-v{version}";
            var workflow = await CreateWorkflowVersionAsync(first.Id, model);
            await StartAsync(workflow.Id);
        }

        await GetAsync<PagedResult<ManagedUserTaskDto>>(
            $"/api/user-tasks/manage?workflowKey={first.WorkflowKey}&pageSize=1",
            "manager",
            "TaskManager");
        fixture.CommandCounter.Reset();
        var oneManaged = await GetAsync<PagedResult<ManagedUserTaskDto>>(
            $"/api/user-tasks/manage?workflowKey={first.WorkflowKey}&pageSize=1",
            "manager",
            "TaskManager");
        var oneManagedCommands = fixture.CommandCounter.ReaderCommands;
        fixture.CommandCounter.Reset();
        var allManaged = await GetAsync<PagedResult<ManagedUserTaskDto>>(
            $"/api/user-tasks/manage?workflowKey={first.WorkflowKey}&pageSize={definitionCount}",
            "manager",
            "TaskManager");
        var allManagedCommands = fixture.CommandCounter.ReaderCommands;

        Assert.Equal(definitionCount, oneManaged.TotalCount);
        Assert.Single(oneManaged.Items);
        Assert.Equal(definitionCount, allManaged.Items.Count);
        Assert.Equal(
            definitionCount,
            allManaged.Items.Select(task => task.WorkflowId).Distinct().Count());
        Assert.All(allManaged.Items, task => AssertTaskAttributesPresent(task.Attributes));
        Assert.Equal(oneManagedCommands, allManagedCommands);
        Assert.InRange(allManagedCommands, 1, 10);

        await GetDistributedPageAsync(
            first.WorkflowKey,
            clientId,
            clientSecret,
            pageSize: 1);
        fixture.CommandCounter.Reset();
        var oneDistributed = await GetDistributedPageAsync(
            first.WorkflowKey,
            clientId,
            clientSecret,
            pageSize: 1);
        var oneDistributedCommands = fixture.CommandCounter.ReaderCommands;
        fixture.CommandCounter.Reset();
        var allDistributed = await GetDistributedPageAsync(
            first.WorkflowKey,
            clientId,
            clientSecret,
            pageSize: definitionCount);
        var allDistributedCommands = fixture.CommandCounter.ReaderCommands;

        Assert.Equal(definitionCount, oneDistributed.TotalCount);
        Assert.Single(oneDistributed.Items);
        Assert.Equal(definitionCount, allDistributed.Items.Count);
        Assert.Equal(
            definitionCount,
            allDistributed.Items.Select(task => task.WorkflowId).Distinct().Count());
        Assert.All(allDistributed.Items, task => AssertTaskAttributesPresent(task.Attributes));
        Assert.InRange(Math.Abs(allDistributedCommands - oneDistributedCommands), 0, 1);
        Assert.InRange(allDistributedCommands, 1, 12);
    }

    private static WorkflowModel BuildAttributedModel(
        string suffix,
        string clientId,
        string clientSecret) =>
        new()
        {
            Id = $"attributes-api-{suffix}",
            Name = $"Attributes API {suffix}",
            InitialEventId = 1,
            TaskAssignmentRoles = ["TaskManager"],
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
                    Attributes =
                    [
                        new WorkflowAttributeModel { Key = "event-kind", Value = "manual" }
                    ]
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Reviewer"],
                    RequiresClaim = true,
                    Attributes =
                    [
                        new WorkflowAttributeModel { Key = "screen", Value = "purchase-review" },
                        new WorkflowAttributeModel { Key = "integration", Value = "erp" }
                    ]
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Done",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Open review", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Approve",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["Reviewer"],
                    Attributes =
                    [
                        new WorkflowAttributeModel { Key = "command", Value = "approve-purchase" }
                    ]
                }
            ]
        };

    private async Task<PagedResult<ManagedUserTaskDto>> GetDistributedPageAsync(
        string workflowKey,
        string clientId,
        string clientSecret,
        int pageSize)
    {
        using var response = await SendDistributorAsync(
            HttpMethod.Get,
            $"/api/task-distribution/workflows/{workflowKey}/tasks?pageSize={pageSize}",
            clientId,
            clientSecret);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<ManagedUserTaskDto>>(response);
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

    private async Task<WorkflowDetailDto> CreateWorkflowVersionAsync(
        long sourceWorkflowId,
        WorkflowModel model)
    {
        using var response = await SendJwtAsync(
            HttpMethod.Put,
            $"/api/workflows/{sourceWorkflowId}",
            new UpdateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<WorkflowDetailDto>(response);
    }

    private async Task<StartInstanceResultDto> StartAsync(long workflowId)
    {
        using var response = await SendJwtAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(workflowId, null, null, null),
            "starter",
            ["Requester"]);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<StartInstanceResultDto>(response);
    }

    private async Task<T> GetAsync<T>(string path, string user, params string[] roles)
    {
        using var response = await SendJwtAsync(HttpMethod.Get, path, user: user, roles: roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<T>(response);
    }

    private async Task<HttpResponseMessage> SendJwtAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        string[]? roles = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles ?? []);
        return await fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendDistributorAsync(
        HttpMethod method,
        string path,
        string clientId,
        string clientSecret,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        request.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
        request.Headers.TryAddWithoutValidation("X-Client-Secret", clientSecret);
        return await fixture.Client.SendAsync(request);
    }

    private static void AssertTaskAttributes(
        IReadOnlyList<WorkflowAttributeModel> attributes,
        string screenValue = "purchase-review")
    {
        Assert.Collection(
            attributes,
            attribute =>
            {
                Assert.Equal("screen", attribute.Key);
                Assert.Equal(screenValue, attribute.Value);
            },
            attribute =>
            {
                Assert.Equal("integration", attribute.Key);
                Assert.Equal("erp", attribute.Value);
            });
    }

    private static void AssertTaskAttributesPresent(
        IReadOnlyList<WorkflowAttributeModel> attributes)
    {
        Assert.Contains(attributes, attribute => attribute.Key == "screen");
        Assert.Contains(
            attributes,
            attribute => attribute.Key == "integration" && attribute.Value == "erp");
    }

    private static void AssertFlowAttributes(IReadOnlyList<WorkflowAttributeModel> attributes)
    {
        var attribute = Assert.Single(attributes);
        Assert.Equal("command", attribute.Key);
        Assert.Equal("approve-purchase", attribute.Value);
    }

    private static void AssertDefinitionAttributes(
        WorkflowDetailDto workflow,
        string screenValue = "purchase-review")
    {
        AssertTaskAttributes(
            workflow.Definition.FlowNodes.Single(node => node.Id == 2).Attributes,
            screenValue);
        AssertFlowAttributes(
            workflow.Definition.SequenceFlows.Single(flow => flow.Id == 201).Attributes);
    }

    private static void AssertNormalizedAttributes(WorkflowDetailDto workflow)
    {
        Assert.Empty(workflow.Definition.FlowNodes.Single(node => node.Id == 1).Attributes);
        Assert.Empty(workflow.Definition.FlowNodes.Single(node => node.Id == 2).Attributes);
        Assert.Empty(workflow.Definition.SequenceFlows.Single(flow => flow.Id == 101).Attributes);
        Assert.Empty(workflow.Definition.SequenceFlows.Single(flow => flow.Id == 201).Attributes);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");
}
