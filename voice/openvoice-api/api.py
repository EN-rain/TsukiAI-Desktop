"""Protected, CPU-only OpenVoice V2 synthesis API.

The service owns the reference recording and all model artifacts. Runtime
requests contain only text, language, and the fixed voice ID; the reference
audio is never uploaded or re-processed per request.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import re
import secrets
import tempfile
import time
import uuid
import wave
from contextlib import asynccontextmanager
from dataclasses import dataclass
from pathlib import Path
from typing import Annotated, Any

from fastapi import Depends, FastAPI, Header, HTTPException, status
from fastapi.responses import FileResponse
from pydantic import BaseModel, ConfigDict, Field, field_validator
from starlette.background import BackgroundTask

from converter_factory import create_cpu_converter
from validation import normalize_language, normalize_text, normalize_voice_id


LOG = logging.getLogger("openvoice-api")
ROOT = Path(__file__).resolve().parent


def _bounded_int(name: str, default: int, minimum: int, maximum: int) -> int:
    try:
        value = int(os.getenv(name, str(default)))
    except ValueError:
        value = default
    return max(minimum, min(maximum, value))


@dataclass(frozen=True)
class RuntimeConfig:
    api_key: str
    openvoice_root: Path
    model_dir: Path
    registry_path: Path
    reference_wav: Path
    embedding_dir: Path
    output_dir: Path
    voice_id: str
    device: str
    max_text_chars: int
    max_wav_bytes: int
    inference_wait_seconds: int
    torch_threads: int
    base_speaker_en: str
    base_speaker_ja: str

    @classmethod
    def from_env(cls) -> "RuntimeConfig":
        root = Path(os.getenv("OPENVOICE_ROOT", "/opt/openvoice")).expanduser()
        return cls(
            api_key=os.getenv("OPENVOICE_API_KEY", "").strip(),
            openvoice_root=root,
            model_dir=Path(os.getenv("OPENVOICE_MODEL_DIR", root / "models" / "checkpoints_v2")),
            registry_path=Path(os.getenv("OPENVOICE_VOICE_REGISTRY", root / "voice_registry.json")),
            reference_wav=Path(os.getenv("OPENVOICE_REFERENCE_WAV", root / "voices" / "references" / "tsuki.wav")),
            embedding_dir=Path(os.getenv("OPENVOICE_EMBEDDING_DIR", root / "voices" / "embeddings")),
            output_dir=Path(os.getenv("OPENVOICE_OUTPUT_DIR", "/tmp/openvoice-output")),
            voice_id=os.getenv("OPENVOICE_VOICE_ID", "tsuki").strip(),
            device=os.getenv("OPENVOICE_DEVICE", "cpu").strip().lower() or "cpu",
            max_text_chars=_bounded_int("OPENVOICE_MAX_TEXT_CHARS", 400, 1, 2000),
            max_wav_bytes=_bounded_int("OPENVOICE_MAX_WAV_BYTES", 16 * 1024 * 1024, 1024 * 1024, 64 * 1024 * 1024),
            inference_wait_seconds=_bounded_int("OPENVOICE_INFERENCE_WAIT_SECONDS", 30, 1, 300),
            torch_threads=_bounded_int("OPENVOICE_TORCH_THREADS", 4, 1, 16),
            base_speaker_en=os.getenv("OPENVOICE_BASE_SPEAKER_EN", "").strip(),
            base_speaker_ja=os.getenv("OPENVOICE_BASE_SPEAKER_JA", "").strip(),
        )


def _load_tensor(torch_module: Any, path: Path, device: str) -> Any:
    """Load only tensor-like checkpoint data from a local, trusted artifact."""

    try:
        return torch_module.load(path, map_location=device, weights_only=True)
    except TypeError:  # Older torch versions do not expose weights_only.
        return torch_module.load(path, map_location=device)


class OpenVoiceEngine:
    def __init__(self, config: RuntimeConfig) -> None:
        self.config = config
        self._torch: Any = None
        self._converter: Any = None
        self._target_se: Any = None
        self._models: dict[str, Any] = {}
        self._speaker_ids: dict[str, Any] = {}
        self._source_se: dict[str, Any] = {}

    @property
    def languages(self) -> tuple[str, ...]:
        return tuple(sorted(self._models))

    def load(self) -> None:
        if self.config.device != "cpu":
            raise RuntimeError("this deployment is CPU-only; OPENVOICE_DEVICE must be cpu")
        if not self.config.registry_path.is_file():
            raise RuntimeError(f"voice registry not found: {self.config.registry_path}")

        import torch

        self._torch = torch
        torch.set_num_threads(self.config.torch_threads)
        try:
            torch.set_num_interop_threads(min(2, self.config.torch_threads))
        except RuntimeError:
            # PyTorch only permits this before parallel work starts.
            pass

        converter_config = self.config.model_dir / "converter" / "config.json"
        converter_checkpoint = self.config.model_dir / "converter" / "checkpoint.pth"
        if not converter_config.is_file() or not converter_checkpoint.is_file():
            raise RuntimeError(
                "OpenVoice V2 converter artifacts are incomplete; expected "
                f"{converter_config} and {converter_checkpoint}"
            )

        self._converter = create_cpu_converter(str(converter_config))
        self._converter.load_ckpt(str(converter_checkpoint))
        self._target_se = self._load_target_embedding()

        for language in ("EN", "JA"):
            self._load_base_language(language)

        self.config.output_dir.mkdir(parents=True, exist_ok=True)

    def _load_target_embedding(self) -> Any:
        try:
            registry = json.loads(self.config.registry_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise RuntimeError(f"could not read voice registry: {self.config.registry_path}") from exc

        voices = registry.get("voices") if isinstance(registry, dict) else None
        if not isinstance(voices, dict):
            raise RuntimeError("voice registry must contain a 'voices' object")
        entry = voices.get(self.config.voice_id)
        if not isinstance(entry, dict):
            raise RuntimeError(f"voice '{self.config.voice_id}' is not present in the registry")
        embedding_value = entry.get("embedding")
        if not isinstance(embedding_value, str) or not embedding_value.strip():
            raise RuntimeError(f"voice '{self.config.voice_id}' has no embedding path")

        embedding_path = Path(embedding_value)
        if not embedding_path.is_absolute():
            embedding_path = self.config.openvoice_root / embedding_path
        embedding_path = embedding_path.resolve()
        root = self.config.openvoice_root.resolve()
        if root not in embedding_path.parents and embedding_path != root:
            raise RuntimeError("voice embedding must remain inside OPENVOICE_ROOT")
        if not embedding_path.is_file():
            raise RuntimeError(f"voice embedding not found: {embedding_path}")

        return _load_tensor(self._torch, embedding_path, self.config.device)

    def _load_base_language(self, language: str) -> None:
        from melo.api import TTS

        melo_language = "JP" if language == "JA" else "EN"
        model = TTS(language=melo_language, device="cpu", use_hf=True)
        speaker_map = getattr(getattr(model, "hps", None), "data", None)
        speaker_map = getattr(speaker_map, "spk2id", None)
        if not speaker_map:
            raise RuntimeError(f"MeloTTS returned no speakers for {melo_language}")

        requested_speaker = self.config.base_speaker_ja if language == "JA" else self.config.base_speaker_en
        speaker_names = {str(name): speaker_id for name, speaker_id in speaker_map.items()}
        speaker_name = requested_speaker if requested_speaker in speaker_names else sorted(speaker_names)[0]
        speaker_id = speaker_names[speaker_name]

        safe_speaker = re.sub(r"[^A-Za-z0-9_.-]+", "_", speaker_name)
        source_embedding_path = self.config.embedding_dir / f"base_{language.lower()}_{safe_speaker}.pth"
        if not source_embedding_path.is_file():
            self._create_source_embedding(model, speaker_id, source_embedding_path)

        self._models[language] = model
        self._speaker_ids[language] = speaker_id
        self._source_se[language] = _load_tensor(self._torch, source_embedding_path, self.config.device)
        LOG.info("loaded base language=%s speaker=%s", language, speaker_name)

    def _create_source_embedding(self, model: Any, speaker_id: Any, output_path: Path) -> None:
        if self._converter is None:
            raise RuntimeError("OpenVoice converter is not loaded")
        output_path.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="openvoice-base-") as temp_dir:
            base_wav = Path(temp_dir) / "base.wav"
            model.tts_to_file("System ready.", speaker_id, str(base_wav), speed=1.0)
            if not base_wav.is_file():
                raise RuntimeError(f"MeloTTS did not create {base_wav}")
            self._converter.extract_se([str(base_wav)], se_save_path=str(output_path))

    def synthesize(self, text: str, language: str, output_path: Path) -> None:
        if self._converter is None or self._target_se is None:
            raise RuntimeError("OpenVoice engine is not ready")
        language = normalize_language(language)
        if language not in self._models:
            raise RuntimeError(f"OpenVoice language is not loaded: {language}")

        output_path.parent.mkdir(parents=True, exist_ok=True)
        model = self._models[language]
        with tempfile.TemporaryDirectory(prefix="openvoice-source-") as temp_dir:
            source_wav = Path(temp_dir) / "source.wav"
            model.tts_to_file(
                text,
                self._speaker_ids[language],
                str(source_wav),
                speed=1.0,
            )
            self._converter.convert(
                audio_src_path=str(source_wav),
                src_se=self._source_se[language],
                tgt_se=self._target_se,
                output_path=str(output_path),
                message="@TsukiAI",
            )

        _validate_wav(output_path, self.config.max_wav_bytes)


def _validate_wav(path: Path, max_bytes: int) -> tuple[int, float]:
    if not path.is_file() or path.stat().st_size < 44 or path.stat().st_size > max_bytes:
        raise RuntimeError("OpenVoice produced an invalid WAV file")
    try:
        with wave.open(str(path), "rb") as wav:
            frames = wav.getnframes()
            rate = wav.getframerate()
    except (wave.Error, OSError) as exc:
        raise RuntimeError("OpenVoice produced an unreadable WAV file") from exc
    return path.stat().st_size, frames / rate if rate else 0.0


class TtsRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    text: str = Field(min_length=1)
    language: str = Field(default="EN", min_length=2, max_length=16)
    voice: str = Field(default="tsuki", min_length=1, max_length=64)

    @field_validator("text")
    @classmethod
    def clean_text(cls, value: str) -> str:
        return normalize_text(value, _config().max_text_chars)

    @field_validator("language")
    @classmethod
    def clean_language(cls, value: str) -> str:
        return normalize_language(value)

    @field_validator("voice")
    @classmethod
    def clean_voice(cls, value: str) -> str:
        return normalize_voice_id(value, _config().voice_id)


class ServiceState:
    def __init__(self) -> None:
        self.engine: OpenVoiceEngine | None = None
        self.inference_lock = asyncio.Lock()


_RUNTIME_CONFIG: RuntimeConfig | None = None
state = ServiceState()


def _config() -> RuntimeConfig:
    global _RUNTIME_CONFIG
    if _RUNTIME_CONFIG is None:
        _RUNTIME_CONFIG = RuntimeConfig.from_env()
    return _RUNTIME_CONFIG


@asynccontextmanager
async def lifespan(_: FastAPI):
    config = _config()
    if not config.api_key:
        raise RuntimeError("OPENVOICE_API_KEY must be set; refusing to start an unauthenticated TTS service")
    if config.device != "cpu":
        raise RuntimeError("OPENVOICE_DEVICE must be cpu for this VM")

    engine = OpenVoiceEngine(config)
    LOG.info("loading OpenVoice V2 CPU models")
    await asyncio.to_thread(engine.load)
    state.engine = engine
    LOG.info("OpenVoice V2 ready voice=%s languages=%s", config.voice_id, ",".join(engine.languages))
    yield
    state.engine = None


app = FastAPI(title="TsukiAI OpenVoice V2", version="1.0.0", lifespan=lifespan)


def require_api_key(
    provided_key: Annotated[str | None, Header(alias="X-Api-Key")] = None,
) -> None:
    expected = _config().api_key
    if not expected:
        raise HTTPException(status_code=503, detail="TTS service authentication is not configured")
    if not provided_key:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="X-Api-Key is required")
    if not secrets.compare_digest(provided_key, expected):
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Invalid API key")


@app.get("/health")
async def health() -> dict[str, object]:
    config = _config()
    engine = state.engine
    return {
        "ok": engine is not None,
        "status": "ready" if engine is not None else "loading",
        "engine": "openvoice-v2",
        "voice": config.voice_id,
        "voices_loaded": 1 if engine is not None else 0,
        "languages": list(engine.languages) if engine is not None else [],
    }


@app.get("/voices", dependencies=[Depends(require_api_key)])
async def voices() -> dict[str, object]:
    config = _config()
    return {"voices": [{"id": config.voice_id, "default_language": "EN"}]}


@app.post("/tts", dependencies=[Depends(require_api_key)])
async def synthesize(request: TtsRequest) -> FileResponse:
    engine = state.engine
    config = _config()
    if engine is None:
        raise HTTPException(status_code=503, detail="OpenVoice model is not ready")

    output_path = config.output_dir / f"openvoice-{uuid.uuid4().hex}.wav"
    acquired = False
    started = time.perf_counter()
    try:
        try:
            await asyncio.wait_for(
                state.inference_lock.acquire(),
                timeout=config.inference_wait_seconds,
            )
            acquired = True
        except asyncio.TimeoutError as exc:
            raise HTTPException(
                status_code=status.HTTP_429_TOO_MANY_REQUESTS,
                detail="OpenVoice service is busy; retry shortly",
                headers={"Retry-After": str(config.inference_wait_seconds)},
            ) from exc

        try:
            await asyncio.to_thread(engine.synthesize, request.text, request.language, output_path)
        finally:
            if acquired:
                state.inference_lock.release()
                acquired = False

        bytes_written, audio_seconds = _validate_wav(output_path, config.max_wav_bytes)
        elapsed_ms = int((time.perf_counter() - started) * 1000)
        LOG.info(
            "synthesis_ok voice=%s language=%s chars=%d gen_ms=%d audio_ms=%d",
            request.voice,
            request.language,
            len(request.text),
            elapsed_ms,
            int(audio_seconds * 1000),
        )
        return FileResponse(
            path=output_path,
            media_type="audio/wav",
            filename="voice.wav",
            background=BackgroundTask(_delete_file, output_path),
        )
    except HTTPException:
        output_path.unlink(missing_ok=True)
        raise
    except Exception as exc:
        if acquired:
            state.inference_lock.release()
        output_path.unlink(missing_ok=True)
        LOG.exception(
            "synthesis_failed voice=%s language=%s chars=%d error=%s",
            request.voice,
            request.language,
            len(request.text),
            type(exc).__name__,
        )
        raise HTTPException(status_code=502, detail="OpenVoice synthesis failed") from exc


async def _delete_file(path: Path) -> None:
    path.unlink(missing_ok=True)


logging.basicConfig(
    level=os.getenv("OPENVOICE_LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)
