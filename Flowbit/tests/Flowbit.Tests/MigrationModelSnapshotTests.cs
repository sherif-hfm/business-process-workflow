using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Flowbit.Infrastructure.Data;
using Xunit;

namespace Flowbit.Tests;

public sealed class MigrationModelSnapshotTests
{
    [Fact]
    public void SnapshotMatchesTheRuntimeModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=flowbit_model_only")
            .Options;
        using var context = new AppDbContext(options);
        var migrations = context.GetService<IMigrationsAssembly>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var runtime = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var snapshotModel = context.GetService<IModelRuntimeInitializer>()
            .Initialize(migrations.ModelSnapshot!.Model, designTime: true);
        var snapshot = snapshotModel.GetRelationalModel();
        var differences = differ.GetDifferences(snapshot, runtime);

        Assert.True(
            differences.Count == 0,
            "Migration snapshot differs from the runtime model: "
            + string.Join(", ", differences.Select(Describe)));
    }

    private static string Describe(MigrationOperation operation) => operation switch
    {
        DropColumnOperation column => $"DropColumn {column.Schema}.{column.Table}.{column.Name}",
        AddColumnOperation column => $"AddColumn {column.Schema}.{column.Table}.{column.Name}",
        DropForeignKeyOperation key => $"DropFK {key.Schema}.{key.Table}.{key.Name}",
        AddForeignKeyOperation key =>
            $"AddFK {key.Schema}.{key.Table}.{key.Name} ({string.Join('+', key.Columns)}) -> {key.PrincipalSchema}.{key.PrincipalTable}",
        _ => operation.GetType().Name
    };
}
