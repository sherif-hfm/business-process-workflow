using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVariableUpdateBatchTests(PostgresApiFixture fixture)
{
    private const long RequestBodyLimit = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void BatchRoutesExposeAuthorizationLimitsAndCompleteResponseMetadata()
    {
        var endpoints = fixture.Factory.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        var contracts = new[]
        {
            new EndpointContract(
                "/api/instance-variable-update-batches/candidates/search",
                HttpMethods.Post,
                HasBody: true,
                [200, 400, 401, 403, 413, 415]),
            new EndpointContract(
                "/api/instance-variable-update-batches",
                HttpMethods.Post,
                HasBody: true,
                [202, 400, 401, 403, 409, 413, 415]),
            new EndpointContract(
                "/api/instance-variable-update-batches",
                HttpMethods.Get,
                HasBody: false,
                [200, 400, 401, 403]),
            new EndpointContract(
                "/api/instance-variable-update-batches/{batchId:long}",
                HttpMethods.Get,
                HasBody: false,
                [200, 400, 401, 403, 404]),
            new EndpointContract(
                "/api/instance-variable-update-batches/{batchId:long}/items",
                HttpMethods.Get,
                HasBody: false,
                [200, 400, 401, 403, 404]),
            new EndpointContract(
                "/api/instance-variable-update-batches/{batchId:long}/confirm",
                HttpMethods.Post,
                HasBody: true,
                [200, 400, 401, 403, 404, 409, 413, 415]),
            new EndpointContract(
                "/api/instance-variable-update-batches/{batchId:long}/cancel",
                HttpMethods.Post,
                HasBody: true,
                [200, 400, 401, 403, 404, 409, 413, 415])
        };

        foreach (var contract in contracts)
        {
            var routeMatches = endpoints.Where(candidate => string.Equals(
                    candidate.RoutePattern.RawText?.TrimEnd('/'),
                    contract.Route.TrimEnd('/'),
                    StringComparison.Ordinal))
                .ToArray();
            Assert.True(
                routeMatches.Length > 0,
                $"Route '{contract.Route}' was not mapped.");
            var methodMatches = routeMatches.Where(candidate =>
                    candidate.Metadata.GetMetadata<HttpMethodMetadata>()!
                        .HttpMethods.Contains(contract.Method, StringComparer.Ordinal))
                .ToArray();
            Assert.True(
                methodMatches.Length == 1,
                $"Route '{contract.Route}' did not expose exactly one {contract.Method} endpoint.");
            var endpoint = methodMatches[0];
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            if (contract.HasBody)
            {
                Assert.Equal(
                    RequestBodyLimit,
                    endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()!
                        .MaxRequestBodySize);
            }
            var responseStatuses = endpoint.Metadata
                .GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Select(metadata => metadata.StatusCode)
                .ToHashSet();
            foreach (var expectedStatus in contract.ResponseStatuses)
            {
                Assert.Contains(expectedStatus, responseStatuses);
            }
        }
    }

    [Fact]
    public async Task ExplicitCrossVersionBatch_FreezesPreparesAndExecutesEachVersionExactlyOnce()
    {
        var family = await CreateFamilyAsync("variable-batch-explicit");
        var versionOneInstance = await StartAsync(family.VersionOne.Id);
        var versionTwoInstance = await StartAsync(family.VersionTwo.Id);
        await AddDeferredOpenJobAsync(family.VersionTwo, versionTwoInstance.Id);

        await PatchDirectAsync(
            versionOneInstance.Id,
            [new InstanceVariableWriteDto(
                "Score",
                JsonSerializer.SerializeToElement(1))]);

        var idempotencyKey = $"explicit-variable-batch-{Guid.NewGuid():N}";
        var request = new CreateInstanceVariableUpdateBatchRequest(
            family.VersionOne.Definition.Id,
            [
                new InstanceVariableWriteDto(
                    "score",
                    JsonSerializer.SerializeToElement(9)),
                new InstanceVariableWriteDto(
                    "reviewNote",
                    JsonSerializer.SerializeToElement("batch"))
            ],
            "  cross-version correction  ",
            ExplicitSelection(versionOneInstance.Id, versionTwoInstance.Id),
            idempotencyKey);
        var created = await CreateBatchAsync(request, "preparer");
        Assert.Equal(InstanceVariableUpdateBatchStatuses.Preparing, created.Summary.Status);
        Assert.Equal(2, created.Summary.TotalItemCount);
        Assert.Equal(2, created.Summary.WorkflowDefinitionCount);
        Assert.Equal(2, PrepareJobs(created).Count);
        var preparingItems = await GetItemsAsync(created.Summary.Id);
        Assert.All(preparingItems, item => Assert.Null(item.UpdateOperationId));

        var replay = await CreateBatchAsync(
            request with
            {
                WorkflowKey = $"  {request.WorkflowKey}  ",
                Selection = new InstanceVariableUpdateBatchSelectionDto(
                    " Explicit ",
                    [versionTwoInstance.Id, versionOneInstance.Id, versionOneInstance.Id],
                    null,
                    [long.MaxValue])
            },
            "preparer");
        Assert.Equal(created.Summary.Id, replay.Summary.Id);
        Assert.Equal(
            PrepareJobs(created).Select(job => job.OriginalJobId).Order(),
            PrepareJobs(replay).Select(job => job.OriginalJobId).Order());

        using (var conflict = await SendAsync(
                   HttpMethod.Post,
                   "/api/instance-variable-update-batches",
                   request with
                   {
                       Variables =
                       [new InstanceVariableWriteDto(
                           "score",
                           JsonSerializer.SerializeToElement(10))]
                   },
                   user: "preparer"))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }

        var prepareJobs = PrepareJobs(created);
        await ProcessJobAsync(prepareJobs[0].JobId!.Value, fixture.Factory.Services);
        var partiallyPrepared = await GetBatchAsync(created.Summary.Id);
        Assert.Equal(
            InstanceVariableUpdateBatchStatuses.Preparing,
            partiallyPrepared.Summary.Status);
        Assert.Equal(1, partiallyPrepared.Summary.EligibleItemCount);

        await ProcessJobAsync(prepareJobs[1].JobId!.Value, fixture.Factory.Services);
        var ready = await GetBatchAsync(created.Summary.Id);
        Assert.Equal(InstanceVariableUpdateBatchStatuses.Ready, ready.Summary.Status);
        Assert.Equal(2, ready.Summary.EligibleItemCount);
        Assert.Equal(0, ready.Summary.IneligibleItemCount);
        Assert.Equal(1, ready.Summary.WarningItemCount);
        var preparedItems = await GetItemsAsync(ready.Summary.Id);
        Assert.All(preparedItems, item =>
        {
            Assert.Equal(InstanceVariableUpdateBatchItemStatuses.Eligible, item.Status);
            Assert.Equal(2, item.Plan.Count);
            Assert.Null(item.UpdateOperationId);
        });
        var preparedWarning = Assert.Single(
            preparedItems.Single(item => item.InstanceId == versionTwoInstance.Id).Warnings);
        Assert.Equal("active_durable_jobs", preparedWarning.Code);
        Assert.False(string.IsNullOrWhiteSpace(preparedWarning.Message));
        Assert.Equal(
            InstanceVariableUpdateOutcomes.Updated,
            Assert.Single(
                preparedItems.Single(item => item.InstanceId == versionOneInstance.Id).Plan,
                outcome => string.Equals(
                    outcome.Name,
                    "Score",
                    StringComparison.OrdinalIgnoreCase)).Outcome);
        Assert.Equal(
            InstanceVariableUpdateOutcomes.Added,
            Assert.Single(
                preparedItems.Single(item => item.InstanceId == versionTwoInstance.Id).Plan,
                outcome => string.Equals(
                    outcome.Name,
                    "score",
                    StringComparison.OrdinalIgnoreCase)).Outcome);

        using (var staleConfirm = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instance-variable-update-batches/{ready.Summary.Id}/confirm",
                   new ConfirmInstanceVariableUpdateBatchRequest(
                       ready.Summary.EligibleItemCount,
                       ready.Summary.IneligibleItemCount,
                       ready.Summary.WarningItemCount,
                       ready.Summary.UpdatedAt.AddTicks(-1)),
                   user: "confirmer"))
        {
            Assert.Equal(HttpStatusCode.Conflict, staleConfirm.StatusCode);
        }
        Assert.Empty(ExecuteJobs(await GetBatchAsync(ready.Summary.Id)));

        var confirmed = await ConfirmAsync(ready, "confirmer");
        Assert.Equal(InstanceVariableUpdateBatchStatuses.Queued, confirmed.Summary.Status);
        Assert.Equal(2, ExecuteJobs(confirmed).Count);

        // There is deliberately no stale-variable fence. This intervening write
        // turns the second item's prepared "added" plan into an actual update.
        await PatchDirectAsync(
            versionTwoInstance.Id,
            [new InstanceVariableWriteDto(
                "SCORE",
                JsonSerializer.SerializeToElement(4))]);

        var executeJobs = ExecuteJobs(confirmed);
        await ProcessJobAsync(executeJobs[0].JobId!.Value, fixture.Factory.Services);
        var partiallyExecuted = await GetBatchAsync(confirmed.Summary.Id);
        Assert.Equal(InstanceVariableUpdateBatchStatuses.Running, partiallyExecuted.Summary.Status);
        Assert.Equal(1, partiallyExecuted.Summary.SucceededItemCount);
        Assert.Equal(1, partiallyExecuted.Summary.QueuedItemCount);

        await ProcessJobAsync(executeJobs[1].JobId!.Value, fixture.Factory.Services);
        var completed = await GetBatchAsync(confirmed.Summary.Id);
        Assert.Equal(InstanceVariableUpdateBatchStatuses.Completed, completed.Summary.Status);
        Assert.Equal(2, completed.Summary.SucceededItemCount);
        Assert.Equal(0, completed.Summary.QueuedItemCount);

        var completedItems = await GetItemsAsync(completed.Summary.Id);
        Assert.All(completedItems, item =>
        {
            Assert.Equal(InstanceVariableUpdateBatchItemStatuses.Succeeded, item.Status);
            Assert.NotNull(item.UpdateOperationId);
            Assert.NotNull(item.Result);
        });
        var executionWarnings = completedItems
            .Single(item => item.InstanceId == versionTwoInstance.Id)
            .Warnings;
        Assert.Equal(2, executionWarnings.Count);
        Assert.All(executionWarnings, warning =>
        {
            Assert.Equal("active_durable_jobs", warning.Code);
            Assert.False(string.IsNullOrWhiteSpace(warning.Message));
        });
        var driftedResult = completedItems
            .Single(item => item.InstanceId == versionTwoInstance.Id)
            .Result!.Value.Deserialize<UpdateInstanceVariablesResultDto>(JsonOptions)
            ?? throw new InvalidOperationException("The batch item result was empty.");
        Assert.Equal(
            InstanceVariableUpdateOutcomes.Updated,
            Assert.Single(
                driftedResult.Variables,
                outcome => string.Equals(
                    outcome.Name,
                    "SCORE",
                    StringComparison.OrdinalIgnoreCase)).Outcome);

        await using var db = fixture.CreateDbContext();
        var audits = await db.InstanceVariableUpdates
            .Where(audit => audit.BatchId == completed.Summary.Id)
            .OrderBy(audit => audit.Id)
            .ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, audit => Assert.Equal("confirmer", audit.PerformedBy));
        Assert.Equal(
            completedItems.Select(item => item.Id).Order(),
            audits.Select(audit => audit.BatchItemId!.Value).Order());
        Assert.Equal(4, await db.InstanceVariables.CountAsync(variable =>
            variable.InstanceVariableUpdateAuditId != null
            && audits.Select(audit => audit.Id)
                .Contains(variable.InstanceVariableUpdateAuditId.Value)));
    }

    [Fact]
    public async Task AllMatching_FreezesExclusionsAndCancellationStopsEveryUnstartedItem()
    {
        var family = await CreateFamilyAsync("variable-batch-all-matching");
        var selectedOne = await StartAsync(family.VersionOne.Id);
        var selectedTwo = await StartAsync(family.VersionOne.Id);
        var excluded = await StartAsync(family.VersionTwo.Id);
        var filter = new InstanceVariableUpdateCandidateFilterDto
        {
            WorkflowKey = $"  {family.VersionOne.Definition.Id}  "
        };
        var created = await CreateBatchAsync(
            new CreateInstanceVariableUpdateBatchRequest(
                $"  {family.VersionOne.Definition.Id}  ",
                [new InstanceVariableWriteDto(
                    "frozenValue",
                    JsonSerializer.SerializeToElement(true))],
                "freeze all running instances",
                new InstanceVariableUpdateBatchSelectionDto(
                    InstanceVariableUpdateBatchSelectionModes.AllMatching,
                    null,
                    filter,
                    [excluded.Id, excluded.Id]),
                $"all-matching-variable-batch-{Guid.NewGuid():N}"),
            "freezer");
        Assert.Equal(2, created.Summary.TotalItemCount);
        Assert.False(created.Selection.TryGetProperty("Mode", out _));
        Assert.Equal(
            InstanceVariableUpdateBatchSelectionModes.AllMatching,
            created.Selection.GetProperty("mode").GetString());
        Assert.Equal(
            family.VersionOne.Definition.Id,
            created.Selection.GetProperty("filter")
                .GetProperty("workflowKey")
                .GetString());
        Assert.Equal(
            [excluded.Id],
            created.Selection.GetProperty("excludedInstanceIds")
                .EnumerateArray()
                .Select(value => value.GetInt64())
                .ToArray());

        var lateMatch = await StartAsync(family.VersionOne.Id);
        foreach (var job in PrepareJobs(created))
        {
            await ProcessJobAsync(job.JobId!.Value, fixture.Factory.Services);
        }
        var ready = await GetBatchAsync(created.Summary.Id);
        Assert.Equal(InstanceVariableUpdateBatchStatuses.Ready, ready.Summary.Status);
        var items = await GetItemsAsync(ready.Summary.Id);
        Assert.Equal(
            new[] { selectedOne.Id, selectedTwo.Id }.Order(),
            items.Select(item => item.InstanceId).Order());
        Assert.DoesNotContain(items, item => item.InstanceId == excluded.Id);
        Assert.DoesNotContain(items, item => item.InstanceId == lateMatch.Id);

        var confirmed = await ConfirmAsync(ready, "freezer");
        Assert.Equal(2, confirmed.Summary.QueuedItemCount);
        using var cancelResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/instance-variable-update-batches/{confirmed.Summary.Id}/cancel",
            new CancelInstanceVariableUpdateBatchRequest("operator cancelled before execution"),
            user: "freezer");
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
        var cancelled = await ReadAsync<InstanceVariableUpdateBatchDetailDto>(cancelResponse);
        Assert.Equal(InstanceVariableUpdateBatchStatuses.Cancelled, cancelled.Summary.Status);
        Assert.Equal(2, cancelled.Summary.CancelledItemCount);
        Assert.Equal(0, cancelled.Summary.QueuedItemCount);
        Assert.NotNull(cancelled.Summary.CompletedAt);
        Assert.All(await GetItemsAsync(cancelled.Summary.Id), item =>
            Assert.Equal(InstanceVariableUpdateBatchItemStatuses.Cancelled, item.Status));

        foreach (var job in ExecuteJobs(confirmed))
        {
            await ProcessJobAsync(job.JobId!.Value, fixture.Factory.Services);
        }
        Assert.Empty(await GetBatchAuditsAsync(cancelled.Summary.Id));
    }

    [Fact]
    public async Task ProcessorRetry_DoesNotRepeatSuccessfulSiblingAndFinalAttemptFailsOnlyPoisonItem()
    {
        var family = await CreateFamilyAsync("variable-batch-retry");
        var poison = await StartAsync(family.VersionOne.Id);
        var healthy = await StartAsync(family.VersionOne.Id);
        var batch = await CreateBatchAsync(
            new CreateInstanceVariableUpdateBatchRequest(
                family.VersionOne.Definition.Id,
                [new InstanceVariableWriteDto(
                    "retryValue",
                    JsonSerializer.SerializeToElement(7))],
                "retry exactly once",
                ExplicitSelection(poison.Id, healthy.Id),
                $"variable-batch-retry-{Guid.NewGuid():N}"),
            "retry-admin");
        await ProcessJobAsync(
            Assert.Single(PrepareJobs(batch)).JobId!.Value,
            fixture.Factory.Services);
        batch = await GetBatchAsync(batch.Summary.Id);
        batch = await ConfirmAsync(batch, "retry-admin");
        var executionJob = Assert.Single(ExecuteJobs(batch));

        var state = new SelectiveThrowingExecutorState(
            poison.Id,
            new InvalidOperationException("poison update failed"));
        await using var factory = CreateSelectiveExecutorFactory(state);
        await using var scope = factory.Services.CreateAsyncScope();
        var lease = await LeaseSpecificJobAsync(
            executionJob.JobId!.Value,
            scope.ServiceProvider);
        var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(lease, CancellationToken.None));
        var retryable = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(InstanceVariableUpdateBatchStatuses.Running, retryable.Summary.Status);
        Assert.Equal(1, retryable.Summary.SucceededItemCount);
        Assert.Equal(1, retryable.Summary.QueuedItemCount);
        var firstItems = await GetItemsAsync(batch.Summary.Id);
        var succeeded = firstItems.Single(item => item.InstanceId == healthy.Id);
        Assert.Equal(InstanceVariableUpdateBatchItemStatuses.Succeeded, succeeded.Status);
        var operationId = succeeded.UpdateOperationId;
        Assert.NotNull(operationId);
        Assert.Equal(1, state.DelegatedAttemptCount);
        Assert.Equal(1, state.PoisonAttemptCount);
        Assert.Single(await GetBatchAuditsAsync(batch.Summary.Id));

        await processor.ProcessAsync(
            lease with { AttemptNumber = lease.Job.MaxAttempts },
            CancellationToken.None);
        var completed = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(
            InstanceVariableUpdateBatchStatuses.CompletedWithIssues,
            completed.Summary.Status);
        Assert.Equal(1, completed.Summary.SucceededItemCount);
        Assert.Equal(1, completed.Summary.FailedItemCount);
        var finalItems = await GetItemsAsync(batch.Summary.Id);
        var finalHealthy = finalItems.Single(item => item.InstanceId == healthy.Id);
        var failedPoison = finalItems.Single(item => item.InstanceId == poison.Id);
        Assert.Equal(operationId, finalHealthy.UpdateOperationId);
        Assert.Equal(InstanceVariableUpdateBatchItemStatuses.Failed, failedPoison.Status);
        Assert.Equal("unexpected_processing_error", failedPoison.ErrorCode);
        Assert.Equal("poison update failed", failedPoison.ErrorDescription);
        Assert.Equal(1, state.DelegatedAttemptCount);
        Assert.Equal(2, state.PoisonAttemptCount);
        Assert.Single(await GetBatchAuditsAsync(batch.Summary.Id));
    }

    private async Task<VersionFamily> CreateFamilyAsync(string label)
    {
        var model = CreateModel(label);
        var versionOne = await CreateWorkflowAsync(model);
        var versionTwoModel = Clone(model);
        versionTwoModel.Name += " v2";
        var versionTwo = await CreateWorkflowAsync(versionTwoModel);
        return new VersionFamily(versionOne, versionTwo);
    }

    private async Task<WorkflowDetailDto> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<WorkflowDetailDto>(response);
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task PatchDirectAsync(
        long instanceId,
        IReadOnlyList<InstanceVariableWriteDto> variables)
    {
        using var response = await SendAsync(
            HttpMethod.Patch,
            $"/api/instances/{instanceId}/variables",
            new UpdateInstanceVariablesRequest(variables, null, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task AddDeferredOpenJobAsync(
        WorkflowDetailDto workflow,
        long instanceId)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = fixture.CreateDbContext();
        db.WorkflowJobs.Add(new WorkflowJobEntity
        {
            InstanceId = instanceId,
            WorkflowDefinitionId = workflow.Id,
            WorkflowKey = workflow.WorkflowKey,
            ActivationId = Guid.NewGuid(),
            NodeId = 2,
            NodeName = "Deferred active job warning",
            NodeType = BpmnFlowNodeTypes.ServiceTask,
            Kind = WorkflowJobKinds.AsyncBefore,
            QueueClass = WorkflowJobClasses.Activity,
            Phase = "before",
            Status = WorkflowJobStatuses.Queued,
            Priority = 0,
            MaxAttempts = 1,
            FailureHandling = WorkflowJobFailureHandling.BoundaryFirst,
            RetryDelays = [],
            DueAt = now.AddDays(1),
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private async Task<InstanceVariableUpdateBatchDetailDto> CreateBatchAsync(
        CreateInstanceVariableUpdateBatchRequest request,
        string user)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instance-variable-update-batches",
            request,
            user);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await ReadAsync<InstanceVariableUpdateBatchDetailDto>(response);
    }

    private async Task<InstanceVariableUpdateBatchDetailDto> GetBatchAsync(long batchId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instance-variable-update-batches/{batchId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceVariableUpdateBatchDetailDto>(response);
    }

    private async Task<IReadOnlyList<InstanceVariableUpdateBatchItemDto>> GetItemsAsync(
        long batchId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instance-variable-update-batches/{batchId}/items?page=1&pageSize=200");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<PagedResult<InstanceVariableUpdateBatchItemDto>>(response)).Items;
    }

    private async Task<InstanceVariableUpdateBatchDetailDto> ConfirmAsync(
        InstanceVariableUpdateBatchDetailDto ready,
        string user)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/instance-variable-update-batches/{ready.Summary.Id}/confirm",
            new ConfirmInstanceVariableUpdateBatchRequest(
                ready.Summary.EligibleItemCount,
                ready.Summary.IneligibleItemCount,
                ready.Summary.WarningItemCount,
                ready.Summary.UpdatedAt),
            user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceVariableUpdateBatchDetailDto>(response);
    }

    private async Task<IReadOnlyList<long>> GetBatchAuditsAsync(long batchId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.InstanceVariableUpdates
            .Where(audit => audit.BatchId == batchId)
            .OrderBy(audit => audit.Id)
            .Select(audit => audit.Id)
            .ToListAsync();
    }

    private static List<InstanceVariableUpdateBatchJobLinkDto> PrepareJobs(
        InstanceVariableUpdateBatchDetailDto batch) => batch.Jobs
        .Where(job => job.Phase == InstanceVariableUpdateBatchPhases.Prepare)
        .OrderBy(job => job.Workflow.Id)
        .ToList();

    private static List<InstanceVariableUpdateBatchJobLinkDto> ExecuteJobs(
        InstanceVariableUpdateBatchDetailDto batch) => batch.Jobs
        .Where(job => job.Phase == InstanceVariableUpdateBatchPhases.Execute)
        .OrderBy(job => job.Workflow.Id)
        .ToList();

    private async Task ProcessJobAsync(long jobId, IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var lease = await LeaseSpecificJobAsync(jobId, scope.ServiceProvider);
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
            .ProcessAsync(lease, CancellationToken.None);
    }

    private async Task<WorkflowJobLeaseRecord> LeaseSpecificJobAsync(
        long jobId,
        IServiceProvider services)
    {
        await using (var db = fixture.CreateDbContext())
        {
            var job = await db.WorkflowJobs.SingleAsync(candidate => candidate.Id == jobId);
            job.Priority = int.MaxValue;
            job.DueAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        var repository = services.GetRequiredService<IWorkflowJobRepository>();
        var leases = await repository.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"instance-variable-update-test-{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: 1,
                MaxPerInstance: 1,
                LeaseDuration: TimeSpan.FromMinutes(2)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        return lease;
    }

    private WebApplicationFactory<Program> CreateSelectiveExecutorFactory(
        SelectiveThrowingExecutorState state) =>
        fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceVariableUpdateExecutor>();
                services.AddSingleton(state);
                services.AddScoped<IInstanceVariableUpdateExecutor>(provider =>
                    new SelectiveThrowingExecutor(
                        provider.GetRequiredService<InstanceVariableUpdateService>(),
                        provider.GetRequiredService<SelectiveThrowingExecutorState>()));
            }));

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "variable-batch-admin")
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, ["admin"]);
        request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        return fixture.Client.SendAsync(request);
    }

    private static InstanceVariableUpdateBatchSelectionDto ExplicitSelection(
        params long[] instanceIds) => new(
        InstanceVariableUpdateBatchSelectionModes.Explicit,
        instanceIds,
        null,
        null);

    private static WorkflowModel CreateModel(string label)
    {
        var key = $"{label}-{Guid.NewGuid():N}";
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
                    Type = BpmnFlowNodeTypes.StartEvent
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    Type = BpmnFlowNodeTypes.UserTask
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
                    Name = "Complete",
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
        WorkflowDetailDto VersionOne,
        WorkflowDetailDto VersionTwo);

    private sealed record EndpointContract(
        string Route,
        string Method,
        bool HasBody,
        IReadOnlyList<int> ResponseStatuses);

    private sealed class SelectiveThrowingExecutor(
        IInstanceVariableUpdateExecutor inner,
        SelectiveThrowingExecutorState state)
        : IInstanceVariableUpdateExecutor
    {
        public Task<InstanceVariableUpdateExecutionOutcome> ExecuteAsync(
            InstanceVariableUpdateExecutionRequest request,
            ActorContext actor,
            CancellationToken cancellationToken)
        {
            if (request.InstanceId == state.PoisonInstanceId)
            {
                state.RecordPoisonAttempt();
                return Task.FromException<InstanceVariableUpdateExecutionOutcome>(
                    state.Exception);
            }
            state.RecordDelegatedAttempt();
            return inner.ExecuteAsync(request, actor, cancellationToken);
        }
    }

    private sealed class SelectiveThrowingExecutorState(
        long poisonInstanceId,
        Exception exception)
    {
        private int poisonAttemptCount;
        private int delegatedAttemptCount;

        public long PoisonInstanceId { get; } = poisonInstanceId;
        public Exception Exception { get; } = exception;
        public int PoisonAttemptCount => Volatile.Read(ref poisonAttemptCount);
        public int DelegatedAttemptCount => Volatile.Read(ref delegatedAttemptCount);
        public void RecordPoisonAttempt() =>
            Interlocked.Increment(ref poisonAttemptCount);
        public void RecordDelegatedAttempt() =>
            Interlocked.Increment(ref delegatedAttemptCount);
    }
}
