# TsukiAI — Desktop → Web Migration Plan

> Status: approved 2026-09-05. Target: single-user web app on a VPS, everything API-reliant,
> own domain. The WPF desktop app stays compiling and usable until the web app reaches parity.

## Target architecture

```
Browser (React SPA, your-domain.com)
   |  HTTPS + SignalR/WebSocket (mic audio up, TTS audio down, streamed LLM tokens)
   v
Caddy (TLS, Let's Encrypt)  ->  TsukiAI.Api (ASP.NET Core 8, reuses TsukiAI.Core)
   |-- SQLite/JSON state on a Docker volume (chat history, provider state, non-secret settings)
   |-- ChromaDB container (REST API — replaces the spawned Python worker)
   |-- VOICEVOX container (official docker image — replaces local run.exe)
   +-- Cloud APIs (Groq/Cerebras/Gemini/... LLM, Groq Whisper/AssemblyAI STT, DeepL) — already HTTP, unchanged
[Phase 2+: discord-voice-bridge as another compose service; Ollama reachable via configurable URL]
```

Key wins: `TsukiAI.Core` is pure net8.0 (no WPF) and the app already serves `/api/voice/*` +
`/api/memory/*` on localhost:5000 — those endpoints become the public API almost as-is. All AI
providers are already HTTP-based, so the voice pipeline (STT -> LLM -> TTS) moves server-side with
minimal logic change; only mic capture/output moves into the browser.

## Code-organization rules

- Cross-platform voice services (`VoiceConversationPipeline`, `VoicevoxClient`,
  `TranslationService`, `AudioProcessingService`, `LatencyTracker`, `IWhisperService`,
  `IVoiceConversationPipeline`) live in **TsukiAI.Core** so both the desktop app and the web API
  compile against the same code. Their namespace stays `TsukiAI.VoiceChat.Services` for now to
  avoid a mass rename; a namespace cleanup can happen later.
- Windows-only services (`MicrophoneCaptureService`, `TtsPlaybackService`, `VoicevoxEngineService`,
  Discord services, all Views) stay in `TsukiAI.VoiceChat` and never get referenced by the API.
- `EnvConfiguration` moved to `TsukiAI.Core.Services` so the API can apply env overrides too.
- `SettingsService.GetBaseDir()` honors `TSUKI_DATA_DIR` (falls back to `%APPDATA%\TsukiAI`) so the
  server can point storage at a Docker volume.
- Secrets (LLM/STT/TTS/DeepL keys, web password) come from environment variables / Docker secrets
  only. The web UI never receives them.

## Phase 1 — API project (`TsukiAI.Api`)  [done]
- [x] Move cross-platform pipeline services into TsukiAI.Core.
- [x] `ChromaHttpSemanticMemoryService` in Core (Chroma REST v2, auto-embed) replacing the Python
      worker + sqlite sidecar.
- [x] `InferenceClientFactory` in Core (multi-provider resolution previously duplicated in App.xaml.cs).
- [x] ASP.NET Core Web API project: ported `VoiceApiController`, `/api/memory/*`, `/api/chat`,
      SignalR `VoiceHub` (turn-based events; token streaming in Phase 2+).
- [x] Real server-side STT: `GroqWhisperService` (multipart forward to Groq; accepts browser
      webm/wav audio directly).
- [x] Single-user auth: `TSUKI_WEB_PASSWORD` + session cookie; API refuses public binding without it.
- [x] `/api/settings` GET/PUT (non-secret subset), `/api/history` GET/DELETE.
- [x] E2E verified: login -> chat turn -> real Groq reply. Two fixes landed during testing:
      stale provider model defaults (llama-3.3-70b-versatile was decommissioned on Groq; now
      openai/gpt-oss-120b) and extra max_tokens headroom for reasoning models (gpt-oss/qwen3
      burn completion tokens on hidden reasoning and return empty content otherwise).

## Phase 2 — Web frontend (React + TypeScript + Vite)  [done]
- [x] `web/` Vite + React 18 + TS + Tailwind v4, dark night-sky theme (ink surfaces, moon-gold
      accent; no dev proxy dependency in production).
- [x] Views: chat (restores voice history, live turns, per-reply TTS playback), voice room
      (push-to-talk button + Space hold, MediaRecorder webm/opus -> STT -> process -> PCM
      playback), settings (non-secret editing), memory (search + teach).
- [x] Auth: local mode auto-session; password form in public mode; 401 event bounces to login.
- [x] API keys never sent to the browser; settings API exposes a non-secret subset only.
- [x] Verified in browser: auto-login, history render, live chat turn end-to-end, all four
      tabs render with correct server state.
- Deployment note: production serving (Caddy static + API proxy) lands in Phase 4.

## Phase 3 — Data, secrets, polish
- One-shot import of `%APPDATA%\TsukiAI\chat_history.json` / settings into server storage.
- All API keys live only in server env/secret files — never sent to the browser. The plaintext
  `settings.json` key store disappears.
- Latency tracking and provider failover (`ProviderSwitchingService`) carried over as-is.

## Phase 4 — Deployment (VPS + Docker + domain)
- Multi-stage Dockerfile (API + frontend build); `docker-compose.yml`: caddy, api, chromadb,
  voicevox (bridge service commented out until Phase 5).
- DNS A-record for the domain -> VPS; Caddy auto-TLS.
- `.env` on server holds keys; `.env.example` updated. The real DeepL key currently sitting in the
  root `.env` must be moved to the server.

## Phase 5 — Deferred
- Discord voice bot: run `discord-voice-bridge` as a second compose service pointing at the public
  API instead of localhost:5000.
- Ollama/local models: keep `OllamaClient`, point it at a LAN/GPU box URL — config only.
- VRChat OSC: only feasible when the server can reach your PC (self-host or tunnel) — decide later.

## Checkpoints
- After Phase 1: verify API boots, `/api/voice/health` responds, login works, a text chat turn
  round-trips (with TTS when VOICEVOX URL is set). Then start the frontend.
