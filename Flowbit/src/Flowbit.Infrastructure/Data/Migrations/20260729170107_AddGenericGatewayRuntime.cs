using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGenericGatewayRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_execution_tokens_parallel_gateway_branches_ParallelBranchId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropForeignKey(
                name: "FK_node_executions_parallel_gateway_branches_EntryParallelBran~",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropForeignKey(
                name: "FK_node_executions_parallel_gateway_branches_ExitParallelBranc~",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropForeignKey(
                name: "FK_parallel_gateway_branches_parallel_gateway_executions_Execu~",
                schema: "flowbit",
                table: "parallel_gateway_branches");

            migrationBuilder.DropTable(
                name: "parallel_gateway_executions",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "parallel_gateway_branches",
                schema: "flowbit");

            migrationBuilder.DropCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropIndex(
                name: "IX_execution_tokens_ParallelBranchId_Status",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.RenameColumn(
                name: "ExitParallelBranchId",
                schema: "flowbit",
                table: "node_executions",
                newName: "ExitGatewayBranchId");

            migrationBuilder.RenameColumn(
                name: "EntryParallelBranchId",
                schema: "flowbit",
                table: "node_executions",
                newName: "EntryGatewayBranchId");

            migrationBuilder.RenameIndex(
                name: "IX_node_executions_ExitParallelBranchId",
                schema: "flowbit",
                table: "node_executions",
                newName: "IX_node_executions_ExitGatewayBranchId");

            migrationBuilder.RenameIndex(
                name: "IX_node_executions_EntryParallelBranchId",
                schema: "flowbit",
                table: "node_executions",
                newName: "IX_node_executions_EntryGatewayBranchId");

            migrationBuilder.RenameColumn(
                name: "ParallelBranchId",
                schema: "flowbit",
                table: "execution_tokens",
                newName: "GatewayBranchId");

            // Generic gateway lineage intentionally starts fresh. The old
            // parallel scope rows were dropped above, so retaining their ids
            // here would create orphaned foreign keys when the generic tables
            // are created.
            migrationBuilder.Sql(
                """
                UPDATE flowbit.execution_tokens
                SET "GatewayBranchId" = NULL;

                UPDATE flowbit.node_executions
                SET "EntryGatewayBranchId" = NULL,
                    "ExitGatewayBranchId" = NULL;
                """);

            migrationBuilder.AddColumn<long[]>(
                name: "ComplexDrainStateIds",
                schema: "flowbit",
                table: "execution_tokens",
                type: "bigint[]",
                nullable: false,
                defaultValueSql: "'{}'::bigint[]");

            migrationBuilder.AddColumn<int>(
                name: "ComplexGatewayCycle",
                schema: "flowbit",
                table: "execution_tokens",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ComplexGatewayStateId",
                schema: "flowbit",
                table: "execution_tokens",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "complex_gateway_states",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    GatewayNodeId = table.Column<int>(type: "integer", nullable: false),
                    Phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Cycle = table.Column<int>(type: "integer", nullable: false),
                    ContributingFlowIds = table.Column<int[]>(type: "integer[]", nullable: false, defaultValueSql: "'{}'::integer[]"),
                    RemainingFlowIds = table.Column<int[]>(type: "integer[]", nullable: false, defaultValueSql: "'{}'::integer[]"),
                    ActivationDrainStateIds = table.Column<long[]>(type: "bigint[]", nullable: false, defaultValueSql: "'{}'::bigint[]"),
                    DrainingTokenIds = table.Column<long[]>(type: "bigint[]", nullable: false, defaultValueSql: "'{}'::bigint[]"),
                    ActiveExecutionId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_complex_gateway_states", x => x.Id);
                    table.CheckConstraint("CK_complex_gateway_states_activation_drain_states", "\"Phase\" <> 'waitingForStart' OR cardinality(\"ActivationDrainStateIds\") = 0");
                    table.CheckConstraint("CK_complex_gateway_states_cycle", "\"Cycle\" >= 0");
                    table.CheckConstraint("CK_complex_gateway_states_draining_tokens", "\"Phase\" = 'interruptedDraining' OR cardinality(\"DrainingTokenIds\") = 0");
                    table.CheckConstraint("CK_complex_gateway_states_phase", "\"Phase\" IN ('waitingForStart', 'waitingForReset', 'interruptedDraining')");
                    table.ForeignKey(
                        name: "FK_complex_gateway_states_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "gateway_branches",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExecutionId = table.Column<long>(type: "bigint", nullable: false),
                    OriginatingFlowId = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_branches", x => x.Id);
                    table.CheckConstraint("CK_gateway_branches_completed_at", "(\"Status\" = 'active' AND \"CompletedAt\" IS NULL) OR (\"Status\" <> 'active' AND \"CompletedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_gateway_branches_ordinal", "\"Ordinal\" >= 0");
                    table.CheckConstraint("CK_gateway_branches_status", "\"Status\" IN ('active', 'merged', 'completed', 'interrupted', 'cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "gateway_executions",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    GatewayNodeId = table.Column<int>(type: "integer", nullable: false),
                    GatewayType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Cycle = table.Column<int>(type: "integer", nullable: true),
                    SelectedFlowIds = table.Column<int[]>(type: "integer[]", nullable: false, defaultValueSql: "'{}'::integer[]"),
                    ParentBranchId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CompletionReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    InterruptingNodeId = table.Column<int>(type: "integer", nullable: true),
                    InterruptingTokenId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_executions", x => x.Id);
                    table.CheckConstraint("CK_gateway_executions_completed_at", "(\"Status\" = 'active' AND \"CompletedAt\" IS NULL) OR (\"Status\" <> 'active' AND \"CompletedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_gateway_executions_direction", "\"Direction\" IN ('split', 'merge')");
                    table.CheckConstraint("CK_gateway_executions_gateway_type", "\"GatewayType\" IN ('parallelGateway', 'inclusiveGateway', 'complexGateway')");
                    table.CheckConstraint("CK_gateway_executions_phase_cycle", "(\"GatewayType\" = 'complexGateway' AND \"Phase\" IN ('start', 'reset') AND \"Cycle\" IS NOT NULL AND \"Cycle\" >= 0) OR (\"GatewayType\" <> 'complexGateway' AND \"Phase\" IS NULL AND \"Cycle\" IS NULL)");
                    table.CheckConstraint("CK_gateway_executions_selected_flows", "cardinality(\"SelectedFlowIds\") >= 1 OR (\"GatewayType\" = 'complexGateway' AND \"Phase\" = 'reset')");
                    table.CheckConstraint("CK_gateway_executions_status", "\"Status\" IN ('active', 'joined', 'completed', 'interrupted', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_gateway_executions_execution_tokens_InterruptingTokenId",
                        column: x => x.InterruptingTokenId,
                        principalSchema: "flowbit",
                        principalTable: "execution_tokens",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_gateway_executions_gateway_branches_ParentBranchId",
                        column: x => x.ParentBranchId,
                        principalSchema: "flowbit",
                        principalTable: "gateway_branches",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_gateway_executions_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions",
                sql: "((\"Status\" IN ('pending', 'active') AND \"CompletionReason\" IS NULL) OR (\"Status\" IN ('completed', 'cancelled', 'faulted', 'merged') AND \"CompletionReason\" IN ('normal', 'userAction', 'messageDelivery', 'multiInstanceItem', 'multiInstanceCompleted', 'multiInstanceInterrupt', 'boundaryCaught', 'normalEnd', 'terminateEnd', 'errorEnd', 'instanceCancelled', 'gatewayScopeCancelled', 'gatewayJoinMerged', 'parallelFork', 'parallelJoin', 'inclusiveSplit', 'inclusiveMerge', 'complexActivation', 'complexReset', 'scopedInterrupt', 'scopedInterruptSkipped')))");

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_ComplexDrainStateIds",
                schema: "flowbit",
                table: "execution_tokens",
                column: "ComplexDrainStateIds",
                filter: "\"Status\" = 'active' AND cardinality(\"ComplexDrainStateIds\") > 0")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_ComplexGatewayStateId_ComplexGatewayCycle_~",
                schema: "flowbit",
                table: "execution_tokens",
                columns: new[] { "ComplexGatewayStateId", "ComplexGatewayCycle", "Status", "ArrivedViaFlowId", "Id" },
                filter: "\"ComplexGatewayStateId\" IS NOT NULL AND \"Status\" = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_GatewayBranchId_Status",
                schema: "flowbit",
                table: "execution_tokens",
                columns: new[] { "GatewayBranchId", "Status" },
                filter: "\"GatewayBranchId\" IS NOT NULL AND \"Status\" = 'active'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_execution_tokens_complex_gateway_registration",
                schema: "flowbit",
                table: "execution_tokens",
                sql: "(\"ComplexGatewayStateId\" IS NULL AND \"ComplexGatewayCycle\" IS NULL) OR (\"ComplexGatewayStateId\" IS NOT NULL AND \"ComplexGatewayCycle\" IS NOT NULL AND \"ComplexGatewayCycle\" >= 0)");

            migrationBuilder.CreateIndex(
                name: "IX_complex_gateway_states_ActiveExecutionId",
                schema: "flowbit",
                table: "complex_gateway_states",
                column: "ActiveExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_complex_gateway_states_InstanceId_GatewayNodeId",
                schema: "flowbit",
                table: "complex_gateway_states",
                columns: new[] { "InstanceId", "GatewayNodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_complex_gateway_states_InstanceId_Phase",
                schema: "flowbit",
                table: "complex_gateway_states",
                columns: new[] { "InstanceId", "Phase" },
                filter: "\"Phase\" <> 'waitingForStart'");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_branches_ExecutionId_Ordinal",
                schema: "flowbit",
                table: "gateway_branches",
                columns: new[] { "ExecutionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gateway_branches_ExecutionId_OriginatingFlowId",
                schema: "flowbit",
                table: "gateway_branches",
                columns: new[] { "ExecutionId", "OriginatingFlowId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gateway_branches_ExecutionId_Status",
                schema: "flowbit",
                table: "gateway_branches",
                columns: new[] { "ExecutionId", "Status" },
                filter: "\"Status\" = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_executions_InstanceId_GatewayNodeId_Status",
                schema: "flowbit",
                table: "gateway_executions",
                columns: new[] { "InstanceId", "GatewayNodeId", "Status" },
                filter: "\"Status\" = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_executions_InstanceId_Status",
                schema: "flowbit",
                table: "gateway_executions",
                columns: new[] { "InstanceId", "Status" },
                filter: "\"Status\" = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_executions_InterruptingTokenId",
                schema: "flowbit",
                table: "gateway_executions",
                column: "InterruptingTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_executions_ParentBranchId_Status",
                schema: "flowbit",
                table: "gateway_executions",
                columns: new[] { "ParentBranchId", "Status" },
                filter: "\"Status\" = 'active'");

            migrationBuilder.AddForeignKey(
                name: "FK_execution_tokens_complex_gateway_states_ComplexGatewayState~",
                schema: "flowbit",
                table: "execution_tokens",
                column: "ComplexGatewayStateId",
                principalSchema: "flowbit",
                principalTable: "complex_gateway_states",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_execution_tokens_gateway_branches_GatewayBranchId",
                schema: "flowbit",
                table: "execution_tokens",
                column: "GatewayBranchId",
                principalSchema: "flowbit",
                principalTable: "gateway_branches",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_node_executions_gateway_branches_EntryGatewayBranchId",
                schema: "flowbit",
                table: "node_executions",
                column: "EntryGatewayBranchId",
                principalSchema: "flowbit",
                principalTable: "gateway_branches",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_node_executions_gateway_branches_ExitGatewayBranchId",
                schema: "flowbit",
                table: "node_executions",
                column: "ExitGatewayBranchId",
                principalSchema: "flowbit",
                principalTable: "gateway_branches",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_complex_gateway_states_gateway_executions_ActiveExecutionId",
                schema: "flowbit",
                table: "complex_gateway_states",
                column: "ActiveExecutionId",
                principalSchema: "flowbit",
                principalTable: "gateway_executions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_gateway_branches_gateway_executions_ExecutionId",
                schema: "flowbit",
                table: "gateway_branches",
                column: "ExecutionId",
                principalSchema: "flowbit",
                principalTable: "gateway_executions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_execution_tokens_complex_gateway_states_ComplexGatewayState~",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropForeignKey(
                name: "FK_execution_tokens_gateway_branches_GatewayBranchId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropForeignKey(
                name: "FK_node_executions_gateway_branches_EntryGatewayBranchId",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropForeignKey(
                name: "FK_node_executions_gateway_branches_ExitGatewayBranchId",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropForeignKey(
                name: "FK_gateway_branches_gateway_executions_ExecutionId",
                schema: "flowbit",
                table: "gateway_branches");

            migrationBuilder.DropTable(
                name: "complex_gateway_states",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "gateway_executions",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "gateway_branches",
                schema: "flowbit");

            migrationBuilder.DropCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropIndex(
                name: "IX_execution_tokens_ComplexDrainStateIds",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropIndex(
                name: "IX_execution_tokens_ComplexGatewayStateId_ComplexGatewayCycle_~",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropIndex(
                name: "IX_execution_tokens_GatewayBranchId_Status",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropCheckConstraint(
                name: "CK_execution_tokens_complex_gateway_registration",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "ComplexDrainStateIds",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "ComplexGatewayCycle",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "ComplexGatewayStateId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.RenameColumn(
                name: "ExitGatewayBranchId",
                schema: "flowbit",
                table: "node_executions",
                newName: "ExitParallelBranchId");

            migrationBuilder.RenameColumn(
                name: "EntryGatewayBranchId",
                schema: "flowbit",
                table: "node_executions",
                newName: "EntryParallelBranchId");

            migrationBuilder.RenameIndex(
                name: "IX_node_executions_ExitGatewayBranchId",
                schema: "flowbit",
                table: "node_executions",
                newName: "IX_node_executions_ExitParallelBranchId");

            migrationBuilder.RenameIndex(
                name: "IX_node_executions_EntryGatewayBranchId",
                schema: "flowbit",
                table: "node_executions",
                newName: "IX_node_executions_EntryParallelBranchId");

            migrationBuilder.RenameColumn(
                name: "GatewayBranchId",
                schema: "flowbit",
                table: "execution_tokens",
                newName: "ParallelBranchId");

            migrationBuilder.CreateTable(
                name: "parallel_gateway_branches",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExecutionId = table.Column<long>(type: "bigint", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    OriginatingFlowId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parallel_gateway_branches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "parallel_gateway_executions",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    InterruptingTokenId = table.Column<long>(type: "bigint", nullable: true),
                    ParentBranchId = table.Column<long>(type: "bigint", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletionReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ForkNodeId = table.Column<int>(type: "integer", nullable: false),
                    InterruptingNodeId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parallel_gateway_executions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_parallel_gateway_executions_execution_tokens_InterruptingTo~",
                        column: x => x.InterruptingTokenId,
                        principalSchema: "flowbit",
                        principalTable: "execution_tokens",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_parallel_gateway_executions_parallel_gateway_branches_Paren~",
                        column: x => x.ParentBranchId,
                        principalSchema: "flowbit",
                        principalTable: "parallel_gateway_branches",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_parallel_gateway_executions_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions",
                sql: "((\"Status\" IN ('pending', 'active') AND \"CompletionReason\" IS NULL) OR (\"Status\" IN ('completed', 'cancelled', 'faulted', 'merged') AND \"CompletionReason\" IN ('normal', 'userAction', 'messageDelivery', 'multiInstanceItem', 'multiInstanceCompleted', 'multiInstanceInterrupt', 'boundaryCaught', 'normalEnd', 'terminateEnd', 'errorEnd', 'instanceCancelled', 'parallelScopeCancelled', 'parallelJoinMerged', 'parallelFork', 'parallelJoin', 'parallelInterrupt', 'parallelInterruptSkipped')))");

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_ParallelBranchId_Status",
                schema: "flowbit",
                table: "execution_tokens",
                columns: new[] { "ParallelBranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_parallel_gateway_branches_ExecutionId_Ordinal",
                schema: "flowbit",
                table: "parallel_gateway_branches",
                columns: new[] { "ExecutionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_parallel_gateway_branches_ExecutionId_OriginatingFlowId",
                schema: "flowbit",
                table: "parallel_gateway_branches",
                columns: new[] { "ExecutionId", "OriginatingFlowId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_parallel_gateway_branches_ExecutionId_Status",
                schema: "flowbit",
                table: "parallel_gateway_branches",
                columns: new[] { "ExecutionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_parallel_gateway_executions_InstanceId_ForkNodeId_Status",
                schema: "flowbit",
                table: "parallel_gateway_executions",
                columns: new[] { "InstanceId", "ForkNodeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_parallel_gateway_executions_InstanceId_Status",
                schema: "flowbit",
                table: "parallel_gateway_executions",
                columns: new[] { "InstanceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_parallel_gateway_executions_InterruptingTokenId",
                schema: "flowbit",
                table: "parallel_gateway_executions",
                column: "InterruptingTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_parallel_gateway_executions_ParentBranchId_Status",
                schema: "flowbit",
                table: "parallel_gateway_executions",
                columns: new[] { "ParentBranchId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_execution_tokens_parallel_gateway_branches_ParallelBranchId",
                schema: "flowbit",
                table: "execution_tokens",
                column: "ParallelBranchId",
                principalSchema: "flowbit",
                principalTable: "parallel_gateway_branches",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_node_executions_parallel_gateway_branches_EntryParallelBran~",
                schema: "flowbit",
                table: "node_executions",
                column: "EntryParallelBranchId",
                principalSchema: "flowbit",
                principalTable: "parallel_gateway_branches",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_node_executions_parallel_gateway_branches_ExitParallelBranc~",
                schema: "flowbit",
                table: "node_executions",
                column: "ExitParallelBranchId",
                principalSchema: "flowbit",
                principalTable: "parallel_gateway_branches",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_parallel_gateway_branches_parallel_gateway_executions_Execu~",
                schema: "flowbit",
                table: "parallel_gateway_branches",
                column: "ExecutionId",
                principalSchema: "flowbit",
                principalTable: "parallel_gateway_executions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
