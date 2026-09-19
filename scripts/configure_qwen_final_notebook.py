"""Create the final all-450 Kaggle training source from the repaired notebook.

The repair notebook remains the recipe-validation source (425/25). This
guarded transform creates a separate Kaggle kernel directory for the final
450-train/0-holdout run and records the exact dataset manifest and checkpoint
identity needed by the trained-custom runtime. It refuses to overwrite an
existing output directory.
"""

from __future__ import annotations

import ast
import copy
import hashlib
import json
import re
import shutil
from pathlib import Path


SELF_HASH_MARKER = "__NOTEBOOK_SOURCE_SHA256__"
EXPECTED_MANIFEST_SHA256 = "7d24bf5388beb109b1e2372513f7fe1caf24b05c3fa0cea6cb029db482d2b4e2"
FINAL_RUN_ID = "tsuki-qwen3-tts-final-all450-alignment-pr278-lr2e-6-epoch1-seed42"
FINAL_ARTIFACT_NAME = "tsukihime_qwen06b_final_all450_staging.zip"


def replace_once(source: str, old: str, new: str, label: str) -> str:
    count = source.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one match, found {count}")
    return source.replace(old, new, 1)


def canonical_notebook_hash(document: dict[str, object]) -> str:
    normalized = copy.deepcopy(document)
    for cell in normalized.get("cells", []):
        if not isinstance(cell, dict) or cell.get("cell_type") != "code":
            continue
        source = cell.get("source", "")
        if isinstance(source, list):
            source = "".join(source)
        if isinstance(source, str):
            cell["source"] = re.sub(
                r'NOTEBOOK_SOURCE_SHA256 = "[^"]+"',
                'NOTEBOOK_SOURCE_SHA256 = "' + SELF_HASH_MARKER + '"',
                source,
                count=1,
            )
    payload = json.dumps(normalized, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def code_cell(document: dict[str, object]) -> tuple[dict[str, object], str]:
    cells = document.get("cells")
    if not isinstance(cells, list):
        raise RuntimeError("notebook has no cell list")
    for cell in cells:
        if not isinstance(cell, dict) or cell.get("cell_type") != "code":
            continue
        value = cell.get("source", "")
        source = "".join(value) if isinstance(value, list) else value
        if not isinstance(source, str):
            raise RuntimeError("notebook code cell source is not text")
        return cell, source
    raise RuntimeError("notebook has no code cell")


FINAL_EVALUATE = r'''def evaluate(repo: Path, attention: str, dtype: object) -> None:
    """Evaluate only the trained custom checkpoint; never use runtime cloning."""

    import soundfile as sf
    import torch
    from qwen_tts import Qwen3TTSModel

    english_target_text = (
        "This is the three sentence Azure and the Colab voice test. "
        "The custom domain is carrying the request to Colab. "
        "The Discord bridge is sending the result as a real voice message."
    )
    japanese_target_text = (
        "こんにちは。月だよ。今夜も君と話せて嬉しいな。月の光が綺麗な夜は心が静かになるんだ。"
        "君はどう？君の声好きだよ。聞くたびに安心するんだ。今日はどんな一日だった？"
        "ゆっくり聞かせてね。夜風が気持ちいい季節だね。これからの時間楽しもう。"
        "君が笑うと私まで嬉しくなるんだ。いつでもここにいるからね。夜が終わっても。"
    )
    acceptance_cases = [
        {"case": "smoke_japanese", "text": "こんにちは。", "language": "Japanese"},
        {"case": "exact_japanese_target", "text": japanese_target_text, "language": "Japanese"},
        {"case": "exact_english_target_english", "text": english_target_text, "language": "English"},
        {"case": "exact_english_target_auto", "text": english_target_text, "language": "Auto"},
        {"case": "exact_english_target_japanese", "text": english_target_text, "language": "Japanese"},
    ]
    prompts = json.loads((DATA / "eval_prompts.json").read_text(encoding="utf-8"))["prompts"]
    cases = acceptance_cases + [
        {"case": f"corpus_prompt_{index:02d}", "text": prompt, "language": LANGUAGE}
        for index, prompt in enumerate(prompts, 1)
    ]
    raw_root = EVALUATION / "raw"
    raw_root.mkdir(parents=True, exist_ok=True)
    case_set_sha256 = hashlib.sha256(json.dumps(cases, ensure_ascii=False, separators=(",", ":")).encode("utf-8")).hexdigest()
    checkpoint_status: dict[str, object] = {}

    for checkpoint in sorted(CHECKPOINTS.glob("checkpoint-epoch-*")):
        name = checkpoint.name
        required = [checkpoint / relative_name for relative_name in ARTIFACT_REQUIRED_FILES]
        if not all(path.is_file() and path.stat().st_size > 0 for path in required):
            checkpoint_status[name] = {"status": "REJECTED_ARTIFACT", "auto_gate": "FAIL", "reasons": ["missing required checkpoint files"]}
            continue

        config_path = checkpoint / "config.json"
        try:
            checkpoint_config = json.loads(config_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            checkpoint_status[name] = {"status": "REJECTED_IDENTITY", "auto_gate": "FAIL", "reasons": [f"invalid config.json: {error}"]}
            continue
        speaker_ids = checkpoint_config.get("talker_config", {}).get("spk_id", {})
        if SPEAKER_NAME not in speaker_ids:
            checkpoint_status[name] = {"status": "REJECTED_IDENTITY", "auto_gate": "FAIL", "reasons": [f"speaker {SPEAKER_NAME!r} is absent"]}
            continue

        out_dir = raw_root / name
        out_dir.mkdir(parents=True, exist_ok=True)
        records: list[dict[str, object]] = []
        model = None
        load_error = None
        try:
            model = Qwen3TTSModel.from_pretrained(str(checkpoint), device_map="cuda:0", dtype=dtype, attn_implementation=attention)
            model_type = getattr(getattr(model, "model", None), "tts_model_type", None)
            if model_type != "custom_voice":
                raise RuntimeError(f"loaded model type={model_type!r}, expected custom_voice")
            supported_speakers = {str(value).casefold() for value in model.get_supported_speakers()}
            if SPEAKER_NAME.casefold() not in supported_speakers:
                raise RuntimeError(f"loaded model speakers={sorted(supported_speakers)} do not contain {SPEAKER_NAME}")
            for index, case in enumerate(cases, 1):
                case_name = str(case["case"])
                prompt = str(case["text"])
                language = str(case["language"])
                output_path = out_dir / f"{index:02d}_{case_name}.wav"
                try:
                    seed_runtime(SEED + 1000 + index)
                    wavs, sr = model.generate_custom_voice(
                        text=prompt,
                        language=language,
                        speaker=SPEAKER_NAME,
                        max_new_tokens=2048,
                    )
                    sf.write(output_path, wavs[0], sr)
                    duration_limit = 45.0 if case_name == "exact_japanese_target" else MAX_EVAL_SECONDS
                    records.append({"index": index, "case": case_name, "prompt": prompt, "language": language, **inspect_wav(output_path, include_acoustic=True, max_duration_seconds=duration_limit)})
                except Exception as error:
                    records.append({"index": index, "case": case_name, "prompt": prompt, "language": language, "status": "REJECTED_GENERATION", "error": repr(error), "duration_sec": 0.0})
        except Exception as error:
            load_error = repr(error)
        finally:
            del model
            gc.collect()
            torch.cuda.empty_cache()

        summary = _summarize_metrics(records)
        write_json(out_dir / "metrics.json", {"checkpoint": name, "speaker": SPEAKER_NAME, "language": LANGUAGE, "case_set_sha256": case_set_sha256, "acceptance_cases": acceptance_cases, "records": records, "summary": summary})
        reasons: list[str] = []
        if load_error is not None:
            reasons.append(f"checkpoint load or custom-speaker check failed: {load_error}")
        if len(records) != len(cases) or any(row.get("status") == "REJECTED_GENERATION" for row in records):
            reasons.append("generation/EOS failure")
        if any(row.get("status") != "PASS" for row in records):
            reasons.append("raw WAV format or duration gate failed")
        periodicity = summary.get("median_periodicity")
        zcr = summary.get("median_zcr")
        centroid = summary.get("median_spectral_centroid_hz")
        if not isinstance(periodicity, (int, float)) or periodicity < 0.60:
            reasons.append("median periodicity below 0.60")
        if isinstance(periodicity, (int, float)) and periodicity < 0.60 and isinstance(zcr, (int, float)) and zcr >= 0.088:
            reasons.append("known-bad ZCR signature is present")
        if isinstance(periodicity, (int, float)) and periodicity < 0.60 and isinstance(centroid, (int, float)) and centroid >= 1900.0:
            reasons.append("known-bad spectral-centroid signature is present")
        auto_pass = not reasons
        checkpoint_status[name] = {
            "status": "APPROVED_FOR_STAGING" if auto_pass else ("REJECTED_GENERATION" if load_error or any(row.get("status") == "REJECTED_GENERATION" for row in records) else "REJECTED_METRICS"),
            "auto_gate": "PASS" if auto_pass else "FAIL",
            "human_review_status": "PENDING" if auto_pass else "NOT_APPLICABLE",
            "production_eligible": False,
            "speaker": SPEAKER_NAME,
            "language": LANGUAGE,
            "reasons": reasons,
            "summary": summary,
            "raw_dir": str(out_dir),
        }

    write_json(REPORTS / "checkpoint_status.json", checkpoint_status)
    staging_candidates = [name for name, value in checkpoint_status.items() if isinstance(value, dict) and value.get("status") == "APPROVED_FOR_STAGING"]
    selection = {
        "status": "PASS" if len(staging_candidates) == 1 else "FAIL",
        "selected_checkpoint": staging_candidates[0] if len(staging_candidates) == 1 else None,
        "candidates": staging_candidates,
        "selection_rule": "exactly one trained custom checkpoint must pass raw-WAV/EOS/acoustic gates; no Base, clone, loss-based, or epoch fallback",
        "human_review_required": True,
    }
    write_json(REPORTS / "selected_checkpoint.json", selection)
    (REPORTS / "final_report.md").write_text(
        "# Qwen3-TTS 0.6B final all-450 trained-custom evaluation\n\n"
        f"Alignment repair: `{ALIGNMENT_PATCH_COMMIT}`; learning rate: `{REPAIR_LEARNING_RATE_TEXT}`; epochs: `{REPAIR_EPOCHS}`; train samples: `{TRAIN_COUNT}`; holdout: `{HOLDOUT_COUNT}`.\n\n"
        "Evaluation calls `generate_custom_voice` with speaker `tsukihime` only, including the exact English target, exact Japanese target, and `こんにちは。` smoke case. Raw WAV/EOS/acoustic gates are recorded in `checkpoint_status.json`. A checkpoint marked `APPROVED_FOR_STAGING` remains pending mandatory human review and is never production-approved by this notebook.\n",
        encoding="utf-8",
    )
'''


def transform(document: dict[str, object]) -> str:
    cell, source = code_cell(document)
    required = (
        "generate_custom_voice",
        "QWEN_COMMIT = \"09364dcc3f947130861186f222a6c58e319ae67a\"",
        'ARTIFACT_NAME = "tsukihime_qwen06b_repaired_staging.zip"',
        "TRAIN_COUNT = 425",
        "HOLDOUT_COUNT = 25",
        '"checkpoint_tree_sha256": tree_sha256(stage_checkpoint)',
    )
    for fragment in required:
        if fragment not in source:
            raise RuntimeError(f"repaired notebook fragment is missing: {fragment}")

    source = replace_once(source, 'REPAIR_RUN_ID = "tsuki-qwen3-tts-alignment-pr278-lr2e-6-epoch1-seed42"', f'REPAIR_RUN_ID = "{FINAL_RUN_ID}"', "final run ID")
    source = replace_once(source, 'TRAIN_COUNT = 425', "TRAIN_COUNT = 450", "final train count")
    source = replace_once(source, "HOLDOUT_COUNT = 25", "HOLDOUT_COUNT = 0", "final holdout count")
    source = replace_once(source, 'ARTIFACT_NAME = "tsukihime_qwen06b_repaired_staging.zip"', f'ARTIFACT_NAME = "{FINAL_ARTIFACT_NAME}"', "final artifact name")
    source = replace_once(
        source,
        'EXPECTED_CORPUS_SHA256 = "72360c27040fb2051c6e425c774f4b61073e2841a00963e0e45dd5444d2ec959"',
        'EXPECTED_CORPUS_SHA256 = "72360c27040fb2051c6e425c774f4b61073e2841a00963e0e45dd5444d2ec959"\nEXPECTED_MANIFEST_SHA256 = "' + EXPECTED_MANIFEST_SHA256 + '"',
        "dataset manifest identity",
    )
    source = replace_once(
        source,
        '    manifest_path = dataset_root / "manifest.jsonl"\n    manifest = [json.loads(line) for line in manifest_path.read_text(encoding="utf-8").splitlines() if line.strip()]',
        '    manifest_path = dataset_root / "manifest.jsonl"\n    manifest_sha256 = sha256_file(manifest_path)\n    if manifest_sha256 != EXPECTED_MANIFEST_SHA256:\n        raise RuntimeError(f"dataset manifest SHA-256={manifest_sha256}, expected={EXPECTED_MANIFEST_SHA256}")\n    manifest = [json.loads(line) for line in manifest_path.read_text(encoding="utf-8").splitlines() if line.strip()]',
        "dataset manifest validation",
    )
    source = replace_once(
        source,
        '        "text_sha256": hashlib.sha256(text_bytes).hexdigest(),\n        "expected_text_sha256": EXPECTED_CORPUS_SHA256,',
        '        "text_sha256": hashlib.sha256(text_bytes).hexdigest(),\n        "expected_text_sha256": EXPECTED_CORPUS_SHA256,\n        "manifest_sha256": manifest_sha256,\n        "expected_manifest_sha256": EXPECTED_MANIFEST_SHA256,',
        "dataset audit manifest record",
    )
    source = replace_once(
        source,
        '        "language": LANGUAGE,\n        "checkpoint": selected.name,',
        '        "language": LANGUAGE,\n        "checkpoint": selected.name,\n        "checkpoint_id": f"{REPAIR_RUN_ID}:{selected.name}:{tree_sha256(stage_checkpoint)}",',
        "artifact checkpoint identity",
    )
    source = replace_once(
        source,
        'def inspect_wav(path: Path, *, include_acoustic: bool = False) -> dict[str, object]:',
        'def inspect_wav(path: Path, *, include_acoustic: bool = False, max_duration_seconds: float | None = None) -> dict[str, object]:',
        "target-aware WAV duration gate signature",
    )
    source = replace_once(
        source,
        '            if duration < 0.5 or duration > MAX_EVAL_SECONDS:\n                flags.append("DURATION_OUTLIER")',
        '            duration_limit = MAX_EVAL_SECONDS if max_duration_seconds is None else max_duration_seconds\n            if duration < 0.5 or duration > duration_limit:\n                flags.append("DURATION_OUTLIER")',
        "target-aware WAV duration gate",
    )
    evaluate_pattern = r"def evaluate\(.*?(?=def package_artifact\()"
    source, count = re.subn(evaluate_pattern, lambda _: FINAL_EVALUATE.rstrip() + "\n\n\n", source, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError("final trained-custom evaluation function replacement failed")

    if "TRAIN_COUNT = 425" in source or "HOLDOUT_COUNT = 25" in source:
        raise RuntimeError("final notebook still contains validation-stage sample counts")
    if "generate_voice_clone" in source:
        raise RuntimeError("final notebook contains forbidden voice-clone evaluation")
    ast.parse(source)
    cell["source"] = source

    source = re.sub(
        r'NOTEBOOK_SOURCE_SHA256 = "[^"]+"',
        'NOTEBOOK_SOURCE_SHA256 = "' + SELF_HASH_MARKER + '"',
        source,
        count=1,
    )
    cell["source"] = source
    digest = canonical_notebook_hash(document)
    source = source.replace(
        'NOTEBOOK_SOURCE_SHA256 = "' + SELF_HASH_MARKER + '"',
        'NOTEBOOK_SOURCE_SHA256 = "' + digest + '"',
        1,
    )
    cell["source"] = source
    ast.parse(source)
    return digest


def main() -> None:
    import argparse

    parser = argparse.ArgumentParser()
    parser.add_argument("--source-dir", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()

    source_dir = args.source_dir.expanduser().resolve()
    output_dir = args.output_dir.expanduser().resolve()
    if not source_dir.is_dir():
        raise SystemExit(f"source directory does not exist: {source_dir}")
    if output_dir == source_dir or source_dir in output_dir.parents:
        raise SystemExit("output directory must be outside the source directory")
    if output_dir.exists():
        raise SystemExit(f"refusing to overwrite existing output directory: {output_dir}")

    shutil.copytree(source_dir, output_dir)
    notebooks = sorted(output_dir.glob("*.ipynb"))
    if len(notebooks) != 1:
        raise SystemExit(f"expected one notebook in copied source, found {len(notebooks)}")
    notebook = notebooks[0]
    document = json.loads(notebook.read_text(encoding="utf-8"))
    digest = transform(document)
    notebook.write_text(json.dumps(document, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
    print(json.dumps({"notebook": str(notebook), "notebook_source_sha256": digest, "train_count": 450, "holdout_count": 0, "artifact_name": FINAL_ARTIFACT_NAME}, sort_keys=True))


if __name__ == "__main__":
    main()
