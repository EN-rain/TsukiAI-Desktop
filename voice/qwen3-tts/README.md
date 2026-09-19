# TsukiAI Qwen3-TTS service

Production uses the trained Tsukihime custom checkpoint directly. The service
loads an approved checkpoint, verifies its manifest and hashes before model
load, and calls `generate_custom_voice(..., speaker="tsukihime")` for every
request.

The production endpoint does not use reference audio, reference transcripts,
x-vector conditioning, cached ICL prompts, runtime LoRA, the official Base
model as an inference fallback, or a built-in generic speaker. If the artifact
identity is incomplete or unapproved, startup fails closed.

## Contract

`GET /health` and `POST /tts` require the `X-Api-Key` header.

```json
{"text":"Hello, I am Tsuki.","language":"English"}
```

Accepted languages are `Auto`, `English`, and `Japanese`. `/tts` returns a
mono PCM WAV and rejects empty or near-silent output before it can reach
Discord.

## Production configuration

The model path, checkpoint ID, and manifest must describe the same immutable
artifact. There is no latest-checkpoint or epoch fallback.

```text
QWEN_TTS_MODE=trained-custom
QWEN_TTS_DEPLOYMENT_STAGE=production
QWEN_TTS_APPROVAL_STATUS=APPROVED_FOR_PRODUCTION
QWEN_TTS_MODEL=/opt/tsuki-qwen3-tts/checkpoints/<approved-checkpoint>
QWEN_TTS_APPROVED_MANIFEST=/opt/tsuki-qwen3-tts/evidence/ARTIFACT_MANIFEST.json
QWEN_TTS_CHECKPOINT_ID=<manifest-checkpoint-id>
QWEN_TTS_GENERATION_VERSION=trained-custom-v1
QWEN_TTS_SPEAKER=tsukihime
QWEN_TTS_DEVICE=cpu
QWEN_TTS_THREADS=4
QWEN_TTS_MAX_TEXT_CHARS=280
QWEN_TTS_CUSTOM_MAX_NEW_TOKENS=2048
QWEN_TTS_PORT=8100
NUMBA_CACHE_DIR=/opt/tsuki-qwen3-tts/numba-cache
```

The API key is supplied separately in `qwen.env` with mode `600`. The Azure
service uses one worker, CPU float32, four PyTorch threads, and eager attention.
`QWEN_TTS_DEVICE=cuda:0` is supported for an isolated GPU staging worker.

`/health` reports `mode=trained-custom`, `speaker=tsukihime`, the immutable
checkpoint ID, generation version, artifact and manifest SHA-256 values,
`reference_required=false`, null reference hashes, and
`runtime_lora_loaded=false`.

## Explicit pronunciation policy

TsukiAI detects script separately from the Qwen pronunciation mode. Japanese
text is sent as `Japanese`. English text uses `TSUKI_QWEN_ENGLISH_LANGUAGE`,
which accepts `English`, `Auto`, or `Japanese`; the current safe default is the
explicit `English` value until the fixed accent matrix has been human-reviewed.
Do not treat the detector alone as proof of a Japanese-accented English voice.

For the matrix, keep checkpoint and sampling fixed and synthesize the same
English prompts under all three language values. Review identity, accent,
intelligibility, rhythm, vowels, consonants, prosody, artifacts, and EOS
behavior before changing the production value.

## Training and release gates

The final Kaggle recipe is separate from the 425/25 validation recipe. It must
record the repaired Qwen alignment commit, learning rate, seeds, dataset text
SHA-256, dataset manifest SHA-256, `450` train samples, `0` holdout samples,
speaker registration, raw WAV metrics, and exact packaged checkpoint hashes.

The package is staging-only until the automatic raw-WAV/EOS/acoustic gates and
mandatory human Japanese/English A/B review pass. Only a separately recorded
`APPROVED_FOR_PRODUCTION` manifest can be loaded by the production service.

Run the benchmark runner only against a verified trained-custom health
response. Keep raw WAV files and the generated CSV/Markdown evidence together;
timing results never replace the human quality gate.

## Historical diagnostics

The official Qwen Base model and full-ICL voice-clone path are historical or
isolated diagnostic material only. They are not compatible with the production
contract and must not be used to select or deploy the Tsuki voice. The old
`GATE0_*` and `GATE1_BASE_FULL_ICL_*` documents are retained as provenance for
earlier investigations.
