using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSettingDescriptionsAndManagementRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "flowbit",
                table: "workflow_settings",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "flowbit",
                table: "engine_settings",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.Sql("""
                WITH desired("Namespace", "Key", "FullKey", "Value", "Description") AS
                (
                    VALUES
                        (
                            'Settings',
                            'RequiredRole',
                            'Settings.RequiredRole',
                            'admin',
                            'Comma-separated roles allowed to view and manage engine and workflow settings. Missing or blank values default to admin.'
                        )
                )
                INSERT INTO flowbit.engine_settings
                    ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
                SELECT
                    desired."Namespace",
                    desired."Key",
                    desired."Value",
                    desired."Description",
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM desired
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM flowbit.engine_settings AS existing
                    WHERE
                        (existing."Namespace" = desired."Namespace"
                         AND existing."Key" = desired."Key")
                        OR
                        (BTRIM(COALESCE(existing."Namespace", '')) = ''
                         AND existing."Key" = desired."FullKey")
                )
                ON CONFLICT ("Namespace", "Key") DO NOTHING;
                """);

            migrationBuilder.Sql("""
                WITH defaults("Namespace", "Key", "FullKey", "Description") AS
                (
                    VALUES
                        ('Settings', 'RequiredRole', 'Settings.RequiredRole',
                         'Comma-separated roles allowed to view and manage engine and workflow settings. Missing or blank values default to admin.'),
                        ('Workflow', 'RequiredRole', 'Workflow.RequiredRole',
                         'Comma-separated roles allowed to manage workflow definitions and running-instance version changes. Missing or blank values default to admin.'),
                        ('WorkflowInstances', 'RequiredRole', 'WorkflowInstances.RequiredRole',
                         'Comma-separated roles granted global workflow-instance list access in addition to workflow assignment roles. Missing or blank values default to admin.'),
                        ('NodeExecution', 'RequiredRole', 'NodeExecution.RequiredRole',
                         'Comma-separated roles granted global visibility of node execution activity. Missing or blank values default to admin.'),
                        ('WorkflowJobs', 'RequiredRole', 'WorkflowJobs.RequiredRole',
                         'Comma-separated roles allowed to view and operate durable workflow jobs and incidents. Missing or blank values default to admin.'),
                        ('Delegation', 'AdminRoles', 'Delegation.AdminRoles',
                         'Comma-separated roles allowed to administer user delegations and delegation policies. Missing or blank values default to admin.'),
                        ('Workflow.Gateway', 'MaxActiveTokens', 'Workflow.Gateway.MaxActiveTokens',
                         'Maximum active execution tokens allowed while routing an instance through gateways. Invalid or missing values default to 1000.'),
                        ('Workflow.MultiInstance', 'MaxInstances', 'Workflow.MultiInstance.MaxInstances',
                         'Maximum child items allowed for one multi-instance user task. Invalid or missing values default to 1000.'),
                        ('Workflow.Async', 'MaxConsecutiveAutomaticActivations', 'Workflow.Async.MaxConsecutiveAutomaticActivations',
                         'Maximum consecutive automatic activations allowed before loop protection stops processing. Invalid or missing values default to 1000.')
                )
                UPDATE flowbit.engine_settings AS existing
                SET "Description" = defaults."Description"
                FROM defaults
                WHERE existing."Description" IS NULL
                  AND
                  (
                      (existing."Namespace" = defaults."Namespace"
                       AND existing."Key" = defaults."Key")
                      OR
                      (BTRIM(COALESCE(existing."Namespace", '')) = ''
                       AND existing."Key" = defaults."FullKey")
                  );
                """);

            migrationBuilder.Sql("""
                WITH defaults("Namespace", "Name", "FullName", "Description") AS
                (
                    VALUES
                        ('examples', 'messageClientId', 'examples.messageClientId',
                         'Example client identifier exposed to workflows as setting.examples.messageClientId.'),
                        ('examples', 'messageCorrelation', 'examples.messageCorrelation',
                         'Example correlation value exposed to workflows as setting.examples.messageCorrelation.')
                )
                UPDATE flowbit.workflow_settings AS existing
                SET "Description" = defaults."Description"
                FROM defaults
                WHERE existing."Description" IS NULL
                  AND
                  (
                      (LOWER(COALESCE(existing."Namespace", '')) = LOWER(defaults."Namespace")
                       AND LOWER(existing."Name") = LOWER(defaults."Name"))
                      OR
                      (BTRIM(COALESCE(existing."Namespace", '')) = ''
                       AND LOWER(existing."Name") = LOWER(defaults."FullName"))
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                schema: "flowbit",
                table: "workflow_settings");

            migrationBuilder.DropColumn(
                name: "Description",
                schema: "flowbit",
                table: "engine_settings");
        }
    }
}
