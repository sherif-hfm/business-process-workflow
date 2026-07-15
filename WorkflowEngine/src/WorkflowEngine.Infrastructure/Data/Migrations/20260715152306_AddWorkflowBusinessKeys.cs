using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkflowEngine.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowBusinessKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BusinessKey",
                table: "workflow_instances",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "BusinessKeyUniqueness",
                table: "workflow_instances",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowKey",
                table: "workflow_instances",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE workflow_instances AS i
                SET "WorkflowKey" = d."WorkflowKey"
                FROM workflow_definitions AS d
                WHERE d."Id" = i."WorkflowDefinitionId";
                """);

            migrationBuilder.AlterColumn<string>(
                name: "WorkflowKey",
                table: "workflow_instances",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(300)",
                oldMaxLength: 300,
                oldDefaultValue: "");

            migrationBuilder.CreateTable(
                name: "workflow_business_key_claims",
                columns: table => new
                {
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    BusinessKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    IsPermanent = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveInstanceId = table.Column<long>(type: "bigint", nullable: true),
                    LastInstanceId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_business_key_claims", x => new { x.WorkflowKey, x.BusinessKey });
                });

            migrationBuilder.CreateTable(
                name: "workflow_business_key_scopes",
                columns: table => new
                {
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_business_key_scopes", x => x.WorkflowKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_WorkflowKey_BusinessKey_Status",
                table: "workflow_instances",
                columns: new[] { "WorkflowKey", "BusinessKey", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_business_key_claims_ActiveInstanceId",
                table: "workflow_business_key_claims",
                column: "ActiveInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_business_key_claims_LastInstanceId",
                table: "workflow_business_key_claims",
                column: "LastInstanceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_business_key_claims");

            migrationBuilder.DropTable(
                name: "workflow_business_key_scopes");

            migrationBuilder.DropIndex(
                name: "IX_workflow_instances_WorkflowKey_BusinessKey_Status",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "BusinessKey",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "BusinessKeyUniqueness",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "WorkflowKey",
                table: "workflow_instances");
        }
    }
}
