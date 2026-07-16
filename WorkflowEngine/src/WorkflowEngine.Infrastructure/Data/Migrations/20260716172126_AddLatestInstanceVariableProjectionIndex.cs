using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkflowEngine.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLatestInstanceVariableProjectionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_instance_variables_InstanceId_VariableName",
                table: "instance_variables");

            migrationBuilder.CreateIndex(
                name: "IX_instance_variables_InstanceId_VariableName_Id",
                table: "instance_variables",
                columns: new[] { "InstanceId", "VariableName", "Id" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_instance_variables_InstanceId_VariableName_Id",
                table: "instance_variables");

            migrationBuilder.CreateIndex(
                name: "IX_instance_variables_InstanceId_VariableName",
                table: "instance_variables",
                columns: new[] { "InstanceId", "VariableName" });
        }
    }
}
