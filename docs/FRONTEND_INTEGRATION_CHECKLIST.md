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
   `email` and `bookingVerificationToken` from the `/book` URL fragment, clear
   the fragment immediately, and send the token as `emailVerificationToken`
   in `POST /api/bookings`. Prompt for a new link after expiry or use; never
   persist the token in browser storage or analytics. Signed-in Client accounts
   with a linked guest profile do not require this step.
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
   bookings either.
4. A successful guest or staff cancellation revokes the guest link.

## Staff operations

1. Listen for SignalR `AccessRevoked`. Immediately clear dashboard state and
   authentication material and return to sign-in. Suspension, deletion, role
   changes and security-stamp changes also make reconnect attempts fail.
2. Image detach/delete responses can be `202 Accepted` with
   `storageDeletion: "Pending"`; the application reference is already gone and
   provider cleanup is durable. Show exhausted jobs from
   `GET /api/admin/media-deletions/failed` and allow an Admin/Manager to call
   `POST /api/admin/media-deletions/{id}/retry`.
3. Admins must resolve every result from
   `GET /api/admin/client-guest-links/issues` with
   `POST /api/admin/client-guest-links/{userId}/reconcile`, supplying the chosen
   guest ID, allowed evidence type, and a non-sensitive reason. Do not put ID
   numbers or document images in the reason field.
4. Complete a refund by posting JSON to
   `POST /api/bookings/{id}/complete-refund` with `transactionReference`, exact
   `amount`, `channel`, matching `evidenceType`, and optional `notes`. For an
   amount at or above the configured high-value threshold, a different
   Admin/Manager must first call `POST /api/bookings/{id}/approve-refund` with a
   reason.

## Release acceptance

Test desktop and mobile paths for registration, email verification, login,
staff MFA enrollment and recovery, room search, direct-transfer booking,
secure booking lookup, add-ons, invoice download, cancellation, profile/avatar
updates and role-restricted dashboard actions. Verify no browser console error,
failed network request, accessibility blocker or secret-bearing URL remains.
