"""Apply the verified Qwen3-TTS recovery changes to the Kaggle notebook.

The notebook is a compact JSON document, so this script performs a guarded,
structured rewrite. Every replacement is checked for an exact match and the
result is parsed with ``ast`` before it is written.
"""

from __future__ import annotations

import ast
import copy
import hashlib
import json
import re
from pathlib import Path


NOTEBOOK = Path(r"C:\Users\LENOVO\Downloads\kaggle-kernel-source-tsuki-20260916\tsuki-qwen3-tts-0-6b-sft.ipynb")
OLD_COMMIT = "022e286b98fbec7e1e916cb940cdf532cd9f488e"
REPAIR_COMMIT = "09364dcc3f947130861186f222a6c58e319ae67a"
SELF_HASH_MARKER = "__NOTEBOOK_SOURCE_SHA256__"


def replace_once(source: str, old: str, new: str, label: str) -> str:
    count = source.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one match, found {count}")
    return source.replace(old, new, 1)


def replace_function(source: str, name: str, next_name: str, body: str) -> str:
    if next_name == "if __name__":
        pattern = rf"def {re.escape(name)}\(.*?(?=if __name__)"
    else:
        pattern = rf"def {re.escape(name)}\(.*?(?=def {re.escape(next_name)}\()"
    updated, count = re.subn(pattern, lambda _: body.rstrip() + "\n\n\n", source, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"function replacement failed for {name!r}")
    return updated


NOTEBOOK_REPLACE_ONCE = r'''def replace_once(source: str, old: str, new: str, label: str) -> str:
    count = source.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one match, found {count}")
    return source.replace(old, new, 1)
'''


def ensure_notebook_replace_once(source: str) -> str:
    if "def replace_once(" in source:
        return source
    marker = "def _acoustic_metrics" if "def _acoustic_metrics" in source else "def inspect_wav"
    return replace_once(source, marker, NOTEBOOK_REPLACE_ONCE.rstrip() + "\n\n\n" + marker, "notebook replacement helper")


def canonical_notebook_hash(document: dict[str, object]) -> str:
    normalized = copy.deepcopy(document)
    for cell in normalized.get("cells", []):
        if not isinstance(cell, dict) or cell.get("cell_type") != "code":
            continue
        source = cell.get("source", "")
        if isinstance(source, list):
            source = "".join(source)
        if isinstance(source, str):
            source = re.sub(
                r'NOTEBOOK_SOURCE_SHA256 = "[^"]+"',
                f'NOTEBOOK_SOURCE_SHA256 = "{SELF_HASH_MARKER}"',
                source,
                count=1,
            )
            cell["source"] = source
    payload = json.dumps(normalized, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def load_code_cell(document: dict[str, object]) -> tuple[dict[str, object], str]:
    cells = document.get("cells")
    if not isinstance(cells, list):
        raise RuntimeError("notebook has no cell list")
    for cell in cells:
        if not isinstance(cell, dict) or cell.get("cell_type") != "code":
            continue
        value = cell.get("source", "")
        source = "".join(value) if isinstance(value, list) else value
        if not isinstance(source, str):
            raise RuntimeError("code cell source is not text")
        return cell, source
    raise RuntimeError("notebook has no code cell")


NEW_INSPECT_WAV = r'''def _acoustic_metrics(samples: array, rate: int) -> dict[str, float]:
    """Return frame-based acoustic metrics used by the release gate."""

    import numpy as np

    values = np.asarray(samples, dtype=np.float32) / 32768.0
    if values.size < 2 or rate <= 0:
        return {
            "periodicity": 0.0,
            "zcr": 0.0,
            "spectral_centroid_hz": 0.0,
            "high_frequency_energy_ratio": 0.0,
            "voiced_frame_ratio": 0.0,
        }

    frame_length = min(max(256, int(rate * 0.04)), values.size)
    hop = max(128, frame_length // 2)
    window = np.hanning(frame_length)
    periodicity: list[float] = []
    zcr: list[float] = []
    centroids: list[float] = []
    high_frequency: list[float] = []
    voiced_frames = 0
    total_frames = 0
    for start in range(0, values.size - frame_length + 1, hop):
        frame = values[start : start + frame_length]
        total_frames += 1
        energy = float(np.sqrt(np.mean(frame * frame)))
        if energy < 0.005:
            continue
        voiced_frames += 1
        zcr.append(float(np.mean(frame[1:] * frame[:-1] < 0)))

        centered = frame - float(np.mean(frame))
        autocorrelation = np.correlate(centered, centered, mode="full")[frame_length - 1 :]
        if autocorrelation.size and autocorrelation[0] > 1e-9:
            min_lag = max(1, int(rate / 500))
            max_lag = min(autocorrelation.size - 1, int(rate / 50))
            if max_lag >= min_lag:
                periodicity.append(float(np.max(autocorrelation[min_lag : max_lag + 1]) / autocorrelation[0]))

        spectrum = np.fft.rfft(frame * window)
        power = np.abs(spectrum) ** 2
        total_power = float(power.sum())
        if total_power > 1e-12:
            frequencies = np.fft.rfftfreq(frame_length, 1.0 / rate)
            centroids.append(float(np.sum(frequencies * power) / total_power))
            high_frequency.append(float(power[frequencies >= 4000.0].sum() / total_power))

    def median_or_zero(values: list[float]) -> float:
        return float(statistics.median(values)) if values else 0.0

    return {
        "periodicity": round(median_or_zero(periodicity), 6),
        "zcr": round(median_or_zero(zcr), 6),
        "spectral_centroid_hz": round(median_or_zero(centroids), 3),
        "high_frequency_energy_ratio": round(median_or_zero(high_frequency), 6),
        "voiced_frame_ratio": round(voiced_frames / total_frames, 6) if total_frames else 0.0,
    }


def inspect_wav(path: Path, *, include_acoustic: bool = False) -> dict[str, object]:
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
        samples.frombytes(raw)
        if channels != 1:
            flags.append("CHANNELS")
        if rate != 24000:
            flags.append("SAMPLE_RATE")
        if width != 2:
            flags.append("BIT_DEPTH")
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
'''


NEW_INSTALL = r'''def install_and_prepare(train_raw: str) -> tuple[Path, str, str, object]:
    run(
        [sys.executable, "-m", "pip", "install", "-q", "qwen-tts==0.1.1", "accelerate==1.12.0", "transformers==4.57.3"],
        log=RUN_INFO / "pip_install.log",
    )
    repo = Path("/kaggle/working/Qwen3-TTS-repair")
    if repo.exists():
        shutil.rmtree(repo)
    run(["git", "clone", "--filter=blob:none", QWEN_REPO, str(repo)], log=RUN_INFO / "git_clone.log")
    try:
        run(
            [
                "git",
                "-C",
                str(repo),
                "fetch",
                "--depth",
                "1",
                "origin",
                f"{QWEN_SOURCE_REF}:refs/remotes/origin/recovery-pr278",
            ],
            log=RUN_INFO / "git_fetch.log",
        )
    except subprocess.CalledProcessError:
        run(["git", "-C", str(repo), "remote", "add", "repair", QWEN_REPAIR_REPO], log=RUN_INFO / "git_remote.log")
        run(["git", "-C", str(repo), "fetch", "--depth", "1", "repair", QWEN_COMMIT], log=RUN_INFO / "git_fetch_repair.log")
    run(["git", "-C", str(repo), "checkout", "--detach", QWEN_COMMIT], log=RUN_INFO / "git_checkout.log")
    repo_commit = subprocess.check_output(["git", "-C", str(repo), "rev-parse", "HEAD"], text=True).strip()
    if repo_commit != QWEN_COMMIT:
        raise RuntimeError(f"Qwen repair source mismatch: {repo_commit} != {QWEN_COMMIT}")

    repo_env = os.environ.copy()
    repo_env["PYTHONPATH"] = str(repo) + os.pathsep + repo_env.get("PYTHONPATH", "")
    if str(repo) not in sys.path:
        sys.path.insert(0, str(repo))

    import torch

    cuda_available = bool(torch.cuda.is_available())
    gpu_count = torch.cuda.device_count() if cuda_available else 0
    bf16 = bool(cuda_available and torch.cuda.is_bf16_supported())
    flash_available = importlib.util.find_spec("flash_attn") is not None
    gpu = {}
    if gpu_count:
        props = torch.cuda.get_device_properties(0)
        gpu = {"name": props.name, "vram_gib": round(props.total_memory / 1024**3, 2), "compute_capability": f"{props.major}.{props.minor}"}
    gpu_info = {
        "cuda_available": cuda_available,
        "gpu_count": gpu_count,
        "cuda_version": torch.version.cuda,
        "torch_version": torch.__version__,
        "bf16_supported": bf16,
        "flash_attention_installed": flash_available,
        "gpu": gpu,
    }
    write_json(RUN_INFO / "gpu_info.json", gpu_info)
    if not cuda_available or gpu_count < 1:
        raise RuntimeError("Kaggle GPU preflight failed: CUDA GPU unavailable")

    attention = "flash_attention_2" if flash_available else "sdpa"
    dtype = torch.bfloat16 if bf16 else torch.float16
    mixed_precision = "bf16" if bf16 else "fp16"
    versions = {
        "qwen_tts": importlib.metadata.version("qwen-tts"),
        "torch": torch.__version__,
        "transformers": importlib.metadata.version("transformers"),
        "accelerate": importlib.metadata.version("accelerate"),
        "python": sys.version,
        "cuda": torch.version.cuda,
        "qwen_repo_commit": repo_commit,
        "qwen_repo_source_ref": QWEN_SOURCE_REF,
        "qwen_repair_commit": ALIGNMENT_PATCH_COMMIT,
    }
    write_json(RUN_INFO / "environment.json", versions)
    write_json(
        RUN_INFO / "config.json",
        {
            "repair_run_id": REPAIR_RUN_ID,
            "model_id": MODEL_ID,
            "tokenizer_id": TOKENIZER_ID,
            "repo_commit": repo_commit,
            "repo_source_ref": QWEN_SOURCE_REF,
            "alignment_patch_commit": ALIGNMENT_PATCH_COMMIT,
            "attention": attention,
            "dtype": str(dtype),
            "mixed_precision": mixed_precision,
            "speaker_name": SPEAKER_NAME,
            "epochs": REPAIR_EPOCHS,
            "batch_size": 2,
            "gradient_accumulation": 4,
            "learning_rate": REPAIR_LEARNING_RATE,
            "weight_decay": 0.01,
            "gradient_clip": 1.0,
            "seed": SEED,
            "dataset_sha256": EXPECTED_CORPUS_SHA256,
            "checkpoint_selection": "evaluation-gated; no hard-coded epoch",
        },
    )

    from qwen_tts import Qwen3TTSModel
    from qwen_tts.core.models import Qwen3TTSConfig

    config = Qwen3TTSConfig.from_pretrained(MODEL_ID)
    config_dict = config.to_dict()
    speaker_dim = int(config_dict["speaker_encoder_config"]["enc_dim"])
    talker_dim = int(config_dict["talker_config"]["hidden_size"])
    text_dim = int(config_dict["talker_config"]["text_hidden_size"])
    if speaker_dim != talker_dim:
        write_json(RUN_INFO / "model_shape_preflight.json", {"status": "FAIL", "speaker_encoder_dim": speaker_dim, "talker_hidden_dim": talker_dim, "text_embedding_dim": text_dim})
        raise RuntimeError(f"0.6B shape preflight failed: speaker={speaker_dim}, talker={talker_dim}")
    model = None
    model_error = None
    for candidate_attention in dict.fromkeys((attention, "sdpa")):
        try:
            model = Qwen3TTSModel.from_pretrained(MODEL_ID, device_map="cuda:0", dtype=dtype, attn_implementation=candidate_attention)
            attention = candidate_attention
            break
        except Exception as error:
            model_error = repr(error)
            gc.collect()
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
    if model is None:
        write_json(RUN_INFO / "model_shape_preflight.json", {"status": "FAIL", "speaker_encoder_dim": speaker_dim, "talker_hidden_dim": talker_dim, "text_embedding_dim": text_dim, "error": model_error})
        raise RuntimeError(f"0.6B model preflight load failed: {model_error}")
    codec_dim = int(model.model.talker.model.codec_embedding.weight.shape[-1])
    actual = {"status": "PASS" if codec_dim == speaker_dim else "FAIL", "speaker_encoder_config_dim": speaker_dim, "talker_config_hidden_dim": talker_dim, "text_embedding_dim": text_dim, "text_projection_required": text_dim != talker_dim, "codec_embedding_dim": codec_dim, "attention": attention, "dtype": str(dtype)}
    write_json(RUN_INFO / "model_shape_preflight.json", actual)
    if codec_dim != speaker_dim:
        raise RuntimeError(f"0.6B model shape mismatch: speaker={speaker_dim}, codec={codec_dim}")
    del model
    gc.collect()
    torch.cuda.empty_cache()

    finetune = repo / "finetuning"
    modeling_source = (repo / "qwen_tts" / "core" / "models" / "modeling_qwen3_tts.py").read_text(encoding="utf-8")
    if "labels=None, shift_labels=labels.contiguous()" not in modeling_source:
        raise RuntimeError("Qwen source does not contain the verified sub-talker shift_labels repair")
    prepare = finetune / "prepare_data.py"
    run(
        [sys.executable, str(prepare), "--device", "cuda:0", "--tokenizer_model_path", TOKENIZER_ID, "--input_jsonl", train_raw, "--output_jsonl", str(DATA / "train_with_codes.jsonl")],
        cwd=finetune,
        log=RUN_INFO / "prepare_data.log",
        env=repo_env,
    )
    coded = [json.loads(line) for line in (DATA / "train_with_codes.jsonl").read_text(encoding="utf-8").splitlines() if line.strip()]
    if len(coded) != TRAIN_COUNT or any("audio_codes" not in row for row in coded):
        raise RuntimeError("audio-code preparation failed or produced incomplete rows")
    write_json(RUN_INFO / "audio_code_preflight.json", {"status": "PASS", "input_rows": TRAIN_COUNT, "output_rows": len(coded), "failed_rows": 0, "audio_code_rows": sum("audio_codes" in row for row in coded)})
    return repo, attention, mixed_precision, dtype
'''


NEW_TRAIN = r'''def train(repo: Path, attention: str, mixed_precision: str, dtype: object) -> None:
    from huggingface_hub import snapshot_download

    source_path = repo / "finetuning" / "sft_12hz.py"
    modeling_path = repo / "qwen_tts" / "core" / "models" / "modeling_qwen3_tts.py"
    source = source_path.read_text(encoding="utf-8")
    modeling_source = modeling_path.read_text(encoding="utf-8")
    required_alignment = (
        "inputs_embeds=input_embeddings,",
        "attention_mask=attention_mask,",
        "labels=codec_0_labels,",
        "talker_hidden_states = hidden_states[codec_mask[:, 1:]]",
    )
    if any(marker not in source for marker in required_alignment):
        raise RuntimeError("Qwen SFT source is missing the verified talker alignment contract")
    if "labels=None, shift_labels=labels.contiguous()" not in modeling_source:
        raise RuntimeError("Qwen model source is missing the verified sub-talker shift_labels contract")

    source = source.replace("self.model.talker.", "model.talker.")
    source = source.replace("import os\n", "import os\nimport random\nimport numpy as np\n", 1)
    injection = (
        f"target_speaker_embedding = None\n"
        f"ATTN_IMPLEMENTATION = {attention!r}\n"
        f"MIXED_PRECISION = {mixed_precision!r}\n"
        f"MODEL_DTYPE = {dtype!r}\n\n\n"
        "def seed_everything(seed: int) -> None:\n"
        "    random.seed(seed)\n"
        "    np.random.seed(seed)\n"
        "    torch.manual_seed(seed)\n"
        "    if torch.cuda.is_available():\n"
        "        torch.cuda.manual_seed_all(seed)"
    )
    source = replace_once(source, "target_speaker_embedding = None", injection, "training configuration injection")
    source = source.replace('mixed_precision="bf16"', "mixed_precision=MIXED_PRECISION")
    source = source.replace("torch_dtype=torch.bfloat16", "torch_dtype=MODEL_DTYPE")
    source = source.replace('attn_implementation="flash_attention_2"', "attn_implementation=ATTN_IMPLEMENTATION")
    source = source.replace(
        'mixed_precision=MIXED_PRECISION, log_with="tensorboard")',
        'mixed_precision=MIXED_PRECISION, log_with="tensorboard", project_dir=args.output_model_path)',
    )
    source = source.replace('parser.add_argument("--lr", type=float, default=2e-5)', 'parser.add_argument("--lr", type=float, default=2e-6)')
    source = source.replace('parser.add_argument("--num_epochs", type=int, default=3)', 'parser.add_argument("--num_epochs", type=int, default=1)')
    source = replace_once(
        source,
        'parser.add_argument("--speaker_name", type=str, default="speaker_test")\n',
        'parser.add_argument("--speaker_name", type=str, default="speaker_test")\n    parser.add_argument("--seed", type=int, default=42)\n',
        "seed argument",
    )
    source = replace_once(
        source,
        "args = parser.parse_args()\n",
        "args = parser.parse_args()\n    seed_everything(args.seed)\n",
        "seed initialization",
    )
    old_dataloader = "train_dataloader = DataLoader(dataset, batch_size=args.batch_size, shuffle=True, collate_fn=dataset.collate_fn)"
    new_dataloader = "generator = torch.Generator()\n    generator.manual_seed(args.seed)\n    train_dataloader = DataLoader(dataset, batch_size=args.batch_size, shuffle=True, collate_fn=dataset.collate_fn, generator=generator)"
    source = replace_once(source, old_dataloader, new_dataloader, "dataloader seed")
    if "inputs_embeds=input_embeddings[:, :-1, :]" in source or "labels=codec_0_labels[:, 1:]" in source:
        raise RuntimeError("old talker double-shift remains in the generated SFT script")
    if "hidden_states = outputs.hidden_states[0][-1][:, :-1,:]" not in source:
        raise RuntimeError("repaired hidden-state slice is missing from the generated SFT script")

    patched = repo / "finetuning" / "qwen_finetuning_sft.py"
    patched.write_text(source, encoding="utf-8")
    repo_env = os.environ.copy()
    repo_env["PYTHONPATH"] = str(repo) + os.pathsep + repo_env.get("PYTHONPATH", "")
    local_model_path = snapshot_download(MODEL_ID, cache_dir=str(ROOT / "hf_cache" / "hub"), local_files_only=False)
    started = time.time()
    run(
        [
            sys.executable,
            patched.name,
            "--init_model_path",
            local_model_path,
            "--output_model_path",
            str(CHECKPOINTS),
            "--train_jsonl",
            str(DATA / "train_with_codes.jsonl"),
            "--batch_size",
            "2",
            "--lr",
            REPAIR_LEARNING_RATE_TEXT,
            "--num_epochs",
            str(REPAIR_EPOCHS),
            "--speaker_name",
            SPEAKER_NAME,
            "--seed",
            str(SEED),
        ],
        cwd=repo / "finetuning",
        log=REPORTS / "training_log.txt",
        env=repo_env,
    )
    write_json(
        RUN_INFO / "training_status.json",
        {
            "status": "COMPLETED",
            "model_id": MODEL_ID,
            "epochs": REPAIR_EPOCHS,
            "learning_rate": REPAIR_LEARNING_RATE,
            "seed": SEED,
            "alignment_patch_commit": ALIGNMENT_PATCH_COMMIT,
            "sft_source_sha256": sha256_file(patched),
            "modeling_source_sha256": sha256_file(modeling_path),
            "seconds": round(time.time() - started, 3),
        },
    )
'''


NEW_EVALUATE = r'''def _summarize_metrics(records: list[dict[str, object]]) -> dict[str, object]:
    def median_value(name: str) -> float | None:
        values = [float(row[name]) for row in records if isinstance(row.get(name), (int, float))]
        return round(statistics.median(values), 6) if values else None

    return {
        "count": len(records),
        "valid_count": sum(row.get("status") == "PASS" for row in records),
        "median_duration_sec": median_value("duration_sec"),
        "median_periodicity": median_value("periodicity"),
        "median_zcr": median_value("zcr"),
        "median_spectral_centroid_hz": median_value("spectral_centroid_hz"),
        "median_high_frequency_energy_ratio": median_value("high_frequency_energy_ratio"),
        "median_clipping_ratio": median_value("clipping_ratio"),
    }


def evaluate(repo: Path, attention: str, dtype: object) -> None:
    import soundfile as sf
    import torch
    from qwen_tts import Qwen3TTSModel

    prompts = json.loads((DATA / "eval_prompts.json").read_text(encoding="utf-8"))["prompts"]
    ref_row = json.loads((DATA / "train_manifest.jsonl").read_text(encoding="utf-8").splitlines()[0])
    ref_text = ref_row["text"]
    raw_root = EVALUATION / "raw"
    raw_root.mkdir(parents=True, exist_ok=True)
    prompt_set_sha256 = hashlib.sha256(json.dumps(prompts, ensure_ascii=False, separators=(",", ":")).encode("utf-8")).hexdigest()

    base_dir = raw_root / "base_06b"
    base_dir.mkdir(parents=True, exist_ok=True)
    base_records: list[dict[str, object]] = []
    seed_runtime(SEED)
    base_model = Qwen3TTSModel.from_pretrained(MODEL_ID, device_map="cuda:0", dtype=dtype, attn_implementation=attention)
    for index, prompt in enumerate(prompts, 1):
        output_path = base_dir / f"prompt_{index:02d}.wav"
        try:
            seed_runtime(SEED + index)
            wavs, sr = base_model.generate_voice_clone(text=prompt, language=LANGUAGE, ref_audio=str(DATA / "ref.wav"), ref_text=ref_text, max_new_tokens=2048)
            sf.write(output_path, wavs[0], sr)
            base_records.append({"index": index, "prompt": prompt, **inspect_wav(output_path, include_acoustic=True)})
        except Exception as error:
            base_records.append({"index": index, "prompt": prompt, "status": "REJECTED_GENERATION", "error": repr(error), "duration_sec": 0.0})
    base_summary = _summarize_metrics(base_records)
    write_json(EVALUATION / "base_metrics.json", {"model": MODEL_ID, "prompt_set_sha256": prompt_set_sha256, "records": base_records, "summary": base_summary})
    del base_model
    gc.collect()
    torch.cuda.empty_cache()

    checkpoint_status: dict[str, object] = {}
    for checkpoint in sorted(CHECKPOINTS.glob("checkpoint-epoch-*")):
        # Keep the evaluated checkpoint's actual directory name in every report.
        # Trainer emits epoch-0 for the first one-epoch run; stripping the
        # directory prefix made packaging look for CHECKPOINTS/epoch-0 instead
        # of the evaluated CHECKPOINTS/checkpoint-epoch-0.
        name = checkpoint.name
        required = [checkpoint / "config.json", checkpoint / "model.safetensors"]
        if not all(path.is_file() and path.stat().st_size > 0 for path in required):
            checkpoint_status[name] = {"status": "REJECTED_ARTIFACT", "auto_gate": "FAIL", "reasons": ["missing required checkpoint files"]}
            continue
        out_dir = raw_root / name
        out_dir.mkdir(parents=True, exist_ok=True)
        records: list[dict[str, object]] = []
        model = None
        load_error = None
        try:
            model = Qwen3TTSModel.from_pretrained(str(checkpoint), device_map="cuda:0", dtype=dtype, attn_implementation=attention)
            for index, prompt in enumerate(prompts, 1):
                output_path = out_dir / f"prompt_{index:02d}.wav"
                try:
                    seed_runtime(SEED + 1000 + index)
                    wavs, sr = model.generate_custom_voice(text=prompt, language=LANGUAGE, speaker=SPEAKER_NAME, max_new_tokens=2048)
                    sf.write(output_path, wavs[0], sr)
                    records.append({"index": index, "prompt": prompt, **inspect_wav(output_path, include_acoustic=True)})
                except Exception as error:
                    records.append({"index": index, "prompt": prompt, "status": "REJECTED_GENERATION", "error": repr(error), "duration_sec": 0.0})
        except Exception as error:
            load_error = repr(error)
        finally:
            del model
            gc.collect()
            torch.cuda.empty_cache()

        summary = _summarize_metrics(records)
        write_json(out_dir / "metrics.json", {"checkpoint": name, "prompt_set_sha256": prompt_set_sha256, "records": records, "summary": summary})
        reasons: list[str] = []
        if load_error is not None:
            reasons.append(f"checkpoint load failed: {load_error}")
        if len(records) != len(prompts) or any(row.get("status") == "REJECTED_GENERATION" for row in records):
            reasons.append("generation/EOS failure")
        if any(row.get("status") != "PASS" for row in records):
            reasons.append("raw WAV format or duration gate failed")
        periodicity = summary.get("median_periodicity")
        zcr = summary.get("median_zcr")
        centroid = summary.get("median_spectral_centroid_hz")
        base_periodicity = base_summary.get("median_periodicity")
        base_zcr = base_summary.get("median_zcr")
        base_centroid = base_summary.get("median_spectral_centroid_hz")
        if not isinstance(periodicity, (int, float)) or periodicity < 0.60:
            reasons.append("median periodicity below 0.60")
        if isinstance(base_periodicity, (int, float)) and isinstance(periodicity, (int, float)) and periodicity < 0.80 * base_periodicity:
            reasons.append("periodicity below 80% of Base control")
        if isinstance(base_zcr, (int, float)) and base_zcr > 0 and isinstance(zcr, (int, float)) and zcr > 1.35 * base_zcr:
            reasons.append("ZCR exceeds 1.35x Base control")
        if isinstance(base_centroid, (int, float)) and base_centroid > 0 and isinstance(centroid, (int, float)) and centroid > 1.50 * base_centroid:
            reasons.append("spectral centroid exceeds 1.5x Base control")
        auto_pass = not reasons
        checkpoint_status[name] = {
            "status": "APPROVED_FOR_STAGING" if auto_pass else ("REJECTED_GENERATION" if load_error or any(row.get("status") == "REJECTED_GENERATION" for row in records) else "REJECTED_METRICS"),
            "auto_gate": "PASS" if auto_pass else "FAIL",
            "human_review_status": "PENDING" if auto_pass else "NOT_APPLICABLE",
            "production_eligible": False,
            "reasons": reasons,
            "summary": summary,
            "base_summary": base_summary,
            "raw_dir": str(out_dir),
        }

    write_json(REPORTS / "checkpoint_status.json", checkpoint_status)
    staging_candidates = [name for name, value in checkpoint_status.items() if isinstance(value, dict) and value.get("status") == "APPROVED_FOR_STAGING"]
    selection = {
        "status": "PASS" if len(staging_candidates) == 1 else "FAIL",
        "selected_checkpoint": staging_candidates[0] if len(staging_candidates) == 1 else None,
        "candidates": staging_candidates,
        "selection_rule": "exactly one checkpoint must pass raw-WAV/EOS/metric gates; no loss-based or epoch-based fallback",
        "human_review_required": True,
    }
    write_json(REPORTS / "selected_checkpoint.json", selection)
    (REPORTS / "final_report.md").write_text(
        "# Qwen3-TTS 0.6B repaired evaluation\n\n"
        f"Alignment repair: `{ALIGNMENT_PATCH_COMMIT}`; learning rate: `{REPAIR_LEARNING_RATE_TEXT}`; epochs: `{REPAIR_EPOCHS}`.\n\n"
        "Raw WAV/EOS/acoustic gates are recorded in `checkpoint_status.json`. A checkpoint marked `APPROVED_FOR_STAGING` is still pending mandatory human review and is never production-approved by this notebook.\n",
        encoding="utf-8",
    )
'''


NEW_PACKAGE = r'''def package_artifact() -> dict[str, object]:
    """Create a self-contained staging archive only from the evaluated candidate."""

    selection_path = REPORTS / "selected_checkpoint.json"
    if not selection_path.is_file():
        raise RuntimeError("Refusing to package: checkpoint selection report is missing")
    selection = json.loads(selection_path.read_text(encoding="utf-8"))
    selected_name = selection.get("selected_checkpoint")
    if selection.get("status") != "PASS" or not isinstance(selected_name, str):
        raise RuntimeError("Refusing to package rejected/unreviewed checkpoint: no single evaluated staging candidate")

    status_report = json.loads((REPORTS / "checkpoint_status.json").read_text(encoding="utf-8"))
    selected_status = status_report.get(selected_name, {})
    if selected_status.get("status") not in {"APPROVED_FOR_STAGING", "APPROVED_FOR_PRODUCTION"}:
        raise RuntimeError(f"Refusing to package rejected/unreviewed checkpoint: {selected_name}")
    selected = (CHECKPOINTS / selected_name).resolve()
    checkpoints_root = CHECKPOINTS.resolve()
    if not selected.is_dir() or checkpoints_root not in selected.parents:
        raise RuntimeError(f"Invalid selected checkpoint path: {selected}")

    config_path = selected / "config.json"
    if not config_path.is_file():
        raise RuntimeError(f"Artifact checkpoint has no config.json: {selected}")
    config = json.loads(config_path.read_text(encoding="utf-8"))
    speaker_ids = config.get("talker_config", {}).get("spk_id", {})
    if SPEAKER_NAME not in speaker_ids:
        raise RuntimeError(f"Artifact checkpoint does not contain speaker {SPEAKER_NAME!r}; found {sorted(speaker_ids)}")

    stage_root = ROOT / "artifact_stage"
    if stage_root.exists():
        shutil.rmtree(stage_root)
    stage_checkpoint = stage_root / "tsukiai_tsukihime_qwen06b" / "checkpoints" / selected.name
    stage_checkpoint.mkdir(parents=True, exist_ok=True)

    runtime_suffixes = {".json", ".safetensors", ".txt"}
    for source in sorted(selected.rglob("*")):
        if not source.is_file() or source.suffix.casefold() not in runtime_suffixes:
            continue
        target = stage_checkpoint / source.relative_to(selected)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)

    missing = [relative_name for relative_name in ARTIFACT_REQUIRED_FILES if not (stage_checkpoint / relative_name).is_file() or (stage_checkpoint / relative_name).stat().st_size <= 0]
    if missing:
        raise RuntimeError(f"Refusing to mix assets from another checkpoint; missing runtime files: {missing}")

    files: list[dict[str, object]] = []
    for path in sorted(stage_checkpoint.rglob("*")):
        if path.is_file():
            files.append({"path": str(path.relative_to(stage_root)).replace("\\", "/"), "size": path.stat().st_size, "sha256": sha256_file(path)})
    status_entry = status_report[selected_name]
    manifest = {
        "artifact_version": 2,
        "status": "APPROVED_FOR_STAGING",
        "production_eligible": False,
        "kernel": KAGGLE_KERNEL_ID,
        "repair_run_id": REPAIR_RUN_ID,
        "notebook_source_sha256": NOTEBOOK_SOURCE_SHA256,
        "model_id": MODEL_ID,
        "speaker": SPEAKER_NAME,
        "language": LANGUAGE,
        "checkpoint": selected.name,
        # The deployable archive intentionally excludes Trainer-only state
        # (optimizer/scheduler/RNG files). Record the runtime tree that was
        # actually packaged, while retaining the full source checkpoint tree
        # as a separate provenance value.
        "checkpoint_tree_sha256": tree_sha256(stage_checkpoint),
        "source_checkpoint_tree_sha256": tree_sha256(selected),
        "alignment_patch_commit": ALIGNMENT_PATCH_COMMIT,
        "base_qwen_commit": BASE_QWEN_COMMIT,
        "dataset": {"text_sha256": EXPECTED_CORPUS_SHA256, "samples": EXPECTED_SAMPLES, "train": TRAIN_COUNT, "holdout": HOLDOUT_COUNT, "seed": SEED},
        "training": {"learning_rate": REPAIR_LEARNING_RATE, "epochs": REPAIR_EPOCHS, "batch_size": 2, "gradient_accumulation": 4, "weight_decay": 0.01, "gradient_clip": 1.0, "seed": SEED},
        "evaluation": {"prompt_set_sha256": hashlib.sha256(json.dumps(json.loads((DATA / "eval_prompts.json").read_text(encoding="utf-8"))["prompts"], ensure_ascii=False, separators=(",", ":")).encode("utf-8")).hexdigest(), "checkpoint_status": status_entry},
        "human_review_status": "PENDING",
        "approval_status": "APPROVED_FOR_STAGING",
        "files": files,
        "repairs": [],
        "selection": "evaluation-gated checkpoint; no hard-coded epoch or loss-based fallback",
    }
    write_json(stage_root / "ARTIFACT_MANIFEST.json", manifest)

    artifact = ROOT / ARTIFACT_NAME
    if artifact.exists():
        artifact.unlink()
    with zipfile.ZipFile(artifact, "w", compression=zipfile.ZIP_STORED, allowZip64=True) as stream:
        for path in sorted(stage_root.rglob("*")):
            if path.is_file():
                stream.write(path, arcname=str(path.relative_to(stage_root)).replace("\\", "/"))

    with zipfile.ZipFile(artifact, "r", allowZip64=True) as stream:
        names = stream.namelist()
        if len(names) != len(set(names)) or stream.testzip() is not None:
            raise RuntimeError("Artifact ZIP failed duplicate-entry or CRC validation")
        required_members = [str(Path("tsukiai_tsukihime_qwen06b") / "checkpoints" / selected.name / name).replace("\\", "/") for name in ARTIFACT_REQUIRED_FILES]
        missing_members = [name for name in required_members if name not in names]
        if missing_members:
            raise RuntimeError(f"Artifact ZIP verification found missing members: {missing_members}")

    artifact_info = {**manifest, "archive": {"name": artifact.name, "bytes": artifact.stat().st_size, "sha256": sha256_file(artifact)}}
    write_json(RUN_INFO / "artifact_manifest.json", artifact_info)
    write_json(REPORTS / "artifact_report.json", artifact_info)
    return artifact_info["archive"]
'''


NEW_MAIN = r'''def main() -> None:
    os.environ["TOKENIZERS_PARALLELISM"] = "false"
    hf_root = ROOT / "hf_cache"
    os.environ["HF_HOME"] = str(hf_root)
    os.environ["HF_HUB_CACHE"] = str(hf_root / "hub")
    os.environ["HF_DATASETS_CACHE"] = str(hf_root / "datasets")
    seed_runtime(SEED)
    dataset_root = find_dataset_root()
    rows, _, _ = audit_dataset(dataset_root)
    train_raw = prepare_data(dataset_root, rows)
    repo, attention, mixed_precision, dtype = install_and_prepare(train_raw)
    train(repo, attention, mixed_precision, dtype)
    evaluate(repo, attention, dtype)
    artifact = package_artifact()
    print(json.dumps({"status": "COMPLETED_FOR_STAGING", "model": MODEL_ID, "train_count": TRAIN_COUNT, "holdout_count": HOLDOUT_COUNT, "checkpoint_dir": str(CHECKPOINTS), "evaluation_dir": str(EVALUATION / "raw"), "artifact": artifact}, ensure_ascii=False))
'''


def ensure_cache_configuration(source: str) -> str:
    source = re.sub(r'\n    os\.environ\["TRANSFORMERS_CACHE"\] = str\(hf_root / "transformers"\)', "", source, count=1)
    source = source.replace('snapshot_download(MODEL_ID, cache_dir=str(ROOT / "hf_cache"), local_files_only=False)', 'snapshot_download(MODEL_ID, cache_dir=str(ROOT / "hf_cache" / "hub"), local_files_only=False)')
    return source


def ensure_training_source_typo_fix(source: str) -> str:
    return source.replace(
        'source = source.replace("self.model.talker.text_projection", "model.talker.text_projection")',
        'source = source.replace("self.model.talker.", "model.talker.")',
    )


def ensure_checkpoint_report_names(source: str) -> str:
    old = '    for checkpoint in sorted(CHECKPOINTS.glob("checkpoint-epoch-*")):\n        name = checkpoint.name.replace("checkpoint-", "")\n'
    new = '    for checkpoint in sorted(CHECKPOINTS.glob("checkpoint-epoch-*")):\n        # Keep the evaluated checkpoint\'s actual directory name in every report.\n        # Trainer emits epoch-0 for the first one-epoch run; stripping the\n        # directory prefix made packaging look for CHECKPOINTS/epoch-0 instead\n        # of the evaluated CHECKPOINTS/checkpoint-epoch-0.\n        name = checkpoint.name\n'
    if old in source:
        return source.replace(old, new, 1)
    if '        name = checkpoint.name\n' in source:
        return source
    raise RuntimeError("checkpoint report naming contract is missing")


def ensure_runtime_tree_identity(source: str) -> str:
    old = '        "checkpoint_tree_sha256": tree_sha256(selected),\n'
    new = (
        '        # The deployable archive intentionally excludes Trainer-only state\n'
        '        # (optimizer/scheduler/RNG files). Record the runtime tree that was\n'
        '        # actually packaged, while retaining the full source checkpoint tree\n'
        '        # as a separate provenance value.\n'
        '        "checkpoint_tree_sha256": tree_sha256(stage_checkpoint),\n'
        '        "source_checkpoint_tree_sha256": tree_sha256(selected),\n'
    )
    if old in source:
        return source.replace(old, new, 1)
    if '"checkpoint_tree_sha256": tree_sha256(stage_checkpoint)' in source and '"source_checkpoint_tree_sha256": tree_sha256(selected)' in source:
        return source
    raise RuntimeError("runtime checkpoint tree identity contract is missing")


def patch_source(source: str) -> str:
    source = replace_once(source, f'QWEN_COMMIT = "{OLD_COMMIT}"\n', f'QWEN_COMMIT = "{REPAIR_COMMIT}"\nQWEN_SOURCE_REF = "refs/pull/278/head"\nQWEN_REPAIR_REPO = "https://github.com/matevosashot/Qwen3-TTS.git"\nBASE_QWEN_COMMIT = "{OLD_COMMIT}"\nALIGNMENT_PATCH_COMMIT = "Qwen3-TTS PR #278 / 09364dcc3f947130861186f222a6c58e319ae67a"\nREPAIR_RUN_ID = "tsuki-qwen3-tts-alignment-pr278-lr2e-6-epoch1-seed42"\nREPAIR_LEARNING_RATE = 2e-6\nREPAIR_LEARNING_RATE_TEXT = "2e-6"\nREPAIR_EPOCHS = 1\nMAX_EVAL_SECONDS = 20.0\n', "Qwen source constants")
    source = replace_once(source, 'ARTIFACT_NAME = "tsukihime_qwen06b_checkpoint-epoch-1.zip"\nARTIFACT_CHECKPOINT = "checkpoint-epoch-1"\nARTIFACT_PREFIX = Path("tsukiai_tsukihime_qwen06b") / "checkpoints" / ARTIFACT_CHECKPOINT\n', 'ARTIFACT_NAME = "tsukihime_qwen06b_repaired_staging.zip"\n', "artifact selection constants")
    source = replace_once(source, 'ROOT = Path("/kaggle/working/tsukiai_tsukihime_qwen06b")\n', f'NOTEBOOK_SOURCE_SHA256 = "{SELF_HASH_MARKER}"\n\nROOT = Path("/kaggle/working/tsukiai_tsukihime_qwen06b")\n', "notebook identity")
    source = replace_once(
        source,
        'RUN_INFO = ROOT / "run_info"\nDATA = ROOT / "data"\nCHECKPOINTS = ROOT / "checkpoints"\nEVALUATION = ROOT / "evaluation"\nREPORTS = ROOT / "reports"\nfor directory in (RUN_INFO, DATA, CHECKPOINTS, EVALUATION, REPORTS):\n    directory.mkdir(parents=True, exist_ok=True)\n',
        'RUN_INFO = ROOT / "run_info"\nDATA = ROOT / "data"\nCHECKPOINTS = ROOT / "checkpoints"\nEVALUATION = ROOT / "evaluation"\nREPORTS = ROOT / "reports"\nif ROOT.exists():\n    shutil.rmtree(ROOT)\nfor directory in (RUN_INFO, DATA, CHECKPOINTS, EVALUATION, REPORTS):\n    directory.mkdir(parents=True, exist_ok=True)\n',
        "clean working root",
    )
    source = replace_once(
        source,
        'def run(command: list[str], *, cwd: Path | None = None, log: Path | None = None) -> None:\n',
        'def run(command: list[str], *, cwd: Path | None = None, log: Path | None = None, env: dict[str, str] | None = None) -> None:\n',
        "run environment parameter",
    )
    source = replace_once(
        source,
        'subprocess.run(command, cwd=cwd, check=True, stdout=output, stderr=subprocess.STDOUT)',
        'subprocess.run(command, cwd=cwd, check=True, stdout=output, stderr=subprocess.STDOUT, env=env)',
        "run environment forwarding",
    )
    source = replace_once(
        source,
        '    return digest.hexdigest()\n\n\ndef run(',
        '    return digest.hexdigest()\n\n\ndef tree_sha256(root: Path) -> str:\n    root = root.expanduser().resolve()\n    files = sorted(path for path in root.rglob("*") if path.is_file())\n    if not files:\n        raise ValueError(f"artifact directory contains no files: {root}")\n    manifest = hashlib.sha256()\n    for path in files:\n        relative = path.relative_to(root).as_posix().encode("utf-8")\n        digest = bytes.fromhex(sha256_file(path))\n        size = path.stat().st_size\n        manifest.update(len(relative).to_bytes(4, "big"))\n        manifest.update(relative)\n        manifest.update(size.to_bytes(8, "big"))\n        manifest.update(digest)\n    return manifest.hexdigest()\n\n\ndef seed_runtime(seed: int) -> None:\n    random.seed(seed)\n    try:\n        import numpy as np\n        np.random.seed(seed)\n    except ImportError:\n        pass\n    try:\n        import torch\n        torch.manual_seed(seed)\n        if torch.cuda.is_available():\n            torch.cuda.manual_seed_all(seed)\n    except ImportError:\n        pass\n\n\ndef run(',
        "tree digest and seeds",
    )
    source = ensure_notebook_replace_once(source)
    source = replace_function(source, "inspect_wav", "_safe_extract_tar", NEW_INSPECT_WAV)
    source = replace_function(source, "install_and_prepare", "train", NEW_INSTALL)
    source = replace_function(source, "train", "evaluate", NEW_TRAIN)
    source = replace_function(source, "evaluate", "package_artifact", NEW_EVALUATE)
    source = replace_function(source, "package_artifact", "main", NEW_PACKAGE)
    source = replace_function(source, "main", "if __name__", NEW_MAIN)
    if 'ARTIFACT_PREFIX' in source:
        raise RuntimeError("stale ARTIFACT_PREFIX reference remains")
    if 'ARTIFACT_CHECKPOINT' in source:
        raise RuntimeError("hard-coded ARTIFACT_CHECKPOINT reference remains")
    return source


def main() -> None:
    document = json.loads(NOTEBOOK.read_text(encoding="utf-8"))
    cell, source = load_code_cell(document)
    if f'QWEN_COMMIT = "{REPAIR_COMMIT}"' in source:
        updated = ensure_notebook_replace_once(source)
        updated = ensure_cache_configuration(updated)
        updated = ensure_training_source_typo_fix(updated)
        updated = ensure_checkpoint_report_names(updated)
        updated = ensure_runtime_tree_identity(updated)
    else:
        updated = patch_source(source)
    ast.parse(updated)
    cell["source"] = updated
    updated = re.sub(r'NOTEBOOK_SOURCE_SHA256 = "[^"]+"', f'NOTEBOOK_SOURCE_SHA256 = "{SELF_HASH_MARKER}"', updated, count=1)
    digest = canonical_notebook_hash(document)
    updated = updated.replace(f'NOTEBOOK_SOURCE_SHA256 = "{SELF_HASH_MARKER}"', f'NOTEBOOK_SOURCE_SHA256 = "{digest}"', 1)
    cell["source"] = updated
    ast.parse(updated)
    NOTEBOOK.write_text(json.dumps(document, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
    print(json.dumps({"notebook": str(NOTEBOOK), "notebook_source_sha256": digest, "repair_commit": REPAIR_COMMIT}, sort_keys=True))


if __name__ == "__main__":
    main()
