using Microsoft.EntityFrameworkCore.Storage;
using WorkflowEngine.Infrastructure.Data;
using WorkflowEngine.Service.Abstractions;

namespace WorkflowEngine.Infrastructure.Repositories;

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
