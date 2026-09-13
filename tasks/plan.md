# Qwen3-TTS 0.6B CPU Deployment

Deploy the official `Qwen/Qwen3-TTS-12Hz-0.6B-Base` model on the verified
Azure `Standard_B4as_v2` VM (`4 vCPU / 16 GiB`) and make it the only TsukiAI
TTS backend. The one-time reference is
`C:\Users\LENOVO\Downloads\tsuki_25s_clip.mp3`; its speech is Japanese and
the exact Japanese transcript is stored in the Qwen service reference file.

## Non-negotiable runtime contract

- Full ICL voice cloning: `x_vector_only_mode=False`.
- English target text uses `language="English"`; Japanese target text uses
  `language="Japanese"`.
- No training, fine-tuning, or alternate TTS engine.
- The reference is processed once at service start and the cached prompt is
  reused for every request.
- The protected local API exposes `GET /health` and `POST /tts`.
- The API rejects empty or near-silent WAV output before Discord receives it.

## Service layout

```text
/opt/tsuki-qwen3-tts/
├── venv/
├── server/
├── reference/
│   ├── tsuki_25s_clip.mp3
│   ├── tsuki_ref_text.txt
│   └── tsuki_icl_prompt.pt
├── outputs/
└── qwen.env
```

The service binds only to `127.0.0.1:8100`. The .NET API calls it with
`TSUKI_QWEN_TTS_URL` and `TSUKI_QWEN_TTS_API_KEY`; the Discord bridge continues
to call the .NET API and sends Discord voice-message attachments encoded as
Opus with duration and waveform metadata.

## Validation gates

1. Install `qwen-tts` in the isolated VM virtual environment.
2. Load the 0.6B model with CPU/float32/eager attention.
3. Build the full ICL prompt from the exact Japanese reference/transcript.
4. Generate and inspect a short English sample and the supplied benchmark
   sentence; record runtime, duration, RTF, RMS, peak, and SHA-256.
5. Update the .NET API and desktop client to the Qwen contract.
6. Stop and remove the previous TTS service and files.
7. Restart the API and Discord bridge, send one labeled voice message to the
   existing chat, and verify that the uploaded OGG is non-empty and playable.
