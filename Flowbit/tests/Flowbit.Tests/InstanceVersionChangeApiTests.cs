using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVersionChangeApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Endpoints_RequireAuthenticationAndConfiguredWorkflowAdministratorRole()
    {
        await SetWorkflowRequiredRoleAsync(string.Empty);
        try
        {
            var seed = await SeedFamilyAsync(versionCount: 2);
            var previewRequest = new PreviewInstanceVersionChangeRequest(seed.Target.Id);
            var changeRequest = new ChangeInstanceVersionRequest(
                seed.Target.Id,
                seed.Source.Id,
                seed.Started.UpdatedAt,
                "authorized release");

            using var anonymousPreview = await SendAnonymousAsync(
                HttpMethod.Post,
                $"/api/instances/{seed.Started.Id}/version-change/preview",
                previewRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousPreview.StatusCode);

            using var anonymousChange = await SendAnonymousAsync(
                HttpMethod.Post,
                $"/api/instances/{seed.Started.Id}/version-change",
                changeRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousChange.StatusCode);

            using var forbiddenPreview = await SendAsync(
                HttpMethod.Post,
                $"/api/instances/{seed.Started.Id}/version-change/preview",
                previewRequest,
                user: "worker",
                roles: ["Worker"],
                suppressDefaultAdmin: true);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenPreview.StatusCode);

            using var forbiddenChange = await SendAsync(
                HttpMethod.Post,
                $"/api/instances/{seed.Started.Id}/version-change",
                changeRequest,
                user: "worker",
                roles: ["Worker"],
                suppressDefaultAdmin: true);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenChange.StatusCode);

            using var defaultAdmin = await SendAsync(
                HttpMethod.Post,
                $"/api/instances/{seed.Started.Id}/version-change/preview",
                previewRequest,
                user: "default-admin",
                roles: ["ADMIN"],
                suppressDefaultAdmin: true);
            Assert.Equal(HttpStatusCode.OK, defaultAdmin.StatusCode);

            await SetWorkflowRequiredRoleAsync(" ReleaseManager, MigrationAdmin ");

            using var formerDefaultAdmin = await SendAsync(
                HttpMethod.Post,
                $"/api/instances/{seed.Started.Id}/version-change/preview",
                previewRequest,
                user: "admin-only",
                roles: ["admin"],
                suppressDefaultAdmin: true);
            Assert.Equal(HttpStatusCode.Forbidden, formerDefaultAdmin.StatusCode);

            using var configuredAdmin = await SendAsync(
                HttpMethod.Post,
                $"/api/instances/{seed.Started.Id}/version-change/preview",
                previewRequest,
                user: "release-operator",
                roles: ["migrationadmin"],
                suppressDefaultAdmin: true);
            Assert.Equal(HttpStatusCode.OK, configuredAdmin.StatusCode);
            var preview = await ReadAsync<InstanceVersionChangePreviewDto>(configuredAdmin);
            Assert.True(preview.Compatible);
        }
        finally
        {
            await SetWorkflowRequiredRoleAsync("admin");
        }
    }

    [Fact]
    public async Task Preview_ReturnsCompatibilityConcurrencyAndRequestClassifications()
    {
        await SetWorkflowRequiredRoleAsync("admin");
        var seed = await SeedFamilyAsync(versionCount: 3);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{seed.Started.Id}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(seed.Target.Id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await ReadAsync<InstanceVersionChangePreviewDto>(response);
        Assert.Equal(seed.Started.Id, preview.InstanceId);
        Assert.Equal(seed.Source.Id, preview.SourceWorkflow.Id);
        Assert.Equal(seed.Target.Id, preview.TargetWorkflow.Id);
        Assert.Equal(1, preview.SourceWorkflow.Version);
        Assert.Equal(3, preview.TargetWorkflow.Version);
        Assert.Equal(InstanceVersionChangeDirections.Upgrade, preview.Direction);
        Assert.True(preview.Compatible);
        Assert.Empty(preview.Blockers);
        Assert.Equal(seed.Source.Id, preview.ExpectedSourceWorkflowId);
        Assert.Equal(seed.Started.UpdatedAt, preview.ExpectedUpdatedAt);

        using var missingInstance = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{long.MaxValue}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(seed.Target.Id));
        Assert.Equal(HttpStatusCode.NotFound, missingInstance.StatusCode);

        using var missingTarget = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{seed.Started.Id}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(long.MaxValue));
        Assert.Equal(HttpStatusCode.NotFound, missingTarget.StatusCode);

        using var malformedTarget = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{seed.Started.Id}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(0));
        Assert.Equal(HttpStatusCode.BadRequest, malformedTarget.StatusCode);

        using var sameVersion = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{seed.Started.Id}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(seed.Source.Id));
        Assert.Equal(HttpStatusCode.BadRequest, sameVersion.StatusCode);

        var otherFamily = await CreateWorkflowAsync(CreateModel("other-family"), publish: true);
        using var crossFamily = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{seed.Started.Id}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(otherFamily.Id));
        Assert.Equal(HttpStatusCode.BadRequest, crossFamily.StatusCode);
    }

    [Fact]
    public async Task Change_UpgradesAndDowngradesWhileReturningAuditAndConflicts()
    {
        await SetWorkflowRequiredRoleAsync("admin");
        var seed = await SeedFamilyAsync(versionCount: 3);
        var upgradePreview = await PreviewAsync(seed.Started.Id, seed.Target.Id);

        using var invalidReason = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{seed.Started.Id}/version-change",
            new ChangeInstanceVersionRequest(
                seed.Target.Id,
                upgradePreview.ExpectedSourceWorkflowId,
                upgradePreview.ExpectedUpdatedAt,
                "   "));
        Assert.Equal(HttpStatusCode.BadRequest, invalidReason.StatusCode);

        using var stale = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{seed.Started.Id}/version-change",
            new ChangeInstanceVersionRequest(
                seed.Target.Id,
                upgradePreview.ExpectedSourceWorkflowId,
                upgradePreview.ExpectedUpdatedAt.AddTicks(-1),
                "stale preview"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var upgradedResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{seed.Started.Id}/version-change",
            new ChangeInstanceVersionRequest(
                seed.Target.Id,
                upgradePreview.ExpectedSourceWorkflowId,
                upgradePreview.ExpectedUpdatedAt,
                "  approved non-adjacent upgrade  "),
            user: "migration-admin",
            roles: ["admin", "Auditor"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.OK, upgradedResponse.StatusCode);
        var upgraded = await ReadAsync<ChangeInstanceVersionResultDto>(upgradedResponse);
        Assert.Equal(seed.Target.Id, upgraded.Instance.Workflow.Id);
        Assert.Equal(seed.Source.Id, upgraded.VersionChange.SourceWorkflow.Id);
        Assert.Equal(seed.Target.Id, upgraded.VersionChange.TargetWorkflow.Id);
        Assert.Equal(InstanceVersionChangeDirections.Upgrade, upgraded.VersionChange.Direction);
        Assert.Equal("migration-admin", upgraded.VersionChange.ChangedBy);
        Assert.Equal(
            ["admin", "Auditor"],
            upgraded.VersionChange.ChangedByRoles,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal("approved non-adjacent upgrade", upgraded.VersionChange.Reason);
        Assert.Contains(
            upgraded.Instance.VersionChanges,
            change => change.Id == upgraded.VersionChange.Id);

        var downgradePreview = await PreviewAsync(seed.Started.Id, seed.Source.Id);
        Assert.Equal(InstanceVersionChangeDirections.Downgrade, downgradePreview.Direction);
        Assert.True(downgradePreview.Compatible);

        using var downgradedResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{seed.Started.Id}/version-change",
            new ChangeInstanceVersionRequest(
                seed.Source.Id,
                downgradePreview.ExpectedSourceWorkflowId,
                downgradePreview.ExpectedUpdatedAt,
                "rollback after verification"));
        Assert.Equal(HttpStatusCode.OK, downgradedResponse.StatusCode);
        var downgraded = await ReadAsync<ChangeInstanceVersionResultDto>(downgradedResponse);
        Assert.Equal(seed.Source.Id, downgraded.Instance.Workflow.Id);
        Assert.Equal(InstanceVersionChangeDirections.Downgrade, downgraded.VersionChange.Direction);
        Assert.Equal(2, downgraded.Instance.VersionChanges.Count);
    }

    [Fact]
    public async Task ConcurrentChanges_FromOnePreview_CommitExactlyOnce()
    {
        await SetWorkflowRequiredRoleAsync("admin");
        var seed = await SeedFamilyAsync(versionCount: 2);
        var preview = await PreviewAsync(seed.Started.Id, seed.Target.Id);
        var request = new ChangeInstanceVersionRequest(
            seed.Target.Id,
            preview.ExpectedSourceWorkflowId,
            preview.ExpectedUpdatedAt,
            "concurrent release");

        var attempts = await Task.WhenAll(
            SendAsync(
                HttpMethod.Post,
                $"/api/instances/{seed.Started.Id}/version-change",
                request,
                user: "operator-one"),
            SendAsync(
                HttpMethod.Post,
                $"/api/instances/{seed.Started.Id}/version-change",
                request,
                user: "operator-two"));
        try
        {
            Assert.Equal(
                1,
                attempts.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(
                1,
                attempts.Count(response => response.StatusCode == HttpStatusCode.Conflict));

            await using var db = fixture.CreateDbContext();
            Assert.Equal(
                1,
                await db.WorkflowInstanceVersionChanges.CountAsync(change =>
                    change.InstanceId == seed.Started.Id));
        }
        finally
        {
            foreach (var attempt in attempts)
            {
                attempt.Dispose();
            }
        }
    }

    [Fact]
    public async Task CompatibleChange_UsesTargetTaskActionImmediatelyAndCompletesOnce()
    {
        await SetWorkflowRequiredRoleAsync("admin");
        var sourceModel = CreateModel("version-change-continuation");
        var sourceAction = sourceModel.SequenceFlows.Single(flow => flow.Id == 20);
        sourceAction.Name = "Approve original request";
        sourceAction.Roles = ["SourceApprover"];
        var source = await CreateWorkflowAsync(sourceModel, publish: true);

        using var startResponse = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(source.Id, null, null, null));
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        var started = await ReadAsync<InstanceDetailDto>(startResponse);
        var task = Assert.Single(await ListActiveTasksAsync(started.Id));

        var originalActions = await GetTaskFlowsAsync(
            task.Id,
            "source-approver",
            ["SourceApprover"]);
        Assert.Equal("Approve original request", Assert.Single(originalActions).Name);

        var targetModel = Clone(sourceModel);
        targetModel.Name += " migrated";
        var targetAction = targetModel.SequenceFlows.Single(flow => flow.Id == 20);
        targetAction.Name = "Finalize migrated request";
        targetAction.Roles = ["MigratedApprover"];
        var target = await CreateWorkflowAsync(targetModel, publish: true);

        var preview = await PreviewAsync(started.Id, target.Id);
        Assert.True(preview.Compatible);
        using var changeResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{started.Id}/version-change",
            new ChangeInstanceVersionRequest(
                target.Id,
                preview.ExpectedSourceWorkflowId,
                preview.ExpectedUpdatedAt,
                "continue under migrated approval rules"));
        Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode);

        Assert.Empty(await GetTaskFlowsAsync(
            task.Id,
            "source-approver",
            ["SourceApprover"]));
        var migratedActions = await GetTaskFlowsAsync(
            task.Id,
            "migrated-approver",
            ["MigratedApprover"]);
        var migratedAction = Assert.Single(migratedActions);
        Assert.Equal(20, migratedAction.Id);
        Assert.Equal("Finalize migrated request", migratedAction.Name);
        Assert.Equal(["MigratedApprover"], migratedAction.Roles);

        using var takeResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/{migratedAction.Id}",
            new TakeFlowRequest(null),
            user: "migrated-approver",
            roles: ["MigratedApprover"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.OK, takeResponse.StatusCode);
        var acknowledgement = await ReadAsync<UserTaskActionAckDto>(takeResponse);
        Assert.Equal("completed", acknowledgement.InstanceStatus, ignoreCase: true);

        using var duplicateTake = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/{migratedAction.Id}",
            new TakeFlowRequest(null),
            user: "migrated-approver",
            roles: ["MigratedApprover"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.Conflict, duplicateTake.StatusCode);

        using var detailResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{started.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var completed = await ReadAsync<InstanceDetailDto>(detailResponse);
        Assert.Equal("completed", completed.Status, ignoreCase: true);
        Assert.Equal(target.Id, completed.Workflow.Id);
        Assert.NotNull(completed.Completion);
        var audit = Assert.Single(completed.VersionChanges);
        Assert.Equal(source.Id, audit.SourceWorkflow.Id);
        Assert.Equal(target.Id, audit.TargetWorkflow.Id);
        Assert.Equal("continue under migrated approval rules", audit.Reason);
        Assert.Single(completed.History, entry => entry.SequenceFlowId == 20);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(
            1,
            await db.NodeExecutions.CountAsync(execution =>
                execution.InstanceId == started.Id
                && execution.NodeId == 3));
        Assert.Equal(
            target.Id,
            await db.NodeExecutions
                .Where(execution =>
                    execution.InstanceId == started.Id
                    && execution.NodeId == 3)
                .Select(execution => execution.WorkflowDefinitionId)
                .SingleAsync());
        Assert.Equal(
            1,
            await db.WorkflowInstanceVersionChanges.CountAsync(change =>
                change.InstanceId == started.Id));
    }

    [Fact]
    public async Task Change_ReturnsConflictForUnpublishedTargetAndTerminalInstance()
    {
        await SetWorkflowRequiredRoleAsync("admin");
        var unpublishedSeed = await SeedFamilyAsync(versionCount: 2);
        var preview = await PreviewAsync(unpublishedSeed.Started.Id, unpublishedSeed.Target.Id);

        using (var unpublish = await SendAsync(
                   HttpMethod.Post,
                   $"/api/workflows/{unpublishedSeed.Target.Id}/unpublish"))
        {
            Assert.Equal(HttpStatusCode.NoContent, unpublish.StatusCode);
        }

        using var unpublishedPreview = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{unpublishedSeed.Started.Id}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(unpublishedSeed.Target.Id));
        Assert.Equal(HttpStatusCode.Conflict, unpublishedPreview.StatusCode);

        using var unpublishedChange = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{unpublishedSeed.Started.Id}/version-change",
            new ChangeInstanceVersionRequest(
                unpublishedSeed.Target.Id,
                preview.ExpectedSourceWorkflowId,
                preview.ExpectedUpdatedAt,
                "target publication changed"));
        Assert.Equal(HttpStatusCode.Conflict, unpublishedChange.StatusCode);

        var terminalSeed = await SeedFamilyAsync(versionCount: 2);
        var terminalUpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        await using (var db = fixture.CreateDbContext())
        {
            var instance = await db.WorkflowInstances.SingleAsync(
                item => item.Id == terminalSeed.Started.Id);
            instance.Status = "Completed";
            instance.UpdatedAt = terminalUpdatedAt;
            await db.SaveChangesAsync();
        }

        using var terminalPreview = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{terminalSeed.Started.Id}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(terminalSeed.Target.Id));
        Assert.Equal(HttpStatusCode.Conflict, terminalPreview.StatusCode);

        using var terminalChange = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{terminalSeed.Started.Id}/version-change",
            new ChangeInstanceVersionRequest(
                terminalSeed.Target.Id,
                terminalSeed.Source.Id,
                terminalUpdatedAt,
                "terminal instance"));
        Assert.Equal(HttpStatusCode.Conflict, terminalChange.StatusCode);
    }

    private async Task<VersionFamilySeed> SeedFamilyAsync(int versionCount)
    {
        var model = CreateModel("version-change");
        var source = await CreateWorkflowAsync(model, publish: true);
        var versions = new List<WorkflowDetailDto> { source };
        for (var version = 2; version <= versionCount; version++)
        {
            var next = Clone(model);
            next.Name = $"{model.Name} v{version}";
            versions.Add(await CreateWorkflowAsync(next, publish: true));
        }

        using var startResponse = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(source.Id, null, null, null),
            user: "starter",
            roles: ["admin"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        var started = await ReadAsync<InstanceDetailDto>(startResponse);
        Assert.Equal(source.Id, started.Workflow.Id);
        Assert.Equal("running", started.Status, ignoreCase: true);
        return new VersionFamilySeed(source, versions[^1], started);
    }

    private async Task<WorkflowDetailDto> CreateWorkflowAsync(
        WorkflowModel model,
        bool publish)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, publish),
            roles: ["admin"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<WorkflowDetailDto>(response);
    }

    private async Task<InstanceVersionChangePreviewDto> PreviewAsync(
        long instanceId,
        long targetWorkflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{instanceId}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(targetWorkflowId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceVersionChangePreviewDto>(response);
    }

    private async Task<IReadOnlyList<UserTaskDto>> ListActiveTasksAsync(long instanceId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}/user-tasks?status=active&page=1&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<PagedResult<UserTaskDto>>(response)).Items;
    }

    private async Task<IReadOnlyList<SequenceFlowModel>> GetTaskFlowsAsync(
        long taskId,
        string user,
        string[] roles)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/user-tasks/{taskId}/flows",
            user: user,
            roles: roles,
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<List<SequenceFlowModel>>(response);
    }

    private Task<HttpResponseMessage> SendAnonymousAsync(
        HttpMethod method,
        string path,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        return fixture.Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        string[]? roles = null,
        bool suppressDefaultAdmin = false)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles ?? ["admin"]);
        if (suppressDefaultAdmin)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        }
        return fixture.Client.SendAsync(request);
    }

    private async Task SetWorkflowRequiredRoleAsync(string value)
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings.SingleOrDefaultAsync(item =>
            item.Namespace == "Workflow" && item.Key == "RequiredRole");
        if (setting is null)
        {
            db.EngineSettings.Add(new EngineSettingEntity
            {
                Namespace = "Workflow",
                Key = "RequiredRole",
                Value = value
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private static WorkflowModel CreateModel(string suffix)
    {
        var key = $"{suffix}-{Guid.NewGuid():N}";
        return new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent,
                    ExternalId = "start"
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    ExternalId = "review"
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Done",
                    Type = BpmnFlowNodeTypes.EndEvent,
                    ExternalId = "done"
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel
                {
                    Id = 10,
                    Name = "Start review",
                    SourceRef = 1,
                    TargetRef = 2
                },
                new SequenceFlowModel
                {
                    Id = 20,
                    Name = "Approve",
                    SourceRef = 2,
                    TargetRef = 3
                }
            ]
        };
    }

    private static WorkflowModel Clone(WorkflowModel model) =>
        JsonSerializer.Deserialize<WorkflowModel>(
            JsonSerializer.Serialize(model, JsonOptions),
            JsonOptions)
        ?? throw new InvalidOperationException("Failed to clone workflow model.");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException(
            $"Response did not contain {typeof(T).Name}.");

    private sealed record VersionFamilySeed(
        WorkflowDetailDto Source,
        WorkflowDetailDto Target,
        InstanceDetailDto Started);
}
