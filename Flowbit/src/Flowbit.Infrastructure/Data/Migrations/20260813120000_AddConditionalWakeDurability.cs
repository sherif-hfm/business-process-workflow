using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Flowbit.Infrastructure.Data;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260813120000_AddConditionalWakeDurability")]
public sealed class AddConditionalWakeDurability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "NodeType",
            schema: "flowbit",
            table: "execution_tokens",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(32)",
            oldMaxLength: 32);

        migrationBuilder.AlterColumn<string>(
            name: "NodeType",
            schema: "flowbit",
            table: "node_executions",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(32)",
            oldMaxLength: 32);

        migrationBuilder.DropCheckConstraint(
            name: "CK_node_executions_completion_reason",
            schema: "flowbit",
            table: "node_executions");
        migrationBuilder.AddCheckConstraint(
            name: "CK_node_executions_completion_reason",
            schema: "flowbit",
            table: "node_executions",
            sql: CompletionReasonConstraint);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_node_executions_completion_reason",
            schema: "flowbit",
            table: "node_executions");
        migrationBuilder.Sql(
            """
            UPDATE flowbit.node_executions
            SET "CompletionReason" = 'normal'
            WHERE "CompletionReason" = 'conditionalTriggered';
            """);
        migrationBuilder.AddCheckConstraint(
            name: "CK_node_executions_completion_reason",
            schema: "flowbit",
            table: "node_executions",
            sql: PreviousCompletionReasonConstraint);

        migrationBuilder.AlterColumn<string>(
            name: "NodeType",
            schema: "flowbit",
            table: "execution_tokens",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(64)",
            oldMaxLength: 64);

        migrationBuilder.AlterColumn<string>(
            name: "NodeType",
            schema: "flowbit",
            table: "node_executions",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(64)",
            oldMaxLength: 64);
    }

    private const string CompletionReasonConstraint =
        "((\"Status\" IN ('pending', 'active') AND \"CompletionReason\" IS NULL) OR "
        + "(\"Status\" IN ('completed', 'cancelled', 'faulted', 'merged') "
        + "AND \"CompletionReason\" IN "
        + "('normal', 'userAction', 'administrativeAction', 'messageDelivery', "
        + "'multiInstanceItem', 'multiInstanceCompleted', 'multiInstanceInterrupt', "
        + "'boundaryCaught', 'normalEnd', 'terminateEnd', 'errorEnd', "
        + "'instanceCancelled', 'gatewayScopeCancelled', 'gatewayJoinMerged', "
        + "'parallelFork', 'parallelJoin', 'inclusiveSplit', 'inclusiveMerge', "
        + "'complexActivation', 'complexReset', 'scopedInterrupt', "
        + "'scopedInterruptSkipped', 'timerFired', 'conditionalTriggered')))";

    private const string PreviousCompletionReasonConstraint =
        "((\"Status\" IN ('pending', 'active') AND \"CompletionReason\" IS NULL) OR "
        + "(\"Status\" IN ('completed', 'cancelled', 'faulted', 'merged') "
        + "AND \"CompletionReason\" IN "
        + "('normal', 'userAction', 'administrativeAction', 'messageDelivery', "
        + "'multiInstanceItem', 'multiInstanceCompleted', 'multiInstanceInterrupt', "
        + "'boundaryCaught', 'normalEnd', 'terminateEnd', 'errorEnd', "
        + "'instanceCancelled', 'gatewayScopeCancelled', 'gatewayJoinMerged', "
        + "'parallelFork', 'parallelJoin', 'inclusiveSplit', 'inclusiveMerge', "
        + "'complexActivation', 'complexReset', 'scopedInterrupt', "
        + "'scopedInterruptSkipped', 'timerFired')))";
}
