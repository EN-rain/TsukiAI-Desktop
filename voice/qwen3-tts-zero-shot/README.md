# Tsuki's Bedroom: isolated Qwen3-TTS zero-shot runtime

This directory is a separate Base-model deployment. It must not replace or
modify `voice/qwen3-tts/api.py`, which remains the trained-custom service.

The IBM runtime uses the exact public model
`Qwen/Qwen3-TTS-12Hz-0.6B-Base`, a persisted `tsuki_v1` profile made from the
supplied reference audio and transcript, and full ICL conditioning
(`x_vector_only_mode=false`). vLLM-Omni is bound to loopback port 8091. The
FastAPI adapter is bound to loopback port 8102 and is the only process reached
through the IBM Cloudflare Tunnel.

The adapter exposes:

- `GET /health` — authenticated readiness and identity attestation.
- `POST /tts` — authenticated WAV response for the existing TsukiAI API.
- `POST /tts/stream` — authenticated 24 kHz mono PCM stream for low-latency
  consumers.

There is no Azure TTS dependency in this runtime. Azure hosts the API/Discord
bridge, Groq LLM, AssemblyAI realtime STT, and Cloudflare relay; IBM hosts
Qwen TTS only.
