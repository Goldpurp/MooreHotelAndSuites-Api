# Housekeeping occupancy safety release

## Behavior

- Stayover tasks record service without changing room status, online inventory, or creating a release inspection.
- Turnover cleaning and inspection start/completion return a conflict if the room is occupied or a checked-in booking references it (including reservation-room assignments and the legacy primary-room link).
- A passed inspection cannot release a room while another turnover-cleaning task is unfinished.
- Open maintenance still keeps a room out of order and offline after inspection.
- Task creation and task updates lock the room within a transaction. Concurrent claims of one task retain the existing single-assignee check and task-row lock.
- Cancelled tasks sort behind active tasks in the capped housekeeping queue.
- Rejected updates roll back; they must not change room state, assignment, completion timestamps, or write a successful-update audit record.

No schema migration, customer data reset, payment activation, or OTA connection is required.

## Tests

`HousekeepingOccupancySafetyTests` covers stayover state preservation, room-status and booking-derived occupancy guards, blocked release, open maintenance, and concurrent cleaner claims. The existing `CompleteHotelOperationsTests.Checkout_creates_cleaning_task_and_inspection_releases_room` covers normal checkout, cleaning, inspection and retry behavior. Run the full unit/integration suites before release as well.

Tests use the existing fixture, which creates and drops a uniquely named disposable database. Configure only a local/test PostgreSQL connection; never point `MOORE_TEST_POSTGRES` at production.

## Local verification — 2026-09-27

- .NET 10.0.400 Release build with warnings treated as errors: passed, zero warnings/errors.
- Unit suite: 42 passed, zero failed/skipped.
- Final integration suite, including HTTP contract and occupancy tests: 316 passed, zero failed/skipped.
- Changed C# files: formatting verification passed.
- Dashboard: 23 tests and production build passed; stayover wording aligned with the backend behavior.
- Database tests used a separate temporary UTF-8 PostgreSQL cluster bound to 127.0.0.1:55439, not production or the pre-existing local databases.

Publishing and deployment are still pending explicit approval for the API and dashboard repositories. Local test success is not a claim that the update is live or that production staff acceptance has been completed.

## Release order and staff acceptance

1. Pass API release gates and deploy the API safeguard before the dashboard housekeeping update (dashboard PR #22).
2. Check API health and dashboard task loading.
3. With a designated test room/reservation, verify checkout → unavailable/dirty → cleaning → clean awaiting inspection → manager inspection → available, and confirm an open maintenance order prevents release.
4. Verify a Housekeeping staff account has cleaning controls but no guest/payment screens. The dashboard exposes inspection actions to management; existing server authorization remains unchanged.
5. Verify a room whose guest has not checked out cannot be released through a turnover task. Stayover completion must leave it occupied.

Keep the prior API/dashboard deployments available for rollback. If rollback is needed after both deployments, roll back the new dashboard first so the new workflow is not exposed against the older backend.

For production hosting, upgrade the Render API service's compute plan from Free to paid. Changing the Render workspace subscription alone does not remove free-service idle sleeping. This release does not make a purchase or change billing.
