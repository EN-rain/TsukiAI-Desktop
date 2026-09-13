"""CPU-only HTTP service for Qwen3-TTS full ICL voice cloning.

The reference audio and its exact transcript are processed once when the
service starts. Every request reuses the resulting voice-clone prompt and
returns a validated PCM WAV. No training or fine-tuning is performed.
"""

from __future__ import annotations

import asyncio
import hashlib
import hmac
import io
import json
import logging
import os
import tempfile
import time
from contextlib import asynccontextmanager
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np
import soundfile as sf
import torch
from fastapi import FastAPI, Header, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel
from qwen_tts import Qwen3TTSModel

from validation import audio_stats, normalize_language, validate_text


LOG = logging.getLogger("tsuki-qwen3-tts")
DEFAULT_REF_TEXT = (
    "こんにちは。月だよ。今夜も君と話せて嬉しいな。月の光が綺麗な夜は心が静かになるんだ。"
    "君はどう？君の声好きだよ。聞くたびに安心するんだ。今日はどんな一日だった？ゆっくり聞かせてね。"
    "夜風が気持ちいい季節だね。これからの時間楽しもう。君が笑うと私まで嬉しくなるんだ。"
    "いつでもここにいるからね。夜が終わっても。"
)


def _env_int(name: str, default: int, minimum: int, maximum: int) -> int:
    try:
        value = int(os.getenv(name, str(default)))
    except ValueError:
        value = default
    return max(minimum, min(maximum, value))


@dataclass(frozen=True)
class Config:
    model_name: str
    reference_audio: Path
    reference_text: str
    prompt_path: Path
    api_key: str
    device: str
    threads: int
    max_text_chars: int
    max_new_tokens: int
    port: int

    @classmethod
    def from_env(cls) -> "Config":
        root = Path(os.getenv("QWEN_TTS_ROOT", "/opt/tsuki-qwen3-tts")).expanduser()
        reference_audio = Path(
            os.getenv("QWEN_TTS_REFERENCE", str(root / "reference" / "tsuki_25s_clip.mp3"))
        ).expanduser()
        reference_text_file = os.getenv("QWEN_TTS_REF_TEXT_FILE", "").strip()
        if reference_text_file:
            reference_text = Path(reference_text_file).expanduser().read_text(encoding="utf-8").strip()
        else:
            reference_text = os.getenv("QWEN_TTS_REF_TEXT", DEFAULT_REF_TEXT).strip()

        return cls(
            model_name=os.getenv("QWEN_TTS_MODEL", "Qwen/Qwen3-TTS-12Hz-0.6B-Base").strip(),
            reference_audio=reference_audio,
            reference_text=reference_text,
            prompt_path=Path(
                os.getenv("QWEN_TTS_PROMPT_PATH", str(root / "reference" / "tsuki_icl_prompt.pt"))
            ).expanduser(),
            api_key=os.getenv("QWEN_TTS_API_KEY", "").strip(),
            device=os.getenv("QWEN_TTS_DEVICE", "cpu").strip().lower() or "cpu",
            threads=_env_int("QWEN_TTS_THREADS", 4, 1, 16),
            max_text_chars=_env_int("QWEN_TTS_MAX_TEXT_CHARS", 280, 1, 2000),
            max_new_tokens=_env_int("QWEN_TTS_MAX_NEW_TOKENS", 2048, 64, 8192),
            port=_env_int("QWEN_TTS_PORT", 8100, 1, 65535),
        )


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _prompt_payload(items: list[Any]) -> list[dict[str, Any]]:
    return [
        {
            "ref_code": item.ref_code,
            "ref_spk_embedding": item.ref_spk_embedding,
            "x_vector_only_mode": item.x_vector_only_mode,
            "icl_mode": item.icl_mode,
            "ref_text": item.ref_text,
        }
        for item in items
    ]


def _save_prompt(path: Path, model_name: str, reference_hash: str, reference_text: str, items: list[Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "version": 1,
        "model_name": model_name,
        "reference_sha256": reference_hash,
        "reference_text": reference_text,
        "items": _prompt_payload(items),
    }
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix=f".{path.name}.", suffix=".tmp", delete=False) as tmp:
        temporary_path = Path(tmp.name)
    try:
        torch.save(payload, temporary_path)
        temporary_path.replace(path)
    finally:
        temporary_path.unlink(missing_ok=True)


def _load_prompt(path: Path, model: Qwen3TTSModel, model_name: str, reference_hash: str, reference_text: str):
    if not path.is_file():
        return None
    try:
        # This file is generated locally by this service and is validated by
        # the model/reference hashes before its tensors are used.
        payload = torch.load(path, map_location="cpu", weights_only=False)
        if (
            payload.get("version") != 1
            or payload.get("model_name") != model_name
            or payload.get("reference_sha256") != reference_hash
            or payload.get("reference_text") != reference_text
        ):
            return None

        from qwen_tts.inference.qwen3_tts_model import VoiceClonePromptItem

        device = getattr(model, "device", torch.device("cpu"))
        items = []
        for item in payload.get("items", []):
            ref_code = item["ref_code"]
            if ref_code is not None:
                ref_code = ref_code.to(device)
            items.append(
                VoiceClonePromptItem(
                    ref_code=ref_code,
                    ref_spk_embedding=item["ref_spk_embedding"].to(device),
                    x_vector_only_mode=bool(item["x_vector_only_mode"]),
                    icl_mode=bool(item["icl_mode"]),
                    ref_text=item.get("ref_text"),
                )
            )
        return items or None
    except Exception as exc:
        LOG.warning("discarding unusable cached Qwen voice prompt: %s", exc)
        return None


class TtsRequest(BaseModel):
    text: str
    language: str = "English"


class QwenEngine:
    def __init__(self, config: Config):
        self.config = config
        self.model: Qwen3TTSModel | None = None
        self.prompt_items = None
        self.reference_hash = ""
        self.ready = False
        self.lock = asyncio.Lock()
        self.model_load_seconds = 0.0

    def load(self) -> None:
        if self.config.device != "cpu":
            raise RuntimeError("Qwen TTS deployment is CPU-only; QWEN_TTS_DEVICE must be cpu")
        if not self.config.api_key:
            raise RuntimeError("QWEN_TTS_API_KEY must be set; refusing to start an unauthenticated TTS service")
        if not self.config.reference_audio.is_file():
            raise FileNotFoundError(f"reference audio does not exist: {self.config.reference_audio}")
        if not self.config.reference_text:
            raise RuntimeError("the Japanese reference transcript must not be empty")

        torch.set_num_threads(self.config.threads)
        torch.set_num_interop_threads(max(1, min(self.config.threads, 4)))
        started = time.perf_counter()
        LOG.info("loading Qwen model=%s device=cpu dtype=float32", self.config.model_name)
        self.model = Qwen3TTSModel.from_pretrained(
            self.config.model_name,
            device_map="cpu",
            dtype=torch.float32,
            attn_implementation="eager",
        )
        self.model_load_seconds = time.perf_counter() - started
        self.reference_hash = _sha256(self.config.reference_audio)
        self.prompt_items = _load_prompt(
            self.config.prompt_path,
            self.model,
            self.config.model_name,
            self.reference_hash,
            self.config.reference_text,
        )
        if self.prompt_items is None:
            prompt_started = time.perf_counter()
            LOG.info("building full ICL voice prompt from the Japanese reference")
            self.prompt_items = self.model.create_voice_clone_prompt(
                ref_audio=str(self.config.reference_audio),
                ref_text=self.config.reference_text,
                x_vector_only_mode=False,
            )
            _save_prompt(
                self.config.prompt_path,
                self.config.model_name,
                self.reference_hash,
                self.config.reference_text,
                self.prompt_items,
            )
            LOG.info("full ICL voice prompt ready in %.3fs", time.perf_counter() - prompt_started)
        else:
            LOG.info("reused cached full ICL voice prompt")

        if not self.prompt_items or any(item.x_vector_only_mode for item in self.prompt_items):
            raise RuntimeError("the production Qwen voice prompt must use full ICL mode")
        self.ready = True
        LOG.info(
            "Qwen ready model=%s reference_sha256=%s load_seconds=%.3f mode=full-icl",
            self.config.model_name,
            self.reference_hash,
            self.model_load_seconds,
        )

    def synthesize(self, text: str, language: str) -> tuple[bytes, dict[str, float | int | str]]:
        if not self.ready or self.model is None or self.prompt_items is None:
            raise RuntimeError("Qwen TTS model is not ready")

        target_language = normalize_language(language)
        text = validate_text(text, self.config.max_text_chars)
        started = time.perf_counter()
        with torch.inference_mode():
            wavs, sample_rate = self.model.generate_voice_clone(
                text=text,
                language=target_language,
                voice_clone_prompt=self.prompt_items,
                max_new_tokens=self.config.max_new_tokens,
                do_sample=True,
                top_k=50,
                top_p=1.0,
                temperature=0.9,
                repetition_penalty=1.05,
                subtalker_dosample=True,
                subtalker_top_k=50,
                subtalker_top_p=1.0,
                subtalker_temperature=0.9,
            )

        if not wavs:
            raise RuntimeError("Qwen returned no waveform")
        samples = np.asarray(wavs[0], dtype=np.float32).reshape(-1)
        samples = np.nan_to_num(samples, nan=0.0, posinf=0.0, neginf=0.0)
        rms, peak, duration = audio_stats(samples, int(sample_rate))
        if duration <= 0 or rms < 1e-4 or peak < 1e-3:
            raise RuntimeError(f"Qwen returned near-silent audio (rms={rms:.8f}, peak={peak:.8f})")

        output = io.BytesIO()
        sf.write(output, np.clip(samples, -1.0, 1.0), int(sample_rate), format="WAV", subtype="PCM_16")
        wav_bytes = output.getvalue()
        runtime = time.perf_counter() - started
        stats: dict[str, float | int | str] = {
            "sample_rate": int(sample_rate),
            "duration_secs": round(duration, 3),
            "runtime_secs": round(runtime, 3),
            "rtf": round(runtime / duration, 3),
            "rms": round(rms, 6),
            "peak": round(peak, 6),
            "mode": "full-icl",
            "language": target_language,
        }
        return wav_bytes, stats


config = Config.from_env()
engine = QwenEngine(config)


@asynccontextmanager
async def lifespan(_: FastAPI):
    engine.load()
    yield


app = FastAPI(title="TsukiAI Qwen3-TTS", version="1.0.0", lifespan=lifespan)


def _authorize(api_key: str | None) -> None:
    if not api_key or not hmac.compare_digest(api_key, config.api_key):
        raise HTTPException(status_code=401, detail="invalid API key")


@app.get("/health")
async def health(x_api_key: str | None = Header(default=None, alias="X-Api-Key")):
    _authorize(x_api_key)
    return {
        "status": "ready" if engine.ready else "loading",
        "engine": "qwen3-tts-12hz-0.6b-base",
        "model": config.model_name,
        "mode": "full-icl",
        "reference_language": "Japanese",
        "target_languages": ["English", "Japanese"],
    }


@app.post("/tts")
async def tts(request: TtsRequest, x_api_key: str | None = Header(default=None, alias="X-Api-Key")):
    _authorize(x_api_key)
    if not engine.ready:
        raise HTTPException(status_code=503, detail="Qwen TTS model is not ready")
    try:
        async with engine.lock:
            wav, stats = await asyncio.to_thread(engine.synthesize, request.text, request.language)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    except Exception as exc:
        LOG.exception("Qwen synthesis failed")
        raise HTTPException(status_code=502, detail="Qwen synthesis failed") from exc

    LOG.info(
        "tts language=%s chars=%d bytes=%d duration=%.3f runtime=%.3f rtf=%.3f rms=%.6f mode=%s",
        stats["language"],
        len(request.text.strip()),
        len(wav),
        stats["duration_secs"],
        stats["runtime_secs"],
        stats["rtf"],
        stats["rms"],
        stats["mode"],
    )
    return Response(
        content=wav,
        media_type="audio/wav",
        headers={
            "X-Qwen-Engine": "qwen3-tts-12hz-0.6b-base",
            "X-Qwen-Mode": "full-icl",
            "X-Qwen-Duration-Secs": str(stats["duration_secs"]),
            "X-Qwen-RTF": str(stats["rtf"]),
        },
    )


if __name__ == "__main__":
    import uvicorn

    logging.basicConfig(level=os.getenv("QWEN_TTS_LOG_LEVEL", "INFO").upper())
    uvicorn.run(app, host="127.0.0.1", port=config.port, workers=1)
