# OpenVoice V2 CPU Deployment

## Objective

Make OpenVoice V2 the only TTS backend for the existing TsukiAI application and deploy it on the verified Azure `Standard_B4as_v2` VM in Malaysia West. Use `C:\Users\LENOVO\Downloads\tsuki_25s_clip.mp3` as the one-time reference for the `tsuki` voice. Remove the Alibaba Qwen, IndexTTS, VoiceVox, and other alternate TTS runtime paths from the application and deployment configuration.

## Constraints and decisions

- Keep the VM private; bind the TTS service to `127.0.0.1:8000`.
- Expose only text and language at runtime. Never accept a reference-audio path or file upload from the TTS API.
- Require an API key for synthesis, cap text length, and serialize CPU inference initially.
- Normalize and extract the target speaker embedding once, then load the saved embedding at service startup.
- Implement a single `/health` and `/tts` contract for OpenVoice and route the current application directly to it.
- Remove provider selection/fallback logic and stale alternate-provider settings from the active application path. Preserve unrelated features and user changes.
- Verify the official OpenVoice V2 converter/model files and CPU behavior on the VM before treating deployment as complete. The official repository's historical V2 checkpoint URL is not assumed to be live.

## Acceptance criteria

1. The new service code passes syntax/unit checks without requiring model downloads.
2. The application has one active TTS implementation: OpenVoice V2; alternate providers are not registered or selectable.
3. The VM has the OpenVoice repository, CPU environment, converter checkpoint, normalized Tsuki reference, and saved target embedding.
4. `/health` reports ready only after models and the target embedding load successfully.
5. Authenticated English and Japanese `/tts` requests return valid RIFF/WAVE audio.
6. The reference MP3 is not sent or processed during ordinary synthesis requests.
7. A CPU benchmark records generation time, audio duration, RTF, RAM, and any failure mode.
8. No credentials, reference audio, generated audio, or local `.env` values are committed.

## Current risks

- MeloTTS/OpenVoice CPU inference may exceed conversational latency on 4 vCPUs.
- The official OpenVoice V2 usage notebook expects bundled base-speaker artifacts; if those artifacts cannot be fetched from an authoritative source, the service will derive and cache source embeddings from its base-TTS output instead.
- The 29 GiB OS disk leaves limited room for multiple model copies and caches; cleanup will be required.

## Verification record

- Azure VM: `b4as-test` / `b4as-test-rg` / `malaysiawest` / `Standard_B4as_v2` / Ubuntu 22.04.5; friendly display name `Tsuki's Bedroom`.
- Azure cleanup: temporary `tsuki-vps` / `TSUKI-VPS-EAST-RG` in East Asia was deleted; the surviving VM is the only Azure VM in the subscription.
- VM networking: private-only; no public IP.
- Local reference: `C:\Users\LENOVO\Downloads\tsuki_25s_clip.mp3` exists and is 318,997 bytes.
- Remote reference checksum: `ce93bb53f197f0683d46ab4ad240e3d434ef6e2b2340190be98dfe382a0f6530`; normalized WAV duration: 28.629342 seconds.
- Target embedding: `/opt/openvoice/voices/embeddings/tsuki_se.pth`, checksum `8f83fc72c972de636025c85ea4d49022211d759244c35a4266044a1b3a8e6566`.
- Service verification: `/health` reports `ready`, `engine=openvoice-v2`, one loaded `tsuki` voice, and `EN`/`JA`; unauthenticated `/tts` returns 401.
- Benchmark verification: EN 64/120/252 chars took 3,992/6,928/20,484 ms with RTF 0.958/0.964/1.384; JA 18/35/82 chars took 2,883/6,128/16,594 ms with RTF 1.018/0.974/1.098. Peak observed service RSS was about 4.97 GB; root disk was 31% used after setup.
- Local verification: C# API and desktop builds succeeded; 3 OpenVoice client tests and 5 Python validation tests passed.
