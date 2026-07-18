using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSequenceFlowOccurrenceTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sequence_flow_occurrences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    SequenceFlowId = table.Column<int>(type: "integer", nullable: false),
                    SourceNodeId = table.Column<int>(type: "integer", nullable: false),
                    TargetNodeId = table.Column<int>(type: "integer", nullable: false),
                    TokenId = table.Column<long>(type: "bigint", nullable: true),
                    UserTaskId = table.Column<long>(type: "bigint", nullable: true),
                    MultiInstanceExecutionId = table.Column<long>(type: "bigint", nullable: true),
                    ItemIndex = table.Column<int>(type: "integer", nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsAction = table.Column<bool>(type: "boolean", nullable: false),
                    IsTraversal = table.Column<bool>(type: "boolean", nullable: false),
                    User = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UserRoles = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    ValuesJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequence_flow_occurrences", x => x.Id);
                    table.CheckConstraint(
                        name: "CK_sequence_flow_occurrences_action_or_traversal",
                        sql: "\"IsAction\" OR \"IsTraversal\"");
                    table.ForeignKey(
                        name: "FK_sequence_flow_occurrences_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sequence_flow_summaries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    SequenceFlowId = table.Column<int>(type: "integer", nullable: false),
                    ActionCount = table.Column<long>(type: "bigint", nullable: false),
                    LastActionUser = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LastActionUserRoles = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    LastActionOccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastActionKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LastActionValuesJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    TraversalCount = table.Column<long>(type: "bigint", nullable: false),
                    LastTraversalUser = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LastTraversalUserRoles = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    LastTraversalOccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastTraversalKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LastTraversalValuesJson = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequence_flow_summaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequence_flow_summaries_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sequence_flow_occurrences_InstanceId_SequenceFlowId_Id",
                table: "sequence_flow_occurrences",
                columns: new[] { "InstanceId", "SequenceFlowId", "Id" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_sequence_flow_occurrences_UserTaskId",
                table: "sequence_flow_occurrences",
                column: "UserTaskId",
                unique: true,
                filter: "\"UserTaskId\" IS NOT NULL AND \"IsAction\"");

            migrationBuilder.CreateIndex(
                name: "IX_sequence_flow_summaries_InstanceId_SequenceFlowId",
                table: "sequence_flow_summaries",
                columns: new[] { "InstanceId", "SequenceFlowId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sequence_flow_occurrences");

            migrationBuilder.DropTable(
                name: "sequence_flow_summaries");
        }
    }
}
