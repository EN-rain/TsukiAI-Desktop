from __future__ import annotations

import ast
import json
import re
import unittest
from pathlib import Path


NOTEBOOK = Path(
    r"C:\Users\LENOVO\Downloads\kaggle-kernel-source-tsuki-20260916-final-all450-v4\tsuki-qwen3-tts-0-6b-sft.ipynb"
)


def notebook_source() -> str:
    document = json.loads(NOTEBOOK.read_text(encoding="utf-8"))
    for cell in document["cells"]:
        if cell.get("cell_type") == "code":
            value = cell.get("source", "")
            return "".join(value) if isinstance(value, list) else value
    raise AssertionError("final notebook has no code cell")


class FinalTrainingNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not NOTEBOOK.is_file():
            raise unittest.SkipTest(f"final Kaggle notebook is unavailable: {NOTEBOOK}")
        cls.source = notebook_source()

    def test_final_run_uses_all_audited_samples(self):
        self.assertIn("TRAIN_COUNT = 450", self.source)
        self.assertIn("HOLDOUT_COUNT = 0", self.source)
        self.assertIn("EXPECTED_MANIFEST_SHA256", self.source)
        self.assertIn('"manifest_sha256": manifest_sha256', self.source)

    def test_final_evaluation_is_trained_custom_only(self):
        self.assertIn("generate_custom_voice", self.source)
        self.assertIn('speaker=SPEAKER_NAME', self.source)
        self.assertNotIn("generate_voice_clone", self.source)
        self.assertNotIn("base_metrics", self.source)

    def test_final_evaluation_contains_locked_acceptance_targets(self):
        self.assertIn('"case": "smoke_japanese", "text": "こんにちは。", "language": "Japanese"', self.source)
        self.assertIn('"case": "exact_japanese_target"', self.source)
        self.assertIn('"case": "exact_english_target_english"', self.source)
        self.assertIn('"case": "exact_english_target_auto"', self.source)
        self.assertIn('"case": "exact_english_target_japanese"', self.source)
        self.assertIn('max_duration_seconds=duration_limit', self.source)
        self.assertIn('"case_set_sha256": case_set_sha256', self.source)
        self.assertIn('len(records) != len(cases)', self.source)

    def test_final_package_records_immutable_checkpoint_identity(self):
        self.assertIn('"checkpoint_id": f"{REPAIR_RUN_ID}:{selected.name}:{tree_sha256(stage_checkpoint)}"', self.source)
        self.assertIn('"status": "APPROVED_FOR_STAGING"', self.source)
        self.assertIn('"production_eligible": False', self.source)
        self.assertIn('"source_checkpoint_tree_sha256": tree_sha256(selected)', self.source)

    def test_notebook_source_is_valid_and_fingerprinted(self):
        ast.parse(self.source)
        match = re.search(r'NOTEBOOK_SOURCE_SHA256 = "([0-9a-f]{64})"', self.source)
        self.assertIsNotNone(match)
        self.assertNotEqual(match.group(1), "0" * 64)


if __name__ == "__main__":
    unittest.main()
