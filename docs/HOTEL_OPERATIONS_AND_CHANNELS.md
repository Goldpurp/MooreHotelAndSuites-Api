# Hotel operations, CRM and distribution

This release completes the provider-independent hotel operations model. All
routes require an authenticated staff identity and are further restricted by
the named authorization policy.

## Reservation policies and amendments

Configure and approve these values before taking bookings:

- `ReservationPolicies__Version`
- `ReservationPolicies__FreeCancellationHours`
- `ReservationPolicies__CancellationPenaltyPercent`
- `ReservationPolicies__DepositPercent`
- `ReservationPolicies__NoShowPenaltyPercent`

Each booking stores its own copy. Existing bookings migrated from the former
behavior use `LEGACY-FULL-REFUND`, a 100% deposit, a 0% cancellation penalty and
a 100% no-show penalty. Staff amendment routes are:

- `POST /api/reservations/{bookingId}/amendments`
- `POST /api/reservations/{bookingId}/amendment-quotes`
- `GET /api/reservations/{bookingId}/amendments`

Create the fresh quote through the booking-specific amendment-quote route, then
submit its ID/token plus exactly matching dates, room selection and occupancy.
Both quote creation and the final amendment exclude the reservation being
changed from inventory; the final write runs under booking, quote, promotion
and room-type locks, consumes the quote once, replaces room assignments, posts
folio reversals/new pricing and writes an append-only before/after record.

## Housekeeping and maintenance

- `GET|POST /api/housekeeping/tasks`
- `PUT /api/housekeeping/tasks/{id}`
- `GET|POST /api/maintenance/work-orders`
- `PUT /api/maintenance/work-orders/{id}`

Checkout creates `CheckoutCleaning` tasks and marks rooms `Dirty`. Cleaning
moves a room to `Clean` and automatically creates an inspection. A passed
inspection returns it to `Available`; a failed inspection reopens corrective
cleaning. In-house room moves dirty the vacated room and create a cleaning
task. A work order creates a linked inventory closure, uses `OutOfOrder` while
active, preserves the elapsed closure for historical reporting, and opens
recovery cleaning when resolved.

## Front desk, reporting and night audit

- `GET /api/operations/board?date=YYYY-MM-DD`
- `GET /api/operations/calendar?fromDate=...&toDate=...`
- `GET /api/operations/reports/operational?fromDate=...&toDate=...`
- `GET /api/operations/night-audits`
- `POST /api/operations/night-audits/{businessDate}`

Calculations use `HotelSettings__TimeZoneId`, not UTC calendar boundaries.
Room revenue is allocated from nightly quote lines and excludes discounts,
included/exclusive taxes, fees and add-ons. ADR is net room revenue divided by
occupied room nights, and RevPAR uses sellable room nights after historical
closures/out-of-order stock. Reports include scheduled and actual arrivals and
departures, global open-folio receivables and pending refunds. Payments and
refunds come from append-only folio entries and their voids. Only a completed
hotel business date can be closed; night audit freezes the result as an
immutable JSON and typed snapshot.

## Guest CRM

- `GET /api/guest-crm/{guestId}`
- `GET /api/guest-crm/{guestId}/duplicates`
- `PUT /api/guest-crm/{guestId}/preferences`
- `POST /api/guest-crm/{guestId}/notes`
- `POST /api/guest-crm/{guestId}/verify-contact`
- `POST /api/guest-crm/merge`

Sensitive notes are visible only to Admin/Manager. Merges require matching
verified email/phone evidence or an explicitly documented government-ID/manual
review, reject two independently linked client accounts, move reservations and
open privacy requests, and retain the duplicate as a merge trace. The retention
worker clears preferences and redacts note bodies along with core guest PII.

## Distribution channels

- `GET|POST /api/channels`
- `PUT /api/channels/{id}`
- `POST /api/channels/{channelCode}/events/inbound`
- `POST /api/channels/{channelId}/inventory-events`
- `PUT /api/channels/events/{eventId}`
- `POST /api/channels/{channelId}/reservation-mappings`
- `GET /api/channels/{channelId}/reconciliation`

The foundation stores bounded JSON events with channel-scoped idempotency,
state transitions, retry/dead-letter visibility, booking mappings and unmapped
reservation counts. These routes are management-only. Do not expose the generic
inbound route to a provider. A provider adapter must authenticate and verify the
provider-specific signature before calling this service, translate payloads,
process events durably, deliver outbound inventory, and pass end-to-end sandbox
and low-volume live reconciliation before the channel is enabled.
