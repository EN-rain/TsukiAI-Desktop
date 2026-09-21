"""Molab/Marimo epoch-0 Qwen3-TTS service.

The notebook installs missing dependencies, loads the pinned epoch-0 model,
and starts its FastAPI/Cloudflare service automatically. All status is printed.
There are no input fields or buttons.
Molab starts manually, like Colab, then serves through its own Cloudflare
custom domain. It needs no Molab-issued API key or URL. Upload the separately
created Cloudflare tunnel credential JSON into the configuration variable below.
Never share or commit the notebook after filling in private credentials.

Optional text inference: set TSUKI_ENABLE_TEXT_LLM=1 and a private
TSUKI_LLM_API_KEY in the Molab runtime before starting. Normal TTS startup
does not load the LLM. The unquantized 9B model requires >=19 GiB free CUDA
memory *after* the pinned TTS model is loaded; /v1/health shows load_state.
"""

import marimo

__generated_with = "0.24.2"
app = marimo.App(width="medium")


@app.cell
def _():
    from pathlib import Path as _Path
    import marimo as mo

    HF_REPO_ID = "tsukihime-exe/tsuki-qwen3-tts-models"
    HF_REVISION = "044462bd5ca834ba7ac44ff044d125decda6e863"
    MODEL_ID = f"{HF_REPO_ID}/epoch-0"
    BASE_MODEL_ID = "Qwen/Qwen3-TTS-12Hz-0.6B-Base"
    GENERATION_VERSION = "hf-epoch0-base-full-icl-v1"
    NOTEBOOK_REVISION = "2026-09-20-molab-gpu-batch-v17"
    MAX_TEXT_CHARS = 280
    MAX_NEW_TOKENS = 1024

    # Separate locally-managed Cloudflare tunnel; Colab remains unchanged.
    PUBLIC_HOSTNAME = "molab-tts.tsukihime.dpdns.org"
    PUBLIC_TTS_URL = f"https://{PUBLIC_HOSTNAME}"
    TUNNEL_NAME = "tsuki-molab-qwen-custom"
    TUNNEL_ID = "ce98db7a-1f14-4e56-a016-e5bd2f5676ea"
    LOCAL_TTS_URL = "http://127.0.0.1:8100"

    # Discord credentials and voice/text channel IDs stay on the VPS.
    # Molab has NO TTS API key. Paste the FULL contents of your locally created
    # Cloudflare tunnel JSON between these triple quotes before uploading this
    # notebook to Molab. Do NOT paste a Molab URL, TTS key, or Discord token.
    # The notebook writes the credential privately on Molab when you run it.
    CLOUDFLARE_TUNNEL_CREDENTIALS_JSON = r'''PASTE_PRIVATE_CLOUDFLARE_TUNNEL_JSON_HERE'''

    RUNTIME_DIR = _Path.cwd() / ".tsuki_epoch0_marimo"
    TUNNEL_CREDENTIALS_FILE = RUNTIME_DIR / "tsuki-molab-tunnel.json"
    MODEL_ROOT = RUNTIME_DIR / "model"
    REFERENCE_DIR = RUNTIME_DIR / "reference"
    STARTUP_STATUS_FILE = RUNTIME_DIR / "startup_status.json"
    RUNTIME_DIR.mkdir(parents=True, exist_ok=True)
    REFERENCE_DIR.mkdir(parents=True, exist_ok=True)
    print(f"[config] {NOTEBOOK_REVISION}; output-only autostart.", flush=True)
    print("[config] Credentials are not printed.", flush=True)

    return (
        BASE_MODEL_ID,
        GENERATION_VERSION,
        HF_REPO_ID,
        HF_REVISION,
        LOCAL_TTS_URL,
        MAX_NEW_TOKENS,
        MAX_TEXT_CHARS,
        MODEL_ID,
        MODEL_ROOT,
        NOTEBOOK_REVISION,
        PUBLIC_HOSTNAME,
        PUBLIC_TTS_URL,
        REFERENCE_DIR,
        RUNTIME_DIR,
        STARTUP_STATUS_FILE,
        TUNNEL_NAME,
        TUNNEL_ID,
        TUNNEL_CREDENTIALS_FILE,
        CLOUDFLARE_TUNNEL_CREDENTIALS_JSON,
        mo,
    )


@app.cell
def _(NOTEBOOK_REVISION):
    """Automatically install missing prerequisites when the notebook loads."""
    print(f"[Tsuki] {NOTEBOOK_REVISION}", flush=True)
    # Private aliases avoid collisions with user-added Molab diagnostic cells.
    import importlib as _tsuki_importlib
    import importlib.util as _tsuki_importlib_util
    import importlib.metadata as _tsuki_package_metadata
    import os as _tsuki_os
    import shutil as _tsuki_shutil
    import subprocess as _tsuki_subprocess
    import sys as _tsuki_sys

    # Module names differ from their pip distribution names.
    python_requirements = {
        "qwen_tts": "qwen-tts",
        "fastapi": "fastapi",
        "soundfile": "soundfile",
        "huggingface_hub": "huggingface_hub",
        "requests": "requests",
        "uvicorn": "uvicorn",
        "numpy": "numpy",
        "torch": "torch",
    }

    def inspect_prerequisites():
        _tsuki_importlib.invalidate_caches()
        missing = [
            package
            for module, package in python_requirements.items()
            if _tsuki_importlib_util.find_spec(module) is None
        ]
        sox_path = _tsuki_shutil.which("sox")
        ffmpeg_path = _tsuki_shutil.which("ffmpeg")
        return {
            "missing_python": missing,
            "sox_path": sox_path,
            "ffmpeg_path": ffmpeg_path,
            "ready": not missing and sox_path is not None and ffmpeg_path is not None,
        }

    setup_error = ""
    initial_state = inspect_prerequisites()
    if initial_state["ready"]:
        print("[setup] Python packages, SoX, and FFmpeg already available.", flush=True)
    else:
        print("[setup] Installing missing dependencies...", flush=True)
    if not initial_state["ready"]:
        try:
            missing_before = inspect_prerequisites()["missing_python"]
            if missing_before:
                print("[setup] Missing Python packages: " + ", ".join(missing_before), flush=True)
                # Keep Molab's existing CUDA-enabled torch if it is installed.
                _tsuki_subprocess.run(
                    [
                        _tsuki_sys.executable,
                        "-m",
                        "pip",
                        "install",
                        "--no-cache-dir",
                        *missing_before,
                    ],
                    check=True,
                    timeout=900,
                )

            if _tsuki_shutil.which("sox") is None or _tsuki_shutil.which("ffmpeg") is None:
                print("[setup] Installing missing SoX/FFmpeg tools...", flush=True)
                if (
                    _tsuki_os.name != "posix"
                    or not hasattr(_tsuki_os, "geteuid")
                    or _tsuki_os.geteuid() != 0
                ):
                    raise RuntimeError(
                        "SoX or FFmpeg is missing; system package installation "
                        "requires root permissions."
                    )
                if _tsuki_shutil.which("apt-get") is None:
                    raise RuntimeError(
                        "SoX or FFmpeg is missing and apt-get is unavailable."
                    )
                _tsuki_subprocess.run(
                    ["apt-get", "update", "-qq"],
                    check=True,
                    timeout=300,
                )
                packages = []
                if _tsuki_shutil.which("sox") is None:
                    packages.extend(["sox", "libsox-fmt-all"])
                if _tsuki_shutil.which("ffmpeg") is None:
                    packages.append("ffmpeg")
                _tsuki_subprocess.run(
                    ["apt-get", "install", "-y", "-qq", *packages],
                    check=True,
                    timeout=300,
                )
        except (OSError, RuntimeError, _tsuki_subprocess.SubprocessError) as error:
            setup_error = f"{type(error).__name__}: {error}"
            print("[setup] FAILED: " + setup_error, flush=True)

    # Install Qwen3.5 dependencies only if the operator opted into text LLM.
    # The existing TTS-only environment remains unchanged otherwise.
    if _tsuki_os.environ.get("TSUKI_ENABLE_TEXT_LLM", "") == "1" and not setup_error:
        try:
            try:
                transformers_version = tuple(
                    int(part) for part in
                    _tsuki_package_metadata.version("transformers").split(".")[:2]
                )
            except (_tsuki_package_metadata.PackageNotFoundError, ValueError):
                transformers_version = (0, 0)
            try:
                _tsuki_package_metadata.version("accelerate")
                accelerate_installed = True
            except _tsuki_package_metadata.PackageNotFoundError:
                accelerate_installed = False
            if transformers_version < (5, 2) or not accelerate_installed:
                print("[setup] Installing opt-in Qwen3.5 Transformers support.", flush=True)
                _tsuki_subprocess.run(
                    [_tsuki_sys.executable, "-m", "pip", "install", "--no-cache-dir",
                     "--upgrade", "transformers>=5.2.0", "accelerate"],
                    check=True, timeout=900,
                )
                _tsuki_importlib.invalidate_caches()
        except (OSError, RuntimeError, _tsuki_subprocess.SubprocessError) as error:
            setup_error = f"Optional LLM dependencies: {type(error).__name__}: {error}"
            print("[setup] FAILED: " + setup_error, flush=True)

    runtime_state = inspect_prerequisites()
    if setup_error:
        runtime_state["error"] = setup_error
        runtime_state["ready"] = False

    if runtime_state["ready"]:
        print("[setup] READY: Python packages, SoX, and FFmpeg found.", flush=True)
    else:
        print("[setup] NOT READY: " + str(runtime_state), flush=True)

    return runtime_state,

@app.cell
def _(runtime_state):
    # Import requests only after setup so a fresh runtime can install it first.
    if runtime_state["ready"]:
        import requests
        print("[dependencies] requests import READY.", flush=True)
    else:
        requests = None
        print("[dependencies] BLOCKED: prerequisite setup is incomplete.", flush=True)

    return requests,


@app.cell
def _(HF_REPO_ID, HF_REVISION, MODEL_ROOT, REFERENCE_DIR, mo):
    print("[loader] Pinned epoch-0 model loader prepared.", flush=True)

    def load_epoch0_model(progress_callback=None):
        import hashlib
        import time
        from concurrent.futures import ThreadPoolExecutor
        from pathlib import Path

        import torch
        from huggingface_hub import HfApi, hf_hub_download, snapshot_download
        from qwen_tts import Qwen3TTSModel

        def progress(stage: str, detail: str) -> None:
            if progress_callback is not None:
                progress_callback(stage, detail)

        progress("checking_gpu", "Checking for a CUDA GPU.")
        if not torch.cuda.is_available():
            raise RuntimeError(
                "A CUDA GPU is required. Select a Marimo GPU runtime before "
                "starting the epoch-0 service."
            )

        def sha256_file(path: Path) -> str:
            digest = hashlib.sha256()
            with path.open("rb") as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(block)
            return digest.hexdigest()

        progress("checking_model", "Checking the pinned Hugging Face revision.")
        repository = HfApi().model_info(
            HF_REPO_ID,
            revision=HF_REVISION,
        )
        if repository.private:
            raise RuntimeError("The pinned Hugging Face model repository is private")

        progress(
            "downloading_model",
            "Downloading the pinned epoch-0 checkpoint. This can take several minutes.",
        )
        # Download in a worker while the running Marimo cell publishes updates.
        # Size is measured on disk; do not claim a percentage without a total.
        def download_checkpoint():
            return snapshot_download(
                repo_id=HF_REPO_ID,
                repo_type="model",
                revision=HF_REVISION,
                allow_patterns=["epoch-0/*"],
                local_dir=str(MODEL_ROOT),
            )

        with ThreadPoolExecutor(max_workers=1) as download_pool:
            download_future = download_pool.submit(download_checkpoint)
            started_download = time.monotonic()
            last_report = 0.0
            while not download_future.done():
                time.sleep(1)
                elapsed = time.monotonic() - started_download
                if elapsed - last_report >= 10:
                    last_report = elapsed
                    downloaded = 0
                    for entry in MODEL_ROOT.rglob("*"):
                        try:
                            if entry.is_file():
                                downloaded += entry.stat().st_size
                        except FileNotFoundError:
                            # The download worker may atomically rename temp files.
                            continue
                    progress(
                        "downloading_model",
                        f"{downloaded / (1024 ** 2):,.1f} MiB currently "
                        f"on disk; {elapsed:,.0f}s elapsed (total unknown).",
                    )
            model_root = Path(download_future.result())
        progress("downloading_model", "Checkpoint download complete.")
        model_path = model_root / "epoch-0"

        progress(
            "downloading_reference",
            "Downloading the reference audio and transcript.",
        )
        reference_audio = Path(
            hf_hub_download(
                repo_id=HF_REPO_ID,
                filename="tsuki_25s_clip.mp3",
                repo_type="model",
                revision=HF_REVISION,
                local_dir=str(REFERENCE_DIR),
            )
        )
        reference_text_file = Path(
            hf_hub_download(
                repo_id=HF_REPO_ID,
                filename="tsuki_ref_text.txt",
                repo_type="model",
                revision=HF_REVISION,
                local_dir=str(REFERENCE_DIR),
            )
        )

        progress("verifying_assets", "Verifying reference-file checksums.")
        if sha256_file(reference_audio) != (
            "ce93bb53f197f0683d46ab4ad240e3d434ef6e2b2340190be98dfe382a0f6530"
        ):
            raise RuntimeError("Reference audio hash does not match the pinned asset")
        if sha256_file(reference_text_file) != (
            "bd3e849d943b80e75f62243c85c3d75dabe2716c2d1b12535b39960d9406949e"
        ):
            raise RuntimeError("Reference transcript hash does not match the pinned asset")

        progress("loading_model", "Loading epoch-0 into the CUDA GPU.")
        reference_text = reference_text_file.read_text(encoding="utf-8").strip()
        # SDPA can reduce attention overhead without changing the pinned
        # checkpoint, full-ICL identity, or reference prompt. Fall back only
        # when the installed Qwen/Transformers version rejects the backend.
        attention_backend = "sdpa"
        try:
            model = Qwen3TTSModel.from_pretrained(
                model_path,
                device_map="cuda:0",
                dtype=torch.bfloat16,
                attn_implementation=attention_backend,
            )
        except (ValueError, NotImplementedError) as error:
            print("[loader] SDPA unsupported; using eager attention: "
                  + type(error).__name__, flush=True)
            attention_backend = "eager"
            model = Qwen3TTSModel.from_pretrained(
                model_path,
                device_map="cuda:0",
                dtype=torch.bfloat16,
                attn_implementation=attention_backend,
            )
        progress("building_prompt", "Building the full-ICL voice-clone prompt.")
        voice_clone_prompt = model.create_voice_clone_prompt(
            ref_audio=str(reference_audio),
            ref_text=reference_text,
            x_vector_only_mode=False,
        )

        return {
            "model": model,
            "voice_clone_prompt": voice_clone_prompt,
            "reference_audio": reference_audio,
            "reference_text_file": reference_text_file,
            "reference_audio_sha256": sha256_file(reference_audio),
            "reference_text_sha256": sha256_file(reference_text_file),
            "device_name": torch.cuda.get_device_name(0),
            "attention_backend": attention_backend,
        }

    return load_epoch0_model,

@app.cell
def _(
    BASE_MODEL_ID,
    GENERATION_VERSION,
    HF_REVISION,
    LOCAL_TTS_URL,
    MAX_NEW_TOKENS,
    MAX_TEXT_CHARS,
    MODEL_ID,
    PUBLIC_HOSTNAME,
    PUBLIC_TTS_URL,
    RUNTIME_DIR,
    TUNNEL_NAME,
    TUNNEL_ID,
    TUNNEL_CREDENTIALS_FILE,
    CLOUDFLARE_TUNNEL_CREDENTIALS_JSON,
    load_epoch0_model,
    mo,
    requests,
    runtime_state,
):
    def launch_epoch0_service(progress_callback):
        import asyncio
        import base64
        import io
        import json
        import os
        import platform
        import stat
        import subprocess
        import threading
        import time
        import urllib.request

        import numpy as np
        import soundfile as sf
        import torch
        import uvicorn
        from fastapi import FastAPI, HTTPException
        from fastapi.responses import Response
        from pydantic import BaseModel, Field

        progress_callback(
            "loading_model",
            "Preparing the epoch-0 model and reference assets.",
        )
        model_state = load_epoch0_model(progress_callback)
        model = model_state["model"]
        voice_clone_prompt = model_state["voice_clone_prompt"]
        # One model invocation at a time: /tts-batch processes up to three
        # utterances in ONE GPU forward generation call. Independent concurrent
        # Qwen inference calls contend for the same GPU and can exhaust VRAM.
        inference_slots = asyncio.Semaphore(1)
        # Process-local observations only. Never persist user text or claim
        # historical data from a previous notebook/GPU session.
        metrics_started_at = time.time()
        measurements = {
            "requests": 0, "successes": 0, "failures": 0,
            "generation_seconds": 0.0, "request_seconds": 0.0,
            "queue_seconds": 0.0, "audio_seconds": 0.0,
            "gpu_batch_calls": 0, "synthesized_utterances": 0,
        }
        measurements_lock = threading.Lock()

        def normalize_language(value: str, text: str) -> str:
            normalized = str(value or "English").strip().lower()
            if normalized in {"en", "english"}:
                return "English"
            if normalized in {"ja", "jp", "japanese"}:
                return "Japanese"
            if normalized == "auto":
                has_japanese = any(
                    "\u3040" <= char <= "\u30ff"
                    or "\u4e00" <= char <= "\u9fff"
                    for char in text
                )
                return "Japanese" if has_japanese else "English"
            raise HTTPException(
                status_code=422,
                detail="language must be Auto, English, or Japanese",
            )

        class TtsRequest(BaseModel):
            text: str = Field(min_length=1, max_length=MAX_TEXT_CHARS)
            language: str = Field(default="English", max_length=16)

        class TtsBatchRequest(BaseModel):
            texts: list[str] = Field(min_length=2, max_length=3)
            languages: list[str] = Field(min_length=2, max_length=3)

        def generate_audio_many(texts: list[str], languages: list[str]):
            if not 1 <= len(texts) <= 3 or len(languages) != len(texts):
                raise ValueError("TTS batch must contain one to three aligned items")
            started = time.perf_counter()
            # max_new_tokens is a PER-SAMPLE acoustic-token cap in Qwen;
            # preserve the tail headroom of the longest utterance in the batch.
            budget = min(MAX_NEW_TOKENS, max(256, max(len(text) for text in texts) * 5))
            with torch.inference_mode():
                generated_wavs, sample_rate = model.generate_voice_clone(
                    text=texts if len(texts) > 1 else texts[0],
                    language=languages if len(texts) > 1 else languages[0],
                    voice_clone_prompt=voice_clone_prompt,
                    max_new_tokens=budget,
                )
            generation_seconds = time.perf_counter() - started
            if len(generated_wavs) != len(texts):
                raise RuntimeError("Qwen returned an incomplete TTS batch")
            outputs = []
            for wav in generated_wavs:
                samples = np.asarray(wav, dtype=np.float32).reshape(-1)
                samples = np.nan_to_num(samples, nan=0.0, posinf=0.0, neginf=0.0)
                if samples.size == 0 or float(np.sqrt(np.mean(samples * samples))) < 1e-4:
                    raise RuntimeError("Qwen returned near-silent audio")
                output = io.BytesIO()
                sf.write(
                    output, np.clip(samples, -1.0, 1.0), int(sample_rate),
                    format="WAV", subtype="PCM_16",
                )
                # Duration is computed from actual samples, not text length.
                audio_seconds = samples.size / int(sample_rate)
                outputs.append((output.getvalue(), audio_seconds))
            return outputs, int(sample_rate), generation_seconds

        def generate_audio(text: str, language: str) -> tuple[bytes, int, float, float]:
            outputs, sample_rate, elapsed = generate_audio_many([text], [language])
            audio, audio_seconds = outputs[0]
            return audio, sample_rate, elapsed, audio_seconds

        local_app = FastAPI(
            title="Tsuki epoch-0 Marimo TTS",
            version=GENERATION_VERSION,
        )

        # BEGIN OPTIONAL TEXT LLM (synced from optional_text_llm.py)
        def mount_optional_text_llm(app, gpu_slots, *, enabled, api_key):
            import asyncio
            import hmac
            import os
            import time
            import uuid

            from fastapi import Header, HTTPException
            from pydantic import BaseModel, Field

            llm_id = "lukey03/Qwen3.5-9B-abliterated"
            llm_state = {"status": "unloaded", "model": None, "tokenizer": None,
                         "error": None, "available_gpu_bytes": None}
            llm_lock = asyncio.Lock()

            class ChatRequest(BaseModel):
                model: str
                messages: list[dict] = Field(min_length=2, max_length=32)
                max_tokens: int = Field(default=256, ge=1, le=512)
                temperature: float = Field(default=0.0, ge=0.0, le=1.5)
                stream: bool = False

            def authorize_chat(value, bearer):
                if not api_key:
                    raise HTTPException(503, detail={"code": "llm_auth_not_configured",
                                                     "message": "Set a private LLM API key on the runtime."})
                bearer_key = (bearer[7:] if bearer and bearer[:7].lower() == "bearer " else "")
                supplied = bearer_key or value
                if not supplied or not hmac.compare_digest(supplied, api_key):
                    raise HTTPException(401, detail={"code": "invalid_api_key"})

            def validate_chat(request):
                if request.model != llm_id:
                    raise HTTPException(422, detail={"code": "model_mismatch", "expected": llm_id})
                if request.stream:
                    raise HTTPException(422, detail={"code": "stream_not_supported"})
                messages = request.messages
                if messages[0].get("role") != "system" or not str(messages[0].get("content", "")).strip():
                    raise HTTPException(422, detail={"code": "system_message_required"})
                if messages[-1].get("role") != "user":
                    raise HTTPException(422, detail={"code": "last_message_must_be_user"})
                for index, item in enumerate(messages):
                    if (set(item) != {"role", "content"}
                            or item["role"] not in {"system", "user", "assistant"}
                            or (item["role"] == "system" and index != 0)
                            or not isinstance(item["content"], str)
                            or not item["content"].strip()
                            or len(item["content"]) > 12000):
                        raise HTTPException(422, detail={"code": "invalid_chat_messages"})
                return messages

            def load_text_model():
                import torch

                if not torch.cuda.is_available():
                    raise RuntimeError("cuda_unavailable: A CUDA GPU is required for this model.")
                free_bytes, _total_bytes = torch.cuda.mem_get_info(0)
                llm_state["available_gpu_bytes"] = free_bytes
                # This public BF16/FP16 checkpoint has ~17.9 GB of weights, before
                # KV cache and the pinned TTS model. Do not silently evict TTS or
                # offload to CPU and then report unusably slow inference as ready.
                if free_bytes < 19 * (1024 ** 3):
                    raise RuntimeError(
                        f"gpu_memory_insufficient: {free_bytes // (1024 ** 2)} MiB free; "
                        "the unquantized 9B checkpoint needs at least 19 GiB free "
                        "alongside TTS. Use a larger GPU or a separately hosted LLM."
                    )
                try:
                    from transformers import AutoModelForCausalLM, AutoTokenizer
                    from transformers.models.qwen3_5.modeling_qwen3_5 import Qwen3_5ForCausalLM
                except ImportError as error:
                    raise RuntimeError(
                        "llm_dependencies_missing: Install a Transformers version "
                        "with Qwen3.5 support and accelerate on the opted-in runtime."
                    ) from error
                tokenizer = AutoTokenizer.from_pretrained(llm_id, trust_remote_code=False)
                model = AutoModelForCausalLM.from_pretrained(
                    llm_id,
                    device_map={"": "cuda:0"},
                    dtype=torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16,
                    trust_remote_code=False,
                )
                if not isinstance(model, Qwen3_5ForCausalLM):
                    del model
                    raise RuntimeError("llm_model_identity_mismatch: Unexpected loaded architecture.")
                model.eval()
                llm_state.update(model=model, tokenizer=tokenizer, error=None, status="ready")

            def generate_chat(messages, max_tokens, temperature):
                import torch

                tokenizer = llm_state["tokenizer"]
                model = llm_state["model"]
                # Preserve the exact system role and text supplied by the VPS persona.
                inputs = tokenizer.apply_chat_template(
                    messages, add_generation_prompt=True, tokenize=True,
                    return_dict=True, return_tensors="pt",
                ).to(model.device)
                prompt_tokens = inputs["input_ids"].shape[-1]
                if prompt_tokens > 4096:
                    raise ValueError("context_too_long: maximum 4096 input tokens")
                with torch.inference_mode():
                    generated = model.generate(
                        **inputs, max_new_tokens=max_tokens,
                        do_sample=temperature > 0,
                        **({"temperature": temperature} if temperature > 0 else {}),
                        pad_token_id=tokenizer.eos_token_id,
                    )
                new_tokens = generated[0][prompt_tokens:]
                return tokenizer.decode(new_tokens, skip_special_tokens=True), prompt_tokens, len(new_tokens)

            @app.get("/v1/health")
            async def optional_llm_health():
                load_state = ("disabled" if not enabled else
                              "configuration_required" if not api_key else llm_state["status"])
                return {"model": llm_id, "enabled": bool(enabled),
                        # Only a successfully loaded model is ready; opt-in alone is
                        # not evidence that memory, dependencies or weights are usable.
                        "status": ("ready" if enabled and api_key and
                                   llm_state["status"] == "ready" else "unavailable"),
                        "load_state": load_state,
                        "loaded": llm_state["model"] is not None,
                        "error": llm_state["error"],
                        "available_gpu_bytes": llm_state["available_gpu_bytes"],
                        "endpoint": "/v1/chat/completions"}

            @app.get("/v1/models")
            async def optional_llm_models(x_api_key: str | None = Header(default=None, alias="X-Api-Key"),
                                          authorization: str | None = Header(default=None, alias="Authorization")):
                authorize_chat(x_api_key, authorization)
                return {"object": "list", "data": ([{"id": llm_id, "object": "model"}]
                        if enabled and llm_state["status"] == "ready" else [])}

            @app.post("/v1/chat/completions")
            async def optional_llm_chat(request: ChatRequest,
                                        x_api_key: str | None = Header(default=None, alias="X-Api-Key"),
                                        authorization: str | None = Header(default=None, alias="Authorization")):
                if not enabled:
                    raise HTTPException(503, detail={"code": "llm_disabled",
                                                     "message": "Set TSUKI_ENABLE_TEXT_LLM=1 on this runtime."})
                authorize_chat(x_api_key, authorization)
                messages = validate_chat(request)
                async with llm_lock:
                    async with gpu_slots:
                        if llm_state["model"] is None:
                            llm_state["status"] = "loading"
                            try:
                                await asyncio.to_thread(load_text_model)
                            except Exception as error:
                                # Avoid leaking file paths, tokens, or provider internals.
                                import torch
                                if isinstance(error, torch.cuda.OutOfMemoryError):
                                    detail = "gpu_memory_insufficient: CUDA exhausted during LLM loading."
                                elif isinstance(error, RuntimeError) and str(error).split(":", 1)[0] in {
                                    "cuda_unavailable", "gpu_memory_insufficient",
                                    "llm_dependencies_missing", "llm_model_identity_mismatch"}:
                                    detail = str(error)
                                else:
                                    detail = f"llm_load_failed: {type(error).__name__}"
                                llm_state.update(status="error", model=None, tokenizer=None, error=detail)
                                torch.cuda.empty_cache()
                                raise HTTPException(503, detail={"code": "llm_unavailable", "message": detail}) from None
                        started = time.time()
                        try:
                            text, prompt_tokens, completion_tokens = await asyncio.to_thread(
                                generate_chat, messages, request.max_tokens, request.temperature)
                        except ValueError as error:
                            if str(error).startswith("context_too_long:"):
                                raise HTTPException(422, detail={"code": "context_too_long"}) from None
                            raise
                        except Exception as error:
                            import torch
                            if isinstance(error, torch.cuda.OutOfMemoryError):
                                torch.cuda.empty_cache()
                                raise HTTPException(503, detail={"code": "gpu_memory_insufficient",
                                                                 "message": "CUDA exhausted during LLM generation."}) from None
                            raise HTTPException(503, detail={"code": "llm_inference_failed",
                                                             "message": type(error).__name__}) from None
                return {"id": "chatcmpl-" + uuid.uuid4().hex, "object": "chat.completion",
                        "created": int(started), "model": llm_id,
                        "choices": [{"index": 0, "message": {"role": "assistant", "content": text},
                                     "finish_reason": ("length" if completion_tokens >= request.max_tokens else "stop")}],
                        "usage": {"prompt_tokens": prompt_tokens,
                                  "completion_tokens": completion_tokens,
                                  "total_tokens": prompt_tokens + completion_tokens}}

            return llm_state
        # END OPTIONAL TEXT LLM
        # Opt-in is private to Molab. A normal TTS run never loads LLM weights.
        mount_optional_text_llm(
            local_app, inference_slots,
            enabled=os.environ.get("TSUKI_ENABLE_TEXT_LLM", "") == "1",
            api_key=os.environ.get("TSUKI_LLM_API_KEY", "").strip(),
        )

        @local_app.get("/health")
        async def health():
            return {
                "status": "ready",
                "mode": "trained-base-full-icl",
                "profile": "epoch-0",
                "model": MODEL_ID,
                "base_model": BASE_MODEL_ID,
                "checkpoint_id": "epoch-0",
                "checkpoint_id_source": f"huggingface:{HF_REVISION}",
                "generation_version": GENERATION_VERSION,
                "deployment_stage": "isolated",
                "approval_status": "APPROVED_FOR_ISOLATED_BASE_FULL_ICL",
                "reference_required": True,
                "reference_sha256": model_state["reference_audio_sha256"],
                "reference_transcript_sha256": model_state["reference_text_sha256"],
                "x_vector_only_mode": False,
                "runtime": {
                    "device": "cuda:0",
                    "device_name": model_state["device_name"],
                    "precision": "bfloat16",
                    "attention_backend": model_state["attention_backend"],
                },
                "full_icl_reference_prompt_precomputed": True,
                "phrase_audio_cache": "not implemented",
                "queue_busy": inference_slots.locked(),
                "voice_concurrency_limit": 1,
                "gpu_batch_size_limit": 3,
                "gpu_batch_endpoint": "/tts-batch",
                "gpu_batching": "one model.generate_voice_clone call for 2-3 utterances",
            }

        @local_app.get("/metrics")
        async def metrics():
            with measurements_lock:
                data = dict(measurements)
            successes = data["successes"]
            return {
                "since_epoch_seconds": metrics_started_at,
                "window": "current Molab notebook process only",
                "requests": data["requests"],
                "successes": successes,
                "failures": data["failures"],
                "avg_generation_ms": round(1000 * data["generation_seconds"] / successes, 1)
                    if successes else None,
                "avg_request_ms": round(1000 * data["request_seconds"] / successes, 1)
                    if successes else None,
                "avg_queue_ms": round(1000 * data["queue_seconds"] / successes, 1)
                    if successes else None,
                "generated_audio_seconds": round(data["audio_seconds"], 3),
                "gpu_batch_calls": data["gpu_batch_calls"],
                "synthesized_utterances": data["synthesized_utterances"],
                "real_time_factor": round(data["generation_seconds"] / data["audio_seconds"], 3)
                    if data["audio_seconds"] else None,
                "full_icl_reference_prompt_precomputed": True,
                "phrase_audio_cache": "not implemented",
                "attention_backend": model_state["attention_backend"],
            }

        @local_app.post("/tts")
        async def tts(request: TtsRequest):
            language = normalize_language(request.language, request.text)
            request_started = time.perf_counter()
            with measurements_lock:
                measurements["requests"] += 1
            try:
                async with inference_slots:
                    queue_seconds = time.perf_counter() - request_started
                    audio, sample_rate, elapsed, audio_seconds = await asyncio.to_thread(
                        generate_audio, request.text, language,
                    )
            except Exception:
                with measurements_lock:
                    measurements["failures"] += 1
                raise
            request_seconds = time.perf_counter() - request_started
            with measurements_lock:
                measurements["successes"] += 1
                measurements["generation_seconds"] += elapsed
                measurements["queue_seconds"] += queue_seconds
                measurements["request_seconds"] += request_seconds
                measurements["audio_seconds"] += audio_seconds
                measurements["synthesized_utterances"] += 1
            return Response(
                content=audio,
                media_type="audio/wav",
                headers={
                    "X-Tsuki-Backend": "marimo-qwen3-tts-epoch0-base-full-icl",
                    "X-Tsuki-Sample-Rate": str(sample_rate),
                    "X-Tsuki-Generation-Ms": str(round(elapsed * 1000)),
                    "X-Tsuki-Queue-Ms": str(round(queue_seconds * 1000)),
                    "X-Tsuki-Audio-Seconds": str(round(audio_seconds, 3)),
                    "X-Tsuki-Rtf": str(round(elapsed / audio_seconds, 3)) if audio_seconds else "unavailable",
                },
            )

        @local_app.post("/tts-batch")
        async def tts_batch(request: TtsBatchRequest):
            if len(request.texts) != len(request.languages):
                raise HTTPException(status_code=422, detail="Batch language count mismatch")
            if any(not text.strip() or len(text) > MAX_TEXT_CHARS
                   for text in request.texts):
                raise HTTPException(status_code=422, detail="Invalid batch utterance")
            languages = [
                normalize_language(language, text)
                for language, text in zip(request.languages, request.texts)
            ]
            request_started = time.perf_counter()
            with measurements_lock:
                measurements["requests"] += 1
            try:
                async with inference_slots:
                    queue_seconds = time.perf_counter() - request_started
                    # This is one batched GPU model invocation, not N
                    # concurrent single-sentence inference calls.
                    outputs, sample_rate, elapsed = await asyncio.to_thread(
                        generate_audio_many,
                        [text.strip() for text in request.texts],
                        languages,
                    )
                if len(outputs) != len(request.texts):
                    raise RuntimeError("Qwen batch returned incomplete audio")
            except Exception:
                with measurements_lock:
                    measurements["failures"] += 1
                raise
            request_seconds = time.perf_counter() - request_started
            audio_seconds = sum(duration for _, duration in outputs)
            with measurements_lock:
                measurements["successes"] += 1
                measurements["gpu_batch_calls"] += 1
                measurements["synthesized_utterances"] += len(outputs)
                measurements["generation_seconds"] += elapsed
                measurements["queue_seconds"] += queue_seconds
                measurements["request_seconds"] += request_seconds
                measurements["audio_seconds"] += audio_seconds
            return {
                "mode": "gpu-batch",
                "batch_size": len(outputs),
                "sample_rate": sample_rate,
                "generation_ms": round(elapsed * 1000),
                "queue_ms": round(queue_seconds * 1000),
                "request_ms": round(request_seconds * 1000),
                "rtf": round(elapsed / audio_seconds, 3)
                    if audio_seconds else None,
                "items": [
                    {
                        "audio_base64": base64.b64encode(audio).decode("ascii"),
                        "audio_seconds": round(duration, 5),
                    }
                    for audio, duration in outputs
                ],
            }

        # The VPS bridge owns Discord credentials and posts the ready announcement.
        # Molab exposes keyless /health, /tts and /tts-batch through its domain.

        progress_callback("starting_api", "Starting the local TTS API.")
        uvicorn_config = uvicorn.Config(
            local_app,
            host="127.0.0.1",
            port=8100,
            log_level="warning",
        )
        uvicorn_server = uvicorn.Server(uvicorn_config)
        uvicorn_thread = threading.Thread(
            target=uvicorn_server.run,
            daemon=True,
        )
        uvicorn_thread.start()
        time.sleep(2)

        progress_callback("checking_local_health", "Checking the local TTS API.")
        health_response = requests.get(
            f"{LOCAL_TTS_URL}/health",
            timeout=30,
        )
        health_response.raise_for_status()
        health_payload = health_response.json()
        if (
            health_payload.get("status") != "ready"
            or health_payload.get("checkpoint_id") != "epoch-0"
            or health_payload.get("reference_sha256")
            != model_state["reference_audio_sha256"]
        ):
            raise RuntimeError(
                "Local health returned an unexpected model identity."
            )
        progress_callback(
            "checking_local_health",
            "Manual Molab keyless mode; epoch-0 model identity verified.",
        )

        tunnel_process = None
        tunnel_running = False
        inline_credentials = CLOUDFLARE_TUNNEL_CREDENTIALS_JSON.strip()
        if not inline_credentials or inline_credentials == "PASTE_PRIVATE_CLOUDFLARE_TUNNEL_JSON_HERE":
            progress_callback(
                "tunnel",
                "Cloudflare tunnel not started: paste its private JSON into "
                "CLOUDFLARE_TUNNEL_CREDENTIALS_JSON in the notebook.",
            )
        else:
            # Locally-managed Cloudflare tunnel: validate the inline JSON, then
            # write it privately for cloudflared. Never log credential contents.
            try:
                credentials = json.loads(inline_credentials)
            except (ValueError, TypeError):
                raise RuntimeError("Cloudflare credential JSON is invalid.") from None
            if not isinstance(credentials, dict) or credentials.get("TunnelID") != TUNNEL_ID:
                raise RuntimeError("Cloudflare credentials belong to a different tunnel.")
            del credentials
            fd = os.open(
                TUNNEL_CREDENTIALS_FILE,
                os.O_WRONLY | os.O_CREAT | os.O_TRUNC,
                0o600,
            )
            with os.fdopen(fd, "w", encoding="utf-8") as credential_stream:
                os.chmod(TUNNEL_CREDENTIALS_FILE, 0o600)
                credential_stream.write(inline_credentials)
            progress_callback("preparing_tunnel", "Preparing the separate Molab Cloudflare tunnel.")
            cloudflared_path = RUNTIME_DIR / "cloudflared"
            if not cloudflared_path.exists():
                machine = platform.machine().lower()
                asset_name = (
                    "cloudflared-linux-arm64"
                    if machine in {"aarch64", "arm64"}
                    else "cloudflared-linux-amd64"
                )
                urllib.request.urlretrieve(
                    f"https://github.com/cloudflare/cloudflared/releases/latest/download/{asset_name}",
                    cloudflared_path,
                )
            cloudflared_path.chmod(cloudflared_path.stat().st_mode | stat.S_IXUSR)
            tunnel_config = RUNTIME_DIR / "molab-tunnel.yml"
            tunnel_config.write_text(
                f"tunnel: {TUNNEL_ID}\n"
                f"credentials-file: {json.dumps(str(TUNNEL_CREDENTIALS_FILE))}\n"
                "ingress:\n"
                f"  - hostname: {PUBLIC_HOSTNAME}\n"
                f"    service: {LOCAL_TTS_URL}\n"
                "  - service: http_status:404\n",
                encoding="utf-8",
            )
            progress_callback("starting_tunnel", "Starting custom-domain Cloudflare tunnel.")
            tunnel_process = subprocess.Popen(
                [
                    str(cloudflared_path), "tunnel",
                    "--config", str(tunnel_config),
                    "--no-autoupdate", "run", TUNNEL_ID,
                ],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.STDOUT,
            )
            time.sleep(5)
            tunnel_running = tunnel_process.poll() is None

        public_ok = False
        if tunnel_running and PUBLIC_TTS_URL:
            progress_callback(
                "checking_public_health",
                "Checking the public endpoint through Cloudflare.",
            )
            for _attempt in range(6):
                try:
                    public_response = requests.get(
                        f"{PUBLIC_TTS_URL}/health",
                        timeout=30,
                    )
                    if public_response.status_code == 200:
                        public_ok = True
                        break
                except requests.RequestException:
                    pass
                time.sleep(5)

        return {
            "status": "ready",
            "local_health": health_response.status_code,
            "tunnel_name": TUNNEL_NAME,
            "tunnel_running": tunnel_running,
            "public_health": public_ok,
            "public_url": PUBLIC_TTS_URL if public_ok else None,
            "local_url": LOCAL_TTS_URL,
            "device_name": model_state["device_name"],
            "server_thread": uvicorn_thread,
            "tunnel_process": tunnel_process,
        }

    if not runtime_state["ready"]:
        raise RuntimeError(
            "Automatic prerequisite installation failed. See [setup] output above."
        )

    print("[config] Molab runtime is manual; no TTS API key or Molab URL is required.", flush=True)
    mo.output.append(mo.md("**Molab:** manual runtime, Cloudflare custom domain."))

    def show_progress(stage, detail):
        print(f"[startup] {stage}: {detail}", flush=True)
        mo.output.append(mo.md(f"**{stage}** — {detail}"))

    print("[startup] Launching epoch-0 service automatically...", flush=True)
    mo.output.append(mo.md("**Starting epoch-0 TTS automatically.**"))
    try:
        service_state = launch_epoch0_service(show_progress)
    except Exception as error:
        # Do not leak accidental credential contents from a tunnel exception.
        print(f"[startup] FAILED: {type(error).__name__}; check the runtime logs.", flush=True)
        mo.output.append(mo.md(f"**Startup failed:** {type(error).__name__}; check runtime logs."))
        raise RuntimeError("Epoch-0 service startup failed; see [startup] output above.") from None

    print(
        f"[startup] READY on {service_state['device_name']}; "
        f"local health HTTP {service_state['local_health']}; "
        f"local endpoint {service_state['local_url']}",
        flush=True,
    )
    mo.output.append(mo.md("**READY:** local API and epoch-0 identity verified."))
    if service_state["public_health"]:
        print(f"[tunnel] VERIFIED: {service_state['public_url']}", flush=True)
    else:
        print("[tunnel] NOT VERIFIED: check CLOUDFLARE_TUNNEL_CREDENTIALS_JSON and tunnel logs.", flush=True)
    print("[discord] VPS bridge owns readiness announcements and channel IDs.", flush=True)
    mo.output.append(mo.md("**Discord:** VPS bridge announces when Molab /health becomes ready."))

    return service_state,


if __name__ == "__main__":
    app.run()
