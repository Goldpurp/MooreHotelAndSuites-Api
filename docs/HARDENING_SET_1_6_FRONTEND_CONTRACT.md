# Frontend contract for production hardening set 1–6

The guest website and staff dashboard are separate repositories and are not
present in this workspace. Their release must consume and verify the following
API contract before production promotion.

## Guest booking UI

- Send `adultCount` (minimum 1) and `childCount` (minimum 0) to
  `POST /api/bookings`.
- Do not allow `adultCount + childCount` to exceed the selected room's
  `capacity`. The API and database remain the authority and return `400` when
  occupancy is invalid.
- Fetch `GET /api/privacy/policies/current`, render links to both documents,
  and send the returned versions with `acceptPrivacyPolicy: true`,
  `privacyPolicyVersion`, `acceptBookingTerms: true`, and
  `bookingTermsVersion`.
- Display `adultCount` and `childCount` returned by booking create, lookup,
  booking history, and invoices.

## Registration UI

- Fetch `GET /api/privacy/policies/current` and send
  `acceptPrivacyPolicy: true` plus the exact returned
  `privacyPolicyVersion` to `POST /api/auth/register`.
- If a policy version changes while the form is open, handle the `400`
  response by refreshing the policy metadata and asking for acceptance again.

## Client privacy UI

- Add an authenticated download action for `GET /api/privacy/export`.
- Add a data-rights form using `POST /api/privacy/requests` with one of:
  `access`, `rectification`, `erasure`, `restriction`, `portability`, or
  `objection`.
- Display the signed-in client's request history from
  `GET /api/privacy/requests/mine`.
- A guest without an account may submit the same request with a booking code
  and the secure booking-link token in `X-Booking-Access-Token`.
- Guest booking lookup is `POST /api/bookings/lookup` with the booking code in
  JSON and the secure-link token in `X-Booking-Access-Token`; query-string
  lookup is intentionally unsupported.

## Staff dashboard

- Treat `403` as a normal authorization outcome. Hide navigation that the
  signed-in department cannot use; never interpret role `Staff` as universal
  access.
- Reservation read: Reception, FrontDesk, Concierge.
- Reservation mutation and guest PII: Reception, FrontDesk.
- Folio read: Reception, FrontDesk, Finance, Cashier. Itemized add-on charges:
  Reception and FrontDesk. Manual bank/cash/other payments and folio close:
  Finance and Cashier. Credits, voids, and non-add-on adjustments: Admin and
  Manager only. Concierge may add an approved booking incidental but cannot
  read the financial folio.
- Operations and visit records: Reception, FrontDesk.
- Housekeeping does not receive these broad scopes; its dedicated task board
  is a separate feature set.
- Admin privacy queue: `GET /api/privacy/requests`; resolve with
  `PATCH /api/privacy/requests/{id}/status`. Completion requires
  `identityVerificationReference`, `fulfillmentEvidenceReference`, and the
  type-specific payload: `rectification` for rectification, or
  `confirmAction: true` for erasure, restriction, and objection. Access and
  portability completion records the generated export digest and timestamp.
  A completed or rejected request
  requires resolution notes and cannot be reopened through this endpoint.
- Obtain a single-use ticket from
  `POST /api/notifications/realtime-ticket` using the JWT in the
  `Authorization` header. Connect to SignalR with that ticket, WebSockets, and
  `skipNegotiation: true`; never put the JWT itself in `access_token`. Obtain a
  fresh ticket for every reconnect. On `AccessRevoked`, immediately clear local
  credentials and return to sign-in. Password reset, account suspension, role
  changes, department changes, and security-sensitive profile changes can
  cause this event.
- Ordinary status controls must not target an Administrator. The emergency
  suspend/reactivate UI requires the acting Admin's current password, current
  TOTP code, a non-sensitive reason, and exact target-email confirmation. Do
  not retain those step-up values. Self-suspension is prohibited and the API
  serializes concurrent actions to preserve one operational administrator.

## Required acceptance tests in the frontend repositories

1. Capacity boundary: exactly at capacity succeeds; one above capacity is
   blocked and the API error is displayed.
2. Policy version and acceptance values are present in booking and registration
   requests.
3. Housekeeping cannot navigate to or fetch guest/reservation lists.
4. Front-desk staff can complete the intended reservation workflow.
5. Password reset removes an already-connected staff session.
6. Privacy export and request lifecycle work on mobile and desktop.
7. `429` responses show a retry message without an automatic request loop.
