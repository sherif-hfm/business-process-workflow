using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Xunit;

namespace Flowbit.Tests;

public sealed class NodeExecutionQueryServiceTests
{
    [Fact]
    public async Task Search_DefaultsBlankGlobalRoleSettingToAdmin()
    {
        var repository = new CapturingRepository();
        var service = new NodeExecutionQueryService(
            repository,
            new StubSettingsRepository(value: "   "));

        await service.SearchAsync(
            new NodeExecutionSearchRequest(),
            Actor("reader", "ADMIN"),
            CancellationToken.None);

        Assert.NotNull(repository.Authorization);
        Assert.True(repository.Authorization!.IsGlobalReader);
        Assert.Equal(["admin"], repository.Authorization.LowerCallerRoles);
    }

    [Fact]
    public async Task Search_PassesWorkflowRoleScopeWhenCallerIsNotGlobalReader()
    {
        var repository = new CapturingRepository();
        var service = new NodeExecutionQueryService(
            repository,
            new StubSettingsRepository(value: "audit-reader, security"));

        await service.SearchAsync(
            new NodeExecutionSearchRequest(),
            Actor("manager", "Finance-Manager"),
            CancellationToken.None);

        Assert.NotNull(repository.Authorization);
        Assert.False(repository.Authorization!.IsGlobalReader);
        Assert.Equal(["finance-manager"], repository.Authorization.LowerCallerRoles);
    }

    [Fact]
    public async Task Search_NormalizesEnumsRangesVariablesAndDefaultSort()
    {
        var repository = new CapturingRepository();
        var service = new NodeExecutionQueryService(
            repository,
            new StubSettingsRepository(value: "admin"));
        var from = new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.FromHours(3));
        var to = from.AddHours(1);

        await service.SearchAsync(
            new NodeExecutionSearchRequest
            {
                NodeTypes = ["USERTASK", "endEvent"],
                Statuses = ["ACTIVE", "completed"],
                CompletionReasons = ["USERACTION"],
                CreatedFrom = from,
                CreatedTo = to,
                Variables = ["decision:APPROVED"],
                Page = 0,
                PageSize = 500
            },
            Actor("reader", "admin"),
            CancellationToken.None);

        var query = Assert.IsType<NodeExecutionQuery>(repository.Query);
        Assert.Equal(["userTask", "endEvent"], query.NodeTypes);
        Assert.Equal(["active", "completed"], query.Statuses);
        Assert.Equal(["userAction"], query.CompletionReasons);
        Assert.Equal(TimeSpan.Zero, query.CreatedFrom!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, query.CreatedTo!.Value.Offset);
        var variableFilter = Assert.IsType<VariableFilterComparisonExpression>(query.VariableFilter);
        Assert.Equal("decision", variableFilter.Field.VariableName);
        Assert.Equal(VariableFilterComparisonOperator.LegacyEqualIgnoreCase, variableFilter.Operator);
        Assert.Equal("APPROVED", variableFilter.Operand.GetString());
        Assert.Equal(1, query.Page);
        Assert.Equal(200, query.PageSize);
        Assert.Equal(
            [
                new NodeExecutionSortCriterion(
                    NodeExecutionSortField.UpdatedAt,
                    SortDirection.Descending),
                new NodeExecutionSortCriterion(
                    NodeExecutionSortField.Id,
                    SortDirection.Descending)
            ],
            query.Sort);
    }

    [Theory]
    [InlineData("unknown:asc")]
    [InlineData("updatedAt:sideways")]
    [InlineData("updatedAt")]
    public async Task Search_RejectsInvalidSort(string sort)
    {
        var service = CreateService();

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.SearchAsync(
                new NodeExecutionSearchRequest { Sort = [sort] },
                Actor("reader", "admin"),
                CancellationToken.None));
    }

    [Fact]
    public async Task Search_RejectsDuplicateSortFields()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.SearchAsync(
                new NodeExecutionSearchRequest
                {
                    Sort = ["updatedAt:asc", "UPDATEDAT:desc"]
                },
                Actor("reader", "admin"),
                CancellationToken.None));
    }

    [Fact]
    public async Task Search_RejectsMoreThanTenVariableFilters()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.SearchAsync(
                new NodeExecutionSearchRequest
                {
                    Variables = Enumerable.Range(0, 11)
                        .Select(index => $"v{index}:value")
                        .ToArray()
                },
                Actor("reader", "admin"),
                CancellationToken.None));
    }

    [Fact]
    public async Task Search_RejectsInvalidIdentifiersEnumsAndRanges()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.SearchAsync(
                new NodeExecutionSearchRequest { ExecutionId = 0 },
                Actor("reader", "admin"),
                CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.SearchAsync(
                new NodeExecutionSearchRequest { Statuses = ["waiting"] },
                Actor("reader", "admin"),
                CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.SearchAsync(
                new NodeExecutionSearchRequest
                {
                    UpdatedFrom = DateTimeOffset.UtcNow,
                    UpdatedTo = DateTimeOffset.UtcNow.AddMinutes(-1)
                },
                Actor("reader", "admin"),
                CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.SearchAsync(
                new NodeExecutionSearchRequest
                {
                    MinDurationMilliseconds = 20,
                    MaxDurationMilliseconds = 10
                },
                Actor("reader", "admin"),
                CancellationToken.None));
    }

    private static NodeExecutionQueryService CreateService() =>
        new(new CapturingRepository(), new StubSettingsRepository("admin"));

    private static ActorContext Actor(string user, params string[] roles) =>
        new(
            user,
            roles,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private sealed class CapturingRepository : INodeExecutionQueryRepository
    {
        public NodeExecutionQuery? Query { get; private set; }
        public NodeExecutionAuthorization? Authorization { get; private set; }

        public Task<PagedResult<NodeExecutionSummaryDto>> SearchAsync(
            NodeExecutionQuery query,
            NodeExecutionAuthorization authorization,
            CancellationToken cancellationToken)
        {
            Query = query;
            Authorization = authorization;
            return Task.FromResult(new PagedResult<NodeExecutionSummaryDto>(
                [],
                query.Page,
                query.PageSize,
                0));
        }

        public Task<NodeExecutionDetailDto?> GetAsync(
            long id,
            NodeExecutionAuthorization authorization,
            CancellationToken cancellationToken) =>
            Task.FromResult<NodeExecutionDetailDto?>(null);
    }

    private sealed class StubSettingsRepository(string? value)
        : IEngineSettingsRepository
    {
        public Task<EngineSettingRecord?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken) =>
            Task.FromResult(value is null
                ? null
                : new EngineSettingRecord(
                    1,
                    "NodeExecution",
                    "RequiredRole",
                    value,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(
            string pattern,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EngineSettingRecord>>([]);

        public Task<EngineSettingRecord> SetAsync(
            string key,
            string settingValue,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            string key,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
