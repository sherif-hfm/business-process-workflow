using Microsoft.EntityFrameworkCore.Storage;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;

namespace Flowbit.Infrastructure.Repositories;

public sealed class UnitOfWork(AppDbContext dbContext) : IUnitOfWork
{
    public async Task<IWorkflowTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        return new EfWorkflowTransaction(transaction);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);

    private sealed class EfWorkflowTransaction(IDbContextTransaction transaction) : IWorkflowTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() =>
            transaction.DisposeAsync();
    }
}
