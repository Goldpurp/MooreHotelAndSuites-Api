# Production operations release — 5 October 2026

This candidate fixes room readiness and terminal booking transitions, complete booking pagination, bank-credit evidence and deduplication, restricted Finance/Cashier reads, accounting periods, client profile care, channel linking and operational queue administration. It adds shared API support for the coordinated dashboard and guest-site fixes.

The application changes require no new EF migration. GeneralCleaning is an additional string-backed task kind. Deploy the API before the dashboard so new payment, maintenance, channel and queue endpoints are available. Retain a compatible previous deployment for rollback; do not roll back production data as a frontend rollback.

Local verification before release preparation: 518 integration and 43 unit tests passed, zero failures/skips; dashboard 57 tests and guest site 31 tests passed; both production builds passed. Six monitor-runner and five restore-guard tests passed. Browser checks covered booking visibility, transfer evidence, guest payment reporting, housekeeping, maintenance, folios, amendments, add-ons, CRM, account correction and channel reconciliation. GitHub release gates must also pass for the candidate commits.

Bank references are validated and deduplicated; staff must still verify the actual bank credit. A recorded channel event is not proof of delivery to a partner. Queue retries do not establish provider delivery.

The monitor service template is not an installed external monitor. Restore guard tests use fake commands and do not prove production backup decryption. A successful current encrypted-backup restore, durable key custody, operator alert delivery, controlled live acceptance, and supported-device/package certification remain separate operational requirements. No release status should imply these have been completed without evidence.
