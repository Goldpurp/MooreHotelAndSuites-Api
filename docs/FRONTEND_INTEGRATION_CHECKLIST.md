# Frontend integration checklist

This repository contains the API only. The guest website and staff dashboard
must complete these checks in their own repositories before the combined
system is released.

## Sensitive account links

Verification, password-reset, staff-setup and email-change links now put their
values after `#`, not in the query string. On the destination page:

1. Read values from `window.location.hash` with `URLSearchParams`.
2. Keep the fragment out of analytics, logs, error reports and referrer data.
3. Clear the fragment with `history.replaceState` after copying the values into
   in-memory state.
4. Submit the values only to the matching API endpoint over HTTPS.
5. Confirm expired, reused and modified tokens fail without revealing account
   existence.

Account verification uses `POST /api/auth/verify-email` with JSON
`{ "userId": "...", "token": "..." }`; the old query-string `GET` route no
longer exists. Password reset uses `POST /api/auth/reset-password` with
`userId`, `token`, `newPassword`, and `confirmNewPassword`. Neither flow sends
an email address or token in an API URL.

## Staff MFA

1. After login, check `mfaSetupRequired` in the successful auth response.
2. If true, retain the short-lived authenticated session only for MFA setup and
   route the user to the setup screen.
3. Call `POST /api/mfa/setup`; show the returned `authenticatorUri` as a QR code
   and allow manual entry of `sharedKey`.
4. Submit the current six-digit code to `POST /api/mfa/enable`.
5. Display the returned recovery codes once and require the user to confirm
   that they stored them securely.
6. Sign out. On the next login, a `202 Accepted` response with
   `requiresTwoFactor: true` means the password was accepted but no application
   token has been issued. Resubmit login with `twoFactorCode`, or with
   `useRecoveryCode: true` for a recovery code.
7. Never persist the shared key, authenticator code or recovery codes in local
   storage, analytics, crash reports or logs.

## Payments and booking totals

1. Before an anonymous reservation, call
   `POST /api/bookings/verification/request` with the guest email. Read
   `bookingVerificationToken` from the `/book` URL fragment, clear the fragment
   immediately, and send the token as `emailVerificationToken` with the email
   entered in `POST /api/bookings`. Prompt for a new link after expiry or use;
   never persist the token in browser storage or analytics. Signed-in Client
   accounts with a linked guest profile do not require this step.
2. Before `POST /api/bookings`, call `POST /api/pricing/quotes`; display its
   currency, nightly lines, discount, included tax, exclusive tax/fees, total
   and expiry exactly as returned. Keep `quoteToken` in memory only and submit
   it with `quoteId` and unchanged room-type/quantity/date/occupancy values. Requote after any
   change, expiry or rejection. Never put the token in a URL, log or analytics.
3. Remove Paystack from every payment selector and do not call an old Paystack
   endpoint. The API keeps the enum value only to read historical records.
4. Offer direct bank transfer. Offer Monnify only when the deployment owner has
   completed the provider activation checklist and the frontend build flag is
   enabled.
5. Use `GET /api/inventory/room-types` and the room-type availability endpoint
   for new booking screens. Treat `roomId` as a legacy single-room selector.
   Render `rooms` as assignment state; a null `roomId` before arrival is valid.
6. Render `folio.amountDue` and `folio.guestCredit` as the settlement truth.
   `amount` is the accumulated charge total, and add-ons are already included;
   do not add them a second time in the UI. Handle the new `partiallyPaid`
   payment status.
7. Treat a `409 Conflict` while adding an add-on as a stale/closed checkout and
   refresh the booking instead of retrying automatically.

## Guest booking links

1. Read `guestAccessExpiresAtUtc` from the booking response. The original link
   lasts 24 hours; show a “send new access link” action after it expires.
2. `POST /api/bookings/access-link` invalidates the previous link and emails a
   replacement that lasts two hours. Duplicate requests within one minute are
   suppressed, so disable the action briefly after submission. Never promise
   that an older tab or link will continue working.
3. Send the fragment token only in `X-Booking-Access-Token`. Code plus email no
   longer authorizes lookup, invoice download, or cancellation for historical
   bookings either. Submit `{ "code": "..." }` to
   `POST /api/bookings/lookup`; never put a guest email, cancellation narrative,
   or credential in a URL/query string.
4. A successful guest or staff cancellation revokes the guest link.

## Staff operations

1. Before each SignalR connection or reconnect, send the normal JWT only in the
   `Authorization` header to `POST /api/notifications/realtime-ticket`. Use the
   returned one-minute, single-use opaque `ticket` as SignalR's access token,
   force WebSockets, and set `skipNegotiation: true`. Never place the normal JWT
   in `access_token`. Listen for `AccessRevoked`, then immediately clear
   dashboard state and authentication material and return to sign-in.
   Suspension, deletion, role changes and security-stamp changes also make
   reconnect attempts fail.
2. The ordinary account-status route continues to reject Administrator
   targets. The emergency admin dialog calls
   `POST /api/admin/management/accounts/{id}/emergency-suspend-admin` or
   `.../emergency-reactivate-admin` with `currentPassword`, the current TOTP
   `authenticatorCode`, a non-sensitive `reason`, and exact confirmation text
   `SUSPEND <target-email>` or `REACTIVATE <target-email>`. Never retain or log
   the password or code. Hide self-action controls and never claim success
   until the API confirms it.
3. Image detach/delete responses can be `202 Accepted` with
   `storageDeletion: "Pending"`; the application reference is already gone and
   provider cleanup is durable. Show exhausted jobs from
   `GET /api/admin/media-deletions/failed` and allow an Admin/Manager to call
   `POST /api/admin/media-deletions/{id}/retry`.
4. Admins must resolve every result from
   `GET /api/admin/client-guest-links/issues` with
   `POST /api/admin/client-guest-links/{userId}/reconcile`, supplying the chosen
   guest ID, allowed evidence type, and a non-sensitive reason. Do not put ID
   numbers or document images in the reason field.
5. Complete a refund by posting JSON to
   `POST /api/bookings/{id}/complete-refund` with `transactionReference`, exact
   `amount`, `channel`, matching `evidenceType`, and optional `notes`. For an
   amount at or above the configured high-value threshold, a different
   Admin/Manager must first call `POST /api/bookings/{id}/approve-refund` with a
   reason.

## Reservation amendments and pricing quotes

1. Before amending a reservation, call
   `POST /api/pricing/quotes/amendments` with `bookingId`, target room type,
   dates, room quantity, and occupancy.
2. Display the repriced breakdown: previous stay amount, new room subtotal,
   price difference, taxes, fees, and updated total amount.
3. Retain `quoteToken` in-memory only and submit with `quoteId` to
   `POST /api/reservation-operations/{bookingId}/amend`. Requote if the stay
   selection changes or expires.
4. Inform staff and guest that a durable `BookingAmendmentConfirmation` email is
   automatically queued and dispatched with updated stay dates, room allocations,
   and remaining balance.

## Folio receipts and staff financial operations

1. Every staff-posted payment (`POST /api/folios/{code}/payments`), discretionary
   credit (`POST /api/folios/{code}/credits`), and processed refund queues a
   durable `FolioReceipt` email within the exact same database transaction.
2. Supply a client-generated UUID in `idempotencyKey` on every financial mutation.
   On network failure or timeout, resubmit with the identical key to receive the
   existing entry without duplicating charges, receipts, or emails.
3. Render `receiptNumber`, `entryType`, `paymentMethod`, `amount`, and
   `balanceRemaining` on staff checkout and folio receipt screens.
4. Only Admin, Manager, Finance, and Cashier accounts are authorized for
   folio payments and closeouts. Reception/FrontDesk accounts may only post
   itemized add-on charges.

## Privacy management and legal holds

1. Staff dashboard identifies guests under legal hold via `isUnderLegalHold` on
   the guest profile.
2. Anonymization and right-to-erasure actions (`DELETE /api/privacy/guests/{id}/erasure`)
   are strictly rejected (400 Bad Request) while a guest is under legal hold.
3. Only Admin accounts may place (`POST /api/privacy/guests/{id}/legal-hold`) or
   release (`DELETE /api/privacy/guests/{id}/legal-hold`) a legal hold with a
   documented reason.
4. Retention sweeps and verified erasures scrub guest PII, booking notes, refund
   notes, guest access tokens, add-on notes, linked Client credentials, external
   logins, claims, tokens, avatars and subject-linked queued email. A linked
   Client is eligible only after both the guest-stay retention period and the
   account-inactivity period expire; every successful login refreshes account
   activity. Staff-linked guests, legal holds and active privacy requests remain
   protected. Quarantined email delivery failures retain only minimal delivery
   failure metadata.
5. Guest-merge `reason` is an external evidence/case reference with no spaces or
   personal narrative; show the server validation message when it is rejected.

## Release acceptance

Test desktop and mobile paths for registration, email verification, login,
staff MFA enrollment and recovery, room search, direct-transfer booking,
secure booking lookup, add-ons, invoice download, cancellation, profile/avatar
updates, reservation amendments, folio settlement receipts, legal hold
enforcement, and role-restricted dashboard actions. Verify no browser console
error, failed network request, accessibility blocker or secret-bearing URL remains.
