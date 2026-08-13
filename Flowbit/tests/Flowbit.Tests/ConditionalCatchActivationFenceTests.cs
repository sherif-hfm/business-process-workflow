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
public sealed class ConditionalCatchActivationFenceTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Conditional_scoped_interrupt_cannot_resurrect_writer_activation()
    {
        var workflowKey = $"conditional-activation-fence-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(
                CreateInterruptingWriterWorkflow(workflowKey));

            using var start = await SendAsync(
                HttpMethod.Post,
                "/api/instances?detail=full",
                new StartInstanceRequest(workflowId, null, null, null));

            Assert.Equal(HttpStatusCode.Created, start.StatusCode);
            var started = await ReadAsync<InstanceDetailDto>(start);
            Assert.Equal(WorkflowInstanceStatuses.Running, started.Status);

            await using var db = fixture.CreateDbContext();
            var tokens = await db.ExecutionTokens
                .Where(token => token.InstanceId == started.Id)
                .OrderBy(token => token.Id)
                .ToListAsync();
            Assert.Single(tokens, token =>
                token.NodeId == 6
                && token.Status == ExecutionTokenStatuses.Active);
            Assert.Single(tokens, token =>
                token.NodeId == 4
                && token.Status == ExecutionTokenStatuses.Cancelled);
            Assert.DoesNotContain(tokens, token => token.NodeId == 7);

            var history = await db.InstanceHistory
                .Where(item => item.InstanceId == started.Id)
                .ToListAsync();
            Assert.Single(history, item =>
                item.Note == InstanceHistoryNotes.ConditionalTriggered);
            Assert.Single(history, item => item.Note == "scopedInterrupt");
            Assert.DoesNotContain(history, item =>
                item.FromStepId == 4 && item.ToStepId == 7);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Immediate_atomic_condition_stages_successor_script_as_durable_work()
    {
        var workflowKey = $"conditional-durable-successor-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(
                CreateImmediateConditionalScriptWorkflow(workflowKey));

            using var start = await SendAsync(
                HttpMethod.Post,
                "/api/instances?detail=full",
                new StartInstanceRequest(workflowId, null, null, null));

            Assert.Equal(HttpStatusCode.Created, start.StatusCode);
            var started = await ReadAsync<InstanceDetailDto>(start);
            Assert.Equal(WorkflowInstanceStatuses.Running, started.Status);

            await using var db = fixture.CreateDbContext();
            var token = await db.ExecutionTokens.SingleAsync(item =>
                item.InstanceId == started.Id
                && item.Status == ExecutionTokenStatuses.Active);
            Assert.Equal(3, token.NodeId);
            Assert.Equal(ExecutionTokenWaitStates.AsyncBefore, token.WaitState);
            Assert.NotNull(token.WaitingJobId);
            Assert.False((await db.InstanceVariableCurrentValues.SingleAsync(item =>
                item.InstanceId == started.Id
                && item.VariableName == "written")).ValueJson.RootElement.GetBoolean());
            Assert.Single(await db.WorkflowJobs.Where(job =>
                job.InstanceId == started.Id
                && job.TokenId == token.Id
                && job.Kind == WorkflowJobKinds.AsyncBefore)
                .ToListAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    private async Task<long> CreateWorkflowAsync(WorkflowModel definition)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(definition, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, "test-admin", ["admin"]);
        return fixture.Client.SendAsync(request);
    }

    private async Task DeleteWorkflowAsync(string workflowKey)
    {
        await using var cleanup = fixture.CreateDbContext();
        await cleanup.WorkflowInstances
            .Where(instance => instance.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowDefinitions
            .Where(definition => definition.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
    }

    private static WorkflowModel CreateInterruptingWriterWorkflow(
        string workflowKey) =>
        new()
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "approved",
                    DataType = WorkflowVariableTypes.Boolean,
                    Required = true,
                    DefaultValue = JsonSerializer.SerializeToElement(false)
                }
            ],
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
                    Name = "Fork",
                    Type = BpmnFlowNodeTypes.ParallelGateway
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Wait for approval",
                    Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                    Conditional = new ConditionalDefinitionModel
                    {
                        Condition = "approved == true",
                        DeliveryMode = ConditionalEventDeliveryModes.Atomic
                    }
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Approve in script",
                    Type = BpmnFlowNodeTypes.ScriptTask,
                    ScriptFormat = ScriptFormats.NCalc,
                    Assignments =
                    [
                        new AssignmentModel
                        {
                            Variable = "approved",
                            Expression = "true"
                        }
                    ]
                },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Interrupt writer branch",
                    Type = BpmnFlowNodeTypes.ScopedInterruptEvent,
                    GatewayRef = 2
                },
                new FlowNodeModel
                {
                    Id = 6,
                    Name = "Recovered work",
                    Type = BpmnFlowNodeTypes.UserTask
                },
                new FlowNodeModel
                {
                    Id = 7,
                    Name = "Stale writer end",
                    Type = BpmnFlowNodeTypes.EndEvent
                },
                new FlowNodeModel
                {
                    Id = 8,
                    Name = "Recovered end",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 10, SourceRef = 1, TargetRef = 2 },
                // Lower flow id ensures the conditional activation is resting
                // before the sibling script writes its dependency.
                new SequenceFlowModel { Id = 20, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 30, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 40, SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 50, SourceRef = 5, TargetRef = 6 },
                new SequenceFlowModel { Id = 60, SourceRef = 4, TargetRef = 7 },
                new SequenceFlowModel { Id = 70, SourceRef = 6, TargetRef = 8 }
            ]
        };

    private static WorkflowModel CreateImmediateConditionalScriptWorkflow(
        string workflowKey) =>
        new()
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "approved",
                    DataType = WorkflowVariableTypes.Boolean,
                    Required = true,
                    DefaultValue = JsonSerializer.SerializeToElement(true)
                },
                new VariableModel
                {
                    Id = 2,
                    Name = "written",
                    DataType = WorkflowVariableTypes.Boolean,
                    Required = true,
                    DefaultValue = JsonSerializer.SerializeToElement(false)
                }
            ],
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
                    Name = "Immediate condition",
                    Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                    Conditional = new ConditionalDefinitionModel
                    {
                        Condition = "approved == true",
                        DeliveryMode = ConditionalEventDeliveryModes.Atomic
                    }
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Durable script successor",
                    Type = BpmnFlowNodeTypes.ScriptTask,
                    ScriptFormat = ScriptFormats.NCalc,
                    Assignments =
                    [
                        new AssignmentModel
                        {
                            Variable = "written",
                            Expression = "true"
                        }
                    ]
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Done",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 10, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 20, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 30, SourceRef = 3, TargetRef = 4 }
            ]
        };

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException(
            $"Response did not contain {typeof(T).Name}.");
}
