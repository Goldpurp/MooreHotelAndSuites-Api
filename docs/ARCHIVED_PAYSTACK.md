# Archived Paystack integration

Paystack is intentionally unavailable in the production runtime.

The previous implementation was incomplete: a booking could select Paystack
without a transaction being initialized, and its webhook path did not provide
the durable, amount-bound, idempotent payment ledger used by Monnify. Keeping
that route active could reserve inventory without a usable checkout and could
accept an inadequately reconciled payment event.

The `PaymentMethod.Paystack` enum value remains only so existing database rows
can still be deserialized. New requests are rejected by both request validation
and the booking service. No Paystack controller, processor, configuration, or
dependency registration is present in the runtime.

Re-enabling Paystack requires a new production implementation with all of the
following before the enum value is accepted again:

- server-owned transaction initialization before the booking is committed;
- exact booking, amount, currency, and customer binding;
- signature verification plus a provider verification call;
- a unique provider transaction ledger and idempotent processing;
- row-level locking and atomic booking, audit, and e-mail-outbox writes;
- expiry, late-payment, overpayment, refund, retry, and replay tests;
- startup validation for all required production credentials.
