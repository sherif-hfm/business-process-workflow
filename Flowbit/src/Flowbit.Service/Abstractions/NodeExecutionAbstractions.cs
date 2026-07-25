using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

public interface INodeExecutionQueryRepository
{
    Task<PagedResult<NodeExecutionSummaryDto>> SearchAsync(
        NodeExecutionQuery query,
        NodeExecutionAuthorization authorization,
        CancellationToken cancellationToken);

    Task<NodeExecutionDetailDto?> GetAsync(
        long id,
        NodeExecutionAuthorization authorization,
        CancellationToken cancellationToken);
}

public interface INodeExecutionQueryService
{
    Task<PagedResult<NodeExecutionSummaryDto>> SearchAsync(
        NodeExecutionSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<NodeExecutionDetailDto?> GetAsync(
        long id,
        ActorContext actor,
        CancellationToken cancellationToken);
}
