using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdministrativeActionCandidateExclusionApiTests(
    PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CandidateSearch_DeduplicatesAndAppliesPositionExclusions()
    {
        var workflowId = await CreateWorkflowAsync();
        _ = await StartAsync(workflowId);
        _ = await StartAsync(workflowId);

        var all = await SearchAsync(new AdministrativeActionCandidateSearchRequest
        {
            WorkflowDefinitionId = workflowId,
            SourceNodeId = 2,
            Page = 1,
            PageSize = 20
        });
        Assert.Equal(2, all.TotalCount);
        var excluded = all.Items[0];

        var filtered = await SearchAsync(new AdministrativeActionCandidateSearchRequest
        {
            WorkflowDefinitionId = workflowId,
            SourceNodeId = 2,
            ExcludedPositions =
            [
                new AdministrativeActionPositionReferenceDto(
                    excluded.PositionKind,
                    excluded.PositionId),
                new AdministrativeActionPositionReferenceDto(
                    excluded.PositionKind,
                    excluded.PositionId)
            ],
            Page = 1,
            PageSize = 20
        });
        Assert.Equal(1, filtered.TotalCount);
        Assert.DoesNotContain(
            filtered.Items,
            candidate => candidate.PositionKind == excluded.PositionKind
                         && candidate.PositionId == excluded.PositionId);

        await AssertInvalidExclusionAsync(
            workflowId,
            new AdministrativeActionPositionReferenceDto("token", excluded.PositionId));
        await AssertInvalidExclusionAsync(
            workflowId,
            new AdministrativeActionPositionReferenceDto(
                AdministrativeActionPositionKinds.UserTask,
                0));
    }

    private async Task<PagedResult<AdministrativeActionCandidateDto>> SearchAsync(
        AdministrativeActionCandidateSearchRequest request)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/administrative-actions/candidates/search",
            request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<AdministrativeActionCandidateDto>>(response);
    }

    private async Task AssertInvalidExclusionAsync(
        long workflowId,
        AdministrativeActionPositionReferenceDto exclusion)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/administrative-actions/candidates/search",
            new AdministrativeActionCandidateSearchRequest
            {
                WorkflowDefinitionId = workflowId,
                SourceNodeId = 2,
                ExcludedPositions = [exclusion],
                Page = 1,
                PageSize = 20
            });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<long> CreateWorkflowAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var model = new WorkflowModel
        {
            Id = $"administrative-candidate-exclusion-{suffix}",
            Name = $"Administrative candidate exclusion {suffix}",
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
                    Name = "Waiting task",
                    Type = BpmnFlowNodeTypes.UserTask
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "End",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel
                {
                    Id = 101,
                    Name = "Begin",
                    SourceRef = 1,
                    TargetRef = 2
                },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Finish",
                    SourceRef = 2,
                    TargetRef = 3
                }
            ]
        };
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
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

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, "candidate-operator", []);
        request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");
}
