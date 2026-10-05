import os
from pathlib import Path
import subprocess
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[2]
class RestoreGuardTests(unittest.TestCase):
    def run_case(self,expected='moore_restore_test',actual='moore_restore_test',objects='0',keymode=0o600):
        with tempfile.TemporaryDirectory(prefix='moore-restore-guards-') as folder:
            work=Path(folder); identity=work/'identity'; identity.write_text('fake identity for guard tests only'); identity.chmod(keymode)
            archive=work/'archive.age';archive.write_text('fake archive for guard tests only')
            for name,body in {'psql':'case "$*" in *current_database*) echo "$FAKE_DB";; *) echo "$FAKE_OBJECTS";; esac', 'age':'echo decrypt-attempt >> "$CALL_LOG"; exit 1', 'pg_restore':'echo restore-attempt >> "$CALL_LOG"; exit 1'}.items():
                tool=work/name;tool.write_text('#!/bin/sh\n'+body+'\n');tool.chmod(0o700)
            env={**os.environ,'PATH':str(work)+os.pathsep+os.environ['PATH'],'RESTORE_AGE_IDENTITY_FILE':str(identity),'RESTORE_DRILL_CONNECTION_STRING':'Host=127.0.0.1;Database='+expected+';Username=qa','RESTORE_DRILL_EXPECTED_DATABASE':expected,'FAKE_DB':actual,'FAKE_OBJECTS':objects,'CALL_LOG':str(work/'calls')}
            result=subprocess.run(['bash',str(ROOT/'scripts/restore-encrypted-backup.sh'),str(archive)],env=env,capture_output=True,text=True)
            calls=(work/'calls').read_text() if (work/'calls').exists() else ''
            self.assertNotEqual(result.returncode,0);self.assertNotIn('restore-attempt',calls);self.assertTrue(identity.exists());self.assertTrue(archive.exists())
            return result.stderr,calls
    def test_production_name_is_rejected(self):self.assertIn('explicitly named',self.run_case(expected='production')[0])
    def test_wrong_connected_database_is_rejected(self):self.assertIn('identity does not match',self.run_case(actual='production')[0])
    def test_nonempty_database_is_rejected(self):self.assertIn('containing application objects',self.run_case(objects='1')[0])
    def test_shared_identity_permissions_are_rejected(self):self.assertIn('0600',self.run_case(keymode=0o644)[0])
    def test_decryption_failure_never_starts_sql_restore(self):self.assertIn('decrypt-attempt',self.run_case()[1])
if __name__=='__main__': unittest.main()
