using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WorkflowEngine.Infrastructure.Data;

public sealed class DatabaseInitializer(AppDbContext dbContext, ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Applying EF Core database migrations...");
        await dbContext.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Database migrations applied successfully.");
    }
}
