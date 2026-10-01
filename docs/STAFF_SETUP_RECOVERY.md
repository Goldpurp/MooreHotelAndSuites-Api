# Staff email correction and setup recovery

This release does not mutate existing production accounts during deployment.

## Changes

- Staff details now opens the existing edit form for authorized staff targets.
- A separate Send setup link action confirms the saved recipient before requesting a link.
- The new authenticated, rate-limited API action rechecks the actor's active role, checks target role/status, locks the target against concurrent edits, and atomically queues the email and an audit event.
- New and resent invitations now open the dashboard's dedicated `/setup-password` screen and submit to `POST /api/auth/setup-password`; they no longer reuse guest password recovery.
- Staff setup tokens have a separate Identity purpose, are single-use, and cannot be submitted to the ordinary reset-password endpoint.
- Editing already invalidates prior setup links and staff sessions; resend does not change passwords or broaden permissions.
- Onboarding and resend messages distinguish queued email from confirmed delivery. Brevo acceptance alone does not guarantee inbox delivery.

## Release and live verification

1. Deploy the dashboard setup page before, or in the same release as, the API that begins issuing its links.
2. Open the existing staff record. Correct only the email, preserving the name, role, department and phone.
3. Save, verify the corrected address in staff details, then use Send setup link and confirm that address.
4. Check Brevo for delivery to the corrected recipient. Do not replay the message addressed to the mistyped mailbox.
5. The staff member must open the fresh link and choose their own password. Never collect their password or setup token in chat.

No schema changes, account deletion, database reset, or production test email are required to deploy this fix. Local regression tests use disposable users and captured email, so live delivery and browser smoke checks remain separate release checks.
