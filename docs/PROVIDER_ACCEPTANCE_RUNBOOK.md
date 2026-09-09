# Provider acceptance runbook

Production startup requires evidence for Brevo and Cloudinary. Monnify may stay
disabled while direct transfer is offered; when Monnify is enabled, every
Monnify and PCI evidence gate becomes mandatory. The API never stores or accepts
card details and must continue redirecting to the provider-hosted checkout.

Evidence references are non-secret ticket, vault-record, or test-run IDs. They
must not contain credentials, guest data, card data, or provider payloads.

## Credential rotation

1. Revoke every database, JWT, Brevo, Cloudinary, Monnify, and administrator
   credential that has previously been shared or used outside the secret manager.
2. Create least-privilege replacements in each provider dashboard and put them
   only in the cloud secret manager.
3. Confirm the revoked values fail and the new values work. Record a separate
   rotation evidence ID for each provider.

## Brevo acceptance

1. Authenticate the sending domain and verify the exact sender address.
2. From staging, deliver booking confirmation, operations alert, payment,
   cancellation, expiry, password reset, secure-link, and checkout messages.
3. Confirm DKIM/SPF/DMARC alignment, receipt in representative mailboxes, no
   secrets in content, and matching accepted/delivered provider events.
4. Simulate one retryable provider error; verify the durable outbox retries and
   clears. Simulate exhaustion and verify the admin health/recovery workflow.
5. Record acceptance time and evidence, then set the three Brevo acceptance
   variables.

## Cloudinary acceptance

1. Upload each allowed format at the size boundary and reject invalid content,
   oversized files, and extension/content mismatches.
2. Replace a room image and an avatar; verify old managed assets enter the
   deletion outbox and are deleted only after the database commit.
3. Simulate a delete failure and verify retry, leasing, idempotency, exhausted
   recovery, and no deletion of an unregistered provider identifier.
4. Check transformation/access rules and ensure provider credentials never reach
   the browser or logs. Record acceptance time and evidence.

## Monnify and PCI acceptance

Keep `MonnifySettings__Enabled=false` and
`ProviderAcceptance__HostedPaymentPageOnly=false` until all steps pass.

1. Use newly rotated sandbox credentials. Verify server-created references,
   hosted checkout, independent server verification, exact amount/currency,
   duplicate event idempotency, invalid signatures, wrong source addresses,
   expired bookings, partial/overpayment handling, and replay handling.
2. Configure and test the production completion webhook. Record webhook evidence.
3. With an approved change window, perform one controlled low-value live payment
   and its full refund. Reconcile API, Monnify, and bank records. Record evidence.
4. Confirm no application page, API, log, analytics tool, or support process
   collects, stores, or transmits cardholder data. Checkout must remain entirely
   provider-hosted.
5. Obtain and review the payment provider/acquirer PCI DSS Attestation of
   Compliance and document the merchant validation method and responsibilities.
   Hosted redirect does not eliminate merchant security duties.
6. Enter every Monnify/PCI acceptance timestamp and evidence reference, set
   `ProviderAcceptance__HostedPaymentPageOnly=true`, then enable Monnify. If any
   check fails, disable it again and continue direct-transfer-only operation.

## Evidence template

- Evidence ID and environment:
- Provider/account identifier (non-secret):
- Credential rotation record:
- Test date/time (UTC), operator, reviewer:
- Test cases and provider event IDs (redacted):
- Expected/actual results:
- Retry/failure-path result:
- Reconciliation result:
- PCI scope/AOC review record, if applicable:
- Open exception, owner, and due date:
- Approval:
