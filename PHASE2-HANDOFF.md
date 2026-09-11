# Phase 2 Handoff — GPT-SoVITS training on Kaggle

## TL;DR

We need to fine-tune GPT-SoVITS v2 on Kaggle to produce a Tsuki-timbre EN TTS model.
Phase 1 (F5-TTS dataset generation) is **done**. The training kernel has **failed 8 versions in a row** on setup issues. The state-of-the-union is in `MOCHI-VOICE-PLAN.md`.

## What's already done

| Artifact | Location | Status |
|---|---|---|
| 251 EN training clips (F5-TTS zero-shot, Tsuki timbre, ref1_slow115 recipe) | Kaggle dataset `edriannieves/mochi-train-en-v2` | ✅ verified |
| JP reference audio for inference | Kaggle dataset `edriannieves/mochi-ref-voice` | ✅ uploaded |
| Training kernel notebook | `mochi-voice/kaggle/mochi_gpt_sovits_train.ipynb` | ⚠️ broken |
| Training kernel on Kaggle | `edriannieves/mochi-gpt-sovits-train` v1–v8 | ❌ all ERROR |

## What failed (chronicle — DO NOT redo these mistakes)

| Kernel | Failure | Root cause |
|---|---|---|
| v1 | Hung silently 107 min | `pip install -q -r requirements.txt` swallowed output; unresolved `x_transformers` resolver loop (latest needs torch≥2.6, we pinned torch 2.4.1 for P100 SM 6.0) |
| v2 | `FileNotFoundError: constraints.txt` (0.0 min) | `os.chdir` into GPT-SoVITS dir made relative path invalid |
| v3 | x_transformers resolver loop (40 min timeout) | Pin `x_transformers==1.42.7` not yet present |
| v4 | `ResolutionImpossible` for `rotary_embedding_torch==1.6.5` (0.5 min) | Hallucinated version — package uses 0.x numbering |
| v5 | opencc source build failed (2.1 min) | requirements.txt has `--no-binary=opencc` forcing source compile; cp312 wheel exists but ignored |
| v6 | Pushed broken notebook (push-time bug) | My incremental patches accumulated duplicate lines |
| v7 | Pushed broken notebook | Same — indent issue in cell 1 |
| **v8** | **Got past install (pip done, GPU ready, files staging), died during runtime cell 3+** | **Unknown — log not fetched yet. Install fixes ARE WORKING** |

**v8 conclusion:** the install-phase fixes work. The remaining errors are in cells 3–12 (preprocess, training). These have NOT been audited.

## Hardware / environment constraints (HARD)

- **GPU**: Tesla P100 (SM 6.0). Cannot use torch ≥ 2.5 for CUDA wheels (dropped SM 6.0 support). Must use `torch==2.4.1+cu121`.
- **Python**: 3.12. No `--no-binary=opencc` source build (fails on py3.12).
- **Session cap**: 12 hr. Expected training: ~6 hr (3 hr install+preprocess, 2.5 hr SoVITS, 2 hr GPT).
- **Internet**: enabled (needed for HF model snapshot_download).

## Known-good pins (verified on PyPI before push)

```python
# in constraints.txt
torch==2.4.1
torchvision==0.19.1
torchaudio==2.4.1
x_transformers==1.42.7   # torch>=2.0
rotary_embedding_torch==0.8.9  # torch>=2.0 (NOT 1.x — package is 0.x)
```

## Working install sequence (cell 1, v8 — DO NOT MODIFY unless broken)

```python
# 1. Pin torch via cu121 wheel index
pip install torch==2.4.1 torchvision==0.19.1 torchaudio==2.4.1 --index-url https://download.pytorch.org/whl/cu121

# 2. Pre-install opencc cp312 wheel BEFORE requirements.txt (satisfies --no-binary=opencc)
pip install opencc

# 3. Install rest with constraints (resolves x_transformers + rotary to verified pins)
pip install -r requirements.txt -c constraints.txt --progress-bar off

# 4. nltk data for english.py (pos_tag / TweetTokenizer)
python -c "import nltk; nltk.download('averaged_perceptron_tagger_eng', quiet=True); nltk.download('punkt_tab', quiet=True)"
```

Each step should stream live (no `-q`, Popen with text=bufsize=1). Use 30-min timeout per step, kill and dump tail on timeout.

## Cells 3–12 — UNVERIFIED, audit needed before push

These are in `mochi_gpt_sovits_train.ipynb` and were NOT audited beyond webui source-check. The v8 kernel died somewhere in this region. Possible issues to check first:

1. **Cell 3 data staging** — glob `**/train.list` works, but zip extraction uses `wav.zip` filename; verify `mochi-train-en-v2` dataset layout.
2. **Cell 4 (1-get-text.py)** — env vars: `inp_text`, `inp_wav_dir`, `exp_name`, `opt_dir`, `bert_pretrained_dir`, `i_part`, `all_parts`, `_CUDA_VISIBLE_DEVICES`, `is_half`. Script auto-downloads G2PW onnx model from modelscope at first use — **needs internet, may be slow** but should succeed.
3. **Cell 5 (2-get-hubert)** — env: same as above + `cnhubert_base_dir` (point at pretrained_models/chinese-hubert-base).
4. **Cell 6 (3-get-semantic)** — env: `pretrained_s2G`, `s2config_path` (use `GPT_SoVITS/configs/s2.json`). Needs `inp_wav_dir` too.
5. **Cell 7 (s2 config)** — JSON-loaded + patched. MUST set `cfg["save_weight_dir"] = "SoVITS_weights_v2"` (else `savee()` writes to None — silent loss of weights). MUST NOT set `cfg["data"]["training_files"]` — that key doesn't exist in s2.json schema; data loader reads from `exp_dir` directly.
6. **Cell 8 (s2 train)** — runs s2_train.py as Popen. Should stream epoch lines. ~2.5 hr.
7. **Cell 9 (s1 config)** — yaml-loaded + patched. Use `precision: "16-mixed"` (exact string; "16" won't work).
8. **Cell 10 (s1 train)** — runs s1_train.py. Needs `_CUDA_VISIBLE_DEVICES` and `hz=25hz` env. ~2 hr.
9. **Cell 11 (inference test)** — imports `GPT_SoVITS.inference_webui` (heavy: gradio, transformers, BigVGAN). The export in cell 12 should run BEFORE this so weights aren't lost if the test fails. **RECOMMEND: reorder — package first, test second.**
10. **Cell 12 (package)** — copies weights + ref + test_en.wav to `/kaggle/working/mochi_model/`. Needs to also write to `/kaggle/output/` (Kaggle's persistent download location) — currently only writes to `/kaggle/working/` which vanishes after session. **Fix: copy `/kaggle/working/mochi_model/*` to `/kaggle/output/mochi_model/`** so the kernel output download works.

## Required Kaggle datasets (Add Data when creating new kernel)

- `edriannieves/mochi-train-en-v2` — 251 EN wavs + train.list
- `edriannieves/mochi-ref-voice` — Tsuki JP refs + ref transcripts

Both are PRIVATE. Must be listed in `kernel-metadata.json` under `dataset_sources` to access them.

## Pretrained models needed (HF)

From `lj1995/GPT-SoVITS`:
- `gsv-v2final-pretrained/s2G2333k.pth` (SoVITS generator)
- `gsv-v2final-pretrained/s2D2333k.pth` (SoVITS discriminator)
- `gsv-v2final-pretrained/s1bert25hz-5kh-longer-epoch=12-step=369668.ckpt` (GPT)
- `chinese-hubert-base/` (SSL features)
- `chinese-roberta-wwm-ext-large/` (BERT features)

Snapshot download via `huggingface_hub.snapshot_download` with `allow_patterns`. ~4 GB total.

## Common bugs to AVOID

- **Don't trust ChatGPT-hallucinated version numbers.** Verify every pinned version exists on PyPI before push (use `urllib.request.urlopen("https://pypi.org/pypi/{name}/{version}/json")`).
- **Don't trust that `--no-binary=PACKAGE` in requirements.txt works on py3.12** — many packages don't have py3.12 wheels and source builds fail.
- **Don't use `capture_output=True` on long subprocesses** — hides progress. Use `Popen(stdout=PIPE, stderr=STDOUT, text=True, bufsize=1)` and iterate stdout.
- **Don't use `-q` with pip** in streaming contexts — kills log visibility.
- **Don't use `cfg["data"]["training_files"]`** — s2.json schema doesn't have that key. Data loader uses `exp_dir` subfolders.
- **Don't use `precision: "16"` in s1 config** — must be `"16-mixed"`.
- **Files in `/kaggle/working/` do NOT survive the session.** Use `/kaggle/output/` for downloads.

## Decision points

The current failure is somewhere in cells 3–12. Two viable paths:

**A. Audit cells 3–12 (recommended)** — open each cell, verify against the actual GPT-SoVITS source files at the URLs in `/tmp/t8/` (if available) or directly from GitHub. Then write to `/kaggle/output/` for log persistence. Push v9.

Useful URLs for re-audit:
- https://raw.githubusercontent.com/RVC-Boss/GPT-SoVITS/main/GPT_SoVITS/configs/s2.json
- https://raw.githubusercontent.com/RVC-Boss/GPT-SoVITS/main/GPT_SoVITS/configs/s1longer-v2.yaml
- https://raw.githubusercontent.com/RVC-Boss/GPT-SoVITS/main/GPT_SoVITS/prepare_datasets/1-get-text.py
- https://raw.githubusercontent.com/RVC-Boss/GPT-SoVITS/main/GPT_SoVITS/prepare_datasets/2-get-hubert-wav32k.py
- https://raw.githubusercontent.com/RVC-Boss/GPT-SoVITS/main/GPT_SoVITS/prepare_datasets/3-get-semantic.py
- https://raw.githubusercontent.com/RVC-Boss/GPT-SoVITS/main/GPT_SoVITS/text/cleaner.py
- https://raw.githubusercontent.com/RVC-Boss/GPT-SoVITS/main/GPT_SoVITS/module/data_utils.py
- https://raw.githubusercontent.com/RVC-Boss/GPT-SoVITS/main/GPT_SoVITS/process_ckpt.py

**B. Pivot to CosyVoice2** — completely different repo, much cleaner deps, better cross-lingual out-of-box. Estimated 1–2 versions to a working training kernel (the GPT-SoVITS audit learnings transfer). Trade-off: VRAM 4GB, may need T4 not P100.

## Files

- Plan: `D:\New folder (2)\TsukiAI 1.0\MOCHI-VOICE-PLAN.md`
- Notebook: `D:\New folder (2)\TsukiAI 1.0\mochi-voice\kaggle\mochi_gpt_sovits_train.ipynb`
- Local dataset copy: `D:\New folder (2)\TsukiAI 1.0\mochi-voice\train-en-v2\` (251 wavs + train.list)
- v8 partial log location on Kaggle: kernel page https://www.kaggle.com/code/edriannieves/mochi-gpt-sovits-train → Logs tab

## What "done" looks like

1. Kernel completes (no error) after ~6 hr.
2. `/kaggle/output/mochi_model/` contains: `sovits_mochi_v2.pth`, `gpt_mochi_v2.ckpt`, `ref_tsuki.wav`, `ref_text.txt`, `test_en.wav`, `README.md`.
3. `test_en.wav` has audibly Tsuki-timbre English speech ("Hello, I really love the way you think about things.").
4. Create new Kaggle Dataset `edriannieves/mochi-model-v1` with that folder.
5. Phase 3: deploy on VPS via FastAPI wrapper around `inference_cli.py`.
