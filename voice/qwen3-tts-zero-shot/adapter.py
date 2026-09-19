"""Private adapter for the isolated IBM Qwen3-TTS Base deployment.

The vLLM-Omni process stays on loopback.  This small service is the only
process exposed through the IBM Cloudflare Tunnel and adds the deployment
attestation and API-key boundary expected by TsukiAI.Api.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
from pathlib import Path
from typing import Any, AsyncIterator

import httpx
from fastapi import Depends, FastAPI, Header, HTTPException, status
from fastapi.responses import JSONResponse, StreamingResponse
from pydantic import BaseModel, Field


MODEL_ID = "Qwen/Qwen3-TTS-12Hz-0.6B-Base"
PROFILE_NAME = "tsuki_v1"
EXPECTED_MODE = "zero-shot"
EXPECTED_APPROVAL = "APPROVED_FOR_ISOLATED_ZERO_SHOT"
MAX_TEXT_CHARS = 1200

VLLM_BASE_URL = os.getenv("VLLM_BASE_URL", "http://127.0.0.1:8091").rstrip("/")
PROFILE_DIR = Path(os.getenv("PROFILE_DIR", "/opt/tsuki-qwen-zero-shot/voices"))
REFERENCE_AUDIO_PATH = Path(
    os.getenv(
        "REFERENCE_AUDIO_PATH",
        "/opt/tsuki-qwen-zero-shot/reference/tsuki_25s_clip.wav",
    )
)
REFERENCE_TRANSCRIPT_PATH = Path(
    os.getenv(
        "REFERENCE_TRANSCRIPT_PATH",
        "/opt/tsuki-qwen-zero-shot/reference/tsuki_25s_clip.txt",
    )
)
API_KEY = os.getenv("QWEN_TTS_API_KEY", "")

app = FastAPI(
    title="Tsuki's Bedroom Qwen3-TTS zero-shot adapter",
    docs_url=None,
    redoc_url=None,
)


class TtsRequest(BaseModel):
    text: str = Field(min_length=1, max_length=MAX_TEXT_CHARS)
    language: str = Field(default="Auto", max_length=16)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _read_transcript() -> str:
    return REFERENCE_TRANSCRIPT_PATH.read_text(encoding="utf-8").strip()


def _profile_state() -> tuple[dict[str, Any] | None, list[str]]:
    problems: list[str] = []
    manifest_path = PROFILE_DIR / "custom_voice_manifest.json"
    profile_path = PROFILE_DIR / f"{PROFILE_NAME}.safetensors"

    if not REFERENCE_AUDIO_PATH.is_file():
        problems.append("reference audio is missing")
    if not REFERENCE_TRANSCRIPT_PATH.is_file():
        problems.append("reference transcript is missing")
    if not manifest_path.is_file():
        problems.append("custom voice manifest is missing")
    if not profile_path.is_file():
        problems.append("tsuki_v1.safetensors is missing")

    manifest: dict[str, Any] | None = None
    if manifest_path.is_file():
        try:
            loaded = json.loads(manifest_path.read_text(encoding="utf-8"))
            if not isinstance(loaded, dict):
                raise ValueError("manifest root is not an object")
            manifest = loaded
        except (OSError, ValueError, json.JSONDecodeError) as exc:
            problems.append(f"custom voice manifest is invalid: {type(exc).__name__}")

    voice = (manifest or {}).get("voices", {}).get(PROFILE_NAME)
    if not isinstance(voice, dict):
        problems.append("manifest does not contain tsuki_v1")
    else:
        if voice.get("mode") != "icl":
            problems.append("tsuki_v1 manifest mode is not icl")
        if voice.get("file") != f"{PROFILE_NAME}.safetensors":
            problems.append("tsuki_v1 manifest file is unexpected")
        if not isinstance(voice.get("ref_text"), str) or not voice["ref_text"].strip():
            problems.append("tsuki_v1 manifest has no reference transcript")

    if problems:
        return None, problems

    assert manifest is not None
    return {
        "manifest_path": str(manifest_path),
        "manifest_sha256": _sha256(manifest_path),
        "profile_path": str(profile_path),
        "profile_sha256": _sha256(profile_path),
        "reference_sha256": _sha256(REFERENCE_AUDIO_PATH),
        "reference_transcript_sha256": hashlib.sha256(
            _read_transcript().encode("utf-8")
        ).hexdigest(),
        "reference_transcript_chars": len(_read_transcript()),
        "manifest": manifest,
    }, []


async def _vllm_state() -> tuple[dict[str, Any], list[str]]:
    problems: list[str] = []
    models: list[str] = []
    try:
        async with httpx.AsyncClient(timeout=4.0) as client:
            health = await client.get(f"{VLLM_BASE_URL}/health")
            if health.status_code != status.HTTP_200_OK:
                problems.append(f"vLLM health returned {health.status_code}")
            model_response = await client.get(f"{VLLM_BASE_URL}/v1/models")
            if model_response.status_code != status.HTTP_200_OK:
                problems.append(f"vLLM models returned {model_response.status_code}")
            else:
                payload = model_response.json()
                models = [
                    str(item.get("id"))
                    for item in payload.get("data", [])
                    if isinstance(item, dict) and item.get("id")
                ]
    except (httpx.HTTPError, ValueError) as exc:
        problems.append(f"vLLM is unreachable: {type(exc).__name__}")

    if MODEL_ID not in models:
        problems.append("vLLM is not serving the required Base model")
    return {"models": models}, problems


def _health_body(
    profile: dict[str, Any] | None,
    profile_problems: list[str],
    vllm: dict[str, Any],
    vllm_problems: list[str],
) -> dict[str, Any]:
    problems = [*profile_problems, *vllm_problems]
    reference_sha256 = profile.get("reference_sha256") if profile else None
    transcript_sha256 = profile.get("reference_transcript_sha256") if profile else None
    return {
        "status": "ready" if not problems else "not_ready",
        "service": "tsukis-bedroom-qwen3-tts-zero-shot",
        "mode": EXPECTED_MODE,
        "profile": PROFILE_NAME,
        "model": MODEL_ID,
        "deployment_stage": "isolated",
        "approval_status": EXPECTED_APPROVAL,
        "reference_required": True,
        "runtime_lora_loaded": False,
        "conditioning": {
            "reference_required": True,
            "reference_sha256": reference_sha256,
            "reference_transcript_sha256": transcript_sha256,
            "x_vector_only_mode": False,
            "profile_manifest_sha256": profile.get("manifest_sha256") if profile else None,
            "profile_sha256": profile.get("profile_sha256") if profile else None,
        },
        "vllm": {"base_url": VLLM_BASE_URL, **vllm},
        "problems": problems,
    }


def require_api_key(x_api_key: str | None = Header(default=None)) -> None:
    if not API_KEY or not x_api_key or not hmac.compare_digest(x_api_key, API_KEY):
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="invalid API key",
        )


def _normalize_language(language: str) -> str:
    normalized = language.strip().lower()
    if normalized in {"ja", "jp", "japanese"}:
        return "Japanese"
    if normalized in {"en", "english"}:
        return "English"
    if normalized == "auto":
        return "Auto"
    raise HTTPException(status_code=422, detail="language must be Auto, English, or Japanese")


async def _require_ready() -> dict[str, Any]:
    profile, profile_problems = _profile_state()
    vllm, vllm_problems = await _vllm_state()
    body = _health_body(profile, profile_problems, vllm, vllm_problems)
    if body["status"] != "ready":
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={"error": "zero-shot runtime is not ready", "health": body},
        )
    return body


@app.get("/health", dependencies=[Depends(require_api_key)])
async def health() -> JSONResponse:
    profile, profile_problems = _profile_state()
    vllm, vllm_problems = await _vllm_state()
    body = _health_body(profile, profile_problems, vllm, vllm_problems)
    code = status.HTTP_200_OK if body["status"] == "ready" else status.HTTP_503_SERVICE_UNAVAILABLE
    return JSONResponse(body, status_code=code)


@app.post("/tts", dependencies=[Depends(require_api_key)])
async def tts(request: TtsRequest) -> StreamingResponse:
    await _require_ready()
    payload = {
        "model": MODEL_ID,
        "input": request.text.strip(),
        "voice": PROFILE_NAME,
        "task_type": "Base",
        "language": _normalize_language(request.language),
        "response_format": "wav",
        "stream": False,
    }
    try:
        async with httpx.AsyncClient(timeout=180.0) as client:
            response = await client.post(f"{VLLM_BASE_URL}/v1/audio/speech", json=payload)
    except httpx.HTTPError as exc:
        raise HTTPException(status_code=502, detail="vLLM synthesis request failed") from exc
    if response.status_code != status.HTTP_200_OK:
        raise HTTPException(status_code=502, detail="vLLM rejected the synthesis request")
    media_type = response.headers.get("content-type", "audio/wav").split(";", 1)[0]
    return StreamingResponse(
        iter([response.content]),
        media_type=media_type,
        headers={"X-Qwen-Profile": PROFILE_NAME, "X-Qwen-Mode": EXPECTED_MODE},
    )


@app.post("/tts/stream", dependencies=[Depends(require_api_key)])
async def tts_stream(request: TtsRequest) -> StreamingResponse:
    await _require_ready()
    payload = {
        "model": MODEL_ID,
        "input": request.text.strip(),
        "voice": PROFILE_NAME,
        "task_type": "Base",
        "language": _normalize_language(request.language),
        "response_format": "pcm",
        "stream": True,
        "stream_format": "audio",
    }

    async def chunks() -> AsyncIterator[bytes]:
        try:
            async with httpx.AsyncClient(timeout=None) as client:
                async with client.stream(
                    "POST", f"{VLLM_BASE_URL}/v1/audio/speech", json=payload
                ) as response:
                    if response.status_code != status.HTTP_200_OK:
                        await response.aread()
                        return
                    async for chunk in response.aiter_bytes():
                        if chunk:
                            yield chunk
        except httpx.HTTPError:
            return

    return StreamingResponse(
        chunks(),
        media_type="application/octet-stream",
        headers={
            "X-Qwen-Profile": PROFILE_NAME,
            "X-Qwen-Mode": EXPECTED_MODE,
            "X-Qwen-Sample-Rate": "24000",
            "X-Qwen-Channels": "1",
        },
    )
