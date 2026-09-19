"""Private HTTP service for the trained Tsukihime Qwen3-TTS checkpoint."""

from __future__ import annotations

import asyncio
import hmac
import io
import logging
import os
import time
from contextlib import asynccontextmanager
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import soundfile as sf
import torch
from fastapi import FastAPI, Header, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel
from qwen_tts import Qwen3TTSModel

from deployment_identity import DeploymentIdentity
from production_artifact import verify_approved_artifact
from runtime_config import (
    RuntimeTuning,
    env_int,
    resolve_approval_status,
    resolve_approved_manifest,
    resolve_deployment_stage,
    resolve_device,
    resolve_mode,
    resolve_model_name,
    resolve_speaker,
)
from validation import audio_stats, normalize_language, validate_text


LOG = logging.getLogger("tsuki-qwen3-tts")
SERVICE_VERSION = "1.1.0-trained-custom"


@dataclass(frozen=True)
class Config:
    mode: str
    model_name: str
    deployment_stage: str
    approval_status: str
    approved_manifest: Path
    checkpoint_id: str
    checkpoint_id_source: str
    generation_version: str
    audio_cache_namespace: str
    speaker: str
    api_key: str
    device: str
    attention_implementation: str
    intra_op_threads: int
    inter_op_threads: int
    max_text_chars: int
    max_new_tokens: int
    custom_max_new_tokens: int
    port: int

    @classmethod
    def from_env(cls) -> "Config":
        mode = resolve_mode()
        deployment_stage = resolve_deployment_stage()
        approval_status = resolve_approval_status()
        if deployment_stage == "production" and approval_status != "APPROVED_FOR_PRODUCTION":
            raise ValueError("production runtime requires QWEN_TTS_APPROVAL_STATUS=APPROVED_FOR_PRODUCTION")
        approved_manifest = Path(resolve_approved_manifest()).expanduser()
        tuning = RuntimeTuning.from_env()
        model_name = resolve_model_name()
        identity = DeploymentIdentity.from_env(model_name, require_explicit=True)
        return cls(
            mode=mode,
            model_name=model_name,
            deployment_stage=deployment_stage,
            approval_status=approval_status,
            approved_manifest=approved_manifest,
            checkpoint_id=identity.checkpoint_id,
            checkpoint_id_source=identity.checkpoint_id_source,
            generation_version=identity.generation_version,
            audio_cache_namespace=identity.audio_cache_namespace,
            speaker=resolve_speaker(),
            api_key=os.getenv("QWEN_TTS_API_KEY", "").strip(),
            device=resolve_device(),
            attention_implementation=tuning.attention_implementation,
            intra_op_threads=tuning.intra_op_threads,
            inter_op_threads=tuning.inter_op_threads,
            max_text_chars=env_int("QWEN_TTS_MAX_TEXT_CHARS", 280, 1, 2000),
            max_new_tokens=tuning.max_new_tokens,
            custom_max_new_tokens=env_int("QWEN_TTS_CUSTOM_MAX_NEW_TOKENS", tuning.max_new_tokens, 64, 8192),
            port=env_int("QWEN_TTS_PORT", 8100, 1, 65535),
        )


class TtsRequest(BaseModel):
    text: str
    language: str = "Auto"


class QwenEngine:
    def __init__(self, config: Config):
        self.config = config
        self.model: Qwen3TTSModel | None = None
        self.ready = False
        self.lock = asyncio.Lock()
        self.model_load_seconds = 0.0
        self.dtype_name = "float32"
        self.artifact_tree_sha256: str | None = None
        self.manifest_sha256: str | None = None

    def _model_dtype(self):
        if self.config.device == "cpu":
            return torch.float32

        if not torch.cuda.is_available():
            raise RuntimeError(
                f"QWEN_TTS_DEVICE={self.config.device} was requested, but CUDA is not available"
            )
        device_index = int(self.config.device.split(":", 1)[1])
        if device_index >= torch.cuda.device_count():
            raise RuntimeError(
                f"QWEN_TTS_DEVICE={self.config.device} is unavailable; "
                f"CUDA exposes {torch.cuda.device_count()} device(s)"
            )
        return torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16

    def load(self) -> None:
        if not self.config.api_key:
            raise RuntimeError("QWEN_TTS_API_KEY must be set; refusing to start an unauthenticated TTS service")
        if self.config.mode != "trained-custom":
            raise RuntimeError("the production Qwen service supports trained-custom only")

        verified = verify_approved_artifact(
            Path(self.config.model_name),
            self.config.approved_manifest,
            stage=self.config.deployment_stage,
            checkpoint_id=self.config.checkpoint_id,
        )
        self.artifact_tree_sha256 = verified.tree_sha256
        self.manifest_sha256 = verified.manifest_sha256

        torch.set_num_threads(self.config.intra_op_threads)
        torch.set_num_interop_threads(self.config.inter_op_threads)
        started = time.perf_counter()
        model_dtype = self._model_dtype()
        self.dtype_name = str(model_dtype).removeprefix("torch.")
        LOG.info(
            "loading trained Qwen checkpoint=%s device=%s dtype=%s attention=%s "
            "intra_op=%d inter_op=%d max_new_tokens=%d tree_sha256=%s",
            self.config.model_name,
            self.config.device,
            self.dtype_name,
            self.config.attention_implementation,
            self.config.intra_op_threads,
            self.config.inter_op_threads,
            self.config.custom_max_new_tokens,
            self.artifact_tree_sha256,
        )
        self.model = Qwen3TTSModel.from_pretrained(
            self.config.model_name,
            device_map=self.config.device,
            dtype=model_dtype,
            attn_implementation=self.config.attention_implementation,
        )
        self.model_load_seconds = time.perf_counter() - started

        model_type = getattr(getattr(self.model, "model", None), "tts_model_type", None)
        if model_type != "custom_voice":
            raise RuntimeError(
                "trained-custom mode requires a Qwen custom_voice checkpoint; "
                f"loaded model type={model_type!r}"
            )
        supported_speakers = self.model.get_supported_speakers()
        if not supported_speakers or self.config.speaker.casefold() not in {
            str(value).casefold() for value in supported_speakers
        }:
            raise RuntimeError(
                f"trained checkpoint does not contain speaker {self.config.speaker!r}; "
                f"found {supported_speakers}"
            )
        self.ready = True
        LOG.info(
            "Qwen ready checkpoint_id=%s checkpoint_id_source=%s generation_version=%s "
            "speaker=%s load_seconds=%.3f mode=trained-custom approval=%s stage=%s",
            self.config.checkpoint_id,
            self.config.checkpoint_id_source,
            self.config.generation_version,
            self.config.speaker,
            self.model_load_seconds,
            self.config.approval_status,
            self.config.deployment_stage,
        )

    def synthesize(self, text: str, language: str) -> tuple[bytes, dict[str, float | int | str]]:
        if not self.ready or self.model is None:
            raise RuntimeError("Qwen TTS model is not ready")

        target_language = normalize_language(language)
        text = validate_text(text, self.config.max_text_chars)
        started = time.perf_counter()
        generation_kwargs = {
            "do_sample": True,
            "top_k": 50,
            "top_p": 1.0,
            "temperature": 0.9,
            "repetition_penalty": 1.05,
            "subtalker_dosample": True,
            "subtalker_top_k": 50,
            "subtalker_top_p": 1.0,
            "subtalker_temperature": 0.9,
        }
        with torch.inference_mode():
            wavs, sample_rate = self.model.generate_custom_voice(
                text=text,
                language=target_language,
                speaker=self.config.speaker,
                max_new_tokens=self.config.custom_max_new_tokens,
                **generation_kwargs,
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
            "mode": self.config.mode,
            "max_new_tokens": self.config.custom_max_new_tokens,
            "language": target_language,
        }
        return wav_bytes, stats


config = Config.from_env()
engine = QwenEngine(config)


@asynccontextmanager
async def lifespan(_: FastAPI):
    engine.load()
    yield


app = FastAPI(title="TsukiAI Qwen3-TTS trained custom voice", version=SERVICE_VERSION, lifespan=lifespan)


def _authorize(api_key: str | None) -> None:
    if not api_key or not hmac.compare_digest(api_key, config.api_key):
        raise HTTPException(status_code=401, detail="invalid API key")


@app.get("/health")
async def health(x_api_key: str | None = Header(default=None, alias="X-Api-Key")):
    _authorize(x_api_key)
    return {
        "status": "ready" if engine.ready else "loading",
        "engine": "qwen3-tts-12hz-0.6b",
        "version": SERVICE_VERSION,
        "model": config.model_name,
        "checkpoint_id": config.checkpoint_id,
        "checkpoint_id_source": config.checkpoint_id_source,
        "generation_version": config.generation_version,
        "audio_cache_namespace": config.audio_cache_namespace,
        "mode": config.mode,
        "speaker": config.speaker,
        "deployment_stage": config.deployment_stage,
        "approval_status": config.approval_status,
        "artifact_tree_sha256": engine.artifact_tree_sha256,
        "manifest_sha256": engine.manifest_sha256,
        "reference_required": False,
        "reference_language": None,
        "conditioning": {
            "reference_required": False,
            "reference_language": None,
            "reference_sha256": None,
            "x_vector_only_mode": None,
        },
        "runtime_lora_loaded": False,
        "training_artifact": config.model_name,
        "target_languages": ["Auto", "English", "Japanese"],
        "runtime": {
            "device": config.device,
            "precision": engine.dtype_name,
            "attention_implementation": config.attention_implementation,
            "intra_op_threads": config.intra_op_threads,
            "inter_op_threads": config.inter_op_threads,
            "max_new_tokens": config.max_new_tokens,
            "custom_max_new_tokens": config.custom_max_new_tokens,
            "sampling_profile": "trained-custom-v2",
            "model_load_seconds": round(engine.model_load_seconds, 3),
        },
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
            "X-Qwen-Engine": "qwen3-tts-12hz-0.6b",
            "X-Qwen-Mode": config.mode,
            "X-Qwen-Duration-Secs": str(stats["duration_secs"]),
            "X-Qwen-Runtime-Secs": str(stats["runtime_secs"]),
            "X-Qwen-RTF": str(stats["rtf"]),
        },
    )


if __name__ == "__main__":
    import uvicorn

    logging.basicConfig(level=os.getenv("QWEN_TTS_LOG_LEVEL", "INFO").upper())
    uvicorn.run("api:app", host="127.0.0.1", port=config.port, workers=1)
