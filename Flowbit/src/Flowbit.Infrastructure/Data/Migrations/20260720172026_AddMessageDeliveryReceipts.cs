using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageDeliveryReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_delivery_receipts",
                columns: table => new
                {
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    WaitHistoryId = table.Column<long>(type: "bigint", nullable: false),
                    SourceNodeId = table.Column<int>(type: "integer", nullable: false),
                    CorrelationHeaderName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ProofVersion = table.Column<short>(type: "smallint", nullable: false),
                    CredentialProofSalt = table.Column<byte[]>(type: "bytea", nullable: false),
                    CredentialProofHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    EnvelopeProofSalt = table.Column<byte[]>(type: "bytea", nullable: false),
                    EnvelopeProofHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_delivery_receipts", x => new { x.InstanceId, x.IdempotencyKey });
                    table.ForeignKey(
                        name: "FK_message_delivery_receipts_instance_history_WaitHistoryId",
                        column: x => x.WaitHistoryId,
                        principalTable: "instance_history",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_message_delivery_receipts_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_message_delivery_receipts_WaitHistoryId",
                table: "message_delivery_receipts",
                column: "WaitHistoryId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_delivery_receipts");
        }
    }
}
