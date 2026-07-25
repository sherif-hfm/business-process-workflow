using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class NodeExecutionApiTests(PostgresApiFixture fixture)
{
    private const string GlobalReaderRole = "ActivityReader";

    [Fact]
    public async Task AuthorizationIsAppliedPerDefinitionVersionBeforeCountAndDetail()
    {
        var seed = await SeedVersionScopedAuthorizationAsync();

        using var unauthenticated = await fixture.Client.GetAsync(
            $"/api/node-executions?workflowKey={seed.WorkflowKey}");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var global = await SearchAsync(
            $"/api/node-executions?workflowKey={seed.WorkflowKey}",
            "global-reader",
            [GlobalReaderRole],
            suppressDefaultAdmin: true);
        Assert.Equal(2, global.TotalCount);
        Assert.Equal([1, 2], global.Items.Select(item => item.WorkflowVersion).Order());

        var versionManager = await SearchAsync(
            $"/api/node-executions?workflowKey={seed.WorkflowKey}",
            "finance-manager",
            ["FinanceManager"],
            suppressDefaultAdmin: true);
        var visible = Assert.Single(versionManager.Items);
        Assert.Equal(1, visible.WorkflowVersion);
        Assert.Equal(1, versionManager.TotalCount);

        var noScope = await SearchAsync(
            $"/api/node-executions?workflowKey={seed.WorkflowKey}",
            "outsider",
            ["Worker"],
            suppressDefaultAdmin: true);
        Assert.Empty(noScope.Items);
        Assert.Equal(0, noScope.TotalCount);

        using var hiddenDetail = await SendAsync(
            HttpMethod.Get,
            $"/api/node-executions/{seed.OpsExecutionId}",
            "finance-manager",
            ["FinanceManager"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.NotFound, hiddenDetail.StatusCode);

        // Global activity visibility is intentionally read-only. It does not
        // confer the workflow version's task-assignment role.
        using var forbiddenAssignment = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{seed.FinanceTaskId}/assign",
            "global-reader",
            [GlobalReaderRole],
            suppressDefaultAdmin: true,
            body: new AssignUserTaskRequest(
                "worker",
                seed.FinanceTaskUpdatedAt,
                "activity read access is not mutation access"));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenAssignment.StatusCode);
    }

    [Fact]
    public async Task SearchCombinesRepeatedGroupsAndUsesLatestScalarVariables()
    {
        var seed = await SeedSearchRowsAsync();
        var prefix = $"/api/node-executions?workflowKey={seed.WorkflowKey}";

        var grouped = await SearchAsync(
            prefix
            + "&status=active&status=faulted"
            + "&nodeType=userTask&nodeType=serviceTask"
            + "&instanceStatus=running"
            + "&sort=id:asc",
            "reader",
            [GlobalReaderRole],
            suppressDefaultAdmin: true);
        Assert.Equal(
            [seed.ActiveExecutionId, seed.FaultedExecutionId],
            grouped.Items.Select(item => item.Id));
        Assert.Equal(2, grouped.TotalCount);

        var reasons = await SearchAsync(
            prefix
            + "&completionReason=boundaryCaught"
            + "&completionReason=normalEnd"
            + "&sort=id:asc",
            "reader",
            [GlobalReaderRole],
            suppressDefaultAdmin: true);
        Assert.Equal(
            [seed.FaultedExecutionId, seed.EndExecutionId],
            reasons.Items.Select(item => item.Id));

        var latestScalar = await SearchAsync(
            prefix + "&var=decision:approved&sort=id:asc",
            "reader",
            [GlobalReaderRole],
            suppressDefaultAdmin: true);
        Assert.Equal(
            [seed.ActiveExecutionId, seed.FaultedExecutionId],
            latestScalar.Items.Select(item => item.Id));

        // The second instance had an older matching scalar followed by a
        // non-matching scalar. Search must never match through the older row.
        Assert.DoesNotContain(
            latestScalar.Items,
            item => item.Id == seed.EndExecutionId);

        var owner = await SearchAsync(
            prefix + "&owner=ALICE",
            "reader",
            [GlobalReaderRole],
            suppressDefaultAdmin: true);
        Assert.Equal(seed.ActiveExecutionId, Assert.Single(owner.Items).Id);

        var correlated = await SearchAsync(
            prefix
            + $"&executionId={seed.FaultedExecutionId}"
            + $"&instanceId={seed.RunningInstanceId}"
            + $"&workflowId={seed.WorkflowId}"
            + "&workflowVersion=1"
            + "&businessKey=CASE-001"
            + $"&tokenId={seed.RunningTokenId}"
            + "&executionKind=node"
            + "&nodeId=3"
            + "&nodeName=call%20SERVICE"
            + "&nodeExternalId=SVC-EXT"
            + "&status=faulted"
            + "&isMultiInstance=false"
            + "&isCutoverSeeded=true"
            + "&startedBy=STARTER"
            + "&completedBy=SYSTEM"
            + "&enteredViaFlowId=102"
            + "&selectedFlowId=103"
            + "&exitedViaFlowId=104"
            + "&minDurationMilliseconds=120000"
            + "&maxDurationMilliseconds=120000",
            "reader",
            [GlobalReaderRole],
            suppressDefaultAdmin: true);
        Assert.Equal(seed.FaultedExecutionId, Assert.Single(correlated.Items).Id);

        var byTask = await SearchAsync(
            prefix + $"&userTaskId={seed.UserTaskId}",
            "reader",
            [GlobalReaderRole],
            suppressDefaultAdmin: true);
        Assert.Equal(seed.ActiveExecutionId, Assert.Single(byTask.Items).Id);

        foreach (var invalidPath in new[]
                 {
                     prefix + "&status=waiting",
                     prefix + "&createdFrom=2026-07-25T12:00:00Z&createdTo=2026-07-25T11:00:00Z",
                     prefix + "&executionId=0",
                     prefix + "&sort=updatedAt:asc&sort=UPDATEDAT:desc"
                 })
        {
            using var invalid = await SendAsync(
                HttpMethod.Get,
                invalidPath,
                "reader",
                [GlobalReaderRole],
                suppressDefaultAdmin: true);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }

    [Fact]
    public async Task NullableSortPagingAndDetailAreStableAndExecutionLocal()
    {
        var seed = await SeedSearchRowsAsync();
        var prefix =
            $"/api/node-executions?workflowKey={seed.WorkflowKey}" +
            "&sort=completedAt:asc&pageSize=1";

        var first = await SearchAsync(
            prefix + "&page=1",
            "reader",
            [GlobalReaderRole],
            suppressDefaultAdmin: true);
        var second = await SearchAsync(
            prefix + "&page=2",
            "reader",
            [GlobalReaderRole],
            suppressDefaultAdmin: true);
        var third = await SearchAsync(
            prefix + "&page=3",
            "reader",
            [GlobalReaderRole],
            suppressDefaultAdmin: true);

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(3, second.TotalCount);
        Assert.Equal(3, third.TotalCount);
        Assert.Equal(seed.FaultedExecutionId, Assert.Single(first.Items).Id);
        Assert.Equal(seed.EndExecutionId, Assert.Single(second.Items).Id);
        // Active execution has a null CompletedAt and must remain last.
        Assert.Equal(seed.ActiveExecutionId, Assert.Single(third.Items).Id);

        using var detailResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/node-executions/{seed.FaultedExecutionId}",
            "reader",
            [GlobalReaderRole],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = (await detailResponse.Content
            .ReadFromJsonAsync<NodeExecutionDetailDto>())!;

        Assert.Equal(NodeExecutionRecordStatuses.Faulted, detail.Status);
        Assert.Equal(NodeExecutionCompletionReasons.BoundaryCaught, detail.CompletionReason);
        Assert.Equal("HTTP_503", detail.Error!.Code);
        Assert.Equal(["Automation"], detail.NodeRoles);
        var change = Assert.Single(detail.VariableChanges);
        Assert.Equal("serviceStatus", change.VariableName);
        Assert.Equal(503, change.Value.GetInt32());
        Assert.DoesNotContain(
            detail.VariableChanges,
            variable => variable.VariableName == "unrelated");
    }

    private async Task<AuthorizationSeed> SeedVersionScopedAuthorizationAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"node-auth-{suffix}";
        var now = DateTimeOffset.UtcNow.AddMinutes(-2);

        await using var db = fixture.CreateDbContext();
        await SetGlobalReaderRoleAsync(db);
        var financeDefinition = Definition(
            workflowKey,
            1,
            "Finance",
            ["FinanceManager"]);
        var opsDefinition = Definition(
            workflowKey,
            2,
            "Operations",
            ["OpsManager"]);
        db.WorkflowDefinitions.AddRange(financeDefinition, opsDefinition);
        await db.SaveChangesAsync();

        var financeInstance = Instance(financeDefinition, workflowKey, now);
        var opsInstance = Instance(opsDefinition, workflowKey, now.AddSeconds(1));
        db.WorkflowInstances.AddRange(financeInstance, opsInstance);
        await db.SaveChangesAsync();

        var financeToken = Token(financeInstance, BpmnFlowNodeTypes.UserTask, now);
        var opsToken = Token(opsInstance, BpmnFlowNodeTypes.Task, now.AddSeconds(1));
        db.ExecutionTokens.AddRange(financeToken, opsToken);
        await db.SaveChangesAsync();

        var financeTask = new UserTaskEntity
        {
            InstanceId = financeInstance.Id,
            TokenId = financeToken.Id,
            NodeId = 2,
            NodeName = "Review",
            Roles = ["Worker"],
            RequiresAssignment = true,
            Status = UserTaskStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.UserTasks.Add(financeTask);
        await db.SaveChangesAsync();

        var financeExecution = ActiveExecution(
            financeInstance,
            financeToken,
            BpmnFlowNodeTypes.UserTask,
            now,
            financeTask.Id);
        var opsExecution = ActiveExecution(
            opsInstance,
            opsToken,
            BpmnFlowNodeTypes.Task,
            now.AddSeconds(1));
        db.NodeExecutions.AddRange(financeExecution, opsExecution);
        await db.SaveChangesAsync();

        return new AuthorizationSeed(
            workflowKey,
            financeTask.Id,
            financeTask.UpdatedAt,
            opsExecution.Id);
    }

    private async Task<SearchSeed> SeedSearchRowsAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"node-search-{suffix}";
        var rawBasis = DateTimeOffset.UtcNow.AddHours(-2);
        var basis = new DateTimeOffset(
            rawBasis.Ticks - rawBasis.Ticks % TimeSpan.TicksPerMillisecond,
            TimeSpan.Zero);

        await using var db = fixture.CreateDbContext();
        await SetGlobalReaderRoleAsync(db);
        var definition = Definition(workflowKey, 1, "Search", ["WorkflowReader"]);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowBusinessKeyClaims.Add(new WorkflowBusinessKeyClaimEntity
        {
            WorkflowKey = workflowKey,
            BusinessKey = "CASE-001"
        });
        await db.SaveChangesAsync();

        var running = Instance(definition, workflowKey, basis);
        running.BusinessKey = "CASE-001";
        var completed = Instance(
            definition,
            workflowKey,
            basis.AddMinutes(1),
            WorkflowInstanceStatuses.Completed);
        db.WorkflowInstances.AddRange(running, completed);
        await db.SaveChangesAsync();

        var runningToken = Token(running, BpmnFlowNodeTypes.UserTask, basis);
        var completedToken = Token(
            completed,
            BpmnFlowNodeTypes.EndEvent,
            basis.AddMinutes(1),
            ExecutionTokenStatuses.Completed);
        db.ExecutionTokens.AddRange(runningToken, completedToken);
        await db.SaveChangesAsync();

        var userTask = new UserTaskEntity
        {
            InstanceId = running.Id,
            TokenId = runningToken.Id,
            NodeId = 2,
            NodeName = "Review",
            Roles = ["Reviewer"],
            Status = UserTaskStatuses.Active,
            ClaimedBy = "Alice",
            CreatedAt = basis,
            UpdatedAt = basis.AddMinutes(20)
        };
        db.UserTasks.Add(userTask);
        await db.SaveChangesAsync();

        var active = ActiveExecution(
            running,
            runningToken,
            BpmnFlowNodeTypes.UserTask,
            basis,
            userTask.Id);
        active.NodeRolesJson = JsonDocument.Parse("""["Reviewer"]""");
        active.TriggeredBy = "Starter";
        active.TriggeredByRolesJson = JsonDocument.Parse("""["Initiator"]""");
        active.UpdatedAt = basis.AddMinutes(20);

        var faulted = new NodeExecutionEntity
        {
            InstanceId = running.Id,
            ExecutionTokenId = runningToken.Id,
            NodeId = 3,
            NodeName = "Call Service",
            NodeExternalId = "svc-ext",
            NodeType = BpmnFlowNodeTypes.ServiceTask,
            ExecutionKind = NodeExecutionKinds.Node,
            Status = NodeExecutionStatuses.Faulted,
            CompletionReason = NodeExecutionCompletionReasons.BoundaryCaught,
            NodeRolesJson = JsonDocument.Parse("""["Automation"]"""),
            TriggeredBy = "Starter",
            TriggeredByRolesJson = JsonDocument.Parse("""["Initiator"]"""),
            CompletedBy = "System",
            CompletedByRolesJson = JsonDocument.Parse("""[]"""),
            EnteredViaFlowId = 102,
            SelectedFlowId = 103,
            ExitedViaFlowId = 104,
            ErrorCode = "HTTP_503",
            ErrorDescription = "Service unavailable",
            CreatedAt = basis.AddMinutes(2),
            StartedAt = basis.AddMinutes(2),
            UpdatedAt = basis.AddMinutes(4),
            CompletedAt = basis.AddMinutes(4),
            IsCutoverSeeded = true
        };
        var end = new NodeExecutionEntity
        {
            InstanceId = completed.Id,
            ExecutionTokenId = completedToken.Id,
            NodeId = 9,
            NodeName = "Done",
            NodeType = BpmnFlowNodeTypes.EndEvent,
            ExecutionKind = NodeExecutionKinds.Node,
            Status = NodeExecutionStatuses.Completed,
            CompletionReason = NodeExecutionCompletionReasons.NormalEnd,
            CreatedAt = basis.AddMinutes(5),
            StartedAt = basis.AddMinutes(5),
            UpdatedAt = basis.AddMinutes(10),
            CompletedAt = basis.AddMinutes(10)
        };
        db.NodeExecutions.AddRange(active, faulted, end);
        await db.SaveChangesAsync();

        db.InstanceVariables.AddRange(
            Variable(running.Id, "decision", "\"rejected\"", basis),
            Variable(running.Id, "decision", "\"APPROVED\"", basis.AddMinutes(1)),
            Variable(completed.Id, "decision", "\"approved\"", basis),
            Variable(completed.Id, "decision", "\"rejected\"", basis.AddMinutes(1)),
            Variable(
                running.Id,
                "serviceStatus",
                "503",
                basis.AddMinutes(4),
                faulted.Id),
            Variable(
                running.Id,
                "unrelated",
                "\"current-value\"",
                basis.AddMinutes(6)));
        await db.SaveChangesAsync();

        return new SearchSeed(
            workflowKey,
            definition.Id,
            running.Id,
            runningToken.Id,
            userTask.Id,
            active.Id,
            faulted.Id,
            end.Id);
    }

    private static WorkflowDefinitionEntity Definition(
        string workflowKey,
        int version,
        string name,
        IReadOnlyList<string> assignmentRoles) =>
        new()
        {
            Name = name,
            WorkflowKey = workflowKey,
            Version = version,
            IsPublished = true,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = name,
                TaskAssignmentRoles = assignmentRoles.ToList(),
                FlowNodes =
                [
                    new FlowNodeModel
                    {
                        Id = 2,
                        Name = "Review",
                        Type = BpmnFlowNodeTypes.UserTask
                    }
                ]
            }
        };

    private static WorkflowInstanceEntity Instance(
        WorkflowDefinitionEntity definition,
        string workflowKey,
        DateTimeOffset createdAt,
        string status = WorkflowInstanceStatuses.Running) =>
        new()
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            Status = status,
            StartedBy = "seed",
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

    private static ExecutionTokenEntity Token(
        WorkflowInstanceEntity instance,
        string nodeType,
        DateTimeOffset createdAt,
        string status = ExecutionTokenStatuses.Active) =>
        new()
        {
            InstanceId = instance.Id,
            NodeId = 2,
            NodeName = "Position",
            NodeType = nodeType,
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

    private static NodeExecutionEntity ActiveExecution(
        WorkflowInstanceEntity instance,
        ExecutionTokenEntity token,
        string nodeType,
        DateTimeOffset createdAt,
        long? userTaskId = null) =>
        new()
        {
            InstanceId = instance.Id,
            ExecutionTokenId = token.Id,
            UserTaskId = userTaskId,
            NodeId = 2,
            NodeName = "Active work",
            NodeType = nodeType,
            ExecutionKind = NodeExecutionKinds.Node,
            Status = NodeExecutionStatuses.Active,
            CreatedAt = createdAt,
            StartedAt = createdAt,
            UpdatedAt = createdAt
        };

    private static InstanceVariableEntity Variable(
        long instanceId,
        string name,
        string json,
        DateTimeOffset setAt,
        long? nodeExecutionId = null) =>
        new()
        {
            InstanceId = instanceId,
            NodeExecutionId = nodeExecutionId,
            VariableName = name,
            ValueJson = JsonDocument.Parse(json),
            SetBy = "seed",
            SetAt = setAt
        };

    private static async Task SetGlobalReaderRoleAsync(
        Flowbit.Infrastructure.Data.AppDbContext db)
    {
        var setting = await db.EngineSettings.SingleOrDefaultAsync(
            item => item.Namespace == "NodeExecution"
                    && item.Key == "RequiredRole");
        if (setting is null)
        {
            db.EngineSettings.Add(new EngineSettingEntity
            {
                Namespace = "NodeExecution",
                Key = "RequiredRole",
                Value = GlobalReaderRole
            });
        }
        else
        {
            setting.Value = GlobalReaderRole;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private async Task<PagedResult<NodeExecutionSummaryDto>> SearchAsync(
        string path,
        string user,
        string[] roles,
        bool suppressDefaultAdmin)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            path,
            user,
            roles,
            suppressDefaultAdmin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content
            .ReadFromJsonAsync<PagedResult<NodeExecutionSummaryDto>>())!;
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string user,
        string[] roles,
        bool suppressDefaultAdmin,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        ApiTestAuth.Authorize(request, user, roles);
        if (suppressDefaultAdmin)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        }
        return fixture.Client.SendAsync(request);
    }

    private sealed record AuthorizationSeed(
        string WorkflowKey,
        long FinanceTaskId,
        DateTimeOffset FinanceTaskUpdatedAt,
        long OpsExecutionId);

    private sealed record SearchSeed(
        string WorkflowKey,
        long WorkflowId,
        long RunningInstanceId,
        long RunningTokenId,
        long UserTaskId,
        long ActiveExecutionId,
        long FaultedExecutionId,
        long EndExecutionId);
}
