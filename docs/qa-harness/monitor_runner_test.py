import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import types
import unittest

spec = importlib.util.spec_from_file_location('monitor', Path(__file__).resolve().parents[2] / 'scripts/production-monitor.py')
monitor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(monitor)

class MonitorTests(unittest.TestCase):
    def exercise(self, result=0, failure=None, heartbeat_failure=False):
        calls=[]
        def send(base, suffix, run_id):
            calls.append(suffix)
            if heartbeat_failure: raise OSError('secret URL must not leak')
        def runner(args, **kwargs):
            self.assertNotIn('secret-check', ' '.join(args))
            if failure: raise failure
            return types.SimpleNamespace(returncode=result)
        with contextlib.redirect_stderr(io.StringIO()) as error:
            status=monitor.run({'HEALTHCHECKS_MONITOR_PING_URL':'https://hc-ping.com/secret-check'},runner,send)
        self.assertNotIn('secret-check',error.getvalue())
        return status,calls
    def test_success(self): self.assertEqual(self.exercise(),(0,['/start','']))
    def test_failure(self): self.assertEqual(self.exercise(result=1),(1,['/start','/fail']))
    def test_timeout(self): self.assertEqual(self.exercise(failure=subprocess.TimeoutExpired('probe',180)),(1,['/start','/fail']))
    def test_heartbeat_outage_fails_even_when_probe_passes(self): self.assertEqual(self.exercise(heartbeat_failure=True)[0],2)
    def test_missing_configuration_fails_closed(self):
        with contextlib.redirect_stderr(io.StringIO()): self.assertEqual(monitor.run({}),2)
    def test_unsafe_or_ambiguous_urls_rejected(self):
        for url in ['http://hc-ping.com/a','https://user:pass@hc-ping.com/a','https://hc-ping.com/a?secret=x','https://hc-ping.com/a\n']:
            with self.assertRaises(ValueError): monitor.validate_ping_url(url)

if __name__=='__main__': unittest.main()
