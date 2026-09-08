using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompleteAccountRetentionAndAuditMinimization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AnonymizedAtUtc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAuthenticatedAtUtc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StatusChangedAtUtc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            // Preserve active legacy clients conservatively: deployment time becomes their
            // initial activity marker, so an upgrade cannot immediately erase an account
            // whose historical sign-in activity was not previously recorded.
            migrationBuilder.Sql(
                """
                UPDATE users
                SET "StatusChangedAtUtc" = COALESCE("CreatedAt", CURRENT_TIMESTAMP);

                UPDATE users
                SET "LastAuthenticatedAtUtc" = CURRENT_TIMESTAMP
                WHERE "Role" = 'Client' AND "Status" = 'Active';

                UPDATE guest_merges
                SET "Reason" = 'LEGACY-MERGE-' || LEFT(REPLACE("Id"::text, '-', ''), 16);
                """);

            // Audit writes are sanitized by the DbContext going forward. Redact
            // sensitive-shaped keys recursively in historical JSON as well, so an
            // upgrade cannot carry older PII-bearing payloads into production.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.moore_redact_legacy_audit_json(payload jsonb)
                RETURNS jsonb
                LANGUAGE sql
                IMMUTABLE
                STRICT
                SET search_path = pg_catalog
                AS $function$
                    SELECT CASE jsonb_typeof(payload)
                        WHEN 'object' THEN COALESCE((
                            SELECT jsonb_object_agg(
                                property.key,
                                CASE
                                    WHEN regexp_replace(lower(property.key), '[^a-z0-9]', '', 'g') ~
                                         '(address|authorization|body|cookie|credential|description|details|email|firstname|ipaddress|lastname|message|name|notes|password|phone|reason|recoverycode|secret|sharedkey|title|token)$'
                                    THEN to_jsonb('[REDACTED]'::text)
                                    WHEN jsonb_typeof(property.value) IN ('object', 'array')
                                    THEN public.moore_redact_legacy_audit_json(property.value)
                                    ELSE property.value
                                END)
                            FROM jsonb_each(payload) AS property
                        ), '{}'::jsonb)
                        WHEN 'array' THEN COALESCE((
                            SELECT jsonb_agg(public.moore_redact_legacy_audit_json(element.value))
                            FROM jsonb_array_elements(payload) AS element(value)
                        ), '[]'::jsonb)
                        ELSE payload
                    END
                $function$;

                UPDATE audit_logs
                SET "OldDataJson" = CASE
                        WHEN jsonb_typeof("OldDataJson") = 'object'
                        THEN public.moore_redact_legacy_audit_json("OldDataJson")
                        ELSE to_jsonb('[REDACTED]'::text)
                    END
                WHERE "OldDataJson" IS NOT NULL;

                UPDATE audit_logs
                SET "NewDataJson" = CASE
                        WHEN jsonb_typeof("NewDataJson") = 'object'
                        THEN public.moore_redact_legacy_audit_json("NewDataJson")
                        ELSE to_jsonb('[REDACTED]'::text)
                    END
                WHERE "NewDataJson" IS NOT NULL;

                DROP FUNCTION public.moore_redact_legacy_audit_json(jsonb);
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Reason",
                table: "guest_merges",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.CreateIndex(
                name: "IX_users_Role_AnonymizedAtUtc_LastAuthenticatedAtUtc_StatusCha~",
                table: "users",
                columns: new[] { "Role", "AnonymizedAtUtc", "LastAuthenticatedAtUtc", "StatusChangedAtUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_users_anonymized_accounts_suspended",
                table: "users",
                sql: "\"AnonymizedAtUtc\" IS NULL OR \"Status\" = 'Suspended'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_Role_AnonymizedAtUtc_LastAuthenticatedAtUtc_StatusCha~",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_users_anonymized_accounts_suspended",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AnonymizedAtUtc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastAuthenticatedAtUtc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "StatusChangedAtUtc",
                table: "users");

            migrationBuilder.AlterColumn<string>(
                name: "Reason",
                table: "guest_merges",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(160)",
                oldMaxLength: 160);
        }
    }
}
