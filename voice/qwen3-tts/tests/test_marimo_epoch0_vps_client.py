import ast
import re
import unittest
from pathlib import Path


NOTEBOOK = (
    Path(r"D:\New folder (2)\TsukiAI 1.0")
    / "voice"
    / "qwen3-tts"
    / "notebooks"
    / "marimo_epoch0_vps_client.py"
)


class MarimoEpoch0VpsClientTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source = NOTEBOOK.read_text(encoding="utf-8") if NOTEBOOK.is_file() else ""

    def test_file_exists_and_parses_as_python(self):
        self.assertTrue(NOTEBOOK.is_file(), f"missing Marimo file: {NOTEBOOK}")
        ast.parse(self.source, filename=str(NOTEBOOK))

    def test_uses_marimo_password_inputs_without_real_credentials(self):
        self.assertIn("mo.ui.text", self.source)
        self.assertIn('kind="password"', self.source)
        self.assertIn("TTS_API_KEY", self.source)
        self.assertNotIn("PASTE_THE_SAME_VALUE_AS_THE_VPS_TTS_API_KEY_HERE", self.source)
        self.assertNotIn("TunnelSecret:", self.source)

    def test_uses_verified_vps_api_contract(self):
        self.assertIn("https://colab-tts.tsukihime.dpdns.org", self.source)
        self.assertIn("/health", self.source)
        self.assertIn("/tts", self.source)
        self.assertIn('"X-Api-Key"', self.source)
        self.assertIn('"language": language_input.value', self.source)

    def test_is_a_marimo_app(self):
        self.assertIn("marimo.App", self.source)
        self.assertIn("@app.cell", self.source)
        self.assertIn("app.run()", self.source)

    def test_generate_uses_run_button(self):
        self.assertIn("mo.ui.run_button", self.source)
        self.assertNotIn('mo.ui.button(label="Generate voice")', self.source)

    def test_health_and_tts_cells_do_not_share_response_variable(self):
        self.assertIsNone(re.search(r"(?m)^\s+response\s*=", self.source))
        self.assertIn("health_response = requests.get", self.source)
        self.assertIn("tts_response = requests.post", self.source)


if __name__ == "__main__":
    unittest.main()
