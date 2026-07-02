using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using WorkflowEngine.Shared.Models;

#nullable disable

namespace WorkflowEngine.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workflow_definitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Definition = table.Column<WorkflowModel>(type: "jsonb", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_instances",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    CurrentStepId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ClaimedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    StartedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_instances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflow_instances_workflow_definitions_WorkflowDefinitionId",
                        column: x => x.WorkflowDefinitionId,
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "instance_history",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    ActionId = table.Column<int>(type: "integer", nullable: true),
                    FromStepId = table.Column<int>(type: "integer", nullable: false),
                    ToStepId = table.Column<int>(type: "integer", nullable: false),
                    PerformedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Payload = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PerformedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instance_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_instance_history_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "instance_variables",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    VariableName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SourceActionId = table.Column<int>(type: "integer", nullable: true),
                    ValueJson = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    SetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instance_variables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_instance_variables_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_instance_history_InstanceId",
                table: "instance_history",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_instance_variables_InstanceId_VariableName",
                table: "instance_variables",
                columns: new[] { "InstanceId", "VariableName" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definitions_Definition",
                table: "workflow_definitions",
                column: "Definition")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definitions_Name_Version",
                table: "workflow_definitions",
                columns: new[] { "Name", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_CurrentStepId",
                table: "workflow_instances",
                column: "CurrentStepId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_Status",
                table: "workflow_instances",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_WorkflowDefinitionId",
                table: "workflow_instances",
                column: "WorkflowDefinitionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "instance_history");

            migrationBuilder.DropTable(
                name: "instance_variables");

            migrationBuilder.DropTable(
                name: "workflow_instances");

            migrationBuilder.DropTable(
                name: "workflow_definitions");
        }
    }
}
