"""Helpers for resolving official OpenVoice V2 base-speaker artifacts."""

from __future__ import annotations

import re
from pathlib import Path


def official_embedding_filename(speaker_name: str) -> str:
    """Return the filename used by the official V2 base-speaker bundle."""

    normalized = re.sub(r"[^a-z0-9]+", "-", speaker_name.strip().lower()).strip("-")
    if not normalized:
        raise ValueError("base speaker name must contain at least one letter or number")
    return f"{normalized}.pth"


def official_embedding_path(directory: Path, speaker_name: str) -> Path:
    """Resolve one official base-speaker embedding below ``directory``."""

    return Path(directory) / official_embedding_filename(speaker_name)
