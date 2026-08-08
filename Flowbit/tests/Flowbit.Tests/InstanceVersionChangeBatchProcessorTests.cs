using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVersionChangeBatchProcessorTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task UnexpectedExecutionFailure_RetriesQueuedItemAndFinalAttemptFailsOnlyItem()
    {
        var batch = await CreateQueuedBatchAsync("processor-final-failure");
        var executor = ControlledExecutor.Throwing(
            new InvalidOperationException("simulated transient infrastructure failure"));
        await using var factory = CreateExecutorFactory(executor);
        await using var scope = factory.Services.CreateAsyncScope();
        var lease = await LeaseSpecificJobAsync(
            batch.ExecutionJobId!.Value,
            scope.ServiceProvider);
        var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(lease, CancellationToken.None));

        var retryable = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(InstanceVersionChangeBatchStatuses.Running, retryable.Summary.Status);
        Assert.Equal(1, retryable.Summary.QueuedItemCount);
        Assert.Equal(0, retryable.Summary.FailedItemCount);
        var started = Assert.Single(await GetItemsAsync(batch.Summary.Id));
        Assert.Equal(InstanceVersionChangeBatchItemStatuses.Queued, started.Status);
        Assert.NotNull(started.StartedAt);
        Assert.Null(started.CompletedAt);

        var finalLease = lease with { AttemptNumber = lease.Job.MaxAttempts };
        await processor.ProcessAsync(finalLease, CancellationToken.None);

        var failed = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(
            InstanceVersionChangeBatchStatuses.CompletedWithIssues,
            failed.Summary.Status);
        Assert.Equal(0, failed.Summary.QueuedItemCount);
        Assert.Equal(1, failed.Summary.FailedItemCount);
        Assert.NotNull(failed.Summary.CompletedAt);
        var failedItem = Assert.Single(await GetItemsAsync(batch.Summary.Id));
        Assert.Equal(InstanceVersionChangeBatchItemStatuses.Failed, failedItem.Status);
        Assert.Equal("unexpected_processing_error", failedItem.ErrorCode);
        Assert.Equal(
            "simulated transient infrastructure failure",
            failedItem.ErrorDescription);
        Assert.NotNull(failedItem.StartedAt);
        Assert.NotNull(failedItem.CompletedAt);
    }

    [Fact]
    public async Task PoisonItem_RetriesWithoutStarvingLaterItem_ThenFailsIndependently()
    {
        var family = await CreateFamilyAsync("processor-poison-isolation");
        var poison = await StartAsync(family.Source.Id);
        var healthy = await StartAsync(family.Source.Id);
        using var create = await SendAsync(
            HttpMethod.Post,
            "/api/instance-version-change-batches",
            new CreateInstanceVersionChangeBatchRequest(
                family.Source.Id,
                family.Target.Id,
                "isolate one poison item",
                ExplicitSelection(poison.Id, healthy.Id),
                $"poison-isolation-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var batch = await ReadAsync<InstanceVersionChangeBatchDetailDto>(create);
        await ProcessJobAsync(batch.PreparationJobId!.Value, fixture.Factory.Services);
        batch = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(2, batch.Summary.EligibleItemCount);

        using var confirm = await SendAsync(
            HttpMethod.Post,
            $"/api/instance-version-change-batches/{batch.Summary.Id}/confirm",
            new ConfirmInstanceVersionChangeBatchRequest(
                batch.Summary.EligibleItemCount,
                batch.Summary.IneligibleItemCount,
                batch.Summary.WarningItemCount,
                batch.Summary.UpdatedAt));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        batch = await ReadAsync<InstanceVersionChangeBatchDetailDto>(confirm);

        var executor = new SelectiveThrowingExecutorState(
            poison.Id,
            new InvalidOperationException("poison item always fails"));
        await using var factory = CreateSelectiveExecutorFactory(executor);
        await using var scope = factory.Services.CreateAsyncScope();
        var lease = await LeaseSpecificJobAsync(
            batch.ExecutionJobId!.Value,
            scope.ServiceProvider);
        var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(lease, CancellationToken.None));

        var retryable = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(InstanceVersionChangeBatchStatuses.Running, retryable.Summary.Status);
        Assert.Equal(1, retryable.Summary.QueuedItemCount);
        Assert.Equal(1, retryable.Summary.SucceededItemCount);
        Assert.Equal(0, retryable.Summary.FailedItemCount);
        var afterFirstAttempt = await GetItemsAsync(batch.Summary.Id);
        var queuedPoison = Assert.Single(
            afterFirstAttempt,
            item => item.InstanceId == poison.Id);
        Assert.Equal(InstanceVersionChangeBatchItemStatuses.Queued, queuedPoison.Status);
        Assert.NotNull(queuedPoison.StartedAt);
        var succeededHealthy = Assert.Single(
            afterFirstAttempt,
            item => item.InstanceId == healthy.Id);
        Assert.Equal(
            InstanceVersionChangeBatchItemStatuses.Succeeded,
            succeededHealthy.Status);
        Assert.NotNull(succeededHealthy.VersionChangeAuditId);
        Assert.Equal(1, executor.PoisonAttemptCount);
        Assert.Equal(1, executor.DelegatedAttemptCount);

        var finalLease = lease with { AttemptNumber = lease.Job.MaxAttempts };
        await processor.ProcessAsync(finalLease, CancellationToken.None);

        var completed = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(
            InstanceVersionChangeBatchStatuses.CompletedWithIssues,
            completed.Summary.Status);
        Assert.Equal(0, completed.Summary.QueuedItemCount);
        Assert.Equal(1, completed.Summary.SucceededItemCount);
        Assert.Equal(1, completed.Summary.FailedItemCount);
        Assert.Equal(0, completed.Summary.SkippedItemCount);
        var finalItems = await GetItemsAsync(batch.Summary.Id);
        var failedPoison = Assert.Single(
            finalItems,
            item => item.InstanceId == poison.Id);
        Assert.Equal(InstanceVersionChangeBatchItemStatuses.Failed, failedPoison.Status);
        Assert.Equal("unexpected_processing_error", failedPoison.ErrorCode);
        Assert.Equal("poison item always fails", failedPoison.ErrorDescription);
        var finalHealthy = Assert.Single(
            finalItems,
            item => item.InstanceId == healthy.Id);
        Assert.Equal(
            InstanceVersionChangeBatchItemStatuses.Succeeded,
            finalHealthy.Status);
        Assert.Equal(succeededHealthy.VersionChangeAuditId, finalHealthy.VersionChangeAuditId);
        Assert.Equal(2, executor.PoisonAttemptCount);
        Assert.Equal(1, executor.DelegatedAttemptCount);

        await using var db = fixture.CreateDbContext();
        var audits = await db.WorkflowInstanceVersionChanges
            .Where(change => change.BatchId == batch.Summary.Id)
            .ToListAsync();
        var audit = Assert.Single(audits);
        Assert.Equal(healthy.Id, audit.InstanceId);
        Assert.Equal(finalHealthy.Id, audit.BatchItemId);
    }

    [Fact]
    public async Task Cancellation_PreservesStartedItemUntilItIsClassifiedThenFinalizesBatch()
    {
        var batch = await CreateQueuedBatchAsync("processor-in-flight-cancel");
        var executor = ControlledExecutor.Blocking(
            new InstanceVersionChangeBatchExecutionOutcome(
                false,
                null,
                "stale_since_preparation",
                "The instance changed while execution was starting.",
                [],
                []));
        await using var factory = CreateExecutorFactory(executor);
        await using var scope = factory.Services.CreateAsyncScope();
        var lease = await LeaseSpecificJobAsync(
            batch.ExecutionJobId!.Value,
            scope.ServiceProvider);
        var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();

        var processing = processor.ProcessAsync(lease, CancellationToken.None);
        await executor.WaitUntilEnteredAsync();
        try
        {
            using var cancellation = await SendAsync(
                HttpMethod.Post,
                $"/api/instance-version-change-batches/{batch.Summary.Id}/cancel",
                new CancelInstanceVersionChangeBatchRequest("operator stopped the remainder"));
            Assert.Equal(HttpStatusCode.OK, cancellation.StatusCode);
            var cancelling = await ReadAsync<InstanceVersionChangeBatchDetailDto>(cancellation);
            Assert.Equal(InstanceVersionChangeBatchStatuses.Cancelled, cancelling.Summary.Status);
            Assert.Equal(1, cancelling.Summary.QueuedItemCount);
            Assert.Null(cancelling.Summary.CompletedAt);
        }
        finally
        {
            executor.Release();
        }

        await processing;
        var cancelled = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(InstanceVersionChangeBatchStatuses.Cancelled, cancelled.Summary.Status);
        Assert.Equal(0, cancelled.Summary.QueuedItemCount);
        Assert.Equal(1, cancelled.Summary.SkippedItemCount);
        Assert.NotNull(cancelled.Summary.CompletedAt);
        var item = Assert.Single(await GetItemsAsync(batch.Summary.Id));
        Assert.Equal(InstanceVersionChangeBatchItemStatuses.Skipped, item.Status);
        Assert.NotNull(item.StartedAt);
        Assert.NotNull(item.CompletedAt);
    }

    [Fact]
    public async Task ExecutionTimeDrift_SkipsChangedInstanceAndCommitsOtherInstanceIndependently()
    {
        var family = await CreateFamilyAsync("processor-partial-drift");
        var changedDirectly = await StartAsync(family.Source.Id);
        var unchanged = await StartAsync(family.Source.Id);
        using var create = await SendAsync(
            HttpMethod.Post,
            "/api/instance-version-change-batches",
            new CreateInstanceVersionChangeBatchRequest(
                family.Source.Id,
                family.Target.Id,
                "partial execution drift",
                ExplicitSelection(changedDirectly.Id, unchanged.Id),
                $"partial-drift-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var batch = await ReadAsync<InstanceVersionChangeBatchDetailDto>(create);
        await ProcessJobAsync(batch.PreparationJobId!.Value, fixture.Factory.Services);
        batch = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(2, batch.Summary.EligibleItemCount);

        using var confirm = await SendAsync(
            HttpMethod.Post,
            $"/api/instance-version-change-batches/{batch.Summary.Id}/confirm",
            new ConfirmInstanceVersionChangeBatchRequest(
                batch.Summary.EligibleItemCount,
                batch.Summary.IneligibleItemCount,
                batch.Summary.WarningItemCount,
                batch.Summary.UpdatedAt));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        batch = await ReadAsync<InstanceVersionChangeBatchDetailDto>(confirm);

        using var directChange = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{changedDirectly.Id}/version-change",
            new ChangeInstanceVersionRequest(
                family.Target.Id,
                family.Source.Id,
                changedDirectly.UpdatedAt,
                "direct change raced the queued batch"));
        Assert.Equal(HttpStatusCode.OK, directChange.StatusCode);

        await ProcessJobAsync(batch.ExecutionJobId!.Value, fixture.Factory.Services);
        var completed = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(
            InstanceVersionChangeBatchStatuses.CompletedWithIssues,
            completed.Summary.Status);
        Assert.Equal(1, completed.Summary.SucceededItemCount);
        Assert.Equal(1, completed.Summary.SkippedItemCount);
        Assert.Equal(1, completed.Summary.StaleItemCount);
        Assert.Equal(0, completed.Summary.BlockedItemCount);
        Assert.Equal(0, completed.Summary.IneligibleItemCount);
        Assert.Equal(0, completed.Summary.FailedItemCount);

        var items = await GetItemsAsync(batch.Summary.Id);
        var drifted = Assert.Single(items, item => item.InstanceId == changedDirectly.Id);
        Assert.Equal(InstanceVersionChangeBatchItemStatuses.Skipped, drifted.Status);
        Assert.Equal("stale_since_preparation", drifted.ErrorCode);
        var succeeded = Assert.Single(items, item => item.InstanceId == unchanged.Id);
        Assert.Equal(InstanceVersionChangeBatchItemStatuses.Succeeded, succeeded.Status);
        Assert.NotNull(succeeded.VersionChangeAuditId);

        await using var db = fixture.CreateDbContext();
        var correlated = await db.WorkflowInstanceVersionChanges
            .Where(change => change.BatchId == batch.Summary.Id)
            .ToListAsync();
        var audit = Assert.Single(correlated);
        Assert.Equal(unchanged.Id, audit.InstanceId);
        Assert.Equal(succeeded.Id, audit.BatchItemId);
    }

    [Fact]
    public async Task Execution_ReconstructsConfirmerIdentityRolesAndAllowedClaims()
    {
        var family = await CreateFamilyAsync("processor-confirmer-snapshot");
        var instance = await StartAsync(family.Source.Id);
        using var create = await SendAsync(
            HttpMethod.Post,
            "/api/instance-version-change-batches",
            new CreateInstanceVersionChangeBatchRequest(
                family.Source.Id,
                family.Target.Id,
                "confirmer snapshot",
                ExplicitSelection(instance.Id),
                $"confirmer-snapshot-{Guid.NewGuid():N}"),
            user: "batch-preparer",
            roles: ["admin", "PreparationRole"],
            department: "preparation-department");
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var batch = await ReadAsync<InstanceVersionChangeBatchDetailDto>(create);
        await ProcessJobAsync(batch.PreparationJobId!.Value, fixture.Factory.Services);
        batch = await GetBatchAsync(batch.Summary.Id);

        using var confirm = await SendAsync(
            HttpMethod.Post,
            $"/api/instance-version-change-batches/{batch.Summary.Id}/confirm",
            new ConfirmInstanceVersionChangeBatchRequest(
                batch.Summary.EligibleItemCount,
                batch.Summary.IneligibleItemCount,
                batch.Summary.WarningItemCount,
                batch.Summary.UpdatedAt),
            user: "batch-confirmer",
            roles: ["admin", "ReleaseManager"],
            department: "release-department");
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        batch = await ReadAsync<InstanceVersionChangeBatchDetailDto>(confirm);

        var executor = ControlledExecutor.Returning(
            new InstanceVersionChangeBatchExecutionOutcome(
                false,
                null,
                "test_classification",
                "The actor snapshot was captured.",
                [],
                []));
        await using var factory = CreateExecutorFactory(executor);
        await ProcessJobAsync(batch.ExecutionJobId!.Value, factory.Services);

        var observed = Assert.IsType<ActorContext>(executor.ObservedActor);
        Assert.Equal("batch-confirmer", observed.User);
        Assert.Contains("admin", observed.Roles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ReleaseManager", observed.Roles, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "PreparationRole",
            observed.Roles,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal("release-department", observed.Claims["department"]);
        Assert.DoesNotContain("preparation-department", observed.Claims.Values);
    }

    [Fact]
    public async Task Create_RejectsInvalidPairsSelectionsReasonsAndConfiguredLimit()
    {
        var family = await CreateFamilyAsync("service-negative");
        var other = await CreateWorkflowAsync(CreateModel("service-negative-other"));
        var instance = await StartAsync(family.Source.Id);
        var actor = new ActorContext(
            "validation-admin",
            ["admin"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider
            .GetRequiredService<IInstanceVersionChangeBatchService>();
        var validSelection = ExplicitSelection(instance.Id);

        await Assert.ThrowsAsync<WorkflowDomainException>(() => service.CreateAsync(
            new CreateInstanceVersionChangeBatchRequest(
                family.Source.Id,
                family.Target.Id,
                "   ",
                validSelection,
                null),
            actor,
            CancellationToken.None));

        await Assert.ThrowsAsync<WorkflowDomainException>(() => service.CreateAsync(
            new CreateInstanceVersionChangeBatchRequest(
                family.Source.Id,
                family.Source.Id,
                "same version is invalid",
                validSelection,
                null),
            actor,
            CancellationToken.None));

        await Assert.ThrowsAsync<WorkflowDomainException>(() => service.CreateAsync(
            new CreateInstanceVersionChangeBatchRequest(
                family.Source.Id,
                other.Id,
                "cross-family is invalid",
                validSelection,
                null),
            actor,
            CancellationToken.None));

        await Assert.ThrowsAsync<WorkflowDomainException>(() => service.CreateAsync(
            new CreateInstanceVersionChangeBatchRequest(
                family.Source.Id,
                family.Target.Id,
                "filter must use the exact source",
                new InstanceVersionChangeBatchSelectionDto(
                    InstanceVersionChangeBatchSelectionModes.AllMatching,
                    null,
                    new InstanceVersionChangeCandidateFilterDto
                    {
                        SourceWorkflowId = other.Id
                    },
                    null),
                null),
            actor,
            CancellationToken.None));

        var overLimit = Enumerable.Range(
                1,
                InstanceVersionChangeBatchConstraints.MaxBatchInstances + 1)
            .Select(value => (long)value)
            .ToArray();
        await Assert.ThrowsAsync<WorkflowDomainException>(() => service.CreateAsync(
            new CreateInstanceVersionChangeBatchRequest(
                family.Source.Id,
                family.Target.Id,
                "selection is over the hard cap",
                new InstanceVersionChangeBatchSelectionDto(
                    InstanceVersionChangeBatchSelectionModes.Explicit,
                    overLimit,
                    null,
                    null),
                null),
            actor,
            CancellationToken.None));
    }

    private async Task<InstanceVersionChangeBatchDetailDto> CreateQueuedBatchAsync(
        string label)
    {
        var family = await CreateFamilyAsync(label);
        var instance = await StartAsync(family.Source.Id);
        using var create = await SendAsync(
            HttpMethod.Post,
            "/api/instance-version-change-batches",
            new CreateInstanceVersionChangeBatchRequest(
                family.Source.Id,
                family.Target.Id,
                "processor behavior test",
                ExplicitSelection(instance.Id),
                $"{label}-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var batch = await ReadAsync<InstanceVersionChangeBatchDetailDto>(create);
        Assert.NotNull(batch.PreparationJobId);
        await ProcessJobAsync(batch.PreparationJobId.Value, fixture.Factory.Services);
        batch = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(InstanceVersionChangeBatchStatuses.Ready, batch.Summary.Status);
        Assert.Equal(1, batch.Summary.EligibleItemCount);

        using var confirm = await SendAsync(
            HttpMethod.Post,
            $"/api/instance-version-change-batches/{batch.Summary.Id}/confirm",
            new ConfirmInstanceVersionChangeBatchRequest(
                batch.Summary.EligibleItemCount,
                batch.Summary.IneligibleItemCount,
                batch.Summary.WarningItemCount,
                batch.Summary.UpdatedAt));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        batch = await ReadAsync<InstanceVersionChangeBatchDetailDto>(confirm);
        Assert.Equal(InstanceVersionChangeBatchStatuses.Queued, batch.Summary.Status);
        Assert.NotNull(batch.ExecutionJobId);
        return batch;
    }

    private async Task<VersionFamily> CreateFamilyAsync(string label)
    {
        var sourceModel = CreateModel(label);
        var source = await CreateWorkflowAsync(sourceModel);
        var targetModel = Clone(sourceModel);
        targetModel.Name = $"{sourceModel.Name} target";
        var target = await CreateWorkflowAsync(targetModel);
        return new VersionFamily(source, target);
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

    private async Task<InstanceVersionChangeBatchDetailDto> GetBatchAsync(long batchId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instance-version-change-batches/{batchId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceVersionChangeBatchDetailDto>(response);
    }

    private async Task<IReadOnlyList<InstanceVersionChangeBatchItemDto>> GetItemsAsync(
        long batchId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instance-version-change-batches/{batchId}/items?page=1&pageSize=200");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<PagedResult<InstanceVersionChangeBatchItemDto>>(response)).Items;
    }

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
            var job = await db.WorkflowJobs.SingleAsync(entity => entity.Id == jobId);
            job.Priority = int.MaxValue;
            job.DueAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        var repository = services.GetRequiredService<IWorkflowJobRepository>();
        var leases = await repository.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"instance-version-change-processor-test-{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: 1,
                MaxPerInstance: 1,
                LeaseDuration: TimeSpan.FromMinutes(2)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        return lease;
    }

    private WebApplicationFactory<Program> CreateExecutorFactory(
        ControlledExecutor executor) =>
        fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceVersionChangeBatchExecutor>();
                services.AddSingleton<IInstanceVersionChangeBatchExecutor>(executor);
            }));

    private WebApplicationFactory<Program> CreateSelectiveExecutorFactory(
        SelectiveThrowingExecutorState state) =>
        fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceVersionChangeBatchExecutor>();
                services.AddSingleton(state);
                services.AddScoped<IInstanceVersionChangeBatchExecutor>(provider =>
                    new SelectiveThrowingExecutor(
                        provider.GetRequiredService<WorkflowEngineService>(),
                        provider.GetRequiredService<SelectiveThrowingExecutorState>()));
            }));

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "processor-admin",
        string[]? roles = null,
        string? department = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles ?? ["admin"]);
        request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        if (!string.IsNullOrWhiteSpace(department))
        {
            request.Headers.TryAddWithoutValidation(
                "X-Test-Claim-department",
                department);
        }
        return fixture.Client.SendAsync(request);
    }

    private static InstanceVersionChangeBatchSelectionDto ExplicitSelection(
        params long[] instanceIds) =>
        new(
            InstanceVersionChangeBatchSelectionModes.Explicit,
            instanceIds,
            null,
            null);

    private static WorkflowJobFence Fence(WorkflowJobLeaseRecord lease) =>
        new(
            lease.Job.Id,
            lease.Job.WorkerId
                ?? throw new InvalidOperationException("Leased test job has no worker id."),
            lease.LeaseToken,
            lease.LeaseGeneration);

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
                    Name = "Begin review",
                    SourceRef = 1,
                    TargetRef = 2
                },
                new SequenceFlowModel
                {
                    Id = 20,
                    Name = "Complete review",
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

    private sealed class ControlledExecutor : IInstanceVersionChangeBatchExecutor
    {
        private readonly Exception? exception;
        private readonly InstanceVersionChangeBatchExecutionOutcome? outcome;
        private readonly bool blocks;
        private readonly TaskCompletionSource entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private ControlledExecutor(
            Exception? exception,
            InstanceVersionChangeBatchExecutionOutcome? outcome,
            bool blocks)
        {
            this.exception = exception;
            this.outcome = outcome;
            this.blocks = blocks;
        }

        public static ControlledExecutor Throwing(Exception exception) =>
            new(exception, null, false);

        public static ControlledExecutor Blocking(
            InstanceVersionChangeBatchExecutionOutcome outcome) =>
            new(null, outcome, true);

        public static ControlledExecutor Returning(
            InstanceVersionChangeBatchExecutionOutcome outcome) =>
            new(null, outcome, false);

        public ActorContext? ObservedActor { get; private set; }

        public async Task<InstanceVersionChangeBatchExecutionOutcome>
            ExecuteInstanceVersionChangeBatchItemAsync(
                InstanceVersionChangeBatchExecutionRequest request,
                ActorContext actor,
                CancellationToken cancellationToken)
        {
            ObservedActor = actor;
            entered.TrySetResult();
            if (blocks)
            {
                await released.Task.WaitAsync(cancellationToken);
            }
            if (exception is not null)
            {
                throw exception;
            }
            return outcome
                ?? throw new InvalidOperationException("No controlled executor result was configured.");
        }

        public Task WaitUntilEnteredAsync() =>
            entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Release() => released.TrySetResult();
    }

    private sealed class SelectiveThrowingExecutor(
        IInstanceVersionChangeBatchExecutor inner,
        SelectiveThrowingExecutorState state)
        : IInstanceVersionChangeBatchExecutor
    {
        public Task<InstanceVersionChangeBatchExecutionOutcome>
            ExecuteInstanceVersionChangeBatchItemAsync(
                InstanceVersionChangeBatchExecutionRequest request,
                ActorContext actor,
                CancellationToken cancellationToken)
        {
            if (request.InstanceId == state.PoisonInstanceId)
            {
                state.RecordPoisonAttempt();
                return Task.FromException<InstanceVersionChangeBatchExecutionOutcome>(
                    state.Exception);
            }

            state.RecordDelegatedAttempt();
            return inner.ExecuteInstanceVersionChangeBatchItemAsync(
                request,
                actor,
                cancellationToken);
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
