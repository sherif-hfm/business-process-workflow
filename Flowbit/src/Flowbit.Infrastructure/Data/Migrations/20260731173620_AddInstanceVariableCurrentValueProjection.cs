using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceVariableCurrentValueProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "instance_variable_current_values",
                schema: "flowbit",
                columns: table => new
                {
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    VariableName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SourceVariableId = table.Column<long>(type: "bigint", nullable: false),
                    ValueJson = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    SetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instance_variable_current_values", x => new { x.InstanceId, x.VariableName });
                    table.ForeignKey(
                        name: "FK_instance_variable_current_values_workflow_instances_Instanc~",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_current_values_ValueJson_gin",
                schema: "flowbit",
                table: "instance_variable_current_values",
                column: "ValueJson")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_current_values_VariableName_InstanceId",
                schema: "flowbit",
                table: "instance_variable_current_values",
                columns: new[] { "VariableName", "InstanceId" });

            migrationBuilder.Sql(
                """
                CREATE INDEX "IX_iv_current_name_root_string_ci_instance"
                ON flowbit.instance_variable_current_values
                    ("VariableName", lower("ValueJson" #>> '{}'), "InstanceId")
                WHERE jsonb_typeof("ValueJson") = 'string';

                CREATE INDEX "IX_iv_current_name_root_number_instance"
                ON flowbit.instance_variable_current_values
                    ("VariableName",
                     (CASE WHEN jsonb_typeof("ValueJson") = 'number'
                           THEN ("ValueJson" #>> '{}')::numeric
                           ELSE NULL END),
                     "InstanceId")
                WHERE jsonb_typeof("ValueJson") = 'number';

                CREATE FUNCTION flowbit.sync_instance_variable_current_value()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    INSERT INTO flowbit.instance_variable_current_values AS current_value
                        ("InstanceId", "VariableName", "SourceVariableId", "ValueJson", "SetAt")
                    VALUES
                        (NEW."InstanceId", NEW."VariableName", NEW."Id", NEW."ValueJson", NEW."SetAt")
                    ON CONFLICT ("InstanceId", "VariableName") DO UPDATE
                    SET
                        "SourceVariableId" = EXCLUDED."SourceVariableId",
                        "ValueJson" = EXCLUDED."ValueJson",
                        "SetAt" = EXCLUDED."SetAt"
                    WHERE EXCLUDED."SourceVariableId" > current_value."SourceVariableId";

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER instance_variables_sync_current_value
                AFTER INSERT ON flowbit.instance_variables
                FOR EACH ROW
                EXECUTE FUNCTION flowbit.sync_instance_variable_current_value();
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO flowbit.instance_variable_current_values AS current_value
                    ("InstanceId", "VariableName", "SourceVariableId", "ValueJson", "SetAt")
                SELECT
                    latest."InstanceId",
                    latest."VariableName",
                    latest."Id",
                    latest."ValueJson",
                    latest."SetAt"
                FROM
                (
                    SELECT DISTINCT ON (variable."InstanceId", variable."VariableName")
                        variable."InstanceId",
                        variable."VariableName",
                        variable."Id",
                        variable."ValueJson",
                        variable."SetAt"
                    FROM flowbit.instance_variables AS variable
                    ORDER BY variable."InstanceId", variable."VariableName", variable."Id" DESC
                ) AS latest
                ON CONFLICT ("InstanceId", "VariableName") DO UPDATE
                SET
                    "SourceVariableId" = EXCLUDED."SourceVariableId",
                    "ValueJson" = EXCLUDED."ValueJson",
                    "SetAt" = EXCLUDED."SetAt"
                WHERE EXCLUDED."SourceVariableId" > current_value."SourceVariableId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS instance_variables_sync_current_value
                    ON flowbit.instance_variables;
                DROP FUNCTION IF EXISTS flowbit.sync_instance_variable_current_value();
                """);

            migrationBuilder.DropTable(
                name: "instance_variable_current_values",
                schema: "flowbit");
        }
    }
}
