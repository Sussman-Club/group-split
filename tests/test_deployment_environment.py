import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


script = Path(__file__).resolve().parents[1] / ".github/scripts/preserve-storage-credentials.py"
spec = importlib.util.spec_from_file_location("storage_credentials", script)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class StorageCredentialsTest(unittest.TestCase):
    def test_existing_account_survives_publish_without_reverting_other_values(self):
        previous = 'STORAGE_ACCESSKEY="old-access"\nSTORAGE_SECRETKEY="old-secret"\nWEB_IMAGE=old\n'
        generated = 'STORAGE_ACCESSKEY=new-access\nSTORAGE_SECRETKEY=new-secret\nWEB_IMAGE=new\n'
        self.assertEqual(
            module.preserve(generated, previous),
            'STORAGE_ACCESSKEY="old-access"\nSTORAGE_SECRETKEY="old-secret"\nWEB_IMAGE=new\n',
        )

    def test_first_deployment_keeps_the_generated_account(self):
        generated = 'STORAGE_ACCESSKEY=new-access\nSTORAGE_SECRETKEY=new-secret\n'
        self.assertEqual(module.preserve(generated, ''), generated)

    def test_preserves_quoted_characters_and_equals_without_interpreting_them(self):
        previous = 'STORAGE_ACCESSKEY="a$b"\nSTORAGE_SECRETKEY="c=d\\\"e"\n'
        generated = 'STORAGE_ACCESSKEY=new\nSTORAGE_SECRETKEY=new\n'
        self.assertEqual(module.preserve(generated, previous), previous)

    def test_incomplete_duplicate_and_empty_accounts_fail_closed(self):
        generated = 'STORAGE_ACCESSKEY=new\nSTORAGE_SECRETKEY=new\n'
        for invalid in (
            'STORAGE_ACCESSKEY=old\n',
            'STORAGE_ACCESSKEY=old\nSTORAGE_ACCESSKEY=again\nSTORAGE_SECRETKEY=old\n',
            'STORAGE_ACCESSKEY=""\nSTORAGE_SECRETKEY=old\n',
        ):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                module.preserve(generated, invalid)
        for previous in (generated, ''):
            with self.subTest(previous=previous), self.assertRaises(ValueError):
                module.preserve('WEB_IMAGE=new\n', previous)

    def test_command_preserves_accounts_and_accepts_an_initial_null_environment(self):
        generated = 'STORAGE_ACCESSKEY=new\nSTORAGE_SECRETKEY=new\nWEB_IMAGE=new\n'
        old_account = 'STORAGE_ACCESSKEY=old\nSTORAGE_SECRETKEY=old\n'
        for previous in (old_account, None):
            with self.subTest(previous=previous), tempfile.TemporaryDirectory() as directory:
                stack = Path(directory) / 'previous.json'
                environment = Path(directory) / '.env.production'
                stack.write_text(json.dumps({'config': {'environment': previous}}))
                environment.write_text(generated)
                subprocess.run([sys.executable, str(script), str(stack), str(environment)], check=True)
                expected = old_account + 'WEB_IMAGE=new\n' if previous else generated
                self.assertEqual(environment.read_text(), expected)

    def test_unreadable_stack_fails_without_changing_the_environment_or_printing_secrets(self):
        generated = 'STORAGE_ACCESSKEY=generated-access\nSTORAGE_SECRETKEY=generated-secret\n'
        for previous in ('not JSON', '{}', '{"config":{"environment":42}}',
                         '{"config":{"environment":"STORAGE_ACCESSKEY=previous-secret"}}'):
            with self.subTest(previous=previous), tempfile.TemporaryDirectory() as directory:
                stack = Path(directory) / 'previous.json'
                environment = Path(directory) / '.env.production'
                stack.write_text(previous)
                environment.write_text(generated)
                result = subprocess.run(
                    [sys.executable, str(script), str(stack), str(environment)],
                    capture_output=True, text=True,
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(environment.read_text(), generated)
                self.assertNotIn('generated-secret', result.stdout + result.stderr)
                self.assertNotIn('previous-secret', result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
