using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowBusinessKeyLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_workflow_business_key_claims_workflow_instances_ActiveInsta~",
                table: "workflow_business_key_claims",
                column: "ActiveInstanceId",
                principalTable: "workflow_instances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_business_key_claims_workflow_instances_LastInstanc~",
                table: "workflow_business_key_claims",
                column: "LastInstanceId",
                principalTable: "workflow_instances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_instances_workflow_business_key_claims_WorkflowKey~",
                table: "workflow_instances",
                columns: new[] { "WorkflowKey", "BusinessKey" },
                principalTable: "workflow_business_key_claims",
                principalColumns: new[] { "WorkflowKey", "BusinessKey" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_workflow_business_key_claims_workflow_instances_ActiveInsta~",
                table: "workflow_business_key_claims");

            migrationBuilder.DropForeignKey(
                name: "FK_workflow_business_key_claims_workflow_instances_LastInstanc~",
                table: "workflow_business_key_claims");

            migrationBuilder.DropForeignKey(
                name: "FK_workflow_instances_workflow_business_key_claims_WorkflowKey~",
                table: "workflow_instances");
        }
    }
}
