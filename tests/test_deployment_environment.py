import importlib.util
from pathlib import Path
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
        with self.assertRaises(ValueError):
            module.preserve('WEB_IMAGE=new\n', generated)


if __name__ == "__main__":
    unittest.main()
