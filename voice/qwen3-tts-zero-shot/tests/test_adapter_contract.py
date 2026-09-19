import hashlib
import importlib.util
import json
import os
import tempfile
import unittest
from pathlib import Path


def load_adapter():
    root = Path(__file__).resolve().parents[1]
    spec = importlib.util.spec_from_file_location("qwen_zero_shot_adapter", root / "adapter.py")
    if spec is None or spec.loader is None:
        raise RuntimeError("could not load adapter module")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class AdapterContractTests(unittest.TestCase):
    def test_profile_state_requires_full_icl_manifest_and_reference(self):
        adapter = load_adapter()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            reference = root / "reference.wav"
            transcript = root / "reference.txt"
            profile_dir = root / "voices"
            profile_dir.mkdir()
            reference.write_bytes(b"reference")
            transcript.write_text("こんにちは。", encoding="utf-8")
            (profile_dir / "tsuki_v1.safetensors").write_bytes(b"profile")
            (profile_dir / "custom_voice_manifest.json").write_text(
                json.dumps(
                    {
                        "model": adapter.MODEL_ID,
                        "voices": {
                            "tsuki_v1": {
                                "file": "tsuki_v1.safetensors",
                                "mode": "icl",
                                "ref_text": "こんにちは。",
                            }
                        },
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
            old_audio = adapter.REFERENCE_AUDIO_PATH
            old_transcript = adapter.REFERENCE_TRANSCRIPT_PATH
            old_profile = adapter.PROFILE_DIR
            try:
                adapter.REFERENCE_AUDIO_PATH = reference
                adapter.REFERENCE_TRANSCRIPT_PATH = transcript
                adapter.PROFILE_DIR = profile_dir
                profile, problems = adapter._profile_state()
            finally:
                adapter.REFERENCE_AUDIO_PATH = old_audio
                adapter.REFERENCE_TRANSCRIPT_PATH = old_transcript
                adapter.PROFILE_DIR = old_profile

            self.assertEqual([], problems)
            self.assertIsNotNone(profile)
            assert profile is not None
            self.assertEqual(
                hashlib.sha256(b"reference").hexdigest(), profile["reference_sha256"]
            )


if __name__ == "__main__":
    unittest.main()
