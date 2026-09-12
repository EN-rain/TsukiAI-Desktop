import unittest
import os
import sys
import tempfile
from types import ModuleType, SimpleNamespace
from pathlib import Path
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from api import OpenVoiceEngine, RuntimeConfig, _melo_language_for_speaker, _resample_wav
from base_speakers import official_embedding_filename


class BaseSpeakerTests(unittest.TestCase):
    def test_newest_english_speaker_uses_the_official_newest_melo_model(self):
        self.assertEqual(_melo_language_for_speaker("EN", "EN-NEWEST"), "EN_NEWEST")

    def test_newest_speaker_selects_newest_model_and_checkpoint_label(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            base_dir = root / "base-speakers"
            base_dir.mkdir()
            (base_dir / "en-newest.pth").touch()
            config = RuntimeConfig(
                api_key="test-key",
                openvoice_root=root,
                model_dir=root / "models",
                registry_path=root / "registry.json",
                reference_wav=root / "reference.wav",
                embedding_dir=root / "embeddings",
                base_speaker_dir=base_dir,
                output_dir=root / "output",
                voice_id="tsuki",
                device="cpu",
                max_text_chars=400,
                max_wav_bytes=16 * 1024 * 1024,
                inference_wait_seconds=30,
                torch_threads=4,
                base_speaker_en="EN-NEWEST",
                base_speaker_ja="JP",
                speed_en=1.0,
                speed_ja=1.0,
                output_sample_rate=24000,
            )
            calls = []
            fake_model = SimpleNamespace(
                hps=SimpleNamespace(data=SimpleNamespace(spk2id={"EN-Newest": 0}))
            )
            fake_melo_api = ModuleType("melo.api")

            def fake_tts(**kwargs):
                calls.append(kwargs)
                return fake_model

            fake_melo_api.TTS = fake_tts
            fake_melo = ModuleType("melo")
            with patch.dict(sys.modules, {"melo": fake_melo, "melo.api": fake_melo_api}):
                with patch("api._load_tensor", return_value=object()):
                    engine = OpenVoiceEngine(config)
                    engine._torch = object()
                    engine._load_base_language("EN")

            self.assertEqual(calls[0]["language"], "EN_NEWEST")
            self.assertEqual(engine._speaker_ids["EN"], 0)

    def test_melo_speaker_names_map_to_official_v2_filenames(self):
        self.assertEqual(official_embedding_filename("EN-US"), "en-us.pth")
        self.assertEqual(official_embedding_filename("EN-Newest"), "en-newest.pth")
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
                base_speaker_en="EN-US",
                base_speaker_ja="JP",
                speed_en=1.0,
                speed_ja=1.0,
                output_sample_rate=24000,
            )
            fake_melo_api = ModuleType("melo.api")
            fake_melo_api.TTS = lambda **_: SimpleNamespace(
                hps=SimpleNamespace(data=SimpleNamespace(spk2id={"EN-US": 0}))
            )
            fake_melo = ModuleType("melo")
            with patch.dict(sys.modules, {"melo": fake_melo, "melo.api": fake_melo_api}):
                with self.assertRaisesRegex(RuntimeError, "official OpenVoice V2 base-speaker embedding is missing"):
                    OpenVoiceEngine(config)._load_base_language("EN")

    def test_runtime_speeds_are_configurable_per_language(self):
        with patch.dict(
            os.environ,
            {
                "OPENVOICE_SPEED_EN": "1.0",
                "OPENVOICE_SPEED_JA": "1.0",
                "OPENVOICE_OUTPUT_SAMPLE_RATE": "24000",
            },
            clear=False,
        ):
            config = RuntimeConfig.from_env()

        self.assertEqual(config.speed_en, 1.0)
        self.assertEqual(config.speed_ja, 1.0)
        self.assertEqual(config.output_sample_rate, 24000)

    def test_output_is_resampled_to_configured_sample_rate(self):
        fake_librosa = ModuleType("librosa")
        fake_librosa.resample = lambda audio, *, orig_sr, target_sr: (audio, orig_sr, target_sr)
        fake_soundfile = ModuleType("soundfile")
        fake_soundfile.read = lambda *_args, **_kwargs: ([0.1, 0.2], 22050)
        fake_soundfile.write = Mock()

        with patch.dict(
            sys.modules,
            {"librosa": fake_librosa, "soundfile": fake_soundfile},
        ):
            _resample_wav(Path("output.wav"), 24000)

        fake_soundfile.write.assert_called_once()
        self.assertEqual(fake_soundfile.write.call_args.args[2], 24000)
        self.assertEqual(fake_soundfile.write.call_args.kwargs["subtype"], "PCM_16")


if __name__ == "__main__":
    unittest.main()
