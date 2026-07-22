using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Flowbit.Infrastructure.Data;

public static class FlowbitDatabase
{
    public const string Schema = "flowbit";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public static void ConfigureProvider(NpgsqlDbContextOptionsBuilder options)
    {
        options.MigrationsHistoryTable(MigrationsHistoryTable, Schema);
    }
}
