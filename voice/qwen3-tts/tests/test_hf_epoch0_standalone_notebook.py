from __future__ import annotations

import json
import unittest
from pathlib import Path


NOTEBOOK = Path(
    r"D:\New folder (2)\TsukiAI 1.0\voice\qwen3-tts\notebooks\hf_epoch0_standalone_inference.ipynb"
)
HF_REPO = "tsukihime-exe/tsuki-qwen3-tts-models"
HF_REVISION = "cf726a2d11c565b3a67407fbf91061b13307feb1"


class StandaloneHfEpoch0NotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not NOTEBOOK.is_file():
            raise AssertionError(f"standalone notebook is missing: {NOTEBOOK}")
        cls.document = json.loads(NOTEBOOK.read_text(encoding="utf-8"))
        cls.source = "\n".join(
            "".join(cell.get("source", []))
            for cell in cls.document.get("cells", [])
            if cell.get("cell_type") in {"code", "markdown"}
        )

    def test_notebook_has_valid_jupyter_structure(self) -> None:
        self.assertEqual(self.document.get("nbformat"), 4)
        self.assertGreaterEqual(len(self.document.get("cells", [])), 6)
        self.assertEqual(self.document.get("metadata", {}).get("kernelspec", {}).get("name"), "python3")

    def test_download_is_pinned_to_private_epoch_zero_repo(self) -> None:
        self.assertIn(f'HF_REPO_ID = "{HF_REPO}"', self.source)
        self.assertIn(f'HF_REVISION = "{HF_REVISION}"', self.source)
        self.assertIn("snapshot_download", self.source)
        self.assertIn("allow_patterns=[\"epoch-0/*\"]", self.source)
        self.assertIn("/epoch-0", self.source)
        self.assertIn("login(token=access_token", self.source)

    def test_notebook_uses_trained_full_icl_checkpoint_path(self) -> None:
        self.assertIn("Qwen3TTSModel.from_pretrained(", self.source)
        self.assertIn("MODEL_PATH,", self.source)
        self.assertIn("create_voice_clone_prompt", self.source)
        self.assertIn("generate_voice_clone", self.source)
        self.assertIn("TRAINED_BASE_FULL_ICL", self.source)
        self.assertNotIn("generate_custom_voice", self.source)

    def test_reference_inputs_are_user_supplied_and_token_is_not_embedded(self) -> None:
        self.assertIn("files.upload", self.source)
        self.assertIn("REFERENCE_AUDIO_NAME", self.source)
        self.assertIn("REFERENCE_TEXT_NAME", self.source)
        self.assertIn("files.download", self.source)
        self.assertNotIn("HF_TOKEN =", self.source)
        self.assertNotRegex(self.source, r"hf_[A-Za-z0-9]{20,}")


if __name__ == "__main__":
    unittest.main()
