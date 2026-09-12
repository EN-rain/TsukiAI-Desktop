# OpenVoice V2 CPU Deployment

## Objective

Make OpenVoice V2 the only TTS backend for the existing TsukiAI application and deploy it on the verified Azure `Standard_B4as_v2` VM in Malaysia West. Use `C:\Users\LENOVO\Downloads\tsuki_25s_clip.mp3` as the one-time reference for the `tsuki` voice. Remove the Alibaba Qwen, IndexTTS, VoiceVox, and other alternate TTS runtime paths from the application and deployment configuration.

## Constraints and decisions

- Keep the VM private; bind the TTS service to `127.0.0.1:8000`.
- Expose only text and language at runtime. Never accept a reference-audio path or file upload from the TTS API.
- Require an API key for synthesis, cap text length, and serialize CPU inference initially.
- Normalize and extract the target speaker embedding once with the official VAD-aware extractor, then load the saved embedding at service startup.
- Load the official OpenVoice V2 base-speaker embeddings for the configured MeloTTS speakers; never derive replacements from an arbitrary sentence.
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
- The official OpenVoice V2 usage notebook expects bundled base-speaker artifacts. The service now fails startup when those artifacts are absent so a different base embedding cannot silently change the voice.
- The 29 GiB OS disk leaves limited room for multiple model copies and caches; cleanup will be required.

## Verification record

- Azure VM: `b4as-test` / `b4as-test-rg` / `malaysiawest` / `Standard_B4as_v2` / Ubuntu 22.04.5; friendly display name `Tsuki's Bedroom`.
- Azure cleanup: temporary `tsuki-vps` / `TSUKI-VPS-EAST-RG` in East Asia was deleted; the surviving VM is the only Azure VM in the subscription.
- VM networking: private-only; no public IP.
- Local reference: `C:\Users\LENOVO\Downloads\tsuki_25s_clip.mp3` exists and is 318,997 bytes.
- Remote reference checksum: `ce93bb53f197f0683d46ab4ad240e3d434ef6e2b2340190be98dfe382a0f6530`; normalized WAV duration: 28.629342 seconds.
- Original-reference embedding: `/opt/openvoice/voices/embeddings/tsuki_se.pth.reference-vad`, extracted with official VAD enabled; file checksum `5949c16aa4fb1db7bdb6f20adfc3b9fdd5500f217322450ed4d99fceeb7794e8`, tensor-payload checksum `eadd8c964a5063e2f8c0d5647ff16f861f571a499ea4cdc1772d1e6dc731ca63`.
- Prior calibration experiment: a Free.ai-render-derived target was tested from `https://api.free.ai/static/outputs/7b9bd60e-aa2b-4634-9473-ce8aabda71b4.wav` (SHA-256 `29d6f5f7411a60cd8a19c54097fccb9535bc185f3f9a5477d34f0d3d3b968d8e`) and is preserved as `/opt/openvoice/voices/embeddings/tsuki_se.pth.freeai-calibrated`; it is not active.
- Calibrated target backup: file checksum `13fd7aed0ebef5b965d86ee8550b0ba344cda34f9f03f81f77e99147158858d0`, tensor-payload checksum `9787baca64d958cd0626c3d688e8a64a3c5cf3003fcec7303901ac38726dba32`.
- Official base embeddings: `en-newest.pth` checksum `6a3798229b1114f0e9cc137b33211809def7dda5a8a9398d5a112c0b42699177`; `jp.pth` checksum `7b645ff428de4a57a22122318968f1e6127ac81fda2e2aa66062deccd3864416`.
- HAR evidence: the exact Free.ai request contained `model=openvoice`, `speed=1`, `format=wav`, the 189-character text, and `tsuki_25s_clip.mp3`; its returned WAV was SHA-256 `69821fb37abde9611abfbcd68fa145556693f33de8f6a9529aaa65463f4b71d3`, mono PCM16, 24 kHz, 15.12 seconds.
- Deployed service source: Git commit `d1542c6` (Free.ai-matched target, EN-US speed 0.8, API-side denoise/loudness mastering, and bridge peak ceiling); the VM uses `OPENVOICE_BASE_SPEAKER_EN=EN-US`, `OPENVOICE_SPEED_EN=0.8`, `OPENVOICE_SPEED_JA=1.0`, and `OPENVOICE_OUTPUT_SAMPLE_RATE=24000`.
- Service verification: `/health` reports `ready`, `engine=openvoice-v2`, one loaded `tsuki` voice, and `EN`/`JA`; unauthenticated `/tts` returns 401.
- Current live smoke verification: the exact HAR text returned a valid mono PCM16 24 kHz WAV; one live run was 11.668042 seconds, and direct converter embedding cosine against the HAR WAV was 0.90618801.
- Earlier calibration comparison: the same legal-consent sentence measured 0.6984 speaker-embedding cosine before calibration and 0.8596 after calibration against an earlier Free.ai render; that calibration is not active because the HAR-matched matrix favored the original reference.
- Earlier benchmark verification: EN 64/120/252 chars took 3,992/6,928/20,484 ms with RTF 0.958/0.964/1.384; JA 18/35/82 chars took 2,883/6,128/16,594 ms with RTF 1.018/0.974/1.098. Peak observed service RSS was about 4.97 GB; root disk was 31% used after setup.
- Local verification: C# API and desktop builds succeeded; 10 OpenVoice Python tests passed.
- HAR-match recheck: the active original-reference target measured cosine `0.7564` against the exact Free.ai WAV; the preserved Free.ai-derived target measured `0.9802`. `EN-US` at speed `0.8` produced `15.151` seconds versus Free.ai `15.120` seconds before mastering; the tested master produced `15.125` seconds, `-16.49 dB` RMS, and `-1.00 dB` peak versus Free.ai `-16.76 dB` RMS and `-0.10 dB` peak.
- Live deployment verification: the exact sentence through `/tts` returned a valid mono PCM16 24 kHz WAV at `14.9125` seconds, `-16.7 dB` mean volume, and `-1.0 dB` peak; both `openvoice-api.service` and `tsuki-discord-voice-bridge.service` were active, and the live sample was posted to the configured Discord text channel.
