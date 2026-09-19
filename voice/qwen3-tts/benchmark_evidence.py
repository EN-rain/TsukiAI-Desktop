"""Deterministic Qwen TTS benchmark runner and evidence reporters.

This module deliberately uses only the Python standard library. It can run on
the inference host without adding production dependencies, reads the API key
from an environment variable, never writes that secret to disk, and keeps
trained-custom identity and cache-miss claims explicit and auditable.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import io
import math
import json
import os
import platform
import re
import socket
import statistics
import threading
import time
import urllib.error
import urllib.request
import uuid
import wave
from array import array
from collections import Counter, defaultdict
from dataclasses import asdict, dataclass, fields
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable, Mapping, Sequence

from benchmark_corpus import CORPUS, CORPUS_VERSION, BenchmarkSample
from benchmark_variants import verify_health_runtime


CACHE_MODES = ("observe", "force-miss", "direct-inference")
QUALITY_RESULT_PENDING = "pending_review"
EVIDENCE_SCOPE_REHEARSAL = "trained_custom_rehearsal"
EVIDENCE_SCOPE_GATE0 = "trained_custom_verified_identity"
EVIDENCE_SCOPES = (EVIDENCE_SCOPE_REHEARSAL, EVIDENCE_SCOPE_GATE0)
_CACHE_HEADERS = (
    "x-tsuki-cache",
    "x-qwen-cache",
    "x-cache",
    "x-cache-status",
    "cf-cache-status",
)
_WORD_PATTERN = re.compile(
    r"[A-Za-z0-9]+(?:['’][A-Za-z0-9]+)*|[\u3040-\u30ff\u3400-\u9fff]"
)


MAX_EVAL_SECONDS = 20.0


def _acoustic_metrics(samples: array, rate: int) -> dict[str, float]:
    """Return frame-based acoustic metrics for a raw PCM16 mono WAV."""

    try:
        import numpy as np
    except ImportError:
        np = None

    values = [sample / 32768.0 for sample in samples]
    if len(values) < 2 or rate <= 0:
        return {
            "periodicity": 0.0,
            "zcr": 0.0,
            "spectral_centroid_hz": 0.0,
            "spectral_flatness": 0.0,
            "high_frequency_energy_ratio": 0.0,
            "voiced_frame_ratio": 0.0,
            "f0_median_hz": 0.0,
            "f0_p10_hz": 0.0,
            "f0_p90_hz": 0.0,
            "hnr_db": 0.0,
        }

    frame_length = min(max(256, int(rate * 0.04)), len(values))
    hop = max(128, frame_length // 2)
    window = np.hanning(frame_length) if np is not None else None
    periodicity: list[float] = []
    zcr: list[float] = []
    centroids: list[float] = []
    flatness: list[float] = []
    high_frequency: list[float] = []
    f0_values: list[float] = []
    hnr_values: list[float] = []
    voiced_frames = 0
    total_frames = 0

    for start in range(0, len(values) - frame_length + 1, hop):
        frame_values = values[start : start + frame_length]
        total_frames += 1
        energy = math.sqrt(sum(value * value for value in frame_values) / frame_length)
        if energy < 0.005:
            continue
        voiced_frames += 1
        zcr.append(sum(a * b < 0 for a, b in zip(frame_values[1:], frame_values[:-1])) / (frame_length - 1))

        if np is not None:
            frame = np.asarray(frame_values, dtype=np.float32)
            centered = frame - float(np.mean(frame))
            autocorrelation = np.correlate(centered, centered, mode="full")[frame_length - 1 :]
            spectrum = np.fft.rfft(frame * window)
            power = np.abs(spectrum) ** 2
            frequencies = np.fft.rfftfreq(frame_length, 1.0 / rate)
        else:
            centered = [value - (sum(frame_values) / frame_length) for value in frame_values]
            autocorrelation = [
                sum(centered[index] * centered[index + lag] for index in range(frame_length - lag))
                for lag in range(frame_length)
            ]
            power = None
            frequencies = None

        if len(autocorrelation) and float(autocorrelation[0]) > 1e-9:
            min_lag = max(1, int(rate / 500))
            max_lag = min(len(autocorrelation) - 1, int(rate / 50))
            if max_lag >= min_lag:
                best_lag = min(range(min_lag, max_lag + 1), key=lambda lag: -float(autocorrelation[lag]))
                score = float(autocorrelation[best_lag] / autocorrelation[0])
                periodicity.append(score)
                if score >= 0.3:
                    f0_values.append(rate / best_lag)
                    hnr_values.append(10.0 * math.log10(max(score, 1e-6) / max(1.0 - score, 1e-6)))

        if np is not None:
            total_power = float(power.sum())
            if total_power > 1e-12:
                centroids.append(float(np.sum(frequencies * power) / total_power))
                high_frequency.append(float(power[frequencies >= 4000.0].sum() / total_power))
                positive_power = power[power > 1e-12]
                if positive_power.size:
                    flatness.append(float(np.exp(np.mean(np.log(positive_power))) / np.mean(positive_power)))

    def median_or_zero(values: list[float]) -> float:
        return float(statistics.median(values)) if values else 0.0

    ordered_f0 = sorted(f0_values)
    return {
        "periodicity": round(median_or_zero(periodicity), 6),
        "zcr": round(median_or_zero(zcr), 6),
        "spectral_centroid_hz": round(median_or_zero(centroids), 3),
        "spectral_flatness": round(median_or_zero(flatness), 6),
        "high_frequency_energy_ratio": round(median_or_zero(high_frequency), 6),
        "voiced_frame_ratio": round(voiced_frames / total_frames, 6) if total_frames else 0.0,
        "f0_median_hz": round(median_or_zero(f0_values), 3),
        "f0_p10_hz": round(ordered_f0[max(0, int(len(ordered_f0) * 0.10))], 3) if ordered_f0 else 0.0,
        "f0_p90_hz": round(ordered_f0[min(len(ordered_f0) - 1, int(len(ordered_f0) * 0.90))], 3) if ordered_f0 else 0.0,
        "hnr_db": round(median_or_zero(hnr_values), 3),
    }


def inspect_wav(path: Path, *, include_acoustic: bool = False) -> dict[str, object]:
    """Inspect a raw PCM16 WAV and return release-gate metrics."""

    flags: list[str] = []
    try:
        raw_bytes = path.read_bytes()
        with wave.open(str(path), "rb") as wav:
            channels = wav.getnchannels()
            rate = wav.getframerate()
            width = wav.getsampwidth()
            frames = wav.getnframes()
            raw = wav.readframes(frames)
        samples = array("h")
        if width == 2:
            samples.frombytes(raw)
        else:
            flags.append("BIT_DEPTH")
        if channels != 1:
            flags.append("CHANNELS")
        if rate != 24000:
            flags.append("SAMPLE_RATE")
        if not samples:
            flags.append("EMPTY")
            duration = 0.0
            peak = 0
            rms = 0.0
            dc_offset = 0.0
            silence_ratio = 1.0
            clipping_ratio = 0.0
        else:
            duration = frames / rate if rate else 0.0
            peak = max(abs(value) for value in samples)
            rms = math.sqrt(sum(value * value for value in samples) / len(samples))
            dc_offset = abs(statistics.mean(samples)) / 32768.0
            silence_ratio = sum(abs(value) <= 128 for value in samples) / len(samples)
            clipping_ratio = sum(abs(value) >= 32760 for value in samples) / len(samples)
            if duration < 0.5 or duration > MAX_EVAL_SECONDS:
                flags.append("DURATION_OUTLIER")
            if peak < 10 or rms <= 1.0:
                flags.append("SILENT_OR_NEAR_SILENT")
            if clipping_ratio > 0.001:
                flags.append("CLIPPING")
            if dc_offset > 0.02:
                flags.append("DC_OFFSET")
            if silence_ratio > 0.80:
                flags.append("EXCESSIVE_SILENCE")
        severe = {
            "EMPTY",
            "CHANNELS",
            "SAMPLE_RATE",
            "BIT_DEPTH",
            "SILENT_OR_NEAR_SILENT",
            "CLIPPING",
            "DURATION_OUTLIER",
        }
        status = "PASS" if not flags else ("REJECT" if severe.intersection(flags) else "REVIEW")
        result: dict[str, object] = {
            "filename": path.name,
            "duration_sec": round(duration, 3),
            "sample_rate": rate,
            "channels": channels,
            "sample_width_bytes": width,
            "peak": peak,
            "rms": round(rms, 3),
            "dc_offset": round(dc_offset, 8),
            "silence_ratio": round(silence_ratio, 6),
            "clipping_ratio": round(clipping_ratio, 8),
            "flags": flags,
            "status": status,
            "sha256": hashlib.sha256(raw_bytes).hexdigest(),
        }
        if include_acoustic:
            result.update(_acoustic_metrics(samples, rate))
        return result
    except (OSError, EOFError, wave.Error, ValueError) as error:
        return {
            "filename": path.name,
            "duration_sec": 0.0,
            "flags": ["UNREADABLE", type(error).__name__],
            "status": "REJECT",
            "sha256": None,
        }


@dataclass(frozen=True)
class BenchmarkMetadata:
    host: str
    cloud: str
    region: str
    vm_sku: str
    cpu_model: str
    backend: str
    backend_version: str
    checkpoint_id: str
    evidence_scope: str
    gate0_qualified: bool
    gate0_attestation: str
    variant_id: str
    precision_quantization: str
    attention_implementation: str
    intra_op_threads: int | None
    inter_op_threads: int | None
    max_new_tokens: int | None
    sampling_profile: str
    model_load_seconds: float | None
    idle_rss_mb: float | None


@dataclass(frozen=True)
class ResourceMetrics:
    peak_rss_mb: float | None = None
    swap_mb: float | None = None
    cpu_average_pct: float | None = None
    cpu_peak_pct: float | None = None


@dataclass(frozen=True)
class BenchmarkResult:
    timestamp: str
    run_id: str
    corpus_version: str
    host: str
    cloud: str
    region: str
    vm_sku: str
    cpu_model: str
    backend: str
    backend_version: str
    checkpoint_id: str
    evidence_scope: str
    gate0_qualified: bool
    gate0_attestation: str
    variant_id: str
    precision_quantization: str
    attention_implementation: str
    intra_op_threads: int | None
    inter_op_threads: int | None
    max_new_tokens: int | None
    sampling_profile: str
    model_load_seconds: float | None
    idle_rss_mb: float | None
    text_id: str
    corpus_bucket: str
    language: str
    iteration: int
    character_count: int
    word_count: int
    generation_seconds: float | None
    request_seconds: float | None
    audio_duration_seconds: float | None
    rtf: float | None
    ttfa_seconds: float | None
    peak_rss_mb: float | None
    swap_mb: float | None
    cpu_average_pct: float | None
    cpu_peak_pct: float | None
    cache_state: str
    cache_miss_mode: str
    evidence_valid: bool
    status: str
    http_status: int | None
    quality_result: str
    audio_path: str
    evidence_note: str
    error: str

    @classmethod
    def from_row(cls, row: Mapping[str, str]) -> "BenchmarkResult":
        optional_float = {
            "generation_seconds",
            "request_seconds",
            "audio_duration_seconds",
            "rtf",
            "ttfa_seconds",
            "model_load_seconds",
            "idle_rss_mb",
            "peak_rss_mb",
            "swap_mb",
            "cpu_average_pct",
            "cpu_peak_pct",
        }
        optional_int = {"intra_op_threads", "inter_op_threads", "max_new_tokens", "http_status"}
        required_int = {"iteration", "character_count", "word_count"}
        values: dict[str, object] = {}
        for item in fields(cls):
            raw = row.get(item.name, "")
            if item.name in optional_float:
                values[item.name] = float(raw) if raw else None
            elif item.name in optional_int:
                values[item.name] = int(raw) if raw else None
            elif item.name in required_int:
                values[item.name] = int(raw)
            elif item.name in {"evidence_valid", "gate0_qualified"}:
                values[item.name] = raw.strip().lower() in {"1", "true", "yes"}
            else:
                values[item.name] = raw
        return cls(**values)  # type: ignore[arg-type]


RESULT_COLUMNS = tuple(item.name for item in fields(BenchmarkResult))
RESULT_SCHEMA_PATH = Path(__file__).with_name("benchmark_result_schema.json")


def utc_timestamp() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def count_words(text: str) -> int:
    """Return a deterministic lexical-unit count for English/Japanese text.

    Japanese segmentation would otherwise require an external tokenizer, so
    kana/kanji characters count as lexical units while Latin words and numbers
    count as normal word tokens. The same rule is applied to every backend.
    """

    return len(_WORD_PATTERN.findall(text))


def wav_duration_seconds(payload: bytes) -> float | None:
    try:
        with wave.open(io.BytesIO(payload), "rb") as stream:
            frame_rate = stream.getframerate()
            if frame_rate <= 0:
                return None
            return stream.getnframes() / frame_rate
    except (EOFError, wave.Error):
        return None


def _optional_float(value: str | None) -> float | None:
    if value is None or not value.strip():
        return None
    try:
        return float(value)
    except ValueError:
        return None


def _header_map(headers: Mapping[str, str]) -> dict[str, str]:
    return {str(key).strip().lower(): str(value).strip() for key, value in headers.items()}


def cache_state_from_headers(headers: Mapping[str, str], cache_mode: str) -> str:
    if cache_mode not in CACHE_MODES:
        raise ValueError(f"unsupported cache mode: {cache_mode}")
    lowered = _header_map(headers)
    for name in _CACHE_HEADERS:
        value = lowered.get(name, "").lower()
        if not value:
            continue
        if "hit" in value and "miss" not in value:
            return "hit"
        if "miss" in value:
            return "miss_reported"
        if "bypass" in value or "dynamic" in value:
            return "miss_bypass_reported"
    if cache_mode == "direct-inference":
        return "miss_declared_direct_inference"
    if cache_mode == "force-miss":
        return "unverified_force_miss"
    return "unknown"


def cache_evidence_valid(cache_state: str, cache_mode: str) -> bool:
    if cache_state == "hit":
        return False
    if cache_state in {"miss_reported", "miss_bypass_reported"}:
        return True
    if cache_mode == "direct-inference" and cache_state == "miss_declared_direct_inference":
        return True
    return False


def cache_request_headers(cache_mode: str, namespace: str) -> dict[str, str]:
    if cache_mode not in CACHE_MODES:
        raise ValueError(f"unsupported cache mode: {cache_mode}")
    if cache_mode == "observe":
        return {}
    headers = {
        "Cache-Control": "no-cache, no-store",
        "Pragma": "no-cache",
        "X-Benchmark-Cache-Namespace": namespace,
    }
    if cache_mode == "force-miss":
        headers["X-Benchmark-Force-Cache-Miss"] = "1"
    return headers


def _cpu_model() -> str:
    processor = platform.processor().strip()
    if processor:
        return processor
    try:
        for line in Path("/proc/cpuinfo").read_text(encoding="utf-8", errors="replace").splitlines():
            if line.lower().startswith("model name") and ":" in line:
                return line.split(":", 1)[1].strip()
    except OSError:
        pass
    return "unknown"


def metadata_from_health(
    health: Mapping[str, object],
    *,
    host: str | None = None,
    cloud: str = "unknown",
    region: str = "unknown",
    vm_sku: str = "unknown",
    cpu_model: str | None = None,
    backend: str | None = None,
    backend_version: str | None = None,
    checkpoint_id: str | None = None,
    evidence_scope: str = EVIDENCE_SCOPE_REHEARSAL,
    gate0_qualified: bool = False,
    gate0_attestation: str = "",
    precision_quantization: str | None = None,
    variant_id: str | None = None,
    idle_rss_mb: float | None = None,
) -> BenchmarkMetadata:
    runtime_raw = health.get("runtime", {})
    runtime = runtime_raw if isinstance(runtime_raw, Mapping) else {}

    def text(value: object, fallback: str = "unknown") -> str:
        cleaned = str(value).strip() if value is not None else ""
        return cleaned or fallback

    def integer(value: object) -> int | None:
        if value is None or value == "":
            return None
        try:
            return int(value)
        except (TypeError, ValueError):
            return None

    return BenchmarkMetadata(
        host=text(host or socket.gethostname()),
        cloud=text(cloud),
        region=text(region),
        vm_sku=text(vm_sku),
        cpu_model=text(cpu_model or _cpu_model()),
        backend=text(backend or health.get("engine")),
        backend_version=text(backend_version or health.get("version")),
        checkpoint_id=text(checkpoint_id or health.get("checkpoint_id") or health.get("model")),
        evidence_scope=evidence_scope,
        gate0_qualified=gate0_qualified,
        gate0_attestation=gate0_attestation,
        variant_id=text(variant_id, "unlabeled"),
        precision_quantization=text(precision_quantization or runtime.get("precision")),
        attention_implementation=text(runtime.get("attention_implementation")),
        intra_op_threads=integer(runtime.get("intra_op_threads")),
        inter_op_threads=integer(runtime.get("inter_op_threads")),
        max_new_tokens=integer(runtime.get("max_new_tokens")),
        sampling_profile=text(runtime.get("sampling_profile")),
        model_load_seconds=_optional_float(str(runtime.get("model_load_seconds", ""))),
        idle_rss_mb=idle_rss_mb,
    )


def _real_sha256(value: object) -> bool:
    return re.fullmatch(r"[0-9a-f]{64}", str(value or "").strip().lower()) is not None


def verify_trained_custom_health(health: Mapping[str, object]) -> None:
    """Require the exact trained-custom identity used by TsukiAI."""

    model = str(health.get("model", "")).strip()
    mode = str(health.get("mode", "")).strip().lower()
    speaker = str(health.get("speaker", "")).strip().lower()
    conditioning_raw = health.get("conditioning")
    conditioning = conditioning_raw if isinstance(conditioning_raw, Mapping) else {}
    problems: list[str] = []
    if str(health.get("status", "")).strip().lower() != "ready":
        problems.append("status must be ready")
    if mode != "trained-custom":
        problems.append(f"mode must be 'trained-custom', got {health.get('mode')!r}")
    if not model or "qwen3-tts-12hz-0.6b-base" in model.casefold():
        problems.append("model must be a trained checkpoint, not the official Base model")
    if speaker != "tsukihime":
        problems.append("speaker must be 'tsukihime'")
    if str(health.get("checkpoint_id", "")).strip() == "":
        problems.append("checkpoint_id must be recorded")
    if str(health.get("checkpoint_id_source", "")).strip().lower() != "env":
        problems.append("checkpoint_id_source must be env")
    if health.get("reference_required") is not False:
        problems.append("reference_required must be false")
    if health.get("runtime_lora_loaded") is not False:
        problems.append("runtime_lora_loaded must be false")
    if conditioning.get("reference_required") is not False:
        problems.append("conditioning.reference_required must be false")
    if conditioning.get("reference_sha256") is not None:
        problems.append("conditioning.reference_sha256 must be null")
    if conditioning.get("x_vector_only_mode") not in {None, False}:
        problems.append("conditioning.x_vector_only_mode must not be enabled")
    if not _real_sha256(health.get("artifact_tree_sha256")):
        problems.append("artifact_tree_sha256 must be a real 64-hex SHA-256")
    if not _real_sha256(health.get("manifest_sha256")):
        problems.append("manifest_sha256 must be a real 64-hex SHA-256")
    if str(health.get("deployment_stage", "")).strip().lower() not in {"staging", "production"}:
        problems.append("deployment_stage must be staging or production")
    if str(health.get("approval_status", "")).strip().upper() not in {"APPROVED_FOR_STAGING", "APPROVED_FOR_PRODUCTION"}:
        problems.append("approval_status must be an approved staging or production state")
    if problems:
        raise ValueError("trained-custom health check failed: " + "; ".join(problems))


def verify_trained_custom_attestation(path: Path, health: Mapping[str, object]) -> str:
    """Validate a trained-custom identity attestation and return its digest."""

    try:
        raw_bytes = path.read_bytes()
        payload = json.loads(raw_bytes.decode("utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"cannot read Gate 0 attestation {path}: {exc}") from exc
    if not isinstance(payload, dict):
        raise ValueError("Gate 0 attestation must be a JSON object")
    if payload.get("gate0_status") != "passed":
        raise ValueError("Gate 0 attestation gate0_status must be 'passed'")

    verify_trained_custom_health(health)

    health_model = str(health.get("model", "")).strip()
    health_checkpoint = str(health.get("checkpoint_id", "")).strip()
    attested_model = str(payload.get("model", "")).strip()
    attested_checkpoint = str(payload.get("checkpoint_id", "")).strip()
    if not health_model or attested_model != health_model:
        raise ValueError("Gate 0 attestation model must exactly match /health model")
    if not health_checkpoint or attested_checkpoint != health_checkpoint:
        raise ValueError("Gate 0 attestation checkpoint_id must exactly match /health checkpoint_id")

    conditioning_raw = health.get("conditioning")
    conditioning = conditioning_raw if isinstance(conditioning_raw, Mapping) else {}
    attested_conditioning_raw = payload.get("conditioning")
    attested_conditioning = attested_conditioning_raw if isinstance(attested_conditioning_raw, Mapping) else {}
    if attested_conditioning.get("reference_required") is not False:
        raise ValueError("trained-custom attestation conditioning.reference_required must be false")
    if attested_conditioning.get("reference_sha256") is not None:
        raise ValueError("trained-custom attestation conditioning.reference_sha256 must be null")
    if attested_conditioning.get("x_vector_only_mode") not in {None, False}:
        raise ValueError("trained-custom attestation conditioning.x_vector_only_mode must not be enabled")
    if payload.get("runtime_lora_loaded") is not False:
        raise ValueError("trained-custom attestation runtime_lora_loaded must be false")

    for field in ("speaker", "artifact_tree_sha256", "manifest_sha256", "approval_status"):
        if str(payload.get(field, "")).strip() != str(health.get(field, "")).strip():
            raise ValueError(f"trained-custom attestation {field} must exactly match /health {field}")

    health_generation_version = str(health.get("generation_version", "")).strip()
    attested_generation_version = str(payload.get("generation_version", "")).strip()
    if not health_generation_version:
        raise ValueError("/health generation_version must be recorded for trained-custom qualified evidence")
    if attested_generation_version != health_generation_version:
        raise ValueError("trained-custom attestation generation_version must exactly match /health generation_version")
    return "sha256:" + hashlib.sha256(raw_bytes).hexdigest()


def resolve_evidence_scope(
    health: Mapping[str, object],
    evidence_scope: str,
    gate0_attestation_path: Path | None,
) -> tuple[bool, str]:
    if evidence_scope == EVIDENCE_SCOPE_REHEARSAL:
        if gate0_attestation_path is not None:
            raise ValueError("trained-custom rehearsal must not carry a qualified identity attestation")
        verify_trained_custom_health(health)
        return False, ""
    if evidence_scope == EVIDENCE_SCOPE_GATE0:
        if gate0_attestation_path is None:
            raise ValueError("Gate 0 qualified evidence requires --gate0-attestation")
        return True, verify_trained_custom_attestation(gate0_attestation_path, health)
    raise ValueError(f"unsupported evidence scope: {evidence_scope}")


def _read_system_cpu_ticks() -> tuple[int, int] | None:
    try:
        first = Path("/proc/stat").read_text(encoding="ascii").splitlines()[0].split()
        if not first or first[0] != "cpu":
            return None
        ticks = [int(value) for value in first[1:]]
        total = sum(ticks)
        idle = (ticks[3] if len(ticks) > 3 else 0) + (ticks[4] if len(ticks) > 4 else 0)
        return total, idle
    except (OSError, ValueError, IndexError):
        return None


def _read_process_memory_mb(pid: int | None) -> tuple[float | None, float | None]:
    if pid is None:
        return None, None
    try:
        values: dict[str, float] = {}
        for line in Path(f"/proc/{pid}/status").read_text(encoding="ascii", errors="replace").splitlines():
            if line.startswith(("VmRSS:", "VmSwap:")):
                key, raw = line.split(":", 1)
                kilobytes = float(raw.strip().split()[0])
                values[key] = kilobytes / 1024.0
        return values.get("VmRSS"), values.get("VmSwap", 0.0)
    except (OSError, ValueError, IndexError):
        return None, None


class ResourceSampler:
    """Samples whole-host CPU and optional target-process RSS/swap on Linux."""

    def __init__(self, pid: int | None, interval_seconds: float = 0.1):
        self.pid = pid
        self.interval_seconds = max(0.02, interval_seconds)
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._cpu_samples: list[float] = []
        self._rss_samples: list[float] = []
        self._swap_samples: list[float] = []

    def start(self) -> None:
        self._thread = threading.Thread(target=self._run, name="benchmark-resource-sampler", daemon=True)
        self._thread.start()

    def stop(self) -> ResourceMetrics:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=max(1.0, self.interval_seconds * 3))
        return ResourceMetrics(
            peak_rss_mb=max(self._rss_samples) if self._rss_samples else None,
            swap_mb=max(self._swap_samples) if self._swap_samples else None,
            cpu_average_pct=statistics.fmean(self._cpu_samples) if self._cpu_samples else None,
            cpu_peak_pct=max(self._cpu_samples) if self._cpu_samples else None,
        )

    def _run(self) -> None:
        previous = _read_system_cpu_ticks()
        self._sample_memory()
        while not self._stop.wait(self.interval_seconds):
            current = _read_system_cpu_ticks()
            if previous is not None and current is not None:
                total_delta = current[0] - previous[0]
                idle_delta = current[1] - previous[1]
                if total_delta > 0:
                    busy_pct = 100.0 * (1.0 - idle_delta / total_delta)
                    self._cpu_samples.append(max(0.0, min(100.0, busy_pct)))
            previous = current
            self._sample_memory()
        self._sample_memory()

    def _sample_memory(self) -> None:
        rss_mb, swap_mb = _read_process_memory_mb(self.pid)
        if rss_mb is not None:
            self._rss_samples.append(rss_mb)
        if swap_mb is not None:
            self._swap_samples.append(swap_mb)


def _request_json(url: str, api_key: str, timeout_seconds: float) -> dict[str, object]:
    request = urllib.request.Request(url, method="GET", headers={"X-Api-Key": api_key})
    with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
        payload = response.read()
    parsed = json.loads(payload.decode("utf-8"))
    if not isinstance(parsed, dict):
        raise ValueError("health response must be a JSON object")
    return parsed


def fetch_health(base_url: str, api_key: str, timeout_seconds: float) -> dict[str, object]:
    return _request_json(f"{base_url.rstrip('/')}/health", api_key, timeout_seconds)


def _cache_note(cache_state: str, cache_mode: str) -> str:
    if cache_state == "hit":
        return "cache hit observed; row is excluded from uncached inference evidence"
    if cache_state == "unverified_force_miss":
        return "force-miss was requested but the response did not acknowledge a cache miss"
    if cache_state == "unknown":
        return "cache state was not reported; row is excluded from uncached inference evidence"
    if cache_mode == "direct-inference" and cache_state == "miss_declared_direct_inference":
        return "operator declared this endpoint to bypass complete-audio caches"
    return ""


def _result_base(
    metadata: BenchmarkMetadata,
    sample: BenchmarkSample,
    run_id: str,
    iteration: int,
    cache_mode: str,
) -> dict[str, object]:
    return {
        "timestamp": utc_timestamp(),
        "run_id": run_id,
        "corpus_version": CORPUS_VERSION,
        **asdict(metadata),
        "text_id": sample.text_id,
        "corpus_bucket": sample.bucket,
        "language": sample.language,
        "iteration": iteration,
        "character_count": len(sample.text),
        "word_count": count_words(sample.text),
        "generation_seconds": None,
        "request_seconds": None,
        "audio_duration_seconds": None,
        "rtf": None,
        "ttfa_seconds": None,
        "peak_rss_mb": None,
        "swap_mb": None,
        "cpu_average_pct": None,
        "cpu_peak_pct": None,
        "cache_state": "unknown",
        "cache_miss_mode": cache_mode,
        "evidence_valid": False,
        "status": "failure",
        "http_status": None,
        "quality_result": QUALITY_RESULT_PENDING,
        "audio_path": "",
        "evidence_note": "",
        "error": "",
    }


def run_sample(
    *,
    base_url: str,
    api_key: str,
    metadata: BenchmarkMetadata,
    sample: BenchmarkSample,
    run_id: str,
    iteration: int,
    cache_mode: str,
    timeout_seconds: float,
    resource_pid: int | None,
    resource_sample_interval: float,
    audio_dir: Path | None,
) -> BenchmarkResult:
    values = _result_base(metadata, sample, run_id, iteration, cache_mode)
    namespace = f"{run_id}:{sample.text_id}:{iteration}"
    headers = {
        "X-Api-Key": api_key,
        "Content-Type": "application/json",
        "Accept": "audio/wav",
        **cache_request_headers(cache_mode, namespace),
    }
    payload = json.dumps({"text": sample.text, "language": sample.language}, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        f"{base_url.rstrip('/')}/tts",
        data=payload,
        method="POST",
        headers=headers,
    )
    sampler = ResourceSampler(resource_pid, resource_sample_interval)
    sampler.start()
    started = time.perf_counter()
    response_headers: dict[str, str] = {}
    wav_payload = b""
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            values["http_status"] = int(response.getcode())
            response_headers = dict(response.headers.items())
            first_byte = response.read(1)
            values["ttfa_seconds"] = time.perf_counter() - started
            wav_payload = first_byte + response.read()
        values["request_seconds"] = time.perf_counter() - started
        if not wav_payload:
            raise ValueError("TTS response contained no audio bytes")

        lowered = _header_map(response_headers)
        generation = _optional_float(lowered.get("x-qwen-runtime-secs"))
        duration = _optional_float(lowered.get("x-qwen-duration-secs"))
        rtf = _optional_float(lowered.get("x-qwen-rtf"))
        if generation is None:
            generation = float(values["request_seconds"])
        if duration is None:
            duration = wav_duration_seconds(wav_payload)
        if rtf is None and generation is not None and duration and duration > 0:
            rtf = generation / duration

        values["generation_seconds"] = generation
        values["audio_duration_seconds"] = duration
        values["rtf"] = rtf
        values["status"] = "success"
        if audio_dir is not None:
            audio_dir.mkdir(parents=True, exist_ok=True)
            audio_path = audio_dir / f"{run_id}-{sample.text_id}-r{iteration}.wav"
            audio_path.write_bytes(wav_payload)
            values["audio_path"] = str(audio_path)
    except urllib.error.HTTPError as exc:
        values["http_status"] = exc.code
        try:
            detail = exc.read(2048).decode("utf-8", errors="replace").strip()
        except OSError:
            detail = ""
        values["error"] = f"HTTP {exc.code}: {detail or exc.reason}"
    except (OSError, ValueError, urllib.error.URLError) as exc:
        values["error"] = str(exc)
    finally:
        resources = sampler.stop()
        values.update(asdict(resources))

    cache_state = cache_state_from_headers(response_headers, cache_mode)
    values["cache_state"] = cache_state
    values["evidence_valid"] = values["status"] == "success" and cache_evidence_valid(cache_state, cache_mode)
    values["evidence_note"] = _cache_note(cache_state, cache_mode)
    return BenchmarkResult(**values)  # type: ignore[arg-type]


def write_results_csv(path: Path, results: Sequence[BenchmarkResult]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=RESULT_COLUMNS)
        writer.writeheader()
        for result in results:
            writer.writerow(asdict(result))


def load_results_csv(path: Path) -> list[BenchmarkResult]:
    with path.open("r", encoding="utf-8", newline="") as stream:
        reader = csv.DictReader(stream)
        missing = [column for column in RESULT_COLUMNS if column not in (reader.fieldnames or [])]
        if missing:
            raise ValueError(f"benchmark CSV is missing required columns: {', '.join(missing)}")
        return [BenchmarkResult.from_row(row) for row in reader]


def _mean(values: Iterable[float | None]) -> float | None:
    present = [value for value in values if value is not None]
    return statistics.fmean(present) if present else None


def _fmt(value: float | None, digits: int = 3) -> str:
    return "" if value is None else f"{value:.{digits}f}"


def render_results_markdown(results: Sequence[BenchmarkResult]) -> str:
    valid = [row for row in results if row.status == "success" and row.evidence_valid]
    successful = [row for row in results if row.status == "success"]
    grouped: dict[str, list[BenchmarkResult]] = defaultdict(list)
    for row in valid:
        grouped[f"{row.evidence_scope}\0{row.corpus_bucket}"].append(row)

    rehearsal_rows = [row for row in results if row.evidence_scope == EVIDENCE_SCOPE_REHEARSAL]
    gate0_rows = [row for row in results if row.gate0_qualified]

    lines = [
        "# TTS Benchmark Results",
        "",
        f"Corpus version: `{results[0].corpus_version if results else CORPUS_VERSION}`",
        "",
        f"Rows: **{len(results)}**  |  Synthesis successes: **{len(successful)}**  |  Valid uncached timing rows: **{len(valid)}**  |  Gate0-qualified rows: **{len(gate0_rows)}**",
        "",
        "`evidence_valid` means cache/timing evidence is usable. It does not mean Gate 0 passed; only `gate0_qualified=true` rows are eligible for Gate 1/2 production decisions.",
        "",
        "## Runtime configuration",
        "",
        "| Scope | Gate0 | Variant | Backend | Checkpoint | Precision | Attention | Intra-op | Inter-op | Max new tokens | Sampling | Model load (s) | Idle RSS (MB) |",
        "|---|---|---|---|---|---|---|---:|---:|---:|---|---:|---:|",
    ]
    if rehearsal_rows:
        lines[4:4] = [
            "**REHEARSAL ONLY:** these trained-custom rows were collected without a verified identity attestation and cannot select a production winner.",
            "",
        ]
    configurations = sorted({
        (
            row.evidence_scope,
            row.gate0_qualified,
            row.variant_id,
            row.backend,
            row.checkpoint_id,
            row.precision_quantization,
            row.attention_implementation,
            row.intra_op_threads,
            row.inter_op_threads,
            row.max_new_tokens,
            row.sampling_profile,
            row.model_load_seconds,
            row.idle_rss_mb,
        )
        for row in results
    }, key=lambda item: tuple("" if value is None else str(value) for value in item))
    for config in configurations:
        scope, gate0_qualified, variant, backend, checkpoint, precision, attention, intra, inter, max_tokens, sampling, model_load, idle_rss = config
        lines.append(
            f"| {scope} | {'yes' if gate0_qualified else 'no'} | {variant} | {backend} | {checkpoint} | {precision} | {attention} | "
            f"{'' if intra is None else intra} | {'' if inter is None else inter} | "
            f"{'' if max_tokens is None else max_tokens} | {sampling} | {_fmt(model_load)} | {_fmt(idle_rss, 1)} |"
        )
    lines.extend([
        "",
        "## Aggregate by corpus bucket",
        "",
        "| Scope | Bucket | Valid timing rows | Generation avg (s) | TTFA avg (s) | Audio avg (s) | RTF avg | CPU avg (%) | CPU peak max (%) | Peak RSS max (MB) | Swap max (MB) |",
        "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|",
    ])
    bucket_order = [
        "very_short",
        "one_sentence",
        "two_sentence",
        "three_sentence",
        "long_detail",
        "japanese",
        "mixed_language",
    ]
    scopes = sorted({row.evidence_scope for row in valid}) or [EVIDENCE_SCOPE_REHEARSAL]
    for scope in scopes:
      for bucket in bucket_order:
        rows = grouped.get(f"{scope}\0{bucket}", [])
        peak_cpu = max((row.cpu_peak_pct for row in rows if row.cpu_peak_pct is not None), default=None)
        peak_rss = max((row.peak_rss_mb for row in rows if row.peak_rss_mb is not None), default=None)
        peak_swap = max((row.swap_mb for row in rows if row.swap_mb is not None), default=None)
        lines.append(
            "| "
            + " | ".join(
                [
                    scope,
                    bucket,
                    str(len(rows)),
                    _fmt(_mean(row.generation_seconds for row in rows)),
                    _fmt(_mean(row.ttfa_seconds for row in rows)),
                    _fmt(_mean(row.audio_duration_seconds for row in rows)),
                    _fmt(_mean(row.rtf for row in rows)),
                    _fmt(_mean(row.cpu_average_pct for row in rows), 1),
                    _fmt(peak_cpu, 1),
                    _fmt(peak_rss, 1),
                    _fmt(peak_swap, 1),
                ]
            )
            + " |"
        )

    cache_counts = Counter(row.cache_state for row in results)
    lines.extend(
        [
            "",
            "## Cache evidence",
            "",
            "| Cache state | Rows |",
            "|---|---:|",
        ]
    )
    for state, count in sorted(cache_counts.items()):
        lines.append(f"| {state} | {count} |")

    lines.extend(
        [
            "",
            "## Per-text results",
            "",
            "| Text ID | Bucket | Iteration | Status | Cache | Valid | Generation (s) | TTFA (s) | Audio (s) | RTF | Quality |",
            "|---|---|---:|---|---|---|---:|---:|---:|---:|---|",
        ]
    )
    for row in results:
        lines.append(
            f"| {row.text_id} | {row.corpus_bucket} | {row.iteration} | {row.status} | {row.cache_state} | "
            f"{'yes' if row.evidence_valid else 'no'} | {_fmt(row.generation_seconds)} | {_fmt(row.ttfa_seconds)} | "
            f"{_fmt(row.audio_duration_seconds)} | {_fmt(row.rtf)} | {row.quality_result} |"
        )
    lines.append("")
    return "\n".join(lines)


def render_quality_scaffold(results: Sequence[BenchmarkResult]) -> str:
    lines = [
        "# TTS Quality Comparison",
        "",
        "Unqualified trained-custom rehearsal rows remain rehearsal evidence even if a reviewer marks their audio quality as pass. A quality verdict does not make identity verification pass.",
        "",
        "Score each dimension from **1 (poor)** to **5 (reference-equivalent/excellent)**. Use `N/A` only when a language-specific dimension does not apply. Set Verdict to `pass` or `reject`; a faster backend must not pass when Tsuki voice quality regresses.",
        "",
        "| Scope | Gate0 | Text ID | Bucket | Backend | Precision | Audio | Voice identity | Tsuki accent/style | Pronunciation | Prosody | Pitch stability | Rhythm | Noise/artifacts | Sentence endings | Japanese quality | English quality | Verdict | Notes |",
        "|---|---|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|",
    ]
    for row in results:
        if row.status != "success":
            continue
        audio = row.audio_path.replace("|", "\\|") if row.audio_path else "(not saved)"
        lines.append(
            f"| {row.evidence_scope} | {'yes' if row.gate0_qualified else 'no'} | {row.text_id} | {row.corpus_bucket} | {row.backend} | {row.precision_quantization} | {audio} |  |  |  |  |  |  |  |  |  |  |  |  |"
        )
    lines.append("")
    return "\n".join(lines)


def _select_corpus(text_ids: Sequence[str], buckets: Sequence[str]) -> tuple[BenchmarkSample, ...]:
    wanted_ids = {value.strip() for value in text_ids if value.strip()}
    wanted_buckets = {value.strip() for value in buckets if value.strip()}
    known_ids = {sample.text_id for sample in CORPUS}
    known_buckets = {sample.bucket for sample in CORPUS}
    unknown_ids = sorted(wanted_ids - known_ids)
    unknown_buckets = sorted(wanted_buckets - known_buckets)
    if unknown_ids:
        raise ValueError(f"unknown text IDs: {', '.join(unknown_ids)}")
    if unknown_buckets:
        raise ValueError(f"unknown corpus buckets: {', '.join(unknown_buckets)}")
    selected = tuple(
        sample
        for sample in CORPUS
        if (not wanted_ids or sample.text_id in wanted_ids)
        and (not wanted_buckets or sample.bucket in wanted_buckets)
    )
    if not selected:
        raise ValueError("corpus selection is empty")
    return selected


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Run the deterministic Tsuki Qwen TTS benchmark corpus.")
    parser.add_argument("--base-url", required=True, help="Qwen service root, for example http://127.0.0.1:8100")
    parser.add_argument("--api-key-env", default="QWEN_TTS_API_KEY", help="environment variable containing the API key")
    parser.add_argument("--output-dir", type=Path, default=Path("benchmark-output"))
    parser.add_argument("--save-audio", action="store_true", help="save WAV responses for the quality review scaffold")
    parser.add_argument(
        "--cache-mode",
        choices=CACHE_MODES,
        default="direct-inference",
        help=(
            "direct-inference follows the corpus rule of calling the private Qwen worker directly; "
            "force-miss requires a cache-aware response to confirm the miss"
        ),
    )
    parser.add_argument("--cloud", default=os.getenv("BENCHMARK_CLOUD", "unknown"))
    parser.add_argument("--region", default=os.getenv("BENCHMARK_REGION", "unknown"))
    parser.add_argument("--vm-sku", default=os.getenv("BENCHMARK_VM_SKU", "unknown"))
    parser.add_argument("--host", default=os.getenv("BENCHMARK_HOST", ""))
    parser.add_argument("--backend", default=os.getenv("BENCHMARK_BACKEND", ""))
    parser.add_argument("--backend-version", default=os.getenv("BENCHMARK_BACKEND_VERSION", ""))
    parser.add_argument("--checkpoint-id", default=os.getenv("BENCHMARK_CHECKPOINT_ID", ""))
    parser.add_argument("--precision-quantization", default=os.getenv("BENCHMARK_PRECISION", ""))
    parser.add_argument("--variant-id", default=os.getenv("BENCHMARK_VARIANT_ID", ""), help="Gate/variant label such as gate1-B or gate2-1024")
    parser.add_argument(
        "--evidence-scope",
        choices=EVIDENCE_SCOPES,
        default=EVIDENCE_SCOPE_REHEARSAL,
        help="defaults to unqualified trained-custom rehearsal; qualified identity evidence requires an explicit attestation",
    )
    parser.add_argument("--gate0-attestation", type=Path, help="JSON trained-custom identity attestation required only for the qualified scope")
    parser.add_argument("--resource-pid", type=int, help="local inference process PID for RSS/swap sampling")
    parser.add_argument("--resource-sample-interval", type=float, default=0.1)
    parser.add_argument("--timeout", type=float, default=300.0)
    parser.add_argument("--repetitions", type=int, default=1)
    parser.add_argument("--text-id", action="append", default=[], help="run only one fixed text ID; repeat as needed")
    parser.add_argument("--bucket", action="append", default=[], help="run only one corpus bucket; repeat as needed")
    parser.add_argument("--run-id", default="")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = _build_parser()
    args = parser.parse_args(argv)
    if args.repetitions < 1:
        parser.error("--repetitions must be at least 1")
    if args.resource_pid is not None and args.resource_pid <= 0:
        parser.error("--resource-pid must be positive")
    api_key = os.getenv(args.api_key_env, "").strip()
    if not api_key:
        parser.error(f"API key environment variable {args.api_key_env!r} is not set")
    try:
        corpus = _select_corpus(args.text_id, args.bucket)
    except ValueError as exc:
        parser.error(str(exc))

    health = fetch_health(args.base_url, api_key, args.timeout)
    try:
        gate0_qualified, gate0_attestation = resolve_evidence_scope(
            health,
            args.evidence_scope,
            args.gate0_attestation,
        )
    except ValueError as exc:
        parser.error(str(exc))
    health_checkpoint = str(health.get("checkpoint_id") or health.get("model") or "").strip()
    if args.checkpoint_id.strip() and args.checkpoint_id.strip() != health_checkpoint:
        parser.error("--checkpoint-id must exactly match the checkpoint identity reported by /health")
    if args.variant_id.strip():
        verify_health_runtime(args.variant_id.strip(), health)
    idle_rss_mb = _read_process_memory_mb(args.resource_pid)[0] if args.resource_pid is not None else None
    metadata = metadata_from_health(
        health,
        host=args.host or None,
        cloud=args.cloud,
        region=args.region,
        vm_sku=args.vm_sku,
        backend=args.backend or None,
        backend_version=args.backend_version or None,
        checkpoint_id=args.checkpoint_id or None,
        evidence_scope=args.evidence_scope,
        gate0_qualified=gate0_qualified,
        gate0_attestation=gate0_attestation,
        precision_quantization=args.precision_quantization or None,
        variant_id=args.variant_id or None,
        idle_rss_mb=idle_rss_mb,
    )
    run_id = args.run_id.strip() or f"{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')}-{uuid.uuid4().hex[:8]}"
    audio_dir = args.output_dir / "audio" if args.save_audio else None
    results: list[BenchmarkResult] = []
    for iteration in range(1, args.repetitions + 1):
        for sample in corpus:
            result = run_sample(
                base_url=args.base_url,
                api_key=api_key,
                metadata=metadata,
                sample=sample,
                run_id=run_id,
                iteration=iteration,
                cache_mode=args.cache_mode,
                timeout_seconds=args.timeout,
                resource_pid=args.resource_pid,
                resource_sample_interval=args.resource_sample_interval,
                audio_dir=audio_dir,
            )
            results.append(result)
            print(
                f"{sample.text_id} r{iteration}: {result.status} generation={_fmt(result.generation_seconds)}s "
                f"rtf={_fmt(result.rtf)} cache={result.cache_state} valid={'yes' if result.evidence_valid else 'no'}",
                flush=True,
            )

    args.output_dir.mkdir(parents=True, exist_ok=True)
    write_results_csv(args.output_dir / "benchmark-results.csv", results)
    (args.output_dir / "benchmark-results.md").write_text(render_results_markdown(results), encoding="utf-8")
    (args.output_dir / "quality-comparison.md").write_text(render_quality_scaffold(results), encoding="utf-8")
    (args.output_dir / RESULT_SCHEMA_PATH.name).write_text(
        RESULT_SCHEMA_PATH.read_text(encoding="utf-8"),
        encoding="utf-8",
    )
    return 0 if all(row.status == "success" for row in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
