# TsukiAI — Desktop → Web Migration Plan

> Status: approved 2026-09-05; **role split revised 2026-09-05** (user decision):
> the **desktop app stays** as the local "assistance and fun" companion — it is not
> being replaced. The web deployment's primary job is a **24/7 persistent Tsuki on
> Discord**; the React SPA rides along as a secondary admin surface (settings,
> memory, chat) reachable from any browser.

## Target architecture

```
Discord voice channel  <---  bridge container (discord-voice-bridge, 24/7)
                                 |  /api/voice/* with X-Api-Key
Browser (React SPA, your-domain.com)  -- HTTPS (settings/memory/chat, secondary)
   |
   v
Caddy (TLS, Let's Encrypt)  ->  TsukiAI.Api (ASP.NET Core 8, reuses TsukiAI.Core)
   |-- JSON state on a Docker volume (chat history, provider state, non-secret settings)
   |-- ChromaDB container (REST API — replaces the spawned Python worker)
   |-- VOICEVOX container (official docker image — replaces local run.exe)
   +-- Cloud APIs (Groq/Cerebras/Gemini/... LLM, Groq Whisper/AssemblyAI STT, DeepL) — already HTTP, unchanged

Desktop (WPF app) — local mic/voice fun against the same Tsuki persona, unchanged
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

## Phase 3 — Data, secrets, polish  [done]
- [x] `scripts/import-local-data.ps1`: copies desktop `%APPDATA%\TsukiAI` history +
      provider-state into the server data dir and writes a **redacted** settings.json
      (API keys stripped); prints which env vars to set instead.
- [x] Per-provider key env vars added to `EnvConfiguration` (TSUKI_CEREBRAS/GROQ/GEMINI/
      GITHUB/MISTRAL_API_KEY) + TSUKI_SEMANTIC_MEMORY_ENABLED — server keys now come from
      environment only. The real DeepL key in the root `.env` stays untracked and moves to
      the server `.env` at deploy time.
- [x] History/provider state stays JSON on the `tsuki-data` Docker volume (SQLite upgrade
      postponed — JSON satisfies single-user scale and keeps the Core services untouched).
- Note: storage layout changed from the original SQLite plan; settings/history files live
  under `TSUKI_DATA_DIR` (default `%APPDATA%\TsukiAI` for desktop, `/data` in Docker).

## Phase 4 — Deployment (VPS + Docker + domain)  [files done, deploy pending]
- [x] Root `Dockerfile`: multi-stage (node builds web -> dotnet publishes API -> single
      aspnet runtime image serving the SPA from wwwroot + API on :8080, non-root user,
      /data volume).
- [x] `docker-compose.yml`: caddy (auto-TLS), api, chromadb, voicevox (optional `voice`
      profile), commented Phase-5 bridge service.
- [x] `Caddyfile`: `{$DOMAIN} -> reverse_proxy api:8080`, gzip.
- [x] `.env.example`: full web-deployment surface documented (DOMAIN, TSUKI_WEB_PASSWORD,
      per-provider keys, STT key/model, memory flag).
- [x] SPA serving verified locally: API serves static assets, SPA fallback, and auth
      together (required UseStaticFiles BEFORE auth middleware — extension requests never
      reach the :nonfile fallback, and auth-after-routing 401'd them otherwise).
- [ ] **Not executable on this machine (no Docker installed).** First deploy on the VPS:
      1. `git clone` repo (or push + pull), copy `.env.example` -> `.env`, fill in
         DOMAIN + TSUKI_WEB_PASSWORD + keys.
      2. Run `scripts/import-local-data.ps1 -TargetDir .\tsuki-data` on the Windows box,
         copy `tsuki-data/` to the VPS, and point the `tsuki-data` volume at it (or
         bind-mount it per compose).
      3. `docker compose up -d --build` — Caddy obtains certificates automatically once
         the DNS A-record points at the VPS.
      4. Watch `docker compose logs -f caddy api` for the first boot; log in with the
         password, flip semantic memory on in Settings, and do a test voice turn.

## Phase 5 — 24/7 Discord assistant [wired, deploy pending]
Promoted from "deferred" to the web deployment's primary workload (user decision 2026-09-05).
- [x] `discord-voice-bridge` already drove `/api/voice/stt`, `/process-binary`, `/test-tts` via
      `CSHARP_API_URL` — same contract the API kept, so the bridge works against the VPS API
      unchanged.
- [x] API-key auth for headless clients: `TSUKI_API_KEY` on the server, `X-Api-Key` header on
      requests (`ApiKeyAuthenticationHandler` + policy scheme that routes X-Api-Key requests to
      it, cookie requests to the browser scheme). Verified matrix: no auth 401, wrong key 401,
      correct key 200, cookie 200, health anon 200.
- [x] Bridge sends `CSHARP_API_KEY` as the X-Api-Key header (index.js sets the axios default).
- [x] `discord-voice-bridge/Dockerfile` + compose `bridge` service enabled: env from
      `discord-voice-bridge/.env` (Discord token/guild/channel), `CSHARP_API_URL=http://api:8080`.
- [ ] At deploy time: create `discord-voice-bridge/.env` from its example, enable the `voice`
      profile (VOICEVOX container) so the pipeline has TTS, `docker compose up -d` and confirm
      the bot joins the voice channel.

## Next rework (user direction, 2026-09-05 evening)
- Web becomes **Tsuki's portfolio + public chat bot** — not a private admin app.
  VPS topology: VPS <-> Discord (24/7 bot), VPS <-> user's PC (link TBD: tunnel),
  VPS <-> portfolio site (public chat against the API).
- Own domain confirmed NOT required for the Discord bridge (outbound-only).
  Caddy/domain deferred until the portfolio needs HTTPS; until then the UI
  stays unpublished (ports closed) or IP-only.
- The cookie-auth admin SPA will be reworked: public portfolio page + chat,
  private admin surface for settings/memory.
- Discord bridge deployed 2026-09-05 with user-provided token; guild/voice
  channel IDs on the server in discord-voice-bridge/.env.

## Still deferred
- Ollama/local models: config-only when a GPU box exists (TSUKI_REMOTE_INFERENCE_URL).
- VRChat OSC: desktop-only anyway; stays with the desktop app.

## Checkpoints
- After Phase 1: verify API boots, `/api/voice/health` responds, login works, a text chat turn
  round-trips (with TTS when VOICEVOX URL is set). Then start the frontend.
