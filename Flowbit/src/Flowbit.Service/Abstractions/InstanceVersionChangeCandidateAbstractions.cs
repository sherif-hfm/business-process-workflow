using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

public interface IInstanceVersionChangeCandidateRepository
{
    Task<PagedResult<InstanceListItem>> SearchAsync(
        InstanceVersionChangeCandidateQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves one exact-source selection to immutable instance identities.
    /// Returns at most <paramref name="limit"/> + 1 rows so callers reject an
    /// over-limit batch instead of truncating it.
    /// </summary>
    Task<IReadOnlyList<FrozenInstanceVersionChangeCandidate>> MaterializeAsync(
        InstanceVersionChangeCandidateQuery query,
        IReadOnlyCollection<long> excludedInstanceIds,
        int limit,
        CancellationToken cancellationToken);
}
