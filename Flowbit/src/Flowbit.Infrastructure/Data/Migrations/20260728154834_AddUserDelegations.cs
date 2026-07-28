using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserDelegations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.AddColumn<string>(
                name: "CompletedActingFor",
                schema: "flowbit",
                table: "user_tasks",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CompletionDelegationId",
                schema: "flowbit",
                table: "user_tasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastActionActingFor",
                schema: "flowbit",
                table: "sequence_flow_summaries",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastActionDelegationId",
                schema: "flowbit",
                table: "sequence_flow_summaries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastTraversalActingFor",
                schema: "flowbit",
                table: "sequence_flow_summaries",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastTraversalDelegationId",
                schema: "flowbit",
                table: "sequence_flow_summaries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActingFor",
                schema: "flowbit",
                table: "sequence_flow_occurrences",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DelegationId",
                schema: "flowbit",
                table: "sequence_flow_occurrences",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletedActingFor",
                schema: "flowbit",
                table: "node_executions",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CompletedDelegationId",
                schema: "flowbit",
                table: "node_executions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TriggeredActingFor",
                schema: "flowbit",
                table: "node_executions",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TriggeredDelegationId",
                schema: "flowbit",
                table: "node_executions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActingFor",
                schema: "flowbit",
                table: "instance_variables",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DelegationId",
                schema: "flowbit",
                table: "instance_variables",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActingFor",
                schema: "flowbit",
                table: "instance_history",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DelegationId",
                schema: "flowbit",
                table: "instance_history",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_delegations",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Delegator = table.Column<string>(type: "citext", maxLength: 300, nullable: false),
                    Delegate = table.Column<string>(type: "citext", maxLength: 300, nullable: false),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequiresAcceptance = table.Column<bool>(type: "boolean", nullable: false),
                    AcceptanceState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    DecisionBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    DecisionAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_delegations", x => x.Id);
                    table.CheckConstraint("CK_user_delegations_acceptance_shape", "((NOT \"RequiresAcceptance\" AND \"AcceptanceState\" = 'notRequired' AND \"DecisionBy\" IS NULL AND \"DecisionAt\" IS NULL AND \"DecisionReason\" IS NULL) OR (\"RequiresAcceptance\" AND \"AcceptanceState\" = 'pending' AND \"DecisionBy\" IS NULL AND \"DecisionAt\" IS NULL AND \"DecisionReason\" IS NULL) OR (\"RequiresAcceptance\" AND \"AcceptanceState\" IN ('accepted', 'rejected') AND \"DecisionBy\" IS NOT NULL AND \"DecisionAt\" IS NOT NULL))");
                    table.CheckConstraint("CK_user_delegations_acceptance_state", "\"AcceptanceState\" IN ('notRequired', 'pending', 'accepted', 'rejected')");
                    table.CheckConstraint("CK_user_delegations_revocation_shape", "((\"RevokedAt\" IS NULL AND \"RevokedBy\" IS NULL AND \"RevocationReason\" IS NULL) OR (\"RevokedAt\" IS NOT NULL AND \"RevokedBy\" IS NOT NULL))");
                    table.CheckConstraint("CK_user_delegations_timestamps", "\"UpdatedAt\" >= \"CreatedAt\" AND (\"DecisionAt\" IS NULL OR \"DecisionAt\" >= \"CreatedAt\") AND (\"RevokedAt\" IS NULL OR \"RevokedAt\" >= \"CreatedAt\")");
                    table.CheckConstraint("CK_user_delegations_validity", "\"ValidUntil\" > \"ValidFrom\"");
                });

            migrationBuilder.CreateTable(
                name: "workflow_delegation_policies",
                schema: "flowbit",
                columns: table => new
                {
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    RequiresAcceptance = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_delegation_policies", x => x.WorkflowKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_delegations_Delegate_WorkflowKey_AcceptanceState_Revok~",
                schema: "flowbit",
                table: "user_delegations",
                columns: new[] { "Delegate", "WorkflowKey", "AcceptanceState", "RevokedAt", "ValidFrom", "ValidUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_user_delegations_Delegator_Delegate_WorkflowKey_AcceptanceS~",
                schema: "flowbit",
                table: "user_delegations",
                columns: new[] { "Delegator", "Delegate", "WorkflowKey", "AcceptanceState", "RevokedAt", "ValidFrom", "ValidUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_user_delegations_Delegator_WorkflowKey_CreatedAt_Id",
                schema: "flowbit",
                table: "user_delegations",
                columns: new[] { "Delegator", "WorkflowKey", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_user_delegations_WorkflowKey_ValidUntil",
                schema: "flowbit",
                table: "user_delegations",
                columns: new[] { "WorkflowKey", "ValidUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_delegations",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "workflow_delegation_policies",
                schema: "flowbit");

            migrationBuilder.DropColumn(
                name: "CompletedActingFor",
                schema: "flowbit",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "CompletionDelegationId",
                schema: "flowbit",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "LastActionActingFor",
                schema: "flowbit",
                table: "sequence_flow_summaries");

            migrationBuilder.DropColumn(
                name: "LastActionDelegationId",
                schema: "flowbit",
                table: "sequence_flow_summaries");

            migrationBuilder.DropColumn(
                name: "LastTraversalActingFor",
                schema: "flowbit",
                table: "sequence_flow_summaries");

            migrationBuilder.DropColumn(
                name: "LastTraversalDelegationId",
                schema: "flowbit",
                table: "sequence_flow_summaries");

            migrationBuilder.DropColumn(
                name: "ActingFor",
                schema: "flowbit",
                table: "sequence_flow_occurrences");

            migrationBuilder.DropColumn(
                name: "DelegationId",
                schema: "flowbit",
                table: "sequence_flow_occurrences");

            migrationBuilder.DropColumn(
                name: "CompletedActingFor",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropColumn(
                name: "CompletedDelegationId",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropColumn(
                name: "TriggeredActingFor",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropColumn(
                name: "TriggeredDelegationId",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropColumn(
                name: "ActingFor",
                schema: "flowbit",
                table: "instance_variables");

            migrationBuilder.DropColumn(
                name: "DelegationId",
                schema: "flowbit",
                table: "instance_variables");

            migrationBuilder.DropColumn(
                name: "ActingFor",
                schema: "flowbit",
                table: "instance_history");

            migrationBuilder.DropColumn(
                name: "DelegationId",
                schema: "flowbit",
                table: "instance_history");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }
    }
}
