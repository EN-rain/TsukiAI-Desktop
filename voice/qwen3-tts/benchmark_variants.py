"""Load and verify the locked Gate 1/2 Qwen benchmark variant matrix."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping


_VARIANTS_PATH = Path(__file__).with_name("benchmark_variants.json")


@dataclass(frozen=True)
class BenchmarkVariant:
    variant_id: str
    gate: int
    description: str
    env: Mapping[str, str]
    expected_runtime: Mapping[str, object]


def load_variants() -> dict[str, BenchmarkVariant]:
    payload = json.loads(_VARIANTS_PATH.read_text(encoding="utf-8"))
    raw_variants = payload.get("variants") if isinstance(payload, dict) else None
    if not isinstance(raw_variants, list):
        raise ValueError("benchmark_variants.json variants must be a list")

    variants: dict[str, BenchmarkVariant] = {}
    for index, raw in enumerate(raw_variants, start=1):
        if not isinstance(raw, dict):
            raise ValueError(f"benchmark variant {index} must be an object")
        variant_id = str(raw.get("id", "")).strip()
        gate = raw.get("gate")
        description = str(raw.get("description", "")).strip()
        env = raw.get("env")
        expected = raw.get("expected_runtime")
        if not variant_id or not isinstance(gate, int) or gate not in {1, 2} or not description:
            raise ValueError(f"benchmark variant {index} has invalid identity fields")
        if not isinstance(env, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in env.items()):
            raise ValueError(f"benchmark variant {variant_id} env must map strings to strings")
        if not isinstance(expected, dict) or not expected:
            raise ValueError(f"benchmark variant {variant_id} expected_runtime must be a non-empty object")
        if variant_id in variants:
            raise ValueError(f"duplicate benchmark variant ID: {variant_id}")
        variants[variant_id] = BenchmarkVariant(variant_id, gate, description, dict(env), dict(expected))
    return variants


VARIANTS = load_variants()


def verify_health_runtime(variant_id: str, health: Mapping[str, object]) -> None:
    """Reject mislabeled evidence when health does not match a locked variant."""

    try:
        variant = VARIANTS[variant_id]
    except KeyError as exc:
        raise ValueError(f"unknown benchmark variant ID: {variant_id}") from exc

    runtime_raw = health.get("runtime")
    runtime = runtime_raw if isinstance(runtime_raw, Mapping) else {}
    mismatches: list[str] = []
    for key, expected in variant.expected_runtime.items():
        actual = runtime.get(key)
        if actual != expected:
            mismatches.append(f"{key}: expected {expected!r}, health reported {actual!r}")
    if mismatches:
        raise ValueError(f"benchmark variant {variant_id} does not match /health: " + "; ".join(mismatches))
