using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WorkflowEngine.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionTokensAndUserTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "execution_tokens",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    NodeName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    NodeExternalId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    NodeType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_execution_tokens_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tasks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    TokenId = table.Column<long>(type: "bigint", nullable: false),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    NodeName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    NodeExternalId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Roles = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    RequiresClaim = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ClaimedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_tasks_execution_tokens_TokenId",
                        column: x => x.TokenId,
                        principalTable: "execution_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_tasks_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_InstanceId_Status",
                table: "execution_tokens",
                columns: new[] { "InstanceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_NodeExternalId_Status",
                table: "execution_tokens",
                columns: new[] { "NodeExternalId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_NodeId_Status",
                table: "execution_tokens",
                columns: new[] { "NodeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_InstanceId_Status",
                table: "user_tasks",
                columns: new[] { "InstanceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_NodeExternalId_Status",
                table: "user_tasks",
                columns: new[] { "NodeExternalId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_NodeId_Status",
                table: "user_tasks",
                columns: new[] { "NodeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_Roles",
                table: "user_tasks",
                column: "Roles")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_Status_UpdatedAt_Id",
                table: "user_tasks",
                columns: new[] { "Status", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_TokenId",
                table: "user_tasks",
                column: "TokenId");

            // Preserve every existing instance's execution position before removing
            // the legacy single-current-node columns. Terminal instances retain a
            // terminal token so their last node remains available in API projections.
            migrationBuilder.Sql("""
                INSERT INTO execution_tokens
                    ("InstanceId", "NodeId", "NodeName", "NodeExternalId", "NodeType", "Status", "CreatedAt", "UpdatedAt")
                SELECT
                    "Id", "CurrentStepId", "CurrentNodeName", "CurrentNodeExternalId", "CurrentNodeType",
                    CASE WHEN "Status" = 'running' THEN 'active' ELSE "Status" END,
                    "CreatedAt", "UpdatedAt"
                FROM workflow_instances;

                INSERT INTO user_tasks
                    ("InstanceId", "TokenId", "NodeId", "NodeName", "NodeExternalId", "Roles", "RequiresClaim",
                     "Status", "ClaimedBy", "CreatedAt", "UpdatedAt", "CompletedAt")
                SELECT
                    w."Id", t."Id", w."CurrentStepId", w."CurrentNodeName", w."CurrentNodeExternalId",
                    w."CurrentNodeRoles", w."CurrentRequiresClaim", 'active', w."ClaimedBy",
                    w."UpdatedAt", w."UpdatedAt", NULL
                FROM workflow_instances w
                JOIN execution_tokens t ON t."InstanceId" = w."Id"
                WHERE w."Status" = 'running' AND w."CurrentNodeType" = 'userTask';
                """);

            migrationBuilder.DropIndex(name: "IX_workflow_instances_CurrentNodeRoles", table: "workflow_instances");
            migrationBuilder.DropIndex(name: "IX_workflow_instances_CurrentStepId", table: "workflow_instances");
            migrationBuilder.DropIndex(name: "IX_workflow_instances_Status_CurrentNodeExternalId", table: "workflow_instances");
            migrationBuilder.DropIndex(name: "IX_workflow_instances_Status_CurrentNodeType_UpdatedAt", table: "workflow_instances");
            migrationBuilder.DropColumn(name: "ClaimedBy", table: "workflow_instances");
            migrationBuilder.DropColumn(name: "CurrentNodeExternalId", table: "workflow_instances");
            migrationBuilder.DropColumn(name: "CurrentNodeName", table: "workflow_instances");
            migrationBuilder.DropColumn(name: "CurrentNodeRoles", table: "workflow_instances");
            migrationBuilder.DropColumn(name: "CurrentNodeType", table: "workflow_instances");
            migrationBuilder.DropColumn(name: "CurrentRequiresClaim", table: "workflow_instances");
            migrationBuilder.DropColumn(name: "CurrentStepId", table: "workflow_instances");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimedBy",
                table: "workflow_instances",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentNodeExternalId",
                table: "workflow_instances",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentNodeName",
                table: "workflow_instances",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<List<string>>(
                name: "CurrentNodeRoles",
                table: "workflow_instances",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "CurrentNodeType",
                table: "workflow_instances",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "CurrentRequiresClaim",
                table: "workflow_instances",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CurrentStepId",
                table: "workflow_instances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Restore the legacy projection when rolling back. User-task fields
            // come from the active work item when one exists; token position is
            // retained for both running and terminal instances.
            migrationBuilder.Sql("""
                UPDATE workflow_instances w
                SET
                    "CurrentStepId" = (SELECT "NodeId" FROM execution_tokens WHERE "InstanceId" = w."Id" ORDER BY "Id" DESC LIMIT 1),
                    "CurrentNodeName" = (SELECT "NodeName" FROM execution_tokens WHERE "InstanceId" = w."Id" ORDER BY "Id" DESC LIMIT 1),
                    "CurrentNodeExternalId" = (SELECT "NodeExternalId" FROM execution_tokens WHERE "InstanceId" = w."Id" ORDER BY "Id" DESC LIMIT 1),
                    "CurrentNodeType" = (SELECT "NodeType" FROM execution_tokens WHERE "InstanceId" = w."Id" ORDER BY "Id" DESC LIMIT 1),
                    "CurrentNodeRoles" = COALESCE((SELECT "Roles" FROM user_tasks WHERE "InstanceId" = w."Id" AND "Status" = 'active' ORDER BY "Id" DESC LIMIT 1), '{}'::text[]),
                    "CurrentRequiresClaim" = COALESCE((SELECT "RequiresClaim" FROM user_tasks WHERE "InstanceId" = w."Id" AND "Status" = 'active' ORDER BY "Id" DESC LIMIT 1), false),
                    "ClaimedBy" = (SELECT "ClaimedBy" FROM user_tasks WHERE "InstanceId" = w."Id" AND "Status" = 'active' ORDER BY "Id" DESC LIMIT 1);
                """);

            migrationBuilder.DropTable(name: "user_tasks");
            migrationBuilder.DropTable(name: "execution_tokens");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_CurrentNodeRoles",
                table: "workflow_instances",
                column: "CurrentNodeRoles")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_CurrentStepId",
                table: "workflow_instances",
                column: "CurrentStepId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_Status_CurrentNodeExternalId",
                table: "workflow_instances",
                columns: new[] { "Status", "CurrentNodeExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_Status_CurrentNodeType_UpdatedAt",
                table: "workflow_instances",
                columns: new[] { "Status", "CurrentNodeType", "UpdatedAt" });
        }
    }
}
