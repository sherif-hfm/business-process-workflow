using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace WorkflowEngine.Infrastructure.Data;

public sealed class DesignTimeAppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__WorkflowEngine")
            ?? "Host=localhost;Port=5432;Database=workflow_engine;Username=workflow;Password=workflow";

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(dataSourceBuilder.Build())
            .Options;

        return new AppDbContext(options);
    }
}
