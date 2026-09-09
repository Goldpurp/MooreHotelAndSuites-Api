# Pricing and quote contract

## Guest flow

1. Select a room type, room quantity, dates, occupancy, optional rate-plan code and optional
   promotion code.
2. Call `POST /api/pricing/quotes` with those values. Treat the returned
   `quoteToken` as a secret: keep it in memory, never put it in a URL, analytics,
   logs, browser persistence or error reporting.
3. Display `currency`, every `lines` entry, included-tax labels, subtotals,
   discount, exclusive taxes/fees, total and `expiresAtUtc` exactly as returned.
4. Before expiry, submit the same room type, quantity, dates and occupancy plus `quoteId` and
   `quoteToken` to `POST /api/bookings`. If anything changes or the API says the
   quote expired, changed or was used, discard it and request a new quote.
5. Never calculate or authorize the charge from frontend arithmetic. The server
   quote and booking response are authoritative.

Example quote request:

```json
{
  "roomTypeId": "00000000-0000-0000-0000-000000000000",
  "roomQuantity": 2,
  "checkIn": "2026-10-10",
  "checkOut": "2026-10-12",
  "adultCount": 2,
  "childCount": 0,
  "ratePlanCode": "STANDARD",
  "promotionCode": "WEEKEND10"
}
```

Quote lines use `roomNight`, `discount`, `tax`, or `fee`. A discount line's
amount is positive but is subtracted from the total. `isInclusive=true` means
the tax is disclosed but already contained in room prices. The total formula is:

```text
roomSubtotal - discountAmount + taxAmount + feeAmount = totalAmount
```

`includedTaxAmount` is informational and is not added again.

## Staff pricing administration

Admin and Manager roles use:

- `GET /api/pricing/configuration`
- `POST|PUT /api/pricing/rate-plans[/{id}]`
- `POST|PUT|DELETE /api/pricing/daily-rates[/{id}]`
- `POST|PUT /api/pricing/rules[/{id}]`
- `POST|PUT /api/pricing/promotions[/{id}]`

Deactivate or update a rate plan, rule or promotion instead of deleting it so
historical quote foreign keys remain valid. Daily rates target exactly one
physical room, one room type, or one current room category. Override precedence
is physical room, room type, then category for the same plan and date. Only one active default rate plan is
allowed. Configuration changes create audit entries.

Rate-plan adjustment applies to the room type's base price only when no daily
override exists. Promotions apply next. Effective pricing rules apply last.
Only percentage taxes may be inclusive. Promotion redemption is counted when a
booking consumes the quote, not when the quote is previewed.

## Integrity and operations

- Quote access tokens are hashed and quotes expire after 15 minutes by default.
- Room-type inventory/date/quantity/occupancy and every aggregate are checked again under a locked
  database transaction at booking creation.
- A quote can link to only one booking, and a limited promotion is locked before
  its redemption count changes.
- Inventory checks still run; a quote does not hold inventory. `roomId` remains
  supported only as a single-room compatibility path. New guest flows send
  `roomTypeId` and may leave physical-room assignment to reception.
- Consumed quote lines are immutable by API design and power booking views and
  invoices. Unconsumed expired quotes are cleaned after one day.
- `Pricing__RequireQuoteForBooking=true` is mandatory in Production. Local tests
  may leave it false for legacy fixtures, but the real frontend must always use
  quotes.
