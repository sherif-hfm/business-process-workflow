using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Flowbit.Infrastructure.Data;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260811120000_AddUserTaskInboxVisibility")]
public sealed class AddUserTaskInboxVisibility : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "workflow_definition_user_task_conditions",
            schema: "flowbit",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                NodeId = table.Column<int>(type: "integer", nullable: false),
                NodeName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                NodeExternalId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                ProgramVersion = table.Column<int>(type: "integer", nullable: false),
                ProgramJson = table.Column<System.Text.Json.JsonDocument>(type: "jsonb", nullable: false),
                VariableNames = table.Column<List<string>>(type: "text[]", nullable: false),
                ExternalReferences = table.Column<List<string>>(type: "text[]", nullable: false),
                SemanticFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workflow_definition_user_task_conditions", x => x.Id);
                table.CheckConstraint(
                    "CK_workflow_definition_user_task_conditions_program_shape",
                    "(jsonb_typeof(\"ProgramJson\") = 'object' AND jsonb_typeof(\"ProgramJson\" -> 'version') = 'number' AND \"ProgramJson\" ->> 'version' = \"ProgramVersion\"::text AND jsonb_typeof(\"ProgramJson\" -> 'variables') = 'array' AND jsonb_array_length(\"ProgramJson\" -> 'variables') <= 8 AND jsonb_typeof(\"ProgramJson\" -> 'externalReferences') = 'array' AND jsonb_array_length(\"ProgramJson\" -> 'externalReferences') <= 16 AND jsonb_typeof(\"ProgramJson\" -> 'instructions') = 'array' AND jsonb_array_length(\"ProgramJson\" -> 'instructions') BETWEEN 1 AND 64 AND octet_length(\"ProgramJson\"::text) <= 32768) IS TRUE");
                table.CheckConstraint(
                    "CK_workflow_definition_user_task_conditions_program_version",
                    "\"ProgramVersion\" = 1");
                table.ForeignKey(
                    name: "FK_workflow_definition_user_task_conditions_workflow_definitions_WorkflowDefinitionId",
                    column: x => x.WorkflowDefinitionId,
                    principalSchema: "flowbit",
                    principalTable: "workflow_definitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.AddColumn<long>(
            name: "InboxVisibilityConditionId",
            schema: "flowbit",
            table: "user_tasks",
            type: "bigint",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_user_tasks_InboxVisibilityConditionId",
            schema: "flowbit",
            table: "user_tasks",
            column: "InboxVisibilityConditionId");

        migrationBuilder.CreateIndex(
            name: "IX_workflow_definition_user_task_conditions_SemanticFingerprint",
            schema: "flowbit",
            table: "workflow_definition_user_task_conditions",
            column: "SemanticFingerprint");

        migrationBuilder.CreateIndex(
            name: "IX_workflow_definition_user_task_conditions_WorkflowDefinitionId_NodeId",
            schema: "flowbit",
            table: "workflow_definition_user_task_conditions",
            columns: new[] { "WorkflowDefinitionId", "NodeId" },
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_user_tasks_workflow_definition_user_task_conditions_InboxVisibilityConditionId",
            schema: "flowbit",
            table: "user_tasks",
            column: "InboxVisibilityConditionId",
            principalSchema: "flowbit",
            principalTable: "workflow_definition_user_task_conditions",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.Sql(EvaluatorSql);
        migrationBuilder.Sql(SnapshotTriggerSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS snapshot_user_task_inbox_visibility_condition ON flowbit.user_tasks;");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS flowbit.snapshot_user_task_inbox_visibility_condition();");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS flowbit.evaluate_inbox_visibility_condition(jsonb, jsonb, jsonb);");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS flowbit.inbox_visibility_compare(jsonb, jsonb, text, text);");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS flowbit.inbox_visibility_to_number(jsonb);");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS flowbit.inbox_visibility_normalize_value(jsonb, text);");

        migrationBuilder.DropForeignKey(
            name: "FK_user_tasks_workflow_definition_user_task_conditions_InboxVisibilityConditionId",
            schema: "flowbit",
            table: "user_tasks");
        migrationBuilder.DropIndex(
            name: "IX_user_tasks_InboxVisibilityConditionId",
            schema: "flowbit",
            table: "user_tasks");
        migrationBuilder.DropColumn(
            name: "InboxVisibilityConditionId",
            schema: "flowbit",
            table: "user_tasks");
        migrationBuilder.DropTable(
            name: "workflow_definition_user_task_conditions",
            schema: "flowbit");
    }

    private const string SnapshotTriggerSql = """
        CREATE OR REPLACE FUNCTION flowbit.snapshot_user_task_inbox_visibility_condition()
        RETURNS trigger
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, pg_temp
        AS $function$
        BEGIN
            SELECT condition."Id"
              INTO NEW."InboxVisibilityConditionId"
              FROM flowbit.workflow_instances instance
              JOIN flowbit.workflow_definition_user_task_conditions condition
                ON condition."WorkflowDefinitionId" = instance."WorkflowDefinitionId"
               AND condition."NodeId" = NEW."NodeId"
             WHERE instance."Id" = NEW."InstanceId";
            RETURN NEW;
        END;
        $function$;

        CREATE TRIGGER snapshot_user_task_inbox_visibility_condition
        BEFORE INSERT ON flowbit.user_tasks
        FOR EACH ROW
        EXECUTE FUNCTION flowbit.snapshot_user_task_inbox_visibility_condition();
        """;

    private const string EvaluatorSql = """
        CREATE OR REPLACE FUNCTION flowbit.inbox_visibility_normalize_value(raw_value jsonb, declared_type text)
        RETURNS jsonb
        LANGUAGE plpgsql
        IMMUTABLE
        PARALLEL SAFE
        SECURITY INVOKER
        SET search_path = pg_catalog, pg_temp
        AS $function$
        DECLARE
            actual_type text;
            scalar text;
            parsed_date date;
            parsed_datetime timestamptz;
            parsed_number numeric;
        BEGIN
            IF raw_value IS NULL OR raw_value = 'null'::jsonb THEN
                RETURN jsonb_build_object('known', false);
            END IF;
            actual_type := jsonb_typeof(raw_value);
            IF declared_type = 'dynamic' THEN
                declared_type := CASE actual_type
                    WHEN 'string' THEN 'string'
                    WHEN 'number' THEN 'number'
                    WHEN 'boolean' THEN 'boolean'
                    ELSE NULL
                END;
            END IF;
            IF declared_type IS NULL THEN
                RETURN jsonb_build_object('known', false);
            END IF;
            scalar := raw_value #>> '{}';
            CASE declared_type
                WHEN 'string' THEN
                    IF actual_type <> 'string' THEN
                        RETURN jsonb_build_object('known', false);
                    END IF;
                    RETURN jsonb_build_object('known', true, 'type', 'string', 'value', scalar);
                WHEN 'boolean' THEN
                    IF actual_type <> 'boolean' THEN
                        RETURN jsonb_build_object('known', false);
                    END IF;
                    RETURN jsonb_build_object('known', true, 'type', 'boolean', 'value', scalar::boolean);
                WHEN 'number' THEN
                    IF actual_type <> 'number' OR length(scalar) > 128 THEN
                        RETURN jsonb_build_object('known', false);
                    END IF;
                    parsed_number := scalar::numeric;
                    RETURN jsonb_build_object('known', true, 'type', 'number', 'value', parsed_number::text);
                WHEN 'date' THEN
                    IF actual_type <> 'string' OR scalar !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' THEN
                        RETURN jsonb_build_object('known', false);
                    END IF;
                    parsed_date := scalar::date;
                    IF to_char(parsed_date, 'YYYY-MM-DD') <> scalar THEN
                        RETURN jsonb_build_object('known', false);
                    END IF;
                    RETURN jsonb_build_object('known', true, 'type', 'date', 'value', scalar);
                WHEN 'datetime' THEN
                    IF actual_type <> 'string'
                       OR scalar !~ '^[0-9]{4}-(0[1-9]|1[0-2])-([0-2][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]+)?(Z|[+-]([01][0-9]|2[0-3]):[0-5][0-9])$' THEN
                        RETURN jsonb_build_object('known', false);
                    END IF;
                    parsed_datetime := scalar::timestamptz;
                    RETURN jsonb_build_object('known', true, 'type', 'datetime', 'value', parsed_datetime::text);
                ELSE
                    RETURN jsonb_build_object('known', false);
            END CASE;
        EXCEPTION WHEN data_exception THEN
            RETURN jsonb_build_object('known', false);
        END;
        $function$;

        CREATE OR REPLACE FUNCTION flowbit.inbox_visibility_to_number(item jsonb)
        RETURNS jsonb
        LANGUAGE plpgsql
        IMMUTABLE
        PARALLEL SAFE
        SECURITY INVOKER
        SET search_path = pg_catalog, pg_temp
        AS $function$
        DECLARE
            scalar text;
            exponent_text text;
            parsed numeric;
        BEGIN
            IF NOT COALESCE((item ->> 'known')::boolean, false) THEN
                RETURN jsonb_build_object('known', false);
            END IF;
            IF item ->> 'type' = 'number' THEN
                RETURN item;
            END IF;
            IF item ->> 'type' <> 'string' THEN
                RETURN jsonb_build_object('known', false);
            END IF;
            scalar := btrim(item ->> 'value', E' \t\n\r\f' || chr(11));
            IF length(scalar) = 0 OR length(scalar) > 128
               OR scalar !~ '^[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)([eE][+-]?[0-9]+)?$' THEN
                RETURN jsonb_build_object('known', false);
            END IF;
            exponent_text := substring(scalar from '[eE]([+-]?[0-9]+)$');
            IF exponent_text IS NOT NULL AND abs(exponent_text::integer) > 100 THEN
                RETURN jsonb_build_object('known', false);
            END IF;
            parsed := scalar::numeric;
            RETURN jsonb_build_object('known', true, 'type', 'number', 'value', parsed::text);
        EXCEPTION WHEN data_exception THEN
            RETURN jsonb_build_object('known', false);
        END;
        $function$;

        CREATE OR REPLACE FUNCTION flowbit.inbox_visibility_compare(
            left_item jsonb,
            right_item jsonb,
            operator_name text,
            comparison_type text)
        RETURNS jsonb
        LANGUAGE plpgsql
        IMMUTABLE
        PARALLEL SAFE
        SECURITY INVOKER
        SET search_path = pg_catalog, pg_temp
        AS $function$
        DECLARE
            left_type text;
            right_type text;
            left_text text;
            right_text text;
            comparison integer;
            result boolean;
            normalized jsonb;
        BEGIN
            IF NOT COALESCE((left_item ->> 'known')::boolean, false)
               OR NOT COALESCE((right_item ->> 'known')::boolean, false) THEN
                RETURN jsonb_build_object('known', false);
            END IF;
            IF operator_name IN ('greater', 'greaterOrEqual', 'less', 'lessOrEqual')
               AND (comparison_type IS NULL
                    OR comparison_type NOT IN ('number', 'date', 'datetime')) THEN
                RETURN jsonb_build_object('known', false);
            END IF;
            IF operator_name IN ('equal', 'notEqual')
               AND (comparison_type IS NULL
                    OR comparison_type NOT IN ('string', 'number', 'boolean', 'date', 'datetime', 'dynamic')) THEN
                RETURN jsonb_build_object('known', false);
            END IF;
            left_type := left_item ->> 'type';
            right_type := right_item ->> 'type';
            IF comparison_type = 'dynamic' THEN
                IF left_type <> right_type OR left_type NOT IN ('string', 'number', 'boolean') THEN
                    RETURN jsonb_build_object('known', false);
                END IF;
                comparison_type := left_type;
            END IF;
            IF comparison_type IN ('date', 'datetime') THEN
                IF left_type = 'string' THEN
                    normalized := flowbit.inbox_visibility_normalize_value(
                        to_jsonb(left_item ->> 'value'), comparison_type);
                    left_item := normalized;
                    left_type := left_item ->> 'type';
                END IF;
                IF right_type = 'string' THEN
                    normalized := flowbit.inbox_visibility_normalize_value(
                        to_jsonb(right_item ->> 'value'), comparison_type);
                    right_item := normalized;
                    right_type := right_item ->> 'type';
                END IF;
            END IF;
            IF left_type <> comparison_type OR right_type <> comparison_type
               OR NOT COALESCE((left_item ->> 'known')::boolean, false)
               OR NOT COALESCE((right_item ->> 'known')::boolean, false) THEN
                RETURN jsonb_build_object('known', false);
            END IF;
            left_text := left_item ->> 'value';
            right_text := right_item ->> 'value';
            comparison := CASE comparison_type
                WHEN 'string' THEN CASE
                    WHEN lower(left_text) < lower(right_text) THEN -1
                    WHEN lower(left_text) > lower(right_text) THEN 1 ELSE 0 END
                WHEN 'number' THEN CASE
                    WHEN left_text::numeric < right_text::numeric THEN -1
                    WHEN left_text::numeric > right_text::numeric THEN 1 ELSE 0 END
                WHEN 'boolean' THEN CASE
                    WHEN left_text::boolean = right_text::boolean THEN 0
                    WHEN left_text::boolean THEN 1 ELSE -1 END
                WHEN 'date' THEN CASE
                    WHEN left_text::date < right_text::date THEN -1
                    WHEN left_text::date > right_text::date THEN 1 ELSE 0 END
                WHEN 'datetime' THEN CASE
                    WHEN left_text::timestamptz < right_text::timestamptz THEN -1
                    WHEN left_text::timestamptz > right_text::timestamptz THEN 1 ELSE 0 END
                ELSE NULL
            END;
            IF comparison IS NULL THEN
                RETURN jsonb_build_object('known', false);
            END IF;
            result := CASE operator_name
                WHEN 'equal' THEN comparison = 0
                WHEN 'notEqual' THEN comparison <> 0
                WHEN 'greater' THEN comparison > 0
                WHEN 'greaterOrEqual' THEN comparison >= 0
                WHEN 'less' THEN comparison < 0
                WHEN 'lessOrEqual' THEN comparison <= 0
                ELSE NULL
            END;
            IF result IS NULL THEN
                RETURN jsonb_build_object('known', false);
            END IF;
            RETURN jsonb_build_object('known', true, 'type', 'boolean', 'value', result);
        EXCEPTION WHEN data_exception THEN
            RETURN jsonb_build_object('known', false);
        END;
        $function$;

        CREATE OR REPLACE FUNCTION flowbit.evaluate_inbox_visibility_condition(
            program jsonb,
            variable_values jsonb,
            external_values jsonb)
        RETURNS boolean
        LANGUAGE plpgsql
        IMMUTABLE
        PARALLEL SAFE
        SECURITY INVOKER
        SET search_path = pg_catalog, pg_temp
        AS $function$
        DECLARE
            stack jsonb[] := ARRAY[]::jsonb[];
            stack_size integer := 0;
            instruction jsonb;
            operation text;
            item jsonb;
            left_item jsonb;
            right_item jsonb;
            pool_item jsonb;
            pool_index integer;
            result_number numeric;
            left_known boolean;
            right_known boolean;
            left_boolean boolean;
            right_boolean boolean;
            type_stack text[] := ARRAY[]::text[];
            type_stack_size integer := 0;
            left_static_type text;
            right_static_type text;
            result_static_type text;
            expected_comparison_type text;
            index_text text;
            depth_stack integer[] := ARRAY[]::integer[];
            result_depth integer;
            literal_count integer := 0;
            comparison_count integer := 0;
        BEGIN
            IF program IS NULL OR jsonb_typeof(program) IS DISTINCT FROM 'object'
               OR jsonb_typeof(program -> 'version') IS DISTINCT FROM 'number'
               OR program ->> 'version' IS DISTINCT FROM '1'
               OR jsonb_typeof(program -> 'variables') IS DISTINCT FROM 'array'
               OR jsonb_typeof(program -> 'externalReferences') IS DISTINCT FROM 'array'
               OR jsonb_typeof(program -> 'instructions') IS DISTINCT FROM 'array'
               OR COALESCE(jsonb_array_length(program -> 'variables'), 9) > 8
               OR COALESCE(jsonb_array_length(program -> 'externalReferences'), 17) > 16
               OR COALESCE(jsonb_array_length(program -> 'instructions'), 0) NOT BETWEEN 1 AND 64 THEN
                RETURN false;
            END IF;

            -- Validate the entire trusted-data program before evaluating any
            -- runtime values. UNKNOWN is reserved for valid programs whose
            -- inputs are missing/invalid; malformed stored bytecode must never
            -- be rescued by a TRUE OR UNKNOWN branch.
            FOR pool_item IN SELECT value FROM jsonb_array_elements(program -> 'variables')
            LOOP
                IF jsonb_typeof(pool_item) IS DISTINCT FROM 'object'
                   OR jsonb_typeof(pool_item -> 'name') IS DISTINCT FROM 'string'
                   OR COALESCE(pool_item ->> 'name', '') = ''
                   OR pool_item ->> 'type' IS NULL
                   OR pool_item ->> 'type' NOT IN ('string', 'number', 'boolean', 'date', 'datetime') THEN
                    RETURN false;
                END IF;
            END LOOP;
            FOR pool_item IN SELECT value FROM jsonb_array_elements(program -> 'externalReferences')
            LOOP
                IF jsonb_typeof(pool_item) IS DISTINCT FROM 'object'
                   OR jsonb_typeof(pool_item -> 'name') IS DISTINCT FROM 'string'
                   OR COALESCE(pool_item ->> 'name', '') = ''
                   OR pool_item ->> 'type' IS NULL
                   OR pool_item ->> 'type' NOT IN ('string', 'number', 'boolean', 'date', 'datetime', 'dynamic') THEN
                    RETURN false;
                END IF;
            END LOOP;

            FOR instruction IN SELECT value FROM jsonb_array_elements(program -> 'instructions')
            LOOP
                IF jsonb_typeof(instruction) IS DISTINCT FROM 'object'
                   OR jsonb_typeof(instruction -> 'op') IS DISTINCT FROM 'string' THEN
                    RETURN false;
                END IF;
                operation := instruction ->> 'op';
                IF operation = 'literal' THEN
                    literal_count := literal_count + 1;
                    IF literal_count > 16 THEN RETURN false; END IF;
                    result_static_type := instruction ->> 'type';
                    IF result_static_type = 'string' THEN
                        IF jsonb_typeof(instruction -> 'value') IS DISTINCT FROM 'string'
                           OR octet_length(instruction ->> 'value') > 512 THEN
                            RETURN false;
                        END IF;
                    ELSIF result_static_type = 'boolean' THEN
                        IF jsonb_typeof(instruction -> 'value') IS DISTINCT FROM 'boolean' THEN
                            RETURN false;
                        END IF;
                    ELSIF result_static_type = 'number' THEN
                        IF jsonb_typeof(instruction -> 'value') IS DISTINCT FROM 'string'
                           OR NOT COALESCE((flowbit.inbox_visibility_to_number(
                               jsonb_build_object(
                                   'known', true,
                                   'type', 'string',
                                   'value', instruction ->> 'value')) ->> 'known')::boolean, false) THEN
                            RETURN false;
                        END IF;
                    ELSE
                        RETURN false;
                    END IF;
                    type_stack_size := type_stack_size + 1;
                    type_stack[type_stack_size] := result_static_type;
                    depth_stack[type_stack_size] := 1;
                ELSIF operation IN ('variable', 'external') THEN
                    index_text := instruction ->> 'index';
                    IF jsonb_typeof(instruction -> 'index') IS DISTINCT FROM 'number'
                       OR index_text IS NULL
                       OR index_text !~ '^(0|[1-9][0-9]*)$' THEN
                        RETURN false;
                    END IF;
                    pool_index := index_text::integer;
                    IF operation = 'variable' THEN
                        IF pool_index >= jsonb_array_length(program -> 'variables') THEN RETURN false; END IF;
                        result_static_type := program -> 'variables' -> pool_index ->> 'type';
                    ELSE
                        IF pool_index >= jsonb_array_length(program -> 'externalReferences') THEN RETURN false; END IF;
                        result_static_type := program -> 'externalReferences' -> pool_index ->> 'type';
                    END IF;
                    type_stack_size := type_stack_size + 1;
                    type_stack[type_stack_size] := result_static_type;
                    depth_stack[type_stack_size] := 1;
                ELSIF operation IN ('number', 'positive', 'negate', 'not') THEN
                    IF type_stack_size < 1 THEN RETURN false; END IF;
                    result_static_type := type_stack[type_stack_size];
                    IF operation = 'number' THEN
                        IF result_static_type NOT IN ('number', 'string', 'dynamic') THEN RETURN false; END IF;
                        result_static_type := 'number';
                    ELSIF operation IN ('positive', 'negate') THEN
                        IF result_static_type NOT IN ('number', 'dynamic') THEN RETURN false; END IF;
                        result_static_type := 'number';
                    ELSE
                        IF result_static_type NOT IN ('boolean', 'dynamic') THEN RETURN false; END IF;
                        result_static_type := 'boolean';
                    END IF;
                    type_stack[type_stack_size] := result_static_type;
                    result_depth := depth_stack[type_stack_size] + 1;
                    IF result_depth > 8 THEN RETURN false; END IF;
                    depth_stack[type_stack_size] := result_depth;
                ELSE
                    IF type_stack_size < 2 THEN RETURN false; END IF;
                    right_static_type := type_stack[type_stack_size];
                    left_static_type := type_stack[type_stack_size - 1];
                    result_depth := greatest(
                        depth_stack[type_stack_size - 1],
                        depth_stack[type_stack_size]) + 1;
                    IF result_depth > 8 THEN RETURN false; END IF;
                    type_stack_size := type_stack_size - 1;
                    IF operation IN ('add', 'subtract', 'multiply', 'divide', 'modulo') THEN
                        IF left_static_type NOT IN ('number', 'dynamic')
                           OR right_static_type NOT IN ('number', 'dynamic') THEN RETURN false; END IF;
                        result_static_type := 'number';
                    ELSIF operation IN ('and', 'or') THEN
                        IF left_static_type NOT IN ('boolean', 'dynamic')
                           OR right_static_type NOT IN ('boolean', 'dynamic') THEN RETURN false; END IF;
                        result_static_type := 'boolean';
                    ELSIF operation IN ('equal', 'notEqual', 'greater', 'greaterOrEqual', 'less', 'lessOrEqual') THEN
                        comparison_count := comparison_count + 1;
                        IF comparison_count > 16 THEN RETURN false; END IF;
                        expected_comparison_type := NULL;
                        IF left_static_type = right_static_type THEN
                            IF operation IN ('greater', 'greaterOrEqual', 'less', 'lessOrEqual')
                               AND left_static_type NOT IN ('number', 'date', 'datetime') THEN
                                RETURN false;
                            END IF;
                            expected_comparison_type := left_static_type;
                        ELSIF left_static_type = 'dynamic' OR right_static_type = 'dynamic' THEN
                            expected_comparison_type := CASE
                                WHEN left_static_type = 'dynamic' THEN right_static_type
                                ELSE left_static_type
                            END;
                            IF operation IN ('greater', 'greaterOrEqual', 'less', 'lessOrEqual')
                               AND expected_comparison_type NOT IN ('number', 'date', 'datetime') THEN
                                RETURN false;
                            END IF;
                        ELSIF left_static_type IN ('date', 'datetime') AND right_static_type = 'string' THEN
                            expected_comparison_type := left_static_type;
                        ELSIF right_static_type IN ('date', 'datetime') AND left_static_type = 'string' THEN
                            expected_comparison_type := right_static_type;
                        ELSE
                            RETURN false;
                        END IF;
                        IF instruction ->> 'type' IS DISTINCT FROM expected_comparison_type THEN
                            RETURN false;
                        END IF;
                        result_static_type := 'boolean';
                    ELSE
                        RETURN false;
                    END IF;
                    type_stack[type_stack_size] := result_static_type;
                    depth_stack[type_stack_size] := result_depth;
                END IF;
                IF type_stack_size < 1 OR type_stack_size > 64 THEN RETURN false; END IF;
            END LOOP;
            IF type_stack_size <> 1 OR type_stack[1] NOT IN ('boolean', 'dynamic') THEN
                RETURN false;
            END IF;

            variable_values := COALESCE(variable_values, '{}'::jsonb);
            external_values := COALESCE(external_values, '{}'::jsonb);
            FOR instruction IN SELECT value FROM jsonb_array_elements(program -> 'instructions')
            LOOP
                operation := instruction ->> 'op';
                IF operation = 'literal' THEN
                    CASE instruction ->> 'type'
                        WHEN 'string' THEN
                            item := flowbit.inbox_visibility_normalize_value(instruction -> 'value', 'string');
                        WHEN 'boolean' THEN
                            item := flowbit.inbox_visibility_normalize_value(instruction -> 'value', 'boolean');
                        WHEN 'number' THEN
                            item := flowbit.inbox_visibility_to_number(
                                jsonb_build_object('known', true, 'type', 'string', 'value', instruction ->> 'value'));
                        ELSE
                            item := jsonb_build_object('known', false);
                    END CASE;
                    stack_size := stack_size + 1;
                    stack[stack_size] := item;
                ELSIF operation IN ('variable', 'external') THEN
                    pool_index := (instruction ->> 'index')::integer;
                    IF pool_index < 0 THEN RETURN false; END IF;
                    IF operation = 'variable' THEN
                        IF pool_index >= jsonb_array_length(program -> 'variables') THEN RETURN false; END IF;
                        pool_item := program -> 'variables' -> pool_index;
                        item := flowbit.inbox_visibility_normalize_value(
                            variable_values -> (pool_item ->> 'name'),
                            pool_item ->> 'type');
                    ELSE
                        IF pool_index >= jsonb_array_length(program -> 'externalReferences') THEN RETURN false; END IF;
                        pool_item := program -> 'externalReferences' -> pool_index;
                        item := flowbit.inbox_visibility_normalize_value(
                            external_values -> lower(pool_item ->> 'name'),
                            pool_item ->> 'type');
                    END IF;
                    stack_size := stack_size + 1;
                    stack[stack_size] := item;
                ELSIF operation IN ('number', 'positive', 'negate', 'not') THEN
                    IF stack_size < 1 THEN RETURN false; END IF;
                    item := stack[stack_size];
                    IF operation = 'number' THEN
                        item := flowbit.inbox_visibility_to_number(item);
                    ELSIF operation IN ('positive', 'negate') THEN
                        IF NOT COALESCE((item ->> 'known')::boolean, false)
                           OR item ->> 'type' <> 'number' THEN
                            item := jsonb_build_object('known', false);
                        ELSIF operation = 'negate' THEN
                            item := jsonb_build_object(
                                'known', true, 'type', 'number',
                                'value', (-(item ->> 'value')::numeric)::text);
                        END IF;
                    ELSE
                        IF NOT COALESCE((item ->> 'known')::boolean, false) THEN
                            item := jsonb_build_object('known', false);
                        ELSIF item ->> 'type' <> 'boolean' THEN
                            item := jsonb_build_object('known', false);
                        ELSE
                            item := jsonb_build_object(
                                'known', true, 'type', 'boolean',
                                'value', NOT (item ->> 'value')::boolean);
                        END IF;
                    END IF;
                    stack[stack_size] := item;
                ELSE
                    IF stack_size < 2 THEN RETURN false; END IF;
                    right_item := stack[stack_size];
                    left_item := stack[stack_size - 1];
                    stack_size := stack_size - 1;
                    IF operation IN ('add', 'subtract', 'multiply', 'divide', 'modulo') THEN
                        IF NOT COALESCE((left_item ->> 'known')::boolean, false)
                           OR NOT COALESCE((right_item ->> 'known')::boolean, false)
                           OR left_item ->> 'type' <> 'number'
                           OR right_item ->> 'type' <> 'number'
                           OR (operation IN ('divide', 'modulo') AND (right_item ->> 'value')::numeric = 0) THEN
                            item := jsonb_build_object('known', false);
                        ELSE
                            result_number := CASE operation
                                WHEN 'add' THEN (left_item ->> 'value')::numeric + (right_item ->> 'value')::numeric
                                WHEN 'subtract' THEN (left_item ->> 'value')::numeric - (right_item ->> 'value')::numeric
                                WHEN 'multiply' THEN (left_item ->> 'value')::numeric * (right_item ->> 'value')::numeric
                                WHEN 'divide' THEN (left_item ->> 'value')::numeric / (right_item ->> 'value')::numeric
                                WHEN 'modulo' THEN mod((left_item ->> 'value')::numeric, (right_item ->> 'value')::numeric)
                            END;
                            item := jsonb_build_object('known', true, 'type', 'number', 'value', result_number::text);
                        END IF;
                    ELSIF operation IN ('and', 'or') THEN
                        left_known := COALESCE((left_item ->> 'known')::boolean, false)
                                      AND left_item ->> 'type' = 'boolean';
                        right_known := COALESCE((right_item ->> 'known')::boolean, false)
                                       AND right_item ->> 'type' = 'boolean';
                        left_boolean := CASE WHEN left_known THEN (left_item ->> 'value')::boolean END;
                        right_boolean := CASE WHEN right_known THEN (right_item ->> 'value')::boolean END;
                        IF operation = 'and' AND ((left_known AND NOT left_boolean) OR (right_known AND NOT right_boolean)) THEN
                            item := jsonb_build_object('known', true, 'type', 'boolean', 'value', false);
                        ELSIF operation = 'or' AND ((left_known AND left_boolean) OR (right_known AND right_boolean)) THEN
                            item := jsonb_build_object('known', true, 'type', 'boolean', 'value', true);
                        ELSIF left_known AND right_known THEN
                            item := jsonb_build_object(
                                'known', true, 'type', 'boolean',
                                'value', CASE WHEN operation = 'and'
                                    THEN left_boolean AND right_boolean
                                    ELSE left_boolean OR right_boolean END);
                        ELSE
                            item := jsonb_build_object('known', false);
                        END IF;
                    ELSIF operation IN ('equal', 'notEqual', 'greater', 'greaterOrEqual', 'less', 'lessOrEqual') THEN
                        item := flowbit.inbox_visibility_compare(
                            left_item, right_item, operation, instruction ->> 'type');
                    ELSE
                        RETURN false;
                    END IF;
                    stack[stack_size] := item;
                END IF;
                IF stack_size < 1 OR stack_size > 64 THEN RETURN false; END IF;
            END LOOP;
            IF stack_size <> 1 THEN RETURN false; END IF;
            item := stack[1];
            RETURN COALESCE((item ->> 'known')::boolean, false)
                   AND item ->> 'type' = 'boolean'
                   AND COALESCE((item ->> 'value')::boolean, false);
        EXCEPTION WHEN data_exception THEN
            RETURN false;
        END;
        $function$;
        """;
}
