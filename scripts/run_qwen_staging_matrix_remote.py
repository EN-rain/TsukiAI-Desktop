"""Generate raw-WAV trained-custom staging evidence on the inference host.

This runner sends only text and language to the endpoint.  Supplied reference
files are represented by a local hash manifest and are never opened or sent
as runtime conditioning input.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import socket
import statistics
import time
import urllib.request
import wave
import zipfile
from array import array
from pathlib import Path


ENGLISH_TARGET = (
    "This is the three sentence Azure and the Colab voice test. The custom domain is carrying "
    "the request to Colab. The Discord bridge is sending the result as a real voice message."
)
JAPANESE_TARGET_SHA256 = "f9650ff65ae06cb9fc9a9445b20fe10969270bb0024aa1653040cedd8543107f"


def read_api_key(path: Path) -> str:
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("QWEN_TTS_API_KEY="):
            value = line.split("=", 1)[1].strip()
            if value:
                return value
    raise RuntimeError("staging API key is missing")


def post_tts(base_url: str, api_key: str, text: str, language: str) -> tuple[bytes, dict[str, str]]:
    payload = json.dumps({"text": text, "language": language}, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        f"{base_url.rstrip('/')}/tts",
        data=payload,
        method="POST",
        headers={
            "Content-Type": "application/json",
            "X-Api-Key": api_key,
            "X-Correlation-ID": f"final-all450-{int(time.time() * 1000)}",
        },
    )
    with urllib.request.urlopen(request, timeout=300) as response:
        return response.read(), {str(key).lower(): str(value) for key, value in response.headers.items()}


def fetch_health(base_url: str, api_key: str) -> dict[str, object]:
    request = urllib.request.Request(
        f"{base_url.rstrip('/')}/health",
        method="GET",
        headers={"X-Api-Key": api_key},
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        health = json.loads(response.read().decode("utf-8"))
    if not isinstance(health, dict):
        raise RuntimeError("health response is not a JSON object")
    expected = {
        "status": "ready",
        "mode": "trained-custom",
        "speaker": "tsukihime",
        "deployment_stage": "staging",
        "approval_status": "APPROVED_FOR_STAGING",
        "reference_required": False,
        "runtime_lora_loaded": False,
    }
    problems = [f"{key}={health.get(key)!r}" for key, value in expected.items() if health.get(key) != value]
    conditioning = health.get("conditioning")
    if not isinstance(conditioning, dict):
        problems.append("conditioning is missing")
    elif conditioning.get("reference_required") is not False or conditioning.get("reference_sha256") is not None:
        problems.append("conditioning contains reference state")
    if not str(health.get("checkpoint_id", "")).strip():
        problems.append("checkpoint_id is missing")
    if len(str(health.get("artifact_tree_sha256", ""))) != 64:
        problems.append("artifact_tree_sha256 is missing")
    if len(str(health.get("manifest_sha256", ""))) != 64:
        problems.append("manifest_sha256 is missing")
    if problems:
        raise RuntimeError("health contract failed: " + ", ".join(problems))
    return health


def acoustic_metrics(samples: array, rate: int) -> dict[str, float]:
    try:
        import numpy as np
    except ImportError:
        np = None
    values = [sample / 32768.0 for sample in samples]
    if len(values) < 2 or rate <= 0:
        return {key: 0.0 for key in (
            "periodicity", "zcr", "spectral_centroid_hz", "spectral_flatness",
            "high_frequency_energy_ratio", "voiced_frame_ratio", "f0_median_hz",
            "f0_p10_hz", "f0_p90_hz", "hnr_db",
        )}
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
            centered = [value - sum(frame_values) / frame_length for value in frame_values]
            autocorrelation = [
                sum(centered[index] * centered[index + lag] for index in range(frame_length - lag))
                for lag in range(frame_length)
            ]
            power = frequencies = None
        if len(autocorrelation) and float(autocorrelation[0]) > 1e-9:
            minimum, maximum = max(1, int(rate / 500)), min(len(autocorrelation) - 1, int(rate / 50))
            if maximum >= minimum:
                best_lag = min(range(minimum, maximum + 1), key=lambda lag: -float(autocorrelation[lag]))
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
    percentile = lambda fraction: ordered_f0[min(len(ordered_f0) - 1, int(len(ordered_f0) * fraction))] if ordered_f0 else 0.0
    return {
        "periodicity": round(median_or_zero(periodicity), 6),
        "zcr": round(median_or_zero(zcr), 6),
        "spectral_centroid_hz": round(median_or_zero(centroids), 3),
        "spectral_flatness": round(median_or_zero(flatness), 6),
        "high_frequency_energy_ratio": round(median_or_zero(high_frequency), 6),
        "voiced_frame_ratio": round(voiced_frames / total_frames, 6) if total_frames else 0.0,
        "f0_median_hz": round(median_or_zero(f0_values), 3),
        "f0_p10_hz": round(percentile(0.10), 3),
        "f0_p90_hz": round(percentile(0.90), 3),
        "hnr_db": round(median_or_zero(hnr_values), 3),
    }


def inspect_wav(path: Path, *, max_duration_seconds: float = 20.0) -> dict[str, object]:
    try:
        raw_bytes = path.read_bytes()
        with wave.open(str(path), "rb") as stream:
            channels, rate, width, frames = (
                stream.getnchannels(),
                stream.getframerate(),
                stream.getsampwidth(),
                stream.getnframes(),
            )
            raw = stream.readframes(frames)
        samples = array("h")
        if width == 2:
            samples.frombytes(raw)
        flags: list[str] = []
        if width != 2:
            flags.append("BIT_DEPTH")
        if channels != 1:
            flags.append("CHANNELS")
        if rate != 24000:
            flags.append("SAMPLE_RATE")
        duration = frames / rate if rate else 0.0
        if duration < 0.5 or duration > max_duration_seconds:
            flags.append("DURATION_OUTLIER")
        peak = max(abs(value) for value in samples) if samples else 0
        rms = math.sqrt(sum(value * value for value in samples) / len(samples)) if samples else 0.0
        dc_offset = abs(statistics.mean(samples)) / 32768.0 if samples else 0.0
        silence_ratio = sum(abs(value) <= 128 for value in samples) / len(samples) if samples else 1.0
        clipping_ratio = sum(abs(value) >= 32760 for value in samples) / len(samples) if samples else 0.0
        if not samples or peak < 10 or rms <= 1.0:
            flags.append("SILENT_OR_NEAR_SILENT")
        if clipping_ratio > 0.001:
            flags.append("CLIPPING")
        if dc_offset > 0.02:
            flags.append("DC_OFFSET")
        if silence_ratio > 0.80:
            flags.append("EXCESSIVE_SILENCE")
        severe = {"BIT_DEPTH", "CHANNELS", "SAMPLE_RATE", "DURATION_OUTLIER", "SILENT_OR_NEAR_SILENT", "CLIPPING"}
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
            "status": "PASS" if not flags else ("REJECT" if severe.intersection(flags) else "REVIEW"),
            "sha256": hashlib.sha256(raw_bytes).hexdigest(),
        }
        result.update(acoustic_metrics(samples, rate))
        return result
    except (OSError, EOFError, wave.Error, ValueError) as error:
        return {"filename": path.name, "duration_sec": 0.0, "flags": ["UNREADABLE", type(error).__name__], "status": "REJECT", "sha256": None}


def safe_name(value: str) -> str:
    return "".join(char if char.isalnum() or char in "-_" else "_" for char in value.lower())


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", default="http://127.0.0.1:8102")
    parser.add_argument("--env-file", type=Path, required=True)
    parser.add_argument("--japanese-text-file", type=Path, required=True)
    parser.add_argument("--accent-prompts-file", type=Path, required=True)
    parser.add_argument("--reference-manifest", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()

    api_key = read_api_key(args.env_file)
    japanese_text = args.japanese_text_file.read_text(encoding="utf-8").strip()
    prompts = json.loads(args.accent_prompts_file.read_text(encoding="utf-8"))
    references = json.loads(args.reference_manifest.read_text(encoding="utf-8"))
    japanese_text_sha256 = hashlib.sha256(japanese_text.encode("utf-8")).hexdigest()
    if japanese_text_sha256 != JAPANESE_TARGET_SHA256:
        raise RuntimeError(
            f"Japanese target SHA-256={japanese_text_sha256}, expected={JAPANESE_TARGET_SHA256}; refusing non-exact target"
        )
    if not japanese_text or not isinstance(prompts, list) or not all(isinstance(item, str) and item.strip() for item in prompts):
        raise RuntimeError("invalid staging inputs")
    args.evidence_dir.mkdir(parents=True, exist_ok=True)
    raw_dir = args.evidence_dir / "raw"
    raw_dir.mkdir(parents=True, exist_ok=True)
    health = fetch_health(args.base_url, api_key)
    (args.evidence_dir / "health.json").write_text(json.dumps(health, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (args.evidence_dir / "supplied_reference_manifest.json").write_text(json.dumps(references, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (args.evidence_dir / "english_accent_prompts.json").write_text(json.dumps(prompts, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    jobs: list[dict[str, object]] = [
        {"id": f"accent-{index:02d}", "text": prompt, "reference": None, "languages": ["English", "Auto", "Japanese"]}
        for index, prompt in enumerate(prompts, 1)
    ]
    jobs.extend([
        {"id": "supplied-good-en", "text": ENGLISH_TARGET, "reference": "english", "languages": ["English", "Auto", "Japanese"]},
        {"id": "supplied-japanese", "text": japanese_text, "reference": "japanese", "languages": ["Japanese"]},
    ])
    total = sum(len(job["languages"]) for job in jobs)
    results: list[dict[str, object]] = []
    completed = 0
    for job in jobs:
        for language in job["languages"]:
            completed += 1
            run_id = f"{job['id']}-{str(language).lower()}"
            output_path = raw_dir / f"{safe_name(run_id)}.wav"
            record: dict[str, object] = {
                "id": job["id"],
                "language": language,
                "reference": job["reference"],
                "text_sha256": hashlib.sha256(str(job["text"]).encode("utf-8")).hexdigest(),
                "audio_path": str(output_path.relative_to(args.evidence_dir)),
            }
            try:
                wav_bytes, headers = post_tts(args.base_url, api_key, str(job["text"]), str(language))
                output_path.write_bytes(wav_bytes)
                record.update({
                    "status": "success",
                    "response_bytes": len(wav_bytes),
                    "response_headers": {key: value for key, value in headers.items() if key.startswith("x-qwen-")},
                    "wav": inspect_wav(output_path, max_duration_seconds=45.0 if record["reference"] == "japanese" else 20.0),
                })
            except Exception as error:
                record.update({"status": "error", "error": f"{type(error).__name__}: {error}"})
            results.append(record)
            print(f"completed {completed}/{total} {run_id} status={record['status']}", flush=True)
    manifest = {
        "status": "STAGING_EVIDENCE",
        "endpoint": args.base_url,
        "generated_at_unix": time.time(),
        "remote_host": socket.gethostname(),
        "health": health,
        "supplied_references": references,
        "english_target": ENGLISH_TARGET,
        "japanese_target_sha256": japanese_text_sha256,
        "expected_japanese_target_sha256": JAPANESE_TARGET_SHA256,
        "accent_prompt_count": len(prompts),
        "jobs": jobs,
        "results": results,
        "human_review_required": True,
        "runtime_conditioning_reference_audio_used": False,
        "runtime_conditioning_reference_transcript_used": False,
    }
    (args.evidence_dir / "reference_matrix.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    archive_path = args.evidence_dir / "reference-matrix.zip"
    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(args.evidence_dir.rglob("*")):
            if path.is_file() and path != archive_path:
                archive.write(path, arcname=path.relative_to(args.evidence_dir).as_posix())
    successful = sum(1 for item in results if item["status"] == "success")
    print(json.dumps({"total": total, "successful": successful, "archive": str(archive_path), "archive_sha256": hashlib.sha256(archive_path.read_bytes()).hexdigest()}), flush=True)
    return 0 if successful == total else 1


if __name__ == "__main__":
    raise SystemExit(main())
