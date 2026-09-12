import unittest
import os
import sys
import tempfile
from types import ModuleType, SimpleNamespace
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from api import OpenVoiceEngine, RuntimeConfig
from base_speakers import official_embedding_filename


class BaseSpeakerTests(unittest.TestCase):
    def test_melo_speaker_names_map_to_official_v2_filenames(self):
        self.assertEqual(official_embedding_filename("EN-AU"), "en-au.pth")
        self.assertEqual(official_embedding_filename("JP"), "jp.pth")

    def test_filename_mapping_normalizes_whitespace_and_underscores(self):
        self.assertEqual(official_embedding_filename("  EN_US  "), "en-us.pth")

    def test_missing_official_embedding_does_not_fallback_to_generated_clip(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            config = RuntimeConfig(
                api_key="test-key",
                openvoice_root=root,
                model_dir=root / "models",
                registry_path=root / "registry.json",
                reference_wav=root / "reference.wav",
                embedding_dir=root / "embeddings",
                base_speaker_dir=root / "base-speakers",
                output_dir=root / "output",
                voice_id="tsuki",
                device="cpu",
                max_text_chars=400,
                max_wav_bytes=16 * 1024 * 1024,
                inference_wait_seconds=30,
                torch_threads=4,
                base_speaker_en="EN-AU",
                base_speaker_ja="JP",
                speed_en=1.0,
                speed_ja=1.0,
            )
            fake_melo_api = ModuleType("melo.api")
            fake_melo_api.TTS = lambda **_: SimpleNamespace(
                hps=SimpleNamespace(data=SimpleNamespace(spk2id={"EN-AU": 0}))
            )
            fake_melo = ModuleType("melo")
            with patch.dict(sys.modules, {"melo": fake_melo, "melo.api": fake_melo_api}):
                with self.assertRaisesRegex(RuntimeError, "official OpenVoice V2 base-speaker embedding is missing"):
                    OpenVoiceEngine(config)._load_base_language("EN")

    def test_runtime_speeds_are_configurable_per_language(self):
        with patch.dict(os.environ, {"OPENVOICE_SPEED_EN": "0.9", "OPENVOICE_SPEED_JA": "1.0"}, clear=False):
            config = RuntimeConfig.from_env()

        self.assertEqual(config.speed_en, 0.9)
        self.assertEqual(config.speed_ja, 1.0)


if __name__ == "__main__":
    unittest.main()
