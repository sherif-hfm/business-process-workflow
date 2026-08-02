using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeedDefaultSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                WITH defaults("Namespace", "Key", "FullKey", "Value") AS
                (
                    VALUES
                        ('Workflow', 'RequiredRole', 'Workflow.RequiredRole', 'admin'),
                        ('WorkflowInstances', 'RequiredRole', 'WorkflowInstances.RequiredRole', 'admin'),
                        ('NodeExecution', 'RequiredRole', 'NodeExecution.RequiredRole', 'admin'),
                        ('WorkflowJobs', 'RequiredRole', 'WorkflowJobs.RequiredRole', 'admin'),
                        ('Delegation', 'AdminRoles', 'Delegation.AdminRoles', 'admin'),
                        ('Workflow.Gateway', 'MaxActiveTokens', 'Workflow.Gateway.MaxActiveTokens', '1000'),
                        ('Workflow.MultiInstance', 'MaxInstances', 'Workflow.MultiInstance.MaxInstances', '1000'),
                        ('Workflow.Async', 'MaxConsecutiveAutomaticActivations', 'Workflow.Async.MaxConsecutiveAutomaticActivations', '1000')
                )
                INSERT INTO flowbit.engine_settings
                    ("Namespace", "Key", "Value", "CreatedAt", "UpdatedAt")
                SELECT
                    defaults."Namespace",
                    defaults."Key",
                    defaults."Value",
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM defaults
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM flowbit.engine_settings AS existing
                    WHERE
                        (existing."Namespace" = defaults."Namespace"
                         AND existing."Key" = defaults."Key")
                        OR
                        (COALESCE(existing."Namespace", '') = ''
                         AND existing."Key" = defaults."FullKey")
                )
                ON CONFLICT ("Namespace", "Key") DO NOTHING;
                """);

            migrationBuilder.Sql("""
                WITH defaults("Namespace", "Name", "FullName", "Value") AS
                (
                    VALUES
                        ('examples', 'messageClientId', 'examples.messageClientId', '"example-message-client"'::jsonb),
                        ('examples', 'messageCorrelation', 'examples.messageCorrelation', '"orders:inbound"'::jsonb)
                )
                INSERT INTO flowbit.workflow_settings
                    ("Namespace", "Name", "Value", "CreatedAt", "UpdatedAt")
                SELECT
                    defaults."Namespace",
                    defaults."Name",
                    defaults."Value",
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM defaults
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM flowbit.workflow_settings AS existing
                    WHERE
                        (LOWER(COALESCE(existing."Namespace", '')) = LOWER(defaults."Namespace")
                         AND LOWER(existing."Name") = LOWER(defaults."Name"))
                        OR
                        (COALESCE(existing."Namespace", '') = ''
                         AND LOWER(existing."Name") = LOWER(defaults."FullName"))
                )
                ON CONFLICT ("Namespace", "Name") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally preserve these operator-owned settings. The migration
            // cannot distinguish a row it inserted from an identical pre-existing
            // row, and deleting customized runtime configuration would be unsafe.
        }
    }
}
