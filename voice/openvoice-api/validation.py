"""Pure request validation shared by the OpenVoice API and unit tests."""

from __future__ import annotations

import re


SUPPORTED_LANGUAGES = {"EN": "EN", "ENGLISH": "EN", "JA": "JA", "JP": "JA", "JAPANESE": "JA"}
VOICE_ID_PATTERN = re.compile(r"^[A-Za-z0-9_-]{1,64}$")


def normalize_text(value: str, max_chars: int) -> str:
    if not isinstance(value, str):
        raise ValueError("text must be a string")

    cleaned = " ".join(value.split())
    if not cleaned:
        raise ValueError("text must not be blank")
    if len(cleaned) > max_chars:
        raise ValueError(f"text exceeds the {max_chars}-character limit")
    return cleaned


def normalize_language(value: str) -> str:
    if not isinstance(value, str):
        raise ValueError("language must be a string")

    normalized = value.strip().upper()
    try:
        return SUPPORTED_LANGUAGES[normalized]
    except KeyError as exc:
        raise ValueError("language must be EN or JA") from exc


def normalize_voice_id(value: str, expected: str) -> str:
    if not isinstance(value, str) or not VOICE_ID_PATTERN.fullmatch(value):
        raise ValueError("voice must contain only letters, numbers, '_' or '-' and be at most 64 characters")
    if value != expected:
        raise ValueError(f"only the configured voice '{expected}' is available")
    return value
