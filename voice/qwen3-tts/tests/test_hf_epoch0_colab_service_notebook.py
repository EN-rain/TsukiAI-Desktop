import json
import unittest
from pathlib import Path


NOTEBOOK = (
    Path(__file__).resolve().parents[1]
    / "notebooks"
    / "hf_epoch0_colab_service.ipynb"
)


class HfEpoch0ColabServiceNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.notebook = json.loads(NOTEBOOK.read_text(encoding="utf-8"))
        cls.source = "\n".join(
            "".join(cell.get("source", []))
            for cell in cls.notebook["cells"]
            if cell.get("cell_type") == "code"
        )

    def test_notebook_is_valid_and_contains_runtime_cells(self):
        self.assertEqual(self.notebook["nbformat"], 4)
        self.assertGreaterEqual(len(self.notebook["cells"]), 6)
        self.assertIn("uvicorn", self.source)
        self.assertIn("FastAPI", self.source)

    def test_notebook_pins_public_epoch_zero_model_and_reference_files(self):
        self.assertIn("tsukihime-exe/tsuki-qwen3-tts-models", self.source)
        self.assertIn("044462bd5ca834ba7ac44ff044d125decda6e863", self.source)
        self.assertIn("allow_patterns=['epoch-0/*']", self.source)
        self.assertIn("tsuki_25s_clip.mp3", self.source)
        self.assertIn("tsuki_ref_text.txt", self.source)
        self.assertNotIn("HF_ACCESS_TOKEN", self.source)

    def test_notebook_uses_the_trained_base_full_icl_path(self):
        self.assertIn("generate_voice_clone", self.source)
        self.assertIn("create_voice_clone_prompt", self.source)
        self.assertNotIn("generate_custom_voice", self.source)
        self.assertIn("'mode': 'trained-base-full-icl'", self.source)
        self.assertIn("'approval_status': 'APPROVED_FOR_ISOLATED_BASE_FULL_ICL'", self.source)
        self.assertIn("x_vector_only_mode=False", self.source)

    def test_notebook_requires_api_key_and_starts_cloudflare_tunnel(self):
        self.assertIn("TSUKI_COLAB_TTS_API_KEY", self.source)
        self.assertIn("X-Api-Key", self.source)
        self.assertIn("tsuki-colab-qwen-custom", self.source)
        self.assertIn("colab-tts.tsukihime.dpdns.org", self.source)
        self.assertIn("token-file", self.source)
        self.assertIn("tsuki_colab_tunnel_credentials.json", self.source)
        self.assertIn("credentials-file", self.source)
        self.assertIn("ingress", self.source)


if __name__ == "__main__":
    unittest.main()
