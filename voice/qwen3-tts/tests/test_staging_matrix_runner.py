from __future__ import annotations

import hashlib
import importlib.util
import unittest
from pathlib import Path


QWEN_ROOT = Path(__file__).resolve().parents[1]
RUNNER_PATH = QWEN_ROOT.parent.parent / "scripts" / "run_qwen_staging_matrix_remote.py"
REFERENCE_TEXT = QWEN_ROOT / "reference" / "tsuki_ref_text.txt"


def load_runner():
    spec = importlib.util.spec_from_file_location("run_qwen_staging_matrix_remote", RUNNER_PATH)
    if spec is None or spec.loader is None:
        raise AssertionError(f"cannot load staging runner: {RUNNER_PATH}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class StagingMatrixRunnerTests(unittest.TestCase):
    def test_runner_locks_the_exact_japanese_target(self):
        runner = load_runner()
        target = REFERENCE_TEXT.read_text(encoding="utf-8").strip()
        self.assertEqual(
            hashlib.sha256(target.encode("utf-8")).hexdigest(),
            runner.JAPANESE_TARGET_SHA256,
        )
        self.assertTrue(target.startswith("こんにちは。"))
        self.assertTrue(target.endswith("夜が終わっても。"))

    def test_runner_allows_reference_length_but_keeps_default_short_limit(self):
        source = RUNNER_PATH.read_text(encoding="utf-8")
        self.assertIn("max_duration_seconds: float = 20.0", source)
        self.assertIn("max_duration_seconds=45.0 if record[\"reference\"] == \"japanese\" else 20.0", source)
        self.assertIn("refusing non-exact target", source)


if __name__ == "__main__":
    unittest.main()
