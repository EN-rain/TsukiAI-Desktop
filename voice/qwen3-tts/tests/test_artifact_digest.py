import tempfile
import unittest
from pathlib import Path

from artifact_digest import digest_artifact_tree


class ArtifactDigestTests(unittest.TestCase):
    def test_digest_is_stable_for_same_tree_and_changes_with_content(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "config.json").write_text('{"model":"tsuki"}', encoding="utf-8")
            weights = root / "weights"
            weights.mkdir()
            (weights / "part.bin").write_bytes(b"checkpoint-bytes")

            first = digest_artifact_tree(root)
            second = digest_artifact_tree(root)
            self.assertEqual(first, second)
            self.assertEqual(first.file_count, 2)
            self.assertEqual(first.total_bytes, len('{"model":"tsuki"}'.encode()) + len(b"checkpoint-bytes"))

            (weights / "part.bin").write_bytes(b"changed-checkpoint-bytes")
            changed = digest_artifact_tree(root)
            self.assertNotEqual(first.sha256, changed.sha256)

    def test_empty_or_missing_artifact_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(ValueError, "contains no files"):
                digest_artifact_tree(Path(temporary))

        with self.assertRaisesRegex(ValueError, "not a directory"):
            digest_artifact_tree(Path("definitely-not-a-real-artifact-directory"))


if __name__ == "__main__":
    unittest.main()
