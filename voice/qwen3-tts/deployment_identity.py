from __future__ import annotations

import hashlib
import os
from collections.abc import Mapping
from dataclasses import dataclass


DEFAULT_GENERATION_VERSION = "trained-custom-v1"


@dataclass(frozen=True)
class DeploymentIdentity:
    """Stable model identity used for health reporting and cache namespacing."""

    checkpoint_id: str
    generation_version: str
    checkpoint_id_source: str

    @classmethod
    def from_env(
        cls,
        model_name: str,
        environ: Mapping[str, str] | None = None,
        *,
        require_explicit: bool = False,
    ) -> "DeploymentIdentity":
        env = os.environ if environ is None else environ
        normalized_model = model_name.strip()
        if not normalized_model:
            raise ValueError("QWEN_TTS_MODEL must not be empty")

        explicit_checkpoint = env.get("QWEN_TTS_CHECKPOINT_ID", "").strip()
        if require_explicit and not explicit_checkpoint:
            raise ValueError("QWEN_TTS_CHECKPOINT_ID must be explicitly recorded for the runtime")
        checkpoint_id = explicit_checkpoint or normalized_model
        checkpoint_id_source = "env" if explicit_checkpoint else "model-name-fallback"
        generation_version = (
            env.get("QWEN_TTS_GENERATION_VERSION", DEFAULT_GENERATION_VERSION).strip()
            or DEFAULT_GENERATION_VERSION
        )
        return cls(
            checkpoint_id=checkpoint_id,
            generation_version=generation_version,
            checkpoint_id_source=checkpoint_id_source,
        )

    @property
    def audio_cache_namespace(self) -> str:
        """Filesystem-safe namespace that changes with checkpoint or generation semantics."""

        material = f"{self.checkpoint_id}\n{self.generation_version}".encode("utf-8")
        digest = hashlib.sha256(material).hexdigest()[:24]
        return f"qwen3tts-{digest}"
