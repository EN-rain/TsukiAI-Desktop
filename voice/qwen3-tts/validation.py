"""Pure request and language validation for the local Qwen TTS service."""

from __future__ import annotations


SUPPORTED_LANGUAGES = {"english": "English", "en": "English", "japanese": "Japanese", "ja": "Japanese", "jp": "Japanese"}


def normalize_language(value: str | None) -> str:
    normalized = (value or "English").strip().lower()
    try:
        return SUPPORTED_LANGUAGES[normalized]
    except KeyError as exc:
        raise ValueError("language must be English or Japanese") from exc


def validate_text(value: str | None, max_chars: int) -> str:
    text = (value or "").strip()
    if not text:
        raise ValueError("text is required")
    if len(text) > max_chars:
        raise ValueError(f"text is limited to {max_chars} characters")
    return text


def audio_stats(samples, sample_rate: int) -> tuple[float, float, float]:
    """Return RMS, peak, and duration for a mono floating-point waveform."""

    if sample_rate <= 0 or samples is None or len(samples) == 0:
        return 0.0, 0.0, 0.0
    peak = float(abs(samples).max())
    rms = float((samples.astype("float64") ** 2).mean() ** 0.5)
    return rms, peak, len(samples) / sample_rate
