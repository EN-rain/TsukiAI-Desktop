# TsukiAI Qwen3-TTS service

This is the only TTS backend used by TsukiAI. It runs the official
`Qwen/Qwen3-TTS-12Hz-0.6B-Base` model on CPU and uses the supplied Japanese
reference plus its exact transcript in full ICL voice-clone mode.

The service loads the model once, builds or reloads the cached voice-clone
prompt once, and reuses it for every request. It performs no training or
fine-tuning. Requests contain text and a target language; reference audio is
never uploaded at runtime.

## Contract

`GET /health` and `POST /tts` require the `X-Api-Key` header.

```json
{"text":"Hello, I am Tsuki.","language":"English"}
```

`/tts` returns a mono PCM WAV. The service rejects empty or near-silent model
output before it can reach Discord.

## CPU deployment

Install the native audio utilities before starting the service:

```bash
sudo apt install -y ffmpeg sox libsndfile1
```

The Azure service uses one worker, `device_map="cpu"`, `dtype=torch.float32`,
four PyTorch threads, and eager attention. No GPU or FlashAttention is used.
`NUMBA_CACHE_DIR` points at a writable service directory so librosa can import
under systemd's read-only system paths.

Reference configuration:

```text
QWEN_TTS_MODEL=Qwen/Qwen3-TTS-12Hz-0.6B-Base
QWEN_TTS_REFERENCE=/opt/tsuki-qwen3-tts/reference/tsuki_25s_clip.mp3
QWEN_TTS_REF_TEXT_FILE=/opt/tsuki-qwen3-tts/reference/tsuki_ref_text.txt
QWEN_TTS_PROMPT_PATH=/opt/tsuki-qwen3-tts/reference/tsuki_icl_prompt.pt
QWEN_TTS_DEVICE=cpu
QWEN_TTS_THREADS=4
QWEN_TTS_MAX_TEXT_CHARS=280
QWEN_TTS_PORT=8100
NUMBA_CACHE_DIR=/opt/tsuki-qwen3-tts/numba-cache
```

The API key is supplied separately in `qwen.env` with mode `600`.
