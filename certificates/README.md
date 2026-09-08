# Supabase database CA

`supabase-ca.crt` is a public trust certificate, not a private key or application
Data Protection certificate. It was downloaded over HTTPS from the URL used by
Supabase Studio's official `ssl:certificate_url` configuration:
https://supabase-downloads.s3-ap-southeast-1.amazonaws.com/prod/ssl/prod-ca-2021.crt

Use `Root Certificate=/app/certificates/supabase-ca.crt` with
`SSL Mode=VerifyFull` for the runtime and external migration container.
On 2026-09-08 the selected session pooler's certificate chain and hostname were
verified with this CA. Review the official dashboard for CA rotation before its
2031 expiry; never replace it with a certificate taken from an unverified peer.
