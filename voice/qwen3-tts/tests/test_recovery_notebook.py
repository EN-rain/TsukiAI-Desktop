from __future__ import annotations

import hashlib
import json
import re
import unittest
from pathlib import Path


NOTEBOOK = Path(r"C:\Users\LENOVO\Downloads\kaggle-kernel-source-tsuki-20260916\tsuki-qwen3-tts-0-6b-sft.ipynb")
REPAIR_COMMIT = "09364dcc3f947130861186f222a6c58e319ae67a"


def notebook_source() -> str:
    document = json.loads(NOTEBOOK.read_text(encoding="utf-8"))
    for cell in document["cells"]:
        if cell.get("cell_type") == "code":
            value = cell.get("source", "")
            return "".join(value) if isinstance(value, list) else value
    raise AssertionError("recovery notebook has no code cell")


class RecoveryNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not NOTEBOOK.is_file():
            raise unittest.SkipTest(f"Kaggle notebook is unavailable: {NOTEBOOK}")
        cls.source = notebook_source()

    def test_talker_and_subtalker_alignment_are_single_shift_contracts(self):
        source = self.source
        self.assertIn(f'QWEN_COMMIT = "{REPAIR_COMMIT}"', source)
        self.assertIn("inputs_embeds=input_embeddings,", source)
        self.assertIn("attention_mask=attention_mask,", source)
        self.assertIn("labels=codec_0_labels,", source)
        self.assertIn("hidden_states = outputs.hidden_states[0][-1][:, :-1,:]", source)
        self.assertIn("talker_hidden_states = hidden_states[codec_mask[:, 1:]]", source)
        self.assertIn('"labels=None, shift_labels=labels.contiguous()"', source)
        self.assertNotIn('inputs_embeds=input_embeddings[:, :-1, :]', source.replace('if "inputs_embeds=input_embeddings[:, :-1, :]" in source', ""))
        self.assertNotIn('labels=codec_0_labels[:, 1:]', source.replace('or "labels=codec_0_labels[:, 1:]" in source', ""))

    def test_repair_run_is_low_lr_single_epoch_and_uses_cloned_source(self):
        source = self.source
        self.assertIn("REPAIR_LEARNING_RATE = 2e-6", source)
        self.assertIn('REPAIR_LEARNING_RATE_TEXT = "2e-6"', source)
        self.assertIn("REPAIR_EPOCHS = 1", source)
        self.assertIn('repo = Path("/kaggle/working/Qwen3-TTS-repair")', source)
        self.assertIn('repo_env["PYTHONPATH"] = str(repo) + os.pathsep', source)
        self.assertIn('"--lr",\n            REPAIR_LEARNING_RATE_TEXT', source)
        self.assertIn('"--num_epochs",\n            str(REPAIR_EPOCHS)', source)
        self.assertIn('"--seed",\n            str(SEED)', source)

    def test_packaging_is_evaluation_gated_and_no_epoch_fallback_remains(self):
        source = self.source
        self.assertNotIn("ARTIFACT_CHECKPOINT", source)
        self.assertIn('"APPROVED_FOR_STAGING"', source)
        self.assertIn('"APPROVED_FOR_PRODUCTION"', source)
        self.assertIn("Refusing to package rejected/unreviewed checkpoint", source)
        self.assertIn('selection.get("selected_checkpoint")', source)
        self.assertIn('"selection_rule": "exactly one checkpoint must pass', source)
        self.assertIn('"checkpoint_tree_sha256": tree_sha256(stage_checkpoint)', source)
        self.assertIn('"source_checkpoint_tree_sha256": tree_sha256(selected)', source)

    def test_quality_gate_records_raw_wav_metrics_and_runaway_limit(self):
        source = self.source
        for key in (
            "periodicity",
            "zcr",
            "spectral_centroid_hz",
            "high_frequency_energy_ratio",
            "clipping_ratio",
            "dc_offset",
            "silence_ratio",
        ):
            self.assertIn(key, source)
        self.assertIn("MAX_EVAL_SECONDS = 20.0", source)
        self.assertIn("generation/EOS failure", source)
        self.assertIn("periodicity < 0.60", source)
        self.assertIn("periodicity < 0.80 * base_periodicity", source)
        self.assertIn("zcr > 1.35 * base_zcr", source)
        self.assertIn("centroid > 1.50 * base_centroid", source)

    def test_notebook_source_fingerprint_is_present(self):
        match = re.search(r'NOTEBOOK_SOURCE_SHA256 = "([0-9a-f]{64})"', self.source)
        self.assertIsNotNone(match)
        self.assertNotEqual(match.group(1), "0" * 64)


if __name__ == "__main__":
    unittest.main()
