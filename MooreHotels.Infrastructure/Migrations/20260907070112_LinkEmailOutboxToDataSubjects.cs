using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LinkEmailOutboxToDataSubjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DataSubjectGuestId",
                table: "email_outbox",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // Legacy admin alerts contain another person's PII inside an encrypted
            // payload, so their subject cannot be recovered safely in SQL. Scrub
            // and quarantine them instead of risking an incorrect association.
            migrationBuilder.Sql("""
                UPDATE email_outbox
                SET "Recipient" = 'privacy-migration@delivery-failure.invalid',
                    "ProtectedPayload" = '{}',
                    "QuarantinedAtUtc" = NOW(),
                    "DeliveryFailureMetadataJson" =
                        '{"quarantinedBy":"data_subject_linkage_migration","reason":"legacy_admin_payload"}'::jsonb
                WHERE "Template" IN ('AdminNewBooking', 'AdminRefund')
                  AND "QuarantinedAtUtc" IS NULL;
                """);

            // Direct-to-guest legacy messages can be linked deterministically by
            // their recipient. If historical duplicates exist, the newest guest
            // record is selected and subsequent CRM merges keep the link current.
            migrationBuilder.Sql("""
                UPDATE email_outbox AS message
                SET "DataSubjectGuestId" = (
                    SELECT guest."Id"
                    FROM guests AS guest
                    WHERE LOWER(guest."Email") = LOWER(message."Recipient")
                    ORDER BY guest."CreatedAt" DESC, guest."Id"
                    LIMIT 1)
                WHERE message."Template" NOT IN ('AdminNewBooking', 'AdminRefund')
                  AND EXISTS (
                    SELECT 1
                    FROM guests AS guest
                    WHERE LOWER(guest."Email") = LOWER(message."Recipient"));
                """);

            // Older dead letters may already be quarantined and therefore skipped
            // by the admin-message update above. A quarantined message can never be
            // delivered again, so retain only non-sensitive recovery metadata.
            migrationBuilder.Sql("""
                UPDATE email_outbox
                SET "Recipient" = 'privacy-migration@delivery-failure.invalid',
                    "ProtectedPayload" = '{}',
                    "DeliveryFailureMetadataJson" =
                        '{"quarantinedBy":"data_subject_linkage_migration","reason":"legacy_quarantine_scrub"}'::jsonb
                WHERE "QuarantinedAtUtc" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_email_outbox_DataSubjectGuestId",
                table: "email_outbox",
                column: "DataSubjectGuestId");

            migrationBuilder.AddForeignKey(
                name: "FK_email_outbox_guests_DataSubjectGuestId",
                table: "email_outbox",
                column: "DataSubjectGuestId",
                principalTable: "guests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_email_outbox_guests_DataSubjectGuestId",
                table: "email_outbox");

            migrationBuilder.DropIndex(
                name: "IX_email_outbox_DataSubjectGuestId",
                table: "email_outbox");

            migrationBuilder.DropColumn(
                name: "DataSubjectGuestId",
                table: "email_outbox");
        }
    }
}
