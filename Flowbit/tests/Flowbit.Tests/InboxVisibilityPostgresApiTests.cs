using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InboxVisibilityPostgresApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ClaimComparisonIsCaseInsensitiveAndMissingClaimHidesEveryPersonalRoute()
    {
        var model = CreateOrdinaryModel(
            "claim-variable",
            "[sys.claim.department] == [department]",
            [Variable(10, "department", WorkflowVariableTypes.String)],
            requiresClaim: true);
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, new Dictionary<string, JsonElement>
        {
            ["department"] = JsonSerializer.SerializeToElement("Finance")
        });
        var taskId = await GetOnlyStoredTaskIdAsync(instance.Id);

        var hiddenInbox = await GetInboxAsync(workflowId, 1, 20, "worker");
        Assert.Empty(hiddenInbox.Items);
        Assert.Equal(0, hiddenInbox.TotalCount);

        await AssertStatusAsync(HttpMethod.Get, $"/api/user-tasks/{taskId}", HttpStatusCode.NotFound);
        await AssertStatusAsync(HttpMethod.Get, $"/api/user-tasks/{taskId}/flows", HttpStatusCode.NotFound);
        await AssertStatusAsync(HttpMethod.Post, $"/api/user-tasks/{taskId}/claim", HttpStatusCode.NotFound);
        await AssertStatusAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{taskId}/flows/201",
            HttpStatusCode.NotFound,
            new TakeFlowRequest(null));

        var visibleInbox = await GetInboxAsync(
            workflowId,
            1,
            20,
            "worker",
            claims: new Dictionary<string, string> { ["DEPARTMENT"] = "finance" });
        Assert.Equal(taskId, Assert.Single(visibleInbox.Items).UserTaskId);

        using var claim = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{taskId}/claim",
            user: "worker",
            claims: new Dictionary<string, string> { ["department"] = "FINANCE" });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
    }

    [Fact]
    public async Task DelegationUsesDelegateClaimsAndRolesAndExposesRepresentedOwner()
    {
        const string owner = "visibility-owner";
        const string delegateUser = "visibility-delegate";
        var model = CreateOrdinaryModel(
            "delegated-context",
            $"[sys.user] == '{owner}' or " +
            $"([sys.actingFor] == '{owner}' and " +
            "[sys.claim.department] == 'delegate-dept')",
            [],
            requiresClaim: true);
        model.FlowNodes.Single(node => node.Id == 2).Roles = ["OwnerRole", "DelegateRole"];
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, []);
        var taskId = await GetOnlyStoredTaskIdAsync(instance.Id);

        using (var claim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{taskId}/claim",
                   user: owner,
                   roles: ["OwnerRole"]))
        {
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        }

        var now = DateTimeOffset.UtcNow;
        using (var delegation = await SendAsync(
                   HttpMethod.Post,
                   "/api/user-delegations",
                   new CreateUserDelegationRequest(
                       delegateUser,
                       [model.Id],
                       now.AddMinutes(-1),
                       now.AddDays(1)),
                   user: owner))
        {
            Assert.Equal(HttpStatusCode.Created, delegation.StatusCode);
        }

        var wrongClaim = await GetInboxAsync(
            workflowId,
            1,
            20,
            delegateUser,
            ["DelegateRole"],
            new Dictionary<string, string> { ["department"] = "owner-dept" });
        Assert.Empty(wrongClaim.Items);

        var delegated = await GetInboxAsync(
            workflowId,
            1,
            20,
            delegateUser,
            ["DelegateRole"],
            new Dictionary<string, string> { ["department"] = "DELEGATE-DEPT" });
        var item = Assert.Single(delegated.Items);
        Assert.Equal(owner, item.DelegatedAccess?.ActingFor);

        using var detailResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/user-tasks/{taskId}",
            user: delegateUser,
            roles: ["DelegateRole"],
            claims: new Dictionary<string, string> { ["department"] = "delegate-dept" });
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        Assert.Equal(owner, (await ReadAsync<UserTaskDto>(detailResponse)).DelegatedAccess?.ActingFor);
    }

    [Fact]
    public async Task HiddenClaimantCannotUnclaimButConfiguredRecoveryRoleBypassesVisibility()
    {
        var model = CreateOrdinaryModel(
            "unclaim-recovery",
            "[visible] == true",
            [Variable(10, "visible", WorkflowVariableTypes.Boolean)],
            requiresClaim: true);
        model.FlowNodes.Single(node => node.Id == 2).Roles = ["Worker"];
        model.UnclaimRoles = ["Supervisor"];
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, new Dictionary<string, JsonElement>
        {
            ["visible"] = JsonSerializer.SerializeToElement(true)
        });
        var taskId = await GetOnlyStoredTaskIdAsync(instance.Id);

        using (var claim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{taskId}/claim",
                   user: "alice",
                   roles: ["Worker"]))
        {
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        }

        using (var hide = await SendAsync(
                   HttpMethod.Patch,
                   $"/api/instances/{instance.Id}/variables",
                   new UpdateInstanceVariablesRequest(
                       [new InstanceVariableWriteDto(
                           "visible",
                           JsonSerializer.SerializeToElement(false))],
                       "Hide personal recovery routes",
                       $"visibility-hide-{Guid.NewGuid():N}"),
                   user: "operator"))
        {
            Assert.Equal(HttpStatusCode.OK, hide.StatusCode);
        }

        fixture.CommandCounter.Reset();
        var hiddenInbox = await GetInboxAsync(
            workflowId,
            page: 1,
            pageSize: 20,
            user: "alice",
            roles: ["Worker"]);
        Assert.Equal(2, fixture.CommandCounter.ReaderCommands);
        Assert.Empty(hiddenInbox.Items);
        Assert.Equal(0, hiddenInbox.TotalCount);

        using (var hiddenOwnerUnclaim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{taskId}/unclaim",
                   user: "alice",
                   // Deliberately omit the node role. A claimant whose normal
                   // access can no longer be resolved must not skip the SQL
                   // visibility predicate.
                   roles: []))
        {
            Assert.Equal(HttpStatusCode.NotFound, hiddenOwnerUnclaim.StatusCode);
        }
        using (var hiddenOwnerLegacyUnclaim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{instance.Id}/unclaim",
                   user: "alice",
                   roles: []))
        {
            Assert.Equal(HttpStatusCode.NotFound, hiddenOwnerLegacyUnclaim.StatusCode);
        }
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(
                "alice",
                await db.UserTasks.Where(task => task.Id == taskId)
                    .Select(task => task.ClaimedBy)
                    .SingleAsync());
        }

        using var recoveryUnclaim = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{instance.Id}/unclaim",
            user: "supervisor",
            roles: ["Supervisor"]);
        Assert.Equal(HttpStatusCode.OK, recoveryUnclaim.StatusCode);
        Assert.Null((await ReadAsync<InstanceDetailDto>(recoveryUnclaim)).UserTasks?.SoleClaimedBy);
    }

    [Fact]
    public async Task LegacyUnclaimPreservesClaimantAccessWhenVisibilityIsTrueButNodeRoleIsStale()
    {
        var model = CreateOrdinaryModel(
            "legacy-unclaim-stale-role",
            "[visible] == true",
            [Variable(10, "visible", WorkflowVariableTypes.Boolean)],
            requiresClaim: true);
        model.FlowNodes.Single(node => node.Id == 2).Roles = ["Worker"];
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, new Dictionary<string, JsonElement>
        {
            ["visible"] = JsonSerializer.SerializeToElement(true)
        });
        var taskId = await GetOnlyStoredTaskIdAsync(instance.Id);

        using (var claim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{taskId}/claim",
                   user: "alice",
                   roles: ["Worker"]))
        {
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        }

        using var unclaim = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{instance.Id}/unclaim",
            user: "alice",
            roles: []);
        Assert.Equal(HttpStatusCode.OK, unclaim.StatusCode);
    }

    [Fact]
    public async Task ArithmeticVariableComparisonNumberConfigAndNumericSettingAreDatabaseEvaluated()
    {
        var options = fixture.Factory.Services.GetRequiredService<WorkflowContextOptions>();
        const string configKey = "visibilityApprovalLimit";
        var hadConfig = options.Config.TryGetValue(configKey, out var previousConfig);
        options.Config[configKey] = "9.5";

        WorkflowSettingRecord? setting = null;
        try
        {
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                setting = await scope.ServiceProvider.GetRequiredService<IWorkflowSettingsService>()
                    .CreateAsync(
                        "visibility",
                        "limit",
                        JsonSerializer.SerializeToElement(20m),
                        null,
                        CancellationToken.None);
            }

            var condition =
                "[requested] + [tax] <= [upper] and " +
                "[requested] + [tax] > Number([config.visibilityApprovalLimit]) and " +
                "[requested] < [setting.visibility.limit]";
            var workflowId = await CreateWorkflowAsync(CreateOrdinaryModel(
                "typed-arithmetic",
                condition,
                [
                    Variable(10, "requested", WorkflowVariableTypes.Number),
                    Variable(11, "tax", WorkflowVariableTypes.Number),
                    Variable(12, "upper", WorkflowVariableTypes.Number)
                ]));
            var instance = await StartAsync(workflowId, new Dictionary<string, JsonElement>
            {
                ["requested"] = JsonSerializer.SerializeToElement(10m),
                ["tax"] = JsonSerializer.SerializeToElement(2m),
                ["upper"] = JsonSerializer.SerializeToElement(12m)
            });

            var visible = await GetInboxAsync(workflowId, 1, 20, "worker");
            Assert.Equal(instance.Id, Assert.Single(visible.Items).InstanceId);

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var updated = await scope.ServiceProvider.GetRequiredService<IWorkflowSettingsService>()
                    .UpdateAsync(
                        setting.Id,
                        JsonSerializer.SerializeToElement(5m),
                        null,
                        setting.UpdatedAt,
                        CancellationToken.None);
                setting = Assert.IsType<WorkflowSettingRecord>(updated);
            }

            var hiddenAfterSettingChange = await GetInboxAsync(workflowId, 1, 20, "worker");
            Assert.Empty(hiddenAfterSettingChange.Items);
            Assert.Equal(0, hiddenAfterSettingChange.TotalCount);
        }
        finally
        {
            if (setting is not null)
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IWorkflowSettingsService>()
                    .DeleteByIdAsync(setting.Id, setting.UpdatedAt, CancellationToken.None);
            }

            if (hadConfig)
            {
                options.Config[configKey] = previousConfig!;
            }
            else
            {
                options.Config.Remove(configKey);
            }
        }
    }

    [Fact]
    public async Task VisibilityRunsBeforeCountOrderingAndPagingWithoutHoles()
    {
        var workflowId = await CreateWorkflowAsync(CreateOrdinaryModel(
            "paging",
            "[visible] == true",
            [Variable(10, "visible", WorkflowVariableTypes.Boolean)]));
        var visibleInstanceIds = new List<long>();
        for (var index = 0; index < 9; index++)
        {
            var isVisible = index % 2 == 0;
            var instance = await StartAsync(workflowId, new Dictionary<string, JsonElement>
            {
                ["visible"] = JsonSerializer.SerializeToElement(isVisible)
            });
            if (isVisible)
            {
                visibleInstanceIds.Add(instance.Id);
            }
        }

        fixture.CommandCounter.Reset();
        var first = await GetInboxAsync(workflowId, 1, 2, "worker");
        Assert.Equal(3, fixture.CommandCounter.ReaderCommands);
        var second = await GetInboxAsync(workflowId, 2, 2, "worker");
        var third = await GetInboxAsync(workflowId, 3, 2, "worker");
        var pastEnd = await GetInboxAsync(workflowId, 4, 2, "worker");

        Assert.All([first, second, third, pastEnd], page => Assert.Equal(5, page.TotalCount));
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.Single(third.Items);
        Assert.Empty(pastEnd.Items);
        var returnedInstances = first.Items.Concat(second.Items).Concat(third.Items)
            .Select(item => item.InstanceId)
            .ToArray();
        Assert.Equal(5, returnedInstances.Distinct().Count());
        Assert.Equal(
            visibleInstanceIds.OrderBy(id => id),
            returnedInstances.OrderBy(id => id));
    }

    [Fact]
    public async Task NullWrongTypeAndDivisionByZeroAllFailClosed()
    {
        var createdSettings = new List<WorkflowSettingRecord>();
        try
        {
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<IWorkflowSettingsService>();
                createdSettings.Add(await settings.CreateAsync(
                    "visibility",
                    "wrongType",
                    JsonSerializer.SerializeToElement("not-a-number"),
                    null,
                    CancellationToken.None));
                createdSettings.Add(await settings.CreateAsync(
                    "visibility",
                    "nullValue",
                    JsonSerializer.SerializeToElement<object?>(null),
                    null,
                    CancellationToken.None));
                createdSettings.Add(await settings.CreateAsync(
                    "visibility",
                    "badDateTime",
                    JsonSerializer.SerializeToElement("2026-08-11T24:00:00Z"),
                    null,
                    CancellationToken.None));
            }

            var condition =
                "[amount] / [denominator] > 1 or " +
                "[amount] < [setting.visibility.wrongType] or " +
                "[amount] < [setting.visibility.nullValue]";
            var workflowId = await CreateWorkflowAsync(CreateOrdinaryModel(
                "unknown",
                condition,
                [
                    Variable(10, "amount", WorkflowVariableTypes.Number),
                    Variable(11, "denominator", WorkflowVariableTypes.Number)
                ]));
            await StartAsync(workflowId, new Dictionary<string, JsonElement>
            {
                ["amount"] = JsonSerializer.SerializeToElement(10m),
                ["denominator"] = JsonSerializer.SerializeToElement(0m)
            });

            var inbox = await GetInboxAsync(workflowId, 1, 20, "worker");
            Assert.Empty(inbox.Items);
            Assert.Equal(0, inbox.TotalCount);

            var badDateTimeWorkflowId = await CreateWorkflowAsync(CreateOrdinaryModel(
                "invalid-rfc3339-datetime",
                "[setting.visibility.badDateTime] < [sys.now]",
                []));
            await StartAsync(badDateTimeWorkflowId, []);
            var badDateTimeInbox = await GetInboxAsync(
                badDateTimeWorkflowId,
                1,
                20,
                "worker");
            Assert.Empty(badDateTimeInbox.Items);
            Assert.Equal(0, badDateTimeInbox.TotalCount);
        }
        finally
        {
            foreach (var setting in createdSettings.AsEnumerable().Reverse())
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IWorkflowSettingsService>()
                    .DeleteByIdAsync(setting.Id, setting.UpdatedAt, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task BooleanDateAndDateTimeOperationsUseTypedDatabaseSemantics()
    {
        var condition =
            "not ([blocked] == true) and [left] < [right] and " +
            "[businessDate] < '2026-08-12' and " +
            "[occurredAt] == '2026-08-11T09:00:00Z'";
        var workflowId = await CreateWorkflowAsync(CreateOrdinaryModel(
            "typed-date-time",
            condition,
            [
                Variable(10, "blocked", WorkflowVariableTypes.Boolean),
                Variable(11, "left", WorkflowVariableTypes.Number),
                Variable(12, "right", WorkflowVariableTypes.Number),
                Variable(13, "businessDate", WorkflowVariableTypes.Date),
                Variable(14, "occurredAt", WorkflowVariableTypes.DateTime)
            ]));
        var instance = await StartAsync(workflowId, new Dictionary<string, JsonElement>
        {
            ["blocked"] = JsonSerializer.SerializeToElement(false),
            ["left"] = JsonSerializer.SerializeToElement(4m),
            ["right"] = JsonSerializer.SerializeToElement(5m),
            ["businessDate"] = JsonSerializer.SerializeToElement("2026-08-11"),
            ["occurredAt"] = JsonSerializer.SerializeToElement("2026-08-11T12:00:00+03:00")
        });

        var inbox = await GetInboxAsync(workflowId, 1, 20, "worker");
        Assert.Equal(instance.Id, Assert.Single(inbox.Items).InstanceId);
    }

    [Fact]
    public async Task VisibilityRunsBeforeOnePerActorRepresentativeSelection()
    {
        var model = CreateMultiInstanceModel();
        model.FlowNodes.Single(node => node.Id == 2).MultiInstance!.OnePerActor = true;
        var workflowId = await CreateWorkflowAsync(model);
        var visible = await StartAsync(workflowId, new Dictionary<string, JsonElement>
        {
            ["visible"] = JsonSerializer.SerializeToElement(true)
        });
        await StartAsync(workflowId, new Dictionary<string, JsonElement>
        {
            ["visible"] = JsonSerializer.SerializeToElement(false)
        });

        var inbox = await GetInboxAsync(workflowId, 1, 20, "worker");

        Assert.Equal(1, inbox.TotalCount);
        Assert.Equal(visible.Id, Assert.Single(inbox.Items).InstanceId);
    }

    [Fact]
    public async Task ConditionProjectionIsSnapshottedByOrdinaryAndMultiInstanceTasksAndNullRemainsUnrestricted()
    {
        var ordinaryWorkflowId = await CreateWorkflowAsync(CreateOrdinaryModel(
            "ordinary-snapshot",
            "[visible] == true",
            [Variable(10, "visible", WorkflowVariableTypes.Boolean)]));
        var ordinary = await StartAsync(ordinaryWorkflowId, new Dictionary<string, JsonElement>
        {
            ["visible"] = JsonSerializer.SerializeToElement(true)
        });

        var miWorkflowId = await CreateWorkflowAsync(CreateMultiInstanceModel());
        var multiInstance = await StartAsync(miWorkflowId, new Dictionary<string, JsonElement>
        {
            ["visible"] = JsonSerializer.SerializeToElement(true)
        });

        var unrestrictedWorkflowId = await CreateWorkflowAsync(CreateOrdinaryModel(
            "unrestricted",
            null,
            [Variable(10, "visible", WorkflowVariableTypes.Boolean)]));
        var unrestricted = await StartAsync(unrestrictedWorkflowId, new Dictionary<string, JsonElement>
        {
            ["visible"] = JsonSerializer.SerializeToElement(false)
        });

        await using var db = fixture.CreateDbContext();
        var ordinaryConditionId = await db.WorkflowDefinitionUserTaskConditions
            .Where(condition => condition.WorkflowDefinitionId == ordinaryWorkflowId)
            .Select(condition => condition.Id)
            .SingleAsync();
        var ordinaryTask = await db.UserTasks.SingleAsync(task => task.InstanceId == ordinary.Id);
        Assert.Equal(ordinaryConditionId, ordinaryTask.InboxVisibilityConditionId);

        var miConditionId = await db.WorkflowDefinitionUserTaskConditions
            .Where(condition => condition.WorkflowDefinitionId == miWorkflowId)
            .Select(condition => condition.Id)
            .SingleAsync();
        var miTasks = await db.UserTasks
            .Where(task => task.InstanceId == multiInstance.Id)
            .OrderBy(task => task.ItemIndex)
            .ToListAsync();
        Assert.Equal(2, miTasks.Count);
        Assert.All(miTasks, task => Assert.Equal(miConditionId, task.InboxVisibilityConditionId));

        var unrestrictedTask = await db.UserTasks.SingleAsync(task => task.InstanceId == unrestricted.Id);
        Assert.Null(unrestrictedTask.InboxVisibilityConditionId);
        var unrestrictedInbox = await GetInboxAsync(unrestrictedWorkflowId, 1, 20, "worker");
        Assert.Equal(unrestrictedTask.Id, Assert.Single(unrestrictedInbox.Items).UserTaskId);
    }

    [Fact]
    public async Task HiddenMultiInstanceParentInterruptDiscoveryAndTakeReturnNotFound()
    {
        var model = CreateMultiInstanceModel();
        model.FlowNodes.Single(node => node.Id == 2).MultiInstance!.Mode =
            MultiInstanceModes.Sequential;
        model.Id = $"inbox-visibility-hidden-mi-interrupt-{Guid.NewGuid():N}";
        model.Name = "Hidden multi-instance parent interrupt";
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 203,
            Name = "Stop review",
            SourceRef = 2,
            TargetRef = 5,
            Roles = ["Manager"],
            CancelRemainingInstances = true
        });
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 5,
            Name = "Interrupted",
            Type = BpmnFlowNodeTypes.EndEvent
        });
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, new Dictionary<string, JsonElement>
        {
            ["visible"] = JsonSerializer.SerializeToElement(false)
        });
        var executionId = Assert.IsType<MultiInstanceProgressDto>(instance.MultiInstance).ExecutionId;

        using (var discovery = await SendAsync(
                   HttpMethod.Get,
                   $"/api/multi-instance-executions/{executionId}/flows",
                   user: "manager",
                   roles: ["Manager"]))
        {
            Assert.Equal(HttpStatusCode.NotFound, discovery.StatusCode);
        }

        using var take = await SendAsync(
            HttpMethod.Post,
            $"/api/multi-instance-executions/{executionId}/flows/203",
            new TakeFlowRequest(null),
            "manager",
            ["Manager"]);
        Assert.Equal(HttpStatusCode.NotFound, take.StatusCode);

        using var legacyTake = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{instance.Id}/flows/201",
            new TakeFlowRequest(null),
            "manager",
            ["Manager"]);
        Assert.Equal(HttpStatusCode.NotFound, legacyTake.StatusCode);
    }

    [Fact]
    public async Task LegacyInstanceRoutesReturnNotFoundWhenEveryParallelItemIsHidden()
    {
        var workflowId = await CreateWorkflowAsync(CreateMultiInstanceModel());
        var instance = await StartAsync(workflowId, new Dictionary<string, JsonElement>
        {
            ["visible"] = JsonSerializer.SerializeToElement(false)
        });
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(2, await db.UserTasks.CountAsync(task =>
                task.InstanceId == instance.Id && task.Status == UserTaskRecordStatuses.Active));
        }

        await AssertStatusAsync(
            HttpMethod.Get,
            $"/api/instances/{instance.Id}/flows",
            HttpStatusCode.NotFound);
        await AssertStatusAsync(
            HttpMethod.Post,
            $"/api/instances/{instance.Id}/claim",
            HttpStatusCode.NotFound);
        await AssertStatusAsync(
            HttpMethod.Post,
            $"/api/instances/{instance.Id}/unclaim",
            HttpStatusCode.NotFound);
        await AssertStatusAsync(
            HttpMethod.Post,
            $"/api/instances/{instance.Id}/flows/201",
            HttpStatusCode.NotFound,
            new TakeFlowRequest(null));
    }

    [Fact]
    public async Task LegacyInstanceRoutesSelectTheOnlyVisibleParallelTask()
    {
        var model = CreateParallelVisibilityModel();
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, new Dictionary<string, JsonElement>
        {
            ["showFirst"] = JsonSerializer.SerializeToElement(true),
            ["showSecond"] = JsonSerializer.SerializeToElement(false)
        });

        using (var discovery = await SendAsync(
                   HttpMethod.Get,
                   $"/api/instances/{instance.Id}/flows",
                   user: "worker"))
        {
            Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
            Assert.Equal(301, Assert.Single(
                await ReadAsync<List<SequenceFlowModel>>(discovery)).Id);
        }

        using var take = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{instance.Id}/flows/301",
            new TakeFlowRequest(null),
            user: "worker");
        Assert.Equal(HttpStatusCode.OK, take.StatusCode);
    }

    [Fact]
    public async Task ManagementAndDistributionBypassHiddenPersonalVisibility()
    {
        var model = CreateOrdinaryModel(
            "management-bypass",
            "[visible] == true",
            [Variable(10, "visible", WorkflowVariableTypes.Boolean)]);
        var taskNode = model.FlowNodes.Single(node => node.Id == 2);
        taskNode.Roles = ["Worker"];
        model.TaskAssignmentRoles = ["AssignmentManager"];
        model.TaskDistribution = new TaskDistributionModel
        {
            ClientId = "visibility-distributor",
            ClientSecret = "visibility-distributor-secret"
        };
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, new Dictionary<string, JsonElement>
        {
            ["visible"] = JsonSerializer.SerializeToElement(false)
        });
        var taskId = await GetOnlyStoredTaskIdAsync(instance.Id);

        var personal = await GetInboxAsync(workflowId, 1, 20, "alice", ["Worker"]);
        Assert.Empty(personal.Items);
        using (var distributionRequest = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"/api/task-distribution/workflows/{Uri.EscapeDataString(model.Id)}/tasks" +
                   $"?instanceId={instance.Id}&pageSize=20"))
        {
            distributionRequest.Headers.Add("X-Client-Id", model.TaskDistribution.ClientId);
            distributionRequest.Headers.Add("X-Client-Secret", model.TaskDistribution.ClientSecret);
            using var distributionResponse = await fixture.Client.SendAsync(distributionRequest);
            Assert.Equal(HttpStatusCode.OK, distributionResponse.StatusCode);
            Assert.Equal(
                taskId,
                Assert.Single(
                    (await ReadAsync<PagedResult<ManagedUserTaskDto>>(distributionResponse)).Items)
                    .UserTaskId);
        }

        using var managedResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/user-tasks/manage?instanceId={instance.Id}&pageSize=20",
            user: "manager",
            roles: ["AssignmentManager"]);
        Assert.Equal(HttpStatusCode.OK, managedResponse.StatusCode);
        var managedTask = Assert.Single(
            (await ReadAsync<PagedResult<ManagedUserTaskDto>>(managedResponse)).Items);
        Assert.Equal(taskId, managedTask.UserTaskId);

        UserTaskAssignmentAckDto assigned;
        using (var assign = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{taskId}/assign",
                   new AssignUserTaskRequest("alice", managedTask.UpdatedAt, "Recovery assignment"),
                   "manager",
                   ["AssignmentManager"]))
        {
            Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
            assigned = await ReadAsync<UserTaskAssignmentAckDto>(assign);
            Assert.Equal("alice", assigned.CurrentOwner);
        }

        using var unassign = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{taskId}/unassign",
            new UnassignUserTaskRequest(assigned.UpdatedAt, "Recovery complete"),
            "manager",
            ["AssignmentManager"]);
        Assert.Equal(HttpStatusCode.OK, unassign.StatusCode);
        Assert.Null((await ReadAsync<UserTaskAssignmentAckDto>(unassign)).CurrentOwner);
    }

    [Fact]
    public async Task MalformedProgramsAreRejectedAndEvaluatorFailsClosed()
    {
        var workflowId = await CreateWorkflowAsync(CreateOrdinaryModel(
            "malformed-program",
            "[visible] == true",
            [Variable(10, "visible", WorkflowVariableTypes.Boolean)]));
        long conditionId;
        await using (var db = fixture.CreateDbContext())
        {
            conditionId = await db.WorkflowDefinitionUserTaskConditions
                .Where(condition => condition.WorkflowDefinitionId == workflowId)
                .Select(condition => condition.Id)
                .SingleAsync();
        }

        const string missingPools =
            """{"version":1,"instructions":[{"op":"literal","type":"boolean","value":true}]}""";
        const string stringVersion =
            """{"version":"1","variables":[],"externalReferences":[],"instructions":[{"op":"literal","type":"boolean","value":true}]}""";
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        foreach (var malformed in new[] { missingPools, stringVersion })
        {
            await using var corrupt = new NpgsqlCommand(
                """
                UPDATE flowbit.workflow_definition_user_task_conditions
                SET "ProgramJson" = @program::jsonb
                WHERE "Id" = @conditionId
                """,
                connection);
            corrupt.Parameters.AddWithValue("program", malformed);
            corrupt.Parameters.AddWithValue("conditionId", conditionId);
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                corrupt.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        Assert.False(await EvaluateAsync(missingPools));
        Assert.False(await EvaluateAsync(stringVersion));
        Assert.False(await EvaluateAsync(
            """
            {
              "version":1,
              "variables":[],
              "externalReferences":[],
              "instructions":[
                {"op":"literal","type":"string","value":"b"},
                {"op":"literal","type":"string","value":"a"},
                {"op":"greater","type":"string"}
              ]
            }
            """));
        Assert.False(await EvaluateAsync(
            """
            {
              "version":1,
              "variables":[],
              "externalReferences":[{"name":"setting.amount","type":"dynamic"}],
              "instructions":[
                {"op":"external","index":0},
                {"op":"positive"},
                {"op":"not"},
                {"op":"literal","type":"boolean","value":true},
                {"op":"or"}
              ]
            }
            """));

        async Task<bool> EvaluateAsync(string program)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT flowbit.evaluate_inbox_visibility_condition(
                    @program::jsonb,
                    jsonb_build_object(),
                    jsonb_build_object())
                """,
                connection);
            command.Parameters.AddWithValue("program", program);
            return Assert.IsType<bool>(await command.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task EvaluatorPreflightRejectsOverLimitProgramsBeforeTrueCanRescueThem()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var externalReferences = new object[]
        {
            new { name = "setting.operand", type = "dynamic" },
            new { name = "setting.rescue", type = "dynamic" }
        };
        const string rescueValues = """{"setting.rescue":true}""";

        var overDepth = new List<object> { new { op = "external", index = 0 } };
        for (var depth = 0; depth < 8; depth++)
        {
            overDepth.Add(new { op = "not" });
        }
        overDepth.Add(new { op = "external", index = 1 });
        overDepth.Add(new { op = "or" });

        var overLiteralCount = new List<object>();
        EmitBalancedBooleanTree(overLiteralCount, 17);
        overLiteralCount.Add(new { op = "external", index = 1 });
        overLiteralCount.Add(new { op = "or" });

        var overComparisonCount = new List<object>();
        _ = EmitBalancedComparisonTree(overComparisonCount, 18);
        overComparisonCount.Add(new { op = "external", index = 1 });
        overComparisonCount.Add(new { op = "or" });

        Assert.False(await EvaluateProgramAsync(
            connection,
            BuildProgram(overDepth, externalReferences),
            externalValues: rescueValues));
        Assert.False(await EvaluateProgramAsync(
            connection,
            BuildProgram(overLiteralCount, externalReferences),
            externalValues: rescueValues));
        Assert.False(await EvaluateProgramAsync(
            connection,
            BuildProgram(overComparisonCount, externalReferences),
            externalValues: rescueValues));

        static void EmitBalancedBooleanTree(List<object> instructions, int leaves)
        {
            if (leaves == 1)
            {
                instructions.Add(new { op = "literal", type = "boolean", value = false });
                return;
            }

            var leftLeaves = leaves / 2;
            EmitBalancedBooleanTree(instructions, leftLeaves);
            EmitBalancedBooleanTree(instructions, leaves - leftLeaves);
            instructions.Add(new { op = "or" });
        }

        static string EmitBalancedComparisonTree(List<object> instructions, int leaves)
        {
            if (leaves == 1)
            {
                instructions.Add(new { op = "external", index = 0 });
                return "dynamic";
            }

            var leftLeaves = leaves / 2;
            var leftType = EmitBalancedComparisonTree(instructions, leftLeaves);
            var rightType = EmitBalancedComparisonTree(instructions, leaves - leftLeaves);
            var comparisonType = leftType == rightType
                ? leftType
                : leftType == "dynamic" ? rightType : leftType;
            instructions.Add(new { op = "equal", type = comparisonType });
            return "boolean";
        }
    }

    [Fact]
    public async Task EvaluatorTrimsInvariantNumberWhitespaceAndPreservesUnknownTruthTables()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        const string numberEqualityProgram = """
            {
              "version":1,
              "variables":[],
              "externalReferences":[{"name":"config.limit","type":"string"}],
              "instructions":[
                {"op":"external","index":0},
                {"op":"number"},
                {"op":"literal","type":"number","value":"42"},
                {"op":"equal","type":"number"}
              ]
            }
            """;
        const string rescuedInvalidNumberProgram = """
            {
              "version":1,
              "variables":[],
              "externalReferences":[{"name":"config.limit","type":"string"}],
              "instructions":[
                {"op":"external","index":0},
                {"op":"number"},
                {"op":"literal","type":"number","value":"42"},
                {"op":"equal","type":"number"},
                {"op":"literal","type":"boolean","value":true},
                {"op":"or"}
              ]
            }
            """;
        const string moduloZeroProgram = """
            {
              "version":1,
              "variables":[],
              "externalReferences":[],
              "instructions":[
                {"op":"literal","type":"number","value":"5"},
                {"op":"literal","type":"number","value":"0"},
                {"op":"modulo"},
                {"op":"literal","type":"number","value":"0"},
                {"op":"equal","type":"number"}
              ]
            }
            """;
        const string rescuedModuloZeroProgram = """
            {
              "version":1,
              "variables":[],
              "externalReferences":[],
              "instructions":[
                {"op":"literal","type":"number","value":"5"},
                {"op":"literal","type":"number","value":"0"},
                {"op":"modulo"},
                {"op":"literal","type":"number","value":"0"},
                {"op":"equal","type":"number"},
                {"op":"literal","type":"boolean","value":true},
                {"op":"or"}
              ]
            }
            """;
        const string notMissingProgram = """
            {
              "version":1,
              "variables":[],
              "externalReferences":[{"name":"setting.missing","type":"dynamic"}],
              "instructions":[{"op":"external","index":0},{"op":"not"}]
            }
            """;
        const string missingNotEqualProgram = """
            {
              "version":1,
              "variables":[],
              "externalReferences":[{"name":"config.missing","type":"string"}],
              "instructions":[
                {"op":"external","index":0},
                {"op":"literal","type":"string","value":"value"},
                {"op":"notEqual","type":"string"}
              ]
            }
            """;
        const string dynamicNumericEqualityProgram = """
            {
              "version":1,
              "variables":[],
              "externalReferences":[{"name":"setting.amount","type":"dynamic"}],
              "instructions":[
                {"op":"external","index":0},
                {"op":"literal","type":"number","value":"1"},
                {"op":"equal","type":"number"}
              ]
            }
            """;
        const string stringEqualityProgram = """
            {
              "version":1,
              "variables":[],
              "externalReferences":[{"name":"config.department","type":"string"}],
              "instructions":[
                {"op":"external","index":0},
                {"op":"literal","type":"string","value":"finance"},
                {"op":"equal","type":"string"}
              ]
            }
            """;

        Assert.True(await EvaluateProgramAsync(
            connection,
            numberEqualityProgram,
            externalValues:
                """{"config.limit":"\t\n\u000b\f\r 42 \r\f\u000b\n\t"}"""));
        Assert.False(await EvaluateProgramAsync(
            connection,
            numberEqualityProgram,
            externalValues: """{"config.limit":"not-a-number"}"""));
        Assert.True(await EvaluateProgramAsync(
            connection,
            rescuedInvalidNumberProgram,
            externalValues: """{"config.limit":"not-a-number"}"""));
        Assert.False(await EvaluateProgramAsync(connection, moduloZeroProgram));
        Assert.True(await EvaluateProgramAsync(connection, rescuedModuloZeroProgram));
        Assert.False(await EvaluateProgramAsync(connection, notMissingProgram));
        Assert.False(await EvaluateProgramAsync(connection, missingNotEqualProgram));
        Assert.True(await EvaluateProgramAsync(
            connection,
            dynamicNumericEqualityProgram,
            externalValues: """{"setting.amount":1.00}"""));
        Assert.True(await EvaluateProgramAsync(
            connection,
            stringEqualityProgram,
            externalValues: """{"config.department":"FINANCE"}"""));
        Assert.False(await EvaluateProgramAsync(
            connection,
            stringEqualityProgram,
            externalValues: """{"config.department":" Finance "}"""));
    }

    private static string BuildProgram(
        IReadOnlyList<object> instructions,
        IReadOnlyList<object> externalReferences) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            variables = Array.Empty<object>(),
            externalReferences,
            instructions
        });

    private static async Task<bool> EvaluateProgramAsync(
        NpgsqlConnection connection,
        string program,
        string variableValues = "{}",
        string externalValues = "{}")
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT flowbit.evaluate_inbox_visibility_condition(
                @program::jsonb,
                @variableValues::jsonb,
                @externalValues::jsonb)
            """,
            connection);
        command.Parameters.AddWithValue("program", program);
        command.Parameters.AddWithValue("variableValues", variableValues);
        command.Parameters.AddWithValue("externalValues", externalValues);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync());
    }

    private async Task<long> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Expected workflow creation to return 201 but received {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private async Task<InstanceDetailDto> StartAsync(
        long workflowId,
        Dictionary<string, JsonElement> variables)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, variables),
            user: "starter");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<long> GetOnlyStoredTaskIdAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks
            .Where(task => task.InstanceId == instanceId)
            .Select(task => task.Id)
            .SingleAsync();
    }

    private async Task<PagedResult<InboxItemDto>> GetInboxAsync(
        long workflowId,
        int page,
        int pageSize,
        string user,
        string[]? roles = null,
        IReadOnlyDictionary<string, string>? claims = null)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/inbox?workflowId={workflowId}&page={page}&pageSize={pageSize}",
            user: user,
            roles: roles,
            claims: claims);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<InboxItemDto>>(response);
    }

    private async Task AssertStatusAsync(
        HttpMethod method,
        string path,
        HttpStatusCode expected,
        object? body = null)
    {
        using var response = await SendAsync(method, path, body, user: "worker");
        Assert.Equal(expected, response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        string[]? roles = null,
        IReadOnlyDictionary<string, string>? claims = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        ApiTestAuth.Authorize(request, user, roles ?? []);
        foreach (var claim in claims ?? new Dictionary<string, string>())
        {
            request.Headers.Add($"X-Test-Claim-{claim.Key}", claim.Value);
        }

        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static VariableModel Variable(int id, string name, string type) => new()
    {
        Id = id,
        Name = name,
        DataType = type,
        Required = true
    };

    private static WorkflowModel CreateOrdinaryModel(
        string label,
        string? inboxCondition,
        List<VariableModel> variables,
        bool requiresClaim = false)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"inbox-visibility-{label}-{suffix}",
            Name = $"Inbox visibility {label} {suffix}",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent,
                    Variables = variables
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    RequiresClaim = requiresClaim,
                    InboxVisibilityCondition = inboxCondition
                },
                new FlowNodeModel { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Review", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "Complete", SourceRef = 2, TargetRef = 3 }
            ]
        };
    }

    private static WorkflowModel CreateMultiInstanceModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"inbox-visibility-mi-snapshot-{suffix}",
            Name = $"Inbox visibility MI snapshot {suffix}",
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 11,
                    Name = "results",
                    DataType = WorkflowVariableTypes.Json,
                    DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>())
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent,
                    Variables = [Variable(10, "visible", WorkflowVariableTypes.Boolean)]
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Two approvals",
                    Type = BpmnFlowNodeTypes.UserTask,
                    InboxVisibilityCondition = "[visible] == true",
                    MultiInstance = new MultiInstanceModel
                    {
                        Mode = MultiInstanceModes.Parallel,
                        Source = MultiInstanceSources.Cardinality,
                        CardinalityExpression = "2",
                        CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll,
                        ResultVariable = "results"
                    }
                },
                new FlowNodeModel { Id = 3, Name = "Selected", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 4, Name = "Fallback", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Approve",
                    SourceRef = 2,
                    TargetRef = 3,
                    CompletionCondition = "CountFlow(201) >= 2",
                    CompletionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 202,
                    Name = "No result",
                    SourceRef = 2,
                    TargetRef = 4,
                    IsDefault = true,
                    IsSelectable = false
                }
            ]
        };
    }

    private static WorkflowModel CreateParallelVisibilityModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"inbox-visibility-parallel-{suffix}",
            Name = $"Inbox visibility parallel {suffix}",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent,
                    Variables =
                    [
                        Variable(10, "showFirst", WorkflowVariableTypes.Boolean),
                        Variable(11, "showSecond", WorkflowVariableTypes.Boolean)
                    ]
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Split",
                    Type = BpmnFlowNodeTypes.ParallelGateway
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "First review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    InboxVisibilityCondition = "[showFirst] == true"
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Second review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    InboxVisibilityCondition = "[showSecond] == true"
                },
                new FlowNodeModel { Id = 5, Name = "First end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 6, Name = "Second end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Split", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "First", SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, Name = "Second", SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, Name = "Finish first", SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 401, Name = "Finish second", SourceRef = 4, TargetRef = 6 }
            ]
        };
    }
}
