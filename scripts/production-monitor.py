#!/usr/bin/env python3
"""Run the operations probe and signal an independent missed-run monitor."""
import os
from pathlib import Path
import subprocess
import sys
import urllib.parse
import urllib.request
import uuid

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, file, code, message, headers, newurl):
        return None

def validate_ping_url(value):
    parsed = urllib.parse.urlsplit(value)
    if parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment or any(ord(c) < 33 for c in value):
        raise ValueError('Configure a valid HTTPS heartbeat URL without credentials, query or fragment.')
    return value.rstrip('/')

def ping(base, suffix, run_id):
    # The secret URL stays out of subprocess arguments and logs. Never follow redirects.
    url = base + suffix + '?rid=' + run_id
    request = urllib.request.Request(url, data=b'', method='POST')
    with urllib.request.build_opener(NoRedirect).open(request, timeout=10) as response:
        if response.status != 200 or response.read(64).strip() != b'OK':
            raise RuntimeError('Heartbeat was not accepted.')

def run(env=None, runner=subprocess.run, send=ping):
    env = os.environ if env is None else env
    try:
        heartbeat = validate_ping_url(env.get('HEALTHCHECKS_MONITOR_PING_URL', ''))
    except ValueError:
        print('Monitor heartbeat configuration is missing or invalid.', file=sys.stderr)
        return 2
    run_id = str(uuid.uuid4())
    heartbeat_failed = False
    try:
        send(heartbeat, '/start', run_id)
    except Exception:
        heartbeat_failed = True
        print('Start heartbeat failed; continuing the API probe.', file=sys.stderr)
    try:
        result = runner(['bash', str(Path(__file__).with_name('check-production-api.sh')),
                         env.get('MONITOR_ENDPOINT', 'https://api.moorehotelandsuites.com/health/operations'),
                         env.get('MONITOR_LATENCY_LIMIT_SECONDS', '2.0'), '5'],
                        timeout=180, check=False)
        succeeded = result.returncode == 0
    except (OSError, subprocess.TimeoutExpired):
        succeeded = False
        print('API probe did not complete.', file=sys.stderr)
    try:
        send(heartbeat, '' if succeeded else '/fail', run_id)
    except Exception:
        heartbeat_failed = True
        print('Completion heartbeat failed.', file=sys.stderr)
    if not succeeded:
        return 1
    return 2 if heartbeat_failed else 0

if __name__ == '__main__':
    raise SystemExit(run())
