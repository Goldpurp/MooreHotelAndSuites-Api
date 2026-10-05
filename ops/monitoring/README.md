# Independent operations monitor

The October QA sample found 150–362-minute gaps in GitHub's nominal ten-minute
schedule. GitHub cron is a secondary check, not evidence of continuous coverage.
This directory is an **undeployed template** for a dedicated, always-on Linux
host outside the API/database failure domain.

Install Python 3, curl and bash; copy `scripts/production-monitor.py` and
`scripts/check-production-api.sh` into `/opt/moore-monitor/scripts/`. Keep these
files root-owned and readable. Install the supplied service/timer in
`/etc/systemd/system/`. Install `monitor.env.example` as
`/etc/moore-monitor/monitor.env`, root-owned mode 0600, with a dedicated real
heartbeat URL. Do not reuse the backup check.

Configure that check for a five-minute period and three-minute grace. Route
failure/missed-run alerts to the two named operators in the recovery runbook.
The URL is a secret; the runner keeps it out of logs and child-process arguments.
It reports start and outcome, rejects redirects and unacknowledged pings, and
returns failure even when only heartbeat delivery fails.

After configuration, use `systemctl daemon-reload` and
`systemctl enable --now moore-api-monitor.timer`; inspect
`systemctl list-timers moore-api-monitor.timer` and
`journalctl -u moore-api-monitor.service`. Verify the service as the configured
unprivileged user; do not disable TLS or hardening to make it pass.

Acceptance requires a successful probe, a controlled failing endpoint test, a
paused timer producing a missed-run alert, receipt by both operators, and at
least 24 hours of measured cadence. Record maximum observed gap. Restart the
timer after the missed-run test. The template alone does not close QA-35.
Separate regional availability checks and daily backup missed-run alerts remain
required. Never set operational evidence declarations from configuration alone.

References: [systemd timer semantics](https://github.com/systemd/systemd/blob/main/man/systemd.timer.xml),
[Healthchecks ping protocol](https://healthchecks.io/docs/http_api/).
