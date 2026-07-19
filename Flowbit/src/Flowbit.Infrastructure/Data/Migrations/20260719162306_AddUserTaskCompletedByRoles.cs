using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTaskCompletedByRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "CompletedByRoles",
                table: "user_tasks",
                type: "text[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedByRoles",
                table: "user_tasks");
        }
    }
}
