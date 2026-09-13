"""Generate a one-off Tsuki English sample with the production Qwen path."""

from __future__ import annotations

import argparse
import logging
import os
import time
from pathlib import Path

import soundfile as sf

from api import Config, QwenEngine


DEFAULT_TEXT = "Hello, I am Tsuki. Thank you for waiting for me."


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--text", default=DEFAULT_TEXT)
    parser.add_argument("--output", default="/opt/tsuki-qwen3-tts/outputs/tsuki-icl-test.wav")
    args = parser.parse_args()

    logging.basicConfig(level=os.getenv("QWEN_TTS_LOG_LEVEL", "INFO").upper())
    config = Config.from_env()
    engine = QwenEngine(config)
    started = time.perf_counter()
    engine.load()
    wav, stats = engine.synthesize(args.text, "English")
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(wav)
    print({"output": str(output), "load_plus_generate_secs": round(time.perf_counter() - started, 3), **stats})


if __name__ == "__main__":
    main()
