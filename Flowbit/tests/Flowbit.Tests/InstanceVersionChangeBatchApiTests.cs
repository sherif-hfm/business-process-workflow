using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVersionChangeBatchApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EveryRoute_RequiresAuthenticationAndConfiguredWorkflowAdministratorRole()
    {
        await SetWorkflowRequiredRoleAsync("admin");
        var routes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (
                HttpMethod.Post,
                "/api/instance-version-change-batches/candidates/search",
                new InstanceVersionChangeCandidateSearchRequest
                {
                    Filter = new InstanceVersionChangeCandidateFilterDto
                    {
                        SourceWorkflowId = 1
                    }
                }),
            (
                HttpMethod.Post,
                "/api/instance-version-change-batches",
                new CreateInstanceVersionChangeBatchRequest(
                    1,
                    2,
                    "authorization probe",
                    new InstanceVersionChangeBatchSelectionDto(
                        InstanceVersionChangeBatchSelectionModes.Explicit,
                        [1],
                        null,
                        null),
                    null)),
            (HttpMethod.Get, "/api/instance-version-change-batches", null),
            (HttpMethod.Get, "/api/instance-version-change-batches/1", null),
            (HttpMethod.Get, "/api/instance-version-change-batches/1/items", null),
            (
                HttpMethod.Post,
                "/api/instance-version-change-batches/1/confirm",
                new ConfirmInstanceVersionChangeBatchRequest(
                    0,
                    0,
                    0,
                    DateTimeOffset.UtcNow)),
            (
                HttpMethod.Post,
                "/api/instance-version-change-batches/1/cancel",
                new CancelInstanceVersionChangeBatchRequest(null))
        };

        foreach (var route in routes)
        {
            using var anonymous = await SendAnonymousAsync(
                route.Method,
                route.Path,
                route.Body);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var forbidden = await SendAsync(
                route.Method,
                route.Path,
                route.Body,
                user: "ordinary-worker",
                roles: ["Worker"],
                suppressDefaultAdmin: true);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        try
        {
            await SetWorkflowRequiredRoleAsync(" ReleaseManager, MigrationAdmin ");

            using var formerDefaultAdmin = await SendAsync(
                HttpMethod.Get,
                "/api/instance-version-change-batches",
                user: "default-admin",
                roles: ["admin"],
                suppressDefaultAdmin: true);
            Assert.Equal(HttpStatusCode.Forbidden, formerDefaultAdmin.StatusCode);

            using var configuredAdministrator = await SendAsync(
                HttpMethod.Get,
                "/api/instance-version-change-batches",
                user: "release-manager",
                roles: ["releasemanager"],
                suppressDefaultAdmin: true);
            Assert.Equal(HttpStatusCode.OK, configuredAdministrator.StatusCode);
        }
        finally
        {
            await SetWorkflowRequiredRoleAsync("admin");
        }
    }

    [Fact]
    public async Task ExplicitBatch_PreparesWithoutMutationThenExecutesIndependentlyWithCorrelatedAudits()
    {
        await SetWorkflowRequiredRoleAsync("admin");
        var family = await CreateVersionFamilyAsync("batch-success");
        var instances = new[]
        {
            await StartAsync(family.Source.Id),
            await StartAsync(family.Source.Id)
        };
        var candidates = await SearchCandidatesAsync(family.Source.Id);
        Assert.Equal(instances.Select(instance => instance.Id).Order(),
            candidates.Items.Select(candidate => candidate.InstanceId).Order());

        var idempotencyKey = $"version-batch-{Guid.NewGuid():N}";
        var request = new CreateInstanceVersionChangeBatchRequest(
            family.Source.Id,
            family.Target.Id,
            "  approve the frozen release population  ",
            new InstanceVersionChangeBatchSelectionDto(
                InstanceVersionChangeBatchSelectionModes.Explicit,
                candidates.Items.Select(candidate => candidate.InstanceId).ToArray(),
                null,
                null),
            idempotencyKey);

        var created = await CreateBatchAsync(request, "preparer", ["admin"]);
        Assert.Equal(InstanceVersionChangeBatchStatuses.Preparing, created.Summary.Status);
        Assert.Equal("approve the frozen release population", created.Summary.Reason);
        Assert.Equal(2, created.Summary.TotalItemCount);
        Assert.NotNull(created.PreparationJobId);

        var replay = await CreateBatchAsync(request, "preparer", ["admin"]);
        Assert.Equal(created.Summary.Id, replay.Summary.Id);
        Assert.Equal(created.PreparationJobId, replay.PreparationJobId);

        using (var conflictingReplay = await SendAsync(
                   HttpMethod.Post,
                   "/api/instance-version-change-batches",
                   request with { Reason = "different idempotent request" },
                   user: "preparer",
                   roles: ["admin"],
                   suppressDefaultAdmin: true))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflictingReplay.StatusCode);
        }

        await ProcessBatchJobAsync(created.PreparationJobId!.Value);
        var ready = await GetBatchAsync(created.Summary.Id);
        Assert.Equal(InstanceVersionChangeBatchStatuses.Ready, ready.Summary.Status);
        Assert.Equal(2, ready.Summary.EligibleItemCount);
        Assert.Equal(0, ready.Summary.IneligibleItemCount);
        Assert.Equal(0, ready.Summary.StaleItemCount);
        Assert.Equal(0, ready.Summary.WarningItemCount);
        Assert.NotNull(ready.PreparedAt);

        var preparedItems = await GetBatchItemsAsync(ready.Summary.Id);
        Assert.Equal(2, preparedItems.Count);
        Assert.All(preparedItems, item =>
        {
            Assert.Equal(InstanceVersionChangeBatchItemStatuses.Eligible, item.Status);
            Assert.Empty(item.Blockers);
            Assert.Null(item.Result);
            Assert.Null(item.VersionChangeAuditId);
        });
        await AssertDefinitionsAsync(
            instances.Select(instance => instance.Id),
            family.Source.Id);

        var confirmation = new ConfirmInstanceVersionChangeBatchRequest(
            ready.Summary.EligibleItemCount,
            ready.Summary.IneligibleItemCount,
            ready.Summary.WarningItemCount,
            ready.Summary.UpdatedAt);
        var queued = await ConfirmBatchAsync(
            ready.Summary.Id,
            confirmation,
            "confirmer",
            ["admin", "Auditor"],
            new Dictionary<string, string> { ["department"] = "release" });
        Assert.Equal(InstanceVersionChangeBatchStatuses.Queued, queued.Summary.Status);
        Assert.Equal("confirmer", queued.Summary.ConfirmedBy);
        Assert.Equal(
            ["admin", "Auditor"],
            queued.ConfirmedByRoles,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, queued.Summary.QueuedItemCount);
        Assert.NotNull(queued.ExecutionJobId);

        var repeatedConfirmation = await ConfirmBatchAsync(
            ready.Summary.Id,
            confirmation,
            "confirmer",
            ["admin", "Auditor"]);
        Assert.Equal(queued.ExecutionJobId, repeatedConfirmation.ExecutionJobId);

        await ProcessBatchJobAsync(queued.ExecutionJobId!.Value);
        var completed = await GetBatchAsync(ready.Summary.Id);
        Assert.Equal(InstanceVersionChangeBatchStatuses.Completed, completed.Summary.Status);
        Assert.Equal(2, completed.Summary.SucceededItemCount);
        Assert.Equal(0, completed.Summary.QueuedItemCount);
        Assert.NotNull(completed.Summary.CompletedAt);
        await AssertDefinitionsAsync(
            instances.Select(instance => instance.Id),
            family.Target.Id);

        var completedItems = await GetBatchItemsAsync(completed.Summary.Id);
        Assert.All(completedItems, item =>
        {
            Assert.Equal(InstanceVersionChangeBatchItemStatuses.Succeeded, item.Status);
            Assert.NotNull(item.Result);
            Assert.NotNull(item.VersionChangeAuditId);
            Assert.Equal(
                item.VersionChangeAuditId,
                item.Result!.Value.GetProperty("versionChangeAuditId").GetInt64());
            Assert.NotNull(item.StartedAt);
            Assert.NotNull(item.CompletedAt);
        });

        await using (var db = fixture.CreateDbContext())
        {
            var audits = await db.WorkflowInstanceVersionChanges
                .Where(change => change.BatchId == completed.Summary.Id)
                .OrderBy(change => change.InstanceId)
                .ToListAsync();
            Assert.Equal(2, audits.Count);
            Assert.All(audits, audit =>
            {
                Assert.NotNull(audit.BatchItemId);
                Assert.Equal("confirmer", audit.ChangedBy);
                Assert.Equal("approve the frozen release population", audit.Reason);
            });
            Assert.Equal(
                completedItems.Select(item => item.Id).Order(),
                audits.Select(audit => audit.BatchItemId!.Value).Order());
        }

        using var detailResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instances[0].Id}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(detailResponse);
        var correlatedAudit = Assert.Single(detail.VersionChanges);
        Assert.Equal(completed.Summary.Id, correlatedAudit.BatchId);
        Assert.Contains(
            completedItems,
            item => item.Id == correlatedAudit.BatchItemId);
    }

    [Fact]
    public async Task AllMatching_FreezesLateMatchesSeparatesStaleAndRejectsUnpublishedTargetAtConfirmation()
    {
        await SetWorkflowRequiredRoleAsync("admin");
        var family = await CreateVersionFamilyAsync("batch-stale");
        var stale = await StartAsync(family.Source.Id);
        var eligible = await StartAsync(family.Source.Id);
        var excluded = await StartAsync(family.Source.Id);
        var request = new CreateInstanceVersionChangeBatchRequest(
            family.Source.Id,
            family.Target.Id,
            "prepare a stable release population",
            new InstanceVersionChangeBatchSelectionDto(
                InstanceVersionChangeBatchSelectionModes.AllMatching,
                null,
                new InstanceVersionChangeCandidateFilterDto
                {
                    SourceWorkflowId = family.Source.Id
                },
                [excluded.Id]),
            $"all-matching-{Guid.NewGuid():N}");
        var created = await CreateBatchAsync(request, "stale-preparer", ["admin"]);
        Assert.Equal(2, created.Summary.TotalItemCount);

        var lateMatch = await StartAsync(family.Source.Id);
        await using (var db = fixture.CreateDbContext())
        {
            var instance = await db.WorkflowInstances.SingleAsync(item => item.Id == stale.Id);
            instance.UpdatedAt = instance.UpdatedAt.AddSeconds(5);
            await db.SaveChangesAsync();
        }

        await ProcessBatchJobAsync(created.PreparationJobId!.Value);
        var ready = await GetBatchAsync(created.Summary.Id);
        Assert.Equal(InstanceVersionChangeBatchStatuses.Ready, ready.Summary.Status);
        Assert.Equal(1, ready.Summary.EligibleItemCount);
        Assert.Equal(1, ready.Summary.IneligibleItemCount);
        Assert.Equal(1, ready.Summary.StaleItemCount);
        Assert.Equal(0, ready.Summary.BlockedItemCount);

        var items = await GetBatchItemsAsync(ready.Summary.Id);
        var staleItem = Assert.Single(items, item => item.InstanceId == stale.Id);
        Assert.Equal(InstanceVersionChangeBatchItemStatuses.Ineligible, staleItem.Status);
        Assert.Equal("stale_since_selection", staleItem.ErrorCode);
        Assert.Contains(
            staleItem.Blockers,
            issue => issue.Code == "stale_since_selection");
        Assert.DoesNotContain(items, item => item.InstanceId == excluded.Id);
        Assert.DoesNotContain(items, item => item.InstanceId == lateMatch.Id);

        using (var unpublish = await SendAsync(
                   HttpMethod.Post,
                   $"/api/workflows/{family.Target.Id}/unpublish"))
        {
            Assert.Equal(HttpStatusCode.NoContent, unpublish.StatusCode);
        }
        using (var rejectedConfirmation = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instance-version-change-batches/{ready.Summary.Id}/confirm",
                   new ConfirmInstanceVersionChangeBatchRequest(
                       ready.Summary.EligibleItemCount,
                       ready.Summary.IneligibleItemCount,
                       ready.Summary.WarningItemCount,
                       ready.Summary.UpdatedAt)))
        {
            Assert.Equal(HttpStatusCode.Conflict, rejectedConfirmation.StatusCode);
        }
        var stillReady = await GetBatchAsync(ready.Summary.Id);
        Assert.Equal(InstanceVersionChangeBatchStatuses.Ready, stillReady.Summary.Status);
        Assert.Null(stillReady.ExecutionJobId);

        using (var cancelResponse = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instance-version-change-batches/{ready.Summary.Id}/cancel",
                   new CancelInstanceVersionChangeBatchRequest("target was withdrawn")))
        {
            Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
        }
        var cancelled = await GetBatchAsync(ready.Summary.Id);
        Assert.Equal(InstanceVersionChangeBatchStatuses.Cancelled, cancelled.Summary.Status);
        Assert.Equal(1, cancelled.Summary.CancelledItemCount);
        Assert.Equal(1, cancelled.Summary.StaleItemCount);
        Assert.NotNull(cancelled.Summary.CompletedAt);
        await AssertDefinitionsAsync(
            [stale.Id, eligible.Id, excluded.Id, lateMatch.Id],
            family.Source.Id);
    }

    [Fact]
    public async Task SelectionModes_RejectAmbiguousExplicitAndAllMatchingPayloads()
    {
        await SetWorkflowRequiredRoleAsync("admin");
        var family = await CreateVersionFamilyAsync("batch-selection-shape");
        var instance = await StartAsync(family.Source.Id);
        var filter = new InstanceVersionChangeCandidateFilterDto
        {
            SourceWorkflowId = family.Source.Id
        };

        using (var ambiguousExplicit = await SendAsync(
                   HttpMethod.Post,
                   "/api/instance-version-change-batches",
                   new CreateInstanceVersionChangeBatchRequest(
                       family.Source.Id,
                       family.Target.Id,
                       "reject mixed explicit selection",
                       new InstanceVersionChangeBatchSelectionDto(
                           InstanceVersionChangeBatchSelectionModes.Explicit,
                           [instance.Id],
                           filter,
                           null),
                       null)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, ambiguousExplicit.StatusCode);
        }

        using var ambiguousAllMatching = await SendAsync(
            HttpMethod.Post,
            "/api/instance-version-change-batches",
            new CreateInstanceVersionChangeBatchRequest(
                family.Source.Id,
                family.Target.Id,
                "reject mixed all-matching selection",
                new InstanceVersionChangeBatchSelectionDto(
                    InstanceVersionChangeBatchSelectionModes.AllMatching,
                    [instance.Id],
                    filter,
                    null),
                null));
        Assert.Equal(HttpStatusCode.BadRequest, ambiguousAllMatching.StatusCode);
    }

    [Fact]
    public async Task Batch_ChangesNonAdjacentVersionsInBothDirections()
    {
        await SetWorkflowRequiredRoleAsync("admin");
        var model = CreateModel("batch-non-adjacent");
        var version1 = await CreateWorkflowAsync(model, publish: true);
        var version2Model = Clone(model);
        version2Model.Name += " v2";
        var version2 = await CreateWorkflowAsync(version2Model, publish: true);
        var version3Model = Clone(model);
        version3Model.Name += " v3";
        var version3 = await CreateWorkflowAsync(version3Model, publish: true);
        Assert.Equal((1, 2, 3), (version1.Version, version2.Version, version3.Version));
        var instance = await StartAsync(version1.Id);

        var upgraded = await ExecuteSingleInstanceBatchAsync(
            instance.Id,
            version1.Id,
            version3.Id,
            "non-adjacent upgrade");
        Assert.Equal(InstanceVersionChangeDirections.Upgrade, upgraded.Summary.Direction);
        Assert.Equal(InstanceVersionChangeBatchStatuses.Completed, upgraded.Summary.Status);
        Assert.Equal(1, upgraded.Summary.SucceededItemCount);
        await AssertDefinitionsAsync([instance.Id], version3.Id);

        var downgraded = await ExecuteSingleInstanceBatchAsync(
            instance.Id,
            version3.Id,
            version1.Id,
            "non-adjacent downgrade");
        Assert.Equal(InstanceVersionChangeDirections.Downgrade, downgraded.Summary.Direction);
        Assert.Equal(InstanceVersionChangeBatchStatuses.Completed, downgraded.Summary.Status);
        Assert.Equal(1, downgraded.Summary.SucceededItemCount);
        await AssertDefinitionsAsync([instance.Id], version1.Id);

        using var detailResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instance.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(detailResponse);
        Assert.Equal(2, detail.VersionChanges.Count);
        Assert.Contains(detail.VersionChanges, audit =>
            audit.BatchId == upgraded.Summary.Id
            && audit.Direction == InstanceVersionChangeDirections.Upgrade);
        Assert.Contains(detail.VersionChanges, audit =>
            audit.BatchId == downgraded.Summary.Id
            && audit.Direction == InstanceVersionChangeDirections.Downgrade);
    }

    private async Task<VersionFamily> CreateVersionFamilyAsync(string label)
    {
        var model = CreateModel(label);
        var source = await CreateWorkflowAsync(model, publish: true);
        var targetModel = Clone(model);
        targetModel.Name += " target";
        var target = await CreateWorkflowAsync(targetModel, publish: true);
        return new VersionFamily(source, target);
    }

    private async Task<WorkflowDetailDto> CreateWorkflowAsync(
        WorkflowModel model,
        bool publish)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, publish));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<WorkflowDetailDto>(response);
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            user: "starter");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<PagedResult<InstanceVersionChangeCandidateDto>>
        SearchCandidatesAsync(long sourceWorkflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instance-version-change-batches/candidates/search",
            new InstanceVersionChangeCandidateSearchRequest
            {
                Filter = new InstanceVersionChangeCandidateFilterDto
                {
                    SourceWorkflowId = sourceWorkflowId
                },
                Page = 1,
                PageSize = 200
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<InstanceVersionChangeCandidateDto>>(response);
    }

    private async Task<InstanceVersionChangeBatchDetailDto> CreateBatchAsync(
        CreateInstanceVersionChangeBatchRequest request,
        string user,
        string[] roles)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instance-version-change-batches",
            request,
            user,
            roles,
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await ReadAsync<InstanceVersionChangeBatchDetailDto>(response);
    }

    private async Task<InstanceVersionChangeBatchDetailDto> GetBatchAsync(long batchId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instance-version-change-batches/{batchId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceVersionChangeBatchDetailDto>(response);
    }

    private async Task<IReadOnlyList<InstanceVersionChangeBatchItemDto>>
        GetBatchItemsAsync(long batchId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instance-version-change-batches/{batchId}/items?page=1&pageSize=200");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<PagedResult<InstanceVersionChangeBatchItemDto>>(response)).Items;
    }

    private async Task<InstanceVersionChangeBatchDetailDto> ConfirmBatchAsync(
        long batchId,
        ConfirmInstanceVersionChangeBatchRequest request,
        string user,
        string[] roles,
        IReadOnlyDictionary<string, string>? claims = null)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/instance-version-change-batches/{batchId}/confirm",
            request,
            user,
            roles,
            suppressDefaultAdmin: true,
            claims);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceVersionChangeBatchDetailDto>(response);
    }

    private async Task<InstanceVersionChangeBatchDetailDto>
        ExecuteSingleInstanceBatchAsync(
            long instanceId,
            long sourceWorkflowId,
            long targetWorkflowId,
            string reason)
    {
        var batch = await CreateBatchAsync(
            new CreateInstanceVersionChangeBatchRequest(
                sourceWorkflowId,
                targetWorkflowId,
                reason,
                new InstanceVersionChangeBatchSelectionDto(
                    InstanceVersionChangeBatchSelectionModes.Explicit,
                    [instanceId],
                    null,
                    null),
                $"{reason.Replace(' ', '-')}-{Guid.NewGuid():N}"),
            "non-adjacent-admin",
            ["admin"]);
        await ProcessBatchJobAsync(batch.PreparationJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(InstanceVersionChangeBatchStatuses.Ready, batch.Summary.Status);
        batch = await ConfirmBatchAsync(
            batch.Summary.Id,
            new ConfirmInstanceVersionChangeBatchRequest(
                batch.Summary.EligibleItemCount,
                batch.Summary.IneligibleItemCount,
                batch.Summary.WarningItemCount,
                batch.Summary.UpdatedAt),
            "non-adjacent-admin",
            ["admin"]);
        await ProcessBatchJobAsync(batch.ExecutionJobId!.Value);
        return await GetBatchAsync(batch.Summary.Id);
    }

    private async Task ProcessBatchJobAsync(long jobId)
    {
        await using (var db = fixture.CreateDbContext())
        {
            var job = await db.WorkflowJobs.SingleAsync(entity => entity.Id == jobId);
            job.Priority = 1_000_000;
            job.DueAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var leases = await repository.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"instance-version-change-batch-test-{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: 1,
                MaxPerInstance: 1,
                LeaseDuration: TimeSpan.FromMinutes(2)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        await scope.ServiceProvider
            .GetRequiredService<IWorkflowJobProcessor>()
            .ProcessAsync(lease, CancellationToken.None);
    }

    private async Task AssertDefinitionsAsync(
        IEnumerable<long> instanceIds,
        long expectedWorkflowDefinitionId)
    {
        var ids = instanceIds.ToArray();
        await using var db = fixture.CreateDbContext();
        var definitions = await db.WorkflowInstances
            .Where(instance => ids.Contains(instance.Id))
            .Select(instance => instance.WorkflowDefinitionId)
            .ToListAsync();
        Assert.Equal(ids.Length, definitions.Count);
        Assert.All(definitions, id => Assert.Equal(expectedWorkflowDefinitionId, id));
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
        bool suppressDefaultAdmin = false,
        IReadOnlyDictionary<string, string>? claims = null)
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
        foreach (var claim in claims ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation($"X-Test-Claim-{claim.Key}", claim.Value);
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

    private static WorkflowModel CreateModel(string label)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"{label}-{suffix}",
            Name = $"{label} {suffix}",
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

    private sealed record VersionFamily(
        WorkflowDetailDto Source,
        WorkflowDetailDto Target);
}
