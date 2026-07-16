using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceCurrentNodeDenormalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_instances_Status",
                table: "workflow_instances");

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

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_CurrentNodeRoles",
                table: "workflow_instances",
                column: "CurrentNodeRoles")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_Status_CurrentNodeType_UpdatedAt",
                table: "workflow_instances",
                columns: new[] { "Status", "CurrentNodeType", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_Status_UpdatedAt_Id",
                table: "workflow_instances",
                columns: new[] { "Status", "UpdatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_instances_CurrentNodeRoles",
                table: "workflow_instances");

            migrationBuilder.DropIndex(
                name: "IX_workflow_instances_Status_CurrentNodeType_UpdatedAt",
                table: "workflow_instances");

            migrationBuilder.DropIndex(
                name: "IX_workflow_instances_Status_UpdatedAt_Id",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "CurrentNodeName",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "CurrentNodeRoles",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "CurrentNodeType",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "CurrentRequiresClaim",
                table: "workflow_instances");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_Status",
                table: "workflow_instances",
                column: "Status");
        }
    }
}
