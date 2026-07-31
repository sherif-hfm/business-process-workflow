using Microsoft.EntityFrameworkCore.Storage;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;

namespace Flowbit.Infrastructure.Repositories;

public sealed class UnitOfWork(AppDbContext dbContext) : IUnitOfWork
{
    public async Task<IWorkflowTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        return new EfWorkflowTransaction(transaction, dbContext);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);

    public void DiscardChanges() =>
        dbContext.ChangeTracker.Clear();

    private sealed class EfWorkflowTransaction(
        IDbContextTransaction transaction,
        AppDbContext dbContext) : IWorkflowTransaction
    {
        private bool _committed;

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await transaction.CommitAsync(cancellationToken);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            if (!_committed)
            {
                // EF keeps entity mutations after a database rollback. Leaving
                // them tracked lets a later job-only SaveChanges accidentally
                // re-persist workflow state that was intentionally rolled back.
                dbContext.ChangeTracker.Clear();
            }
        }
    }
}
