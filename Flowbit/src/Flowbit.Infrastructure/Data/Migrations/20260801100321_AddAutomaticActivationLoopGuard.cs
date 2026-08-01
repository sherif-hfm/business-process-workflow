using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticActivationLoopGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutomaticActivationCount",
                schema: "flowbit",
                table: "complex_gateway_states",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AutomaticActivationCount",
                schema: "flowbit",
                table: "workflow_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AutomaticActivationCount",
                schema: "flowbit",
                table: "execution_tokens",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long[]>(
                name: "AutomaticActivationStateIds",
                schema: "flowbit",
                table: "execution_tokens",
                type: "bigint[]",
                nullable: false,
                defaultValueSql: "'{}'::bigint[]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_complex_gateway_states_automatic_activation_count",
                schema: "flowbit",
                table: "complex_gateway_states",
                sql: "\"AutomaticActivationCount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workflow_jobs_automatic_activation_count",
                schema: "flowbit",
                table: "workflow_jobs",
                sql: "\"AutomaticActivationCount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_execution_tokens_automatic_activation_count",
                schema: "flowbit",
                table: "execution_tokens",
                sql: "\"AutomaticActivationCount\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_AutomaticActivationStateIds",
                schema: "flowbit",
                table: "execution_tokens",
                column: "AutomaticActivationStateIds",
                filter: "cardinality(\"AutomaticActivationStateIds\") > 0")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_execution_tokens_AutomaticActivationStateIds",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropCheckConstraint(
                name: "CK_complex_gateway_states_automatic_activation_count",
                schema: "flowbit",
                table: "complex_gateway_states");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workflow_jobs_automatic_activation_count",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_execution_tokens_automatic_activation_count",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "AutomaticActivationCount",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropColumn(
                name: "AutomaticActivationCount",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "AutomaticActivationStateIds",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "AutomaticActivationCount",
                schema: "flowbit",
                table: "complex_gateway_states");
        }
    }
}
