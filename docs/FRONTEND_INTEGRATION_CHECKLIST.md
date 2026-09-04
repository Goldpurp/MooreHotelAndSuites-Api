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
2. Remove Paystack from every payment selector and do not call an old Paystack
   endpoint. The API keeps the enum value only to read historical records.
3. Offer direct bank transfer. Offer Monnify only when the deployment owner has
   completed the provider activation checklist and the frontend build flag is
   enabled.
4. Render the API-returned booking amount as the authoritative total. Add-ons
   are already included in that amount and are also itemized on the invoice;
   do not add them a second time in the UI.
5. Treat a `409 Conflict` while adding an add-on as a stale/closed checkout and
   refresh the booking instead of retrying automatically.

## Release acceptance

Test desktop and mobile paths for registration, email verification, login,
staff MFA enrollment and recovery, room search, direct-transfer booking,
secure booking lookup, add-ons, invoice download, cancellation, profile/avatar
updates and role-restricted dashboard actions. Verify no browser console error,
failed network request, accessibility blocker or secret-bearing URL remains.
