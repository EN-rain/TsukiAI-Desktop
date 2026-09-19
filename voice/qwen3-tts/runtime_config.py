"""Pure environment parsing for the trained Tsukihime Qwen runtime."""

from __future__ import annotations

import os
import re
from dataclasses import dataclass
from typing import Mapping


_ENV = Mapping[str, str]
_SUPPORTED_ATTENTION = {"eager", "sdpa"}
_SUPPORTED_MODES = {"trained-custom"}
_SUPPORTED_STAGES = {"staging", "production"}
_SUPPORTED_APPROVAL_STATUSES = {"APPROVED_FOR_STAGING", "APPROVED_FOR_PRODUCTION"}
BASE_MODEL_NAME = "Qwen/Qwen3-TTS-12Hz-0.6B-Base"
_CUDA_DEVICE = re.compile(r"cuda:\d+")


def _raw(env: _ENV | None, name: str) -> str | None:
    if env is None:
        return os.getenv(name)
    return env.get(name)


def _enabled(env: _ENV | None, name: str) -> bool:
    return (_raw(env, name) or "").strip().lower() in {"1", "true", "yes", "on"}


def resolve_mode(env: _ENV | None = None) -> str:
    """Resolve the only allowed production inference mode."""

    value = (_raw(env, "QWEN_TTS_MODE") or "trained-custom").strip().lower()
    aliases = {
        "custom": "trained-custom",
        "trained": "trained-custom",
        "trained_custom": "trained-custom",
    }
    value = aliases.get(value, value)
    if value not in _SUPPORTED_MODES:
        raise ValueError(
            "production Qwen TTS supports trained-custom only; "
            "full-icl voice cloning is not a production mode"
        )
    return value


def resolve_model_name(env: _ENV | None = None) -> str:
    """Require an explicit local trained checkpoint for production."""

    resolve_mode(env)
    model_name = (_raw(env, "QWEN_TTS_MODEL") or "").strip()
    if not model_name:
        raise ValueError(
            "QWEN_TTS_MODEL must explicitly point to an approved trained checkpoint"
        )
    if model_name.casefold() == BASE_MODEL_NAME.casefold():
        raise ValueError(
            "trained-custom production cannot use the official Qwen Base model"
        )
    return model_name


def resolve_deployment_stage(env: _ENV | None = None) -> str:
    """Resolve whether the worker is being started in staging or production."""

    value = (_raw(env, "QWEN_TTS_DEPLOYMENT_STAGE") or "production").strip().lower()
    aliases = {"prod": "production", "stage": "staging"}
    value = aliases.get(value, value)
    if value not in _SUPPORTED_STAGES:
        choices = ", ".join(sorted(_SUPPORTED_STAGES))
        raise ValueError(f"QWEN_TTS_DEPLOYMENT_STAGE must be one of: {choices}")
    return value


def resolve_approval_status(env: _ENV | None = None) -> str:
    """Require the immutable artifact approval state used by the runtime."""

    value = (_raw(env, "QWEN_TTS_APPROVAL_STATUS") or "").strip().upper()
    if value not in _SUPPORTED_APPROVAL_STATUSES:
        raise ValueError(
            "QWEN_TTS_APPROVAL_STATUS must be explicitly set to "
            "APPROVED_FOR_STAGING or APPROVED_FOR_PRODUCTION"
        )
    return value


def resolve_approved_manifest(env: _ENV | None = None) -> str:
    """Require a local manifest that attests the exact checkpoint bytes."""

    value = (_raw(env, "QWEN_TTS_APPROVED_MANIFEST") or "").strip()
    if not value:
        raise ValueError("QWEN_TTS_APPROVED_MANIFEST must point to the approved artifact manifest")
    return value


def resolve_speaker(env: _ENV | None = None) -> str:
    """Resolve the only production speaker ID."""

    raw = _raw(env, "QWEN_TTS_SPEAKER")
    speaker = ("tsukihime" if raw is None else raw).strip()
    if not speaker:
        raise ValueError("QWEN_TTS_SPEAKER must not be empty")
    if speaker.casefold() != "tsukihime":
        raise ValueError("trained-custom production speaker must be tsukihime")
    return speaker


def resolve_device(env: _ENV | None = None) -> str:
    """Normalize the supported CPU/GPU device spellings."""

    value = (_raw(env, "QWEN_TTS_DEVICE") or "cpu").strip().lower()
    if value == "cuda":
        return "cuda:0"
    if value == "cpu" or _CUDA_DEVICE.fullmatch(value):
        return value
    raise ValueError("QWEN_TTS_DEVICE must be cpu, cuda, or cuda:<index>")


def env_int(
    name: str,
    default: int,
    minimum: int,
    maximum: int,
    *,
    env: _ENV | None = None,
) -> int:
    raw = _raw(env, name)
    try:
        value = int(raw if raw is not None else str(default))
    except ValueError:
        value = default
    return max(minimum, min(maximum, value))


def env_choice(
    name: str,
    default: str,
    allowed: set[str],
    *,
    env: _ENV | None = None,
) -> str:
    raw = _raw(env, name)
    value = (raw if raw is not None else default).strip().lower()
    if value not in allowed:
        choices = ", ".join(sorted(allowed))
        raise ValueError(f"{name} must be one of: {choices}")
    return value


@dataclass(frozen=True)
class RuntimeTuning:
    attention_implementation: str
    intra_op_threads: int
    inter_op_threads: int
    max_new_tokens: int

    @classmethod
    def from_env(cls, env: _ENV | None = None) -> "RuntimeTuning":
        # QWEN_TTS_THREADS remains the compatibility input for the trained worker.
        legacy_threads = env_int("QWEN_TTS_THREADS", 4, 1, 16, env=env)
        intra_raw = _raw(env, "QWEN_TTS_INTRA_OP_THREADS")
        intra_op_threads = (
            env_int("QWEN_TTS_INTRA_OP_THREADS", legacy_threads, 1, 16, env=env)
            if intra_raw is not None
            else legacy_threads
        )

        # Preserve the existing derived inter-op policy unless explicitly set.
        legacy_inter_op = max(1, min(intra_op_threads, 4))
        inter_raw = _raw(env, "QWEN_TTS_INTER_OP_THREADS")
        inter_op_threads = (
            env_int("QWEN_TTS_INTER_OP_THREADS", legacy_inter_op, 1, 16, env=env)
            if inter_raw is not None
            else legacy_inter_op
        )

        return cls(
            attention_implementation=env_choice(
                "QWEN_TTS_ATTN_IMPLEMENTATION",
                "eager",
                _SUPPORTED_ATTENTION,
                env=env,
            ),
            intra_op_threads=intra_op_threads,
            inter_op_threads=inter_op_threads,
            max_new_tokens=env_int("QWEN_TTS_MAX_NEW_TOKENS", 2048, 64, 8192, env=env),
        )
