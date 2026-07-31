using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTimerStartDefaultActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DefaultActivatedAt",
                schema: "flowbit",
                table: "workflow_definitions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultActivationId",
                schema: "flowbit",
                table: "workflow_definitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE flowbit.workflow_definitions
                SET "DefaultActivationId" = gen_random_uuid(),
                    "DefaultActivatedAt" = clock_timestamp()
                WHERE "IsPublished" = TRUE
                  AND "IsDefault" = TRUE
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_workflow_definitions_default_activation",
                schema: "flowbit",
                table: "workflow_definitions",
                sql: "(\"IsPublished\" AND \"IsDefault\" AND \"DefaultActivationId\" IS NOT NULL AND \"DefaultActivatedAt\" IS NOT NULL) OR ((NOT \"IsPublished\" OR NOT \"IsDefault\") AND \"DefaultActivationId\" IS NULL AND \"DefaultActivatedAt\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_workflow_definitions_default_activation",
                schema: "flowbit",
                table: "workflow_definitions");

            migrationBuilder.DropColumn(
                name: "DefaultActivatedAt",
                schema: "flowbit",
                table: "workflow_definitions");

            migrationBuilder.DropColumn(
                name: "DefaultActivationId",
                schema: "flowbit",
                table: "workflow_definitions");
        }
    }
}
