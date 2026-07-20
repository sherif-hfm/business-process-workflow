using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddErrorEndFaultContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FaultCode",
                table: "execution_tokens",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FaultDescription",
                table: "execution_tokens",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE execution_tokens
                SET "FaultDescription" = "NodeName"
                WHERE "NodeType" = 'errorEndEvent'
                  AND "Status" = 'faulted'
                  AND "FaultDescription" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FaultCode",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "FaultDescription",
                table: "execution_tokens");
        }
    }
}
