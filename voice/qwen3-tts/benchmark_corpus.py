"""Load and validate the canonical deterministic Qwen TTS benchmark corpus."""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class BenchmarkSample:
    text_id: str
    bucket: str
    language: str
    text: str


_CORPUS_PATH = Path(__file__).with_name("benchmark_corpus.json")


def _load_corpus() -> tuple[str, tuple[BenchmarkSample, ...], dict[str, int]]:
    raw = _CORPUS_PATH.read_bytes()
    payload = json.loads(raw.decode("utf-8"))
    if not isinstance(payload, dict):
        raise ValueError("benchmark_corpus.json must contain a JSON object")
    version = payload.get("version")
    items = payload.get("items")
    rules = payload.get("rules")
    if not isinstance(version, int) or version < 1:
        raise ValueError("benchmark corpus version must be a positive integer")
    if not isinstance(items, list):
        raise ValueError("benchmark corpus items must be a list")
    if not isinstance(rules, dict) or not isinstance(rules.get("text_counts"), dict):
        raise ValueError("benchmark corpus rules.text_counts must be an object")

    samples: list[BenchmarkSample] = []
    seen_ids: set[str] = set()
    for index, item in enumerate(items, start=1):
        if not isinstance(item, dict):
            raise ValueError(f"benchmark corpus item {index} must be an object")
        text_id = str(item.get("id", "")).strip()
        bucket = str(item.get("category", "")).strip()
        language = str(item.get("language", "")).strip()
        text = str(item.get("text", "")).strip()
        if not text_id or not bucket or not language or not text:
            raise ValueError(f"benchmark corpus item {index} has an empty required field")
        if text_id in seen_ids:
            raise ValueError(f"duplicate benchmark text ID: {text_id}")
        seen_ids.add(text_id)
        samples.append(BenchmarkSample(text_id, bucket, language, text))

    expected_counts: dict[str, int] = {}
    for bucket, count in rules["text_counts"].items():
        if not isinstance(bucket, str) or not isinstance(count, int) or count < 0:
            raise ValueError("benchmark corpus text_counts must map names to non-negative integers")
        expected_counts[bucket] = count

    digest = hashlib.sha256(raw).hexdigest()[:12]
    return f"v{version}-sha256-{digest}", tuple(samples), expected_counts


CORPUS_VERSION, CORPUS, EXPECTED_BUCKET_COUNTS = _load_corpus()
