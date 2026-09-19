"""Generate auditable trained-custom comparisons for the supplied references.

This is staging evidence, not a production promotion tool. It sends text only
to the trained-custom endpoint and records the supplied reference-file hashes
without using those files as runtime conditioning inputs.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import time
import urllib.error
import urllib.request
from pathlib import Path


ENGLISH_TEXT = "This is the three sentence Azure and the Colab voice test. The custom domain is carrying the request to Colab. The Discord bridge is sending the result as a real voice message."
DEFAULT_JAPANESE_TEXT = Path(
    r"D:\New folder (2)\TsukiAI 1.0\voice\qwen3-tts\reference\tsuki_ref_text.txt"
)
DEFAULT_REFERENCES = (
    Path(r"C:\Users\LENOVO\Downloads\goodoutputtts.ogg"),
    Path(r"C:\Users\LENOVO\Downloads\tsuki_25s_clip.mp3"),
)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def reference_metadata(path: Path) -> dict[str, object]:
    if not path.is_file():
        raise FileNotFoundError(path)
    return {
        "path": str(path),
        "name": path.name,
        "bytes": path.stat().st_size,
        "sha256": sha256_file(path),
        "used_as_runtime_conditioning": False,
    }


def post_tts(base_url: str, api_key: str, text: str, language: str, timeout: float) -> tuple[bytes, dict[str, str]]:
    payload = json.dumps({"text": text, "language": language}, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        f"{base_url.rstrip('/')}/tts",
        data=payload,
        method="POST",
        headers={
            "Content-Type": "application/json",
            "X-Api-Key": api_key,
            "X-Correlation-ID": f"reference-matrix-{int(time.time())}",
        },
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read(), {str(key).lower(): str(value) for key, value in response.headers.items()}


def fetch_health(base_url: str, api_key: str, timeout: float) -> dict[str, object]:
    request = urllib.request.Request(
        f"{base_url.rstrip('/')}/health",
        method="GET",
        headers={"X-Api-Key": api_key},
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        payload = json.loads(response.read().decode("utf-8"))
    if not isinstance(payload, dict):
        raise ValueError("health response is not a JSON object")
    return payload


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--api-key-env", default="QWEN_TTS_API_KEY")
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--japanese-text-file", type=Path, default=DEFAULT_JAPANESE_TEXT)
    parser.add_argument("--english-reference", type=Path, default=DEFAULT_REFERENCES[0])
    parser.add_argument("--japanese-reference", type=Path, default=DEFAULT_REFERENCES[1])
    parser.add_argument("--timeout", type=float, default=300.0)
    args = parser.parse_args()

    api_key = os.getenv(args.api_key_env, "").strip()
    if not api_key:
        parser.error(f"API key environment variable {args.api_key_env!r} is not set")

    try:
        japanese_text = args.japanese_text_file.read_text(encoding="utf-8").strip()
    except OSError as exc:
        parser.error(f"cannot read Japanese transcript: {exc}")
    if not japanese_text:
        parser.error("Japanese transcript is empty")

    args.output_dir.mkdir(parents=True, exist_ok=True)
    supplied_references = {
        "english": reference_metadata(args.english_reference),
        "japanese": reference_metadata(args.japanese_reference),
    }
    (args.output_dir / "supplied_reference_manifest.json").write_text(
        json.dumps(supplied_references, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )

    # Import the repository's standard-library WAV metric implementation only
    # after argument validation so this script remains safe to inspect locally.
    import sys

    qwen_dir = Path(__file__).resolve().parents[1] / "voice" / "qwen3-tts"
    sys.path.insert(0, str(qwen_dir))
    from benchmark_evidence import inspect_wav, verify_trained_custom_health

    try:
        health = fetch_health(args.base_url, api_key, args.timeout)
        verify_trained_custom_health(health)
    except (OSError, ValueError, urllib.error.HTTPError, urllib.error.URLError) as exc:
        parser.error(f"endpoint is not a verified trained-custom runtime: {exc}")
    (args.output_dir / "health.json").write_text(
        json.dumps(health, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )

    jobs = [
        {"id": "supplied-good-en", "text": ENGLISH_TEXT, "reference": "english", "languages": ["English", "Auto", "Japanese"]},
        {"id": "supplied-japanese", "text": japanese_text, "reference": "japanese", "languages": ["Japanese"]},
    ]
    results: list[dict[str, object]] = []
    for job in jobs:
        for language in job["languages"]:
            run_id = f"{job['id']}-{str(language).lower()}"
            output_path = args.output_dir / f"{run_id}.wav"
            record: dict[str, object] = {
                "id": job["id"],
                "language": language,
                "reference_name": supplied_references[str(job["reference"])]["name"],
                "text_sha256": hashlib.sha256(str(job["text"]).encode("utf-8")).hexdigest(),
                "audio_path": str(output_path),
            }
            try:
                wav_bytes, headers = post_tts(args.base_url, api_key, str(job["text"]), str(language), args.timeout)
                output_path.write_bytes(wav_bytes)
                record.update({
                    "status": "success",
                    "response_bytes": len(wav_bytes),
                    "response_headers": {key: value for key, value in headers.items() if key.startswith("x-qwen-")},
                    "wav": inspect_wav(output_path, include_acoustic=True),
                })
            except (OSError, ValueError, urllib.error.HTTPError, urllib.error.URLError) as exc:
                record.update({"status": "error", "error": str(exc)})
            results.append(record)
            print(json.dumps(record, ensure_ascii=False, sort_keys=True), flush=True)

    manifest = {
        "status": "STAGING_EVIDENCE",
        "endpoint": args.base_url,
        "health": health,
        "supplied_references": supplied_references,
        "english_text": ENGLISH_TEXT,
        "japanese_text_file": str(args.japanese_text_file),
        "japanese_text_sha256": hashlib.sha256(japanese_text.encode("utf-8")).hexdigest(),
        "jobs": jobs,
        "results": results,
        "human_review_required": True,
        "runtime_conditioning_reference_audio_used": False,
    }
    (args.output_dir / "reference_matrix.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    return 0 if all(item["status"] == "success" for item in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
