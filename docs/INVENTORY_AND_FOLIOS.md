# Inventory, assignment, and folio operations

## Inventory model

New reservations sell a `roomTypeId` and `roomQuantity`, not a physical room.
`roomId` remains a compatibility path for older single-room clients. A booking
contains one immutable room-type snapshot in `rooms` for each reserved unit;
`assignedRoomId` may remain null until reception prepares the arrival.

Public endpoints:

- `GET /api/inventory/room-types`
- `GET /api/inventory/room-types/{id}/availability?checkIn=YYYY-MM-DD&checkOut=YYYY-MM-DD&units=1`

Admin and Manager endpoints:

- `GET /api/inventory/management/room-types?includeInactive=true`
- `POST|PUT /api/inventory/room-types[/{id}]`
- `GET|POST /api/inventory/closures`
- `DELETE /api/inventory/closures/{id}` deactivates a closure and preserves its audit trail.

Reception, FrontDesk, Admin, and Manager users assign or move a room with:

```text
PUT /api/inventory/bookings/{bookingId}/rooms/{reservationRoomId}/assignment
```

The server locks the physical room, checks its type, online/maintenance state,
closures, and overlapping assignments, then records the actor and reason.
Every unit must be assigned to an online, clean, available room before check-in.

Availability is the minimum remaining unit count across every night of the stay:
online non-maintenance physical units, minus active room/room-type closures,
minus active non-expired reservation units. Booking creation repeats this check
under a room-type advisory lock; a quote never holds stock.

## Folio model

Every booking opens one folio in the booking currency. Entries are append-only:
room charges, add-ons, tax, fees, discounts, payments, credits, refunds,
adjustments, and voids. Existing entries cannot be edited or deleted; corrections
post one reversing `void` entry.

Authorized folio endpoints:

- `GET /api/folios/{bookingCode}`
- `POST /api/folios/{bookingCode}/charges`
- `POST /api/folios/{bookingCode}/payments`
- `POST /api/folios/{bookingCode}/credits`
- `POST /api/folios/{bookingCode}/entries/{entryId}/void`
- `POST /api/folios/{bookingCode}/close`

Every write requires a client-generated idempotency key. Payment and refund
external references are unique. Payments can be split; a reservation becomes
confirmed only when a payment request asks for confirmation and the net payment
meets the booking's snapshotted deposit requirement. A new charge after payment
changes the status to `partiallyPaid` when a balance remains.

Folio access follows separation of duties: Reception/FrontDesk can read folios
and post itemized add-on charges; Finance/Cashier can read folios, record manual
bank/cash/other payments and close settled folios; only Admin/Manager can post
credits, non-add-on adjustments, or void entries. Concierge can add an approved
booking incidental but cannot read the financial folio. Provider-originated
Monnify payments are never accepted through the manual staff-payment endpoint.

Cancellation posts a credit for the billed stay without erasing historical
charges. Any net guest credit changes payment status to `refundPending`; partial
refunds reduce that credit and preserve every payment/refund entry. Refunds at
or above the configured high-value threshold require approval by a different
Admin or Manager.

Checkout requires a zero folio balance—neither money due nor guest credit—and
closes the folio. Closed folios reject further entries. Invoice and booking
responses expose payments, refunds, amount due, and guest credit from the folio.

## Migration and launch review

`CompleteHotelInventoryAndFolios` backfills one legacy room type per category,
maps existing rooms/bookings/quotes, creates reservation-unit snapshots, and
reconstructs opening folio entries from historical charges, add-ons, payment,
cancellation, and refund fields. Before production migration:

1. Restore a current production backup into an isolated rehearsal database.
2. Apply the idempotent migration bundle and run the production database
   validation script.
3. Reconcile every backfilled folio balance against the historical booking and
   provider settlement data.
4. Review migrated `LEGACY-*` room-type names, base rates, occupancy, amenities,
   and physical-room mappings; update them before enabling room-type sales.
5. Test a two-room quote/booking/assignment, split payment, post-payment add-on,
   void, partial refund, and zero-balance checkout with real staff roles.
6. Keep the pre-migration backup until financial and inventory reconciliation is
   signed off. Do not deploy a frontend that still assumes `roomId` is required.
