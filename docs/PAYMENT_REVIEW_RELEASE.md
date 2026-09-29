# Bank-transfer review and expired-booking reconciliation

## Behaviour

- Guest checkout reserves an unpaid booking before displaying bank details, with
  a reference and one-hour deadline. **I have made this transfer** then records
  `PaymentReported` under the booking/inventory locks. This is a guest claim, not a
  verified payment. Dates cannot be edited after the hold is created; use the
  reservation-management flow for changes. After a page reload, use the emailed
  booking reference/secure link to access and report payment on the reservation.
  Existing unreported bookings keep the one-hour hold. The API also supports an
  atomic payment report at creation for callers that explicitly supply that flag.
- A secure booking link or signed-in booking owner can report an existing transfer.
  Reported pending reservations retain inventory until explicit staff review.
- A late report never reacquires inventory: it enters review as cancelled.
  Cancelled bookings with revoked access tokens must use owner sign-in or contact
  the hotel. Knowing the booking email alone does not authorize payment reporting.
- Admins/managers see a global **Payment reviews** control. The queue refreshes
  every 30 seconds while visible and marks reports overdue after one hour.
  Staff must monitor it: there is intentionally no automatic release of reported
  payments and no automatic external bank verification.
- **Confirm** requires `VERIFY`, notes, the unique bank-statement credit reference
  and the exact full reservation amount in NGN. Expired reservations are restored
  only after room-type capacity, closures and assigned-room conflicts are checked
  under the inventory allocation lock. Past stays and non-expiry cancellations
  cannot be restored here. Arrange alternatives separately with guest consent.
- **Refund** records the actual verified bank credit, releases the reservation and
  creates guest credit/RefundPending. It does not send money or record a completed
  refund. Use the existing Settlements refund workflow and its approval controls.
- **Reject** requires a documented review with no verified credit. It releases
  the hold, records no payment/refund and emails the guest. Investigate delayed
  transfers with the bank/guest first. A later actual credit can still be recorded
  for refund using the booking reference.

## Integrity and limitations

Booking row locks serialize expiry, reporting and review. Reported states are
excluded from automatic expiry and all existing expiry-based inventory releases.
Payment review and allocation use the same room-type advisory lock. A separate
bank-reference lock and globally unique folio idempotency key prevent the same
reference from being credited to multiple bookings. Staff must use the exact bank
statement reference consistently; this cannot independently detect a fabricated
reference or match historical manual credits that never stored a bank reference.

Reinstatement reverses the original expiry credit with an append-only ledger entry;
it does not edit/delete historical charges. Payment, audit and email outbox entries
commit atomically. Reported payments cannot be bypassed through ordinary cancellation,
status changes, amendment or general folio posting controls. Repeated review requests
do not create duplicate credits. Revoked guest tokens are never reactivated.

Partial/excess transfers cannot automatically confirm a stay. Staff must reconcile
these with accounts/the guest; the verified amount can be routed to the refund
workflow. There is no automatic Monnify integration or payment movement in this feature.

## Deployment order and rollback

No database schema change is required: the new payment-status string and existing
audit, notification, folio and email-outbox tables are used. **Older API builds do
not understand the new enum value.**

1. Run backend and frontend release gates; take the usual pre-release backup.
2. Deploy the API to all instances/workers first. Do not allow mixed old/new API
   workers to process reported-payment rows. Use a maintenance window if needed.
3. Deploy the manager dashboard before enabling the guest-site release, so incoming
   reports have a staffed review path. Confirm the review queue loads for a manager.
4. Deploy the guest site. In a controlled non-production environment, test report →
   overdue hold → confirm, expiry → refund, and sold-room restoration rejection.
5. Monitor outstanding reports and email delivery. Existing old bookings are not
   assumed paid or automatically converted; staff reconcile them individually.

Do not roll back to an API that cannot deserialize PaymentReported after reports
exist. Prefer a forward fix; otherwise pause writes and prepare a reviewed data
compatibility plan. Never bulk mark reports Paid, erase the database, or reuse a
bank reference to get past an error.

Browser verification was attempted but the connected browser timed out twice.
Local automated tests/builds are required, but the deployed visual smoke check
remains a release gate. No live bookings or real payments were modified during development.

## Local verification (2026-09-28)

- Backend: 42 unit tests and 338 integration tests passed, no failures/skips.
- Dashboard: 29 tests and production build passed.
- Guest site: 22 tests and production build passed.
- New coverage includes secure reporting, atomic report-at-creation support,
  expiry/report races, held inventory, sold-room and missing-inventory rejection,
  same-credit concurrency, exact bank amount checks, role restrictions, ordinary
  cancellation/confirmation bypass rejection, refund handoff, and HTTP report →
  overdue queue → manager confirmation.
- Tests used a disposable local PostgreSQL database and recorded email test doubles.
  The local database instance was stopped after verification.
