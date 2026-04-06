# TsukiAI Discord Voice Bridge

Node.js sidecar for Discord voice capture/playback. It forwards voice turns to the C# app for STT, LLM, and TTS, then plays the returned audio in Discord.

## What It Does

- Joins a Discord voice channel as a bot
- Captures user speech and segments turns with RMS VAD
- Sends audio/text to TsukiAI C# API endpoints
- Plays returned PCM audio back to Discord
- Exposes a local bridge endpoint for manual TTS playback:
  - `POST http://127.0.0.1:3001/play-tts`

## Runtime Requirements

- Node.js 18+
- Discord bot token with voice permissions (`Connect`, `Speak`, `Use Voice Activity`)
- TsukiAI C# app running with local API enabled (`http://localhost:5000` by default)

## Install

```bash
cd discord-voice-bridge
npm install
```

## Configure

Create `discord-voice-bridge/.env`:

```env
DISCORD_TOKEN=your_discord_bot_token
GUILD_ID=your_discord_server_id
VOICE_CHANNEL_ID=your_voice_channel_id
CSHARP_API_URL=http://localhost:5000

# STT mode: groq | assemblyai | local
STT_MODE=groq
GROQ_API_KEY=
ASSEMBLYAI_API_KEY=

# Optional bridge HTTP port
BRIDGE_HTTP_PORT=3001

# Optional VAD tuning
VAD_RMS_THRESHOLD=300
VAD_SILENCE_FRAMES=45
VAD_MAX_SEGMENT_SEC=12
VAD_MAX_TURN_SEC=30
VAD_END_OF_TURN_MS=1800
VAD_USER_COOLDOWN_MS=3000
VAD_MIN_SEGMENT_BYTES=9600
```

Important:
- Set `CSHARP_API_URL` explicitly in `.env`. The bridge uses this to enable full STT->LLM->TTS mode.
- `STT_MODE=local` uses C# Whisper via `/api/voice/stt`.
- `STT_MODE=groq` or `assemblyai` uses cloud STT in Node, then sends text to C# for LLM/TTS.

## Run

```bash
npm start
```

Dev mode:

```bash
npm run dev
```

## C# API Endpoints Used

- `POST /api/voice/stt` (used when `STT_MODE=local`)
- `POST /api/voice/process-binary` (text -> LLM -> TTS audio bytes)
- `POST /api/voice/test-tts` (used by bridge `/play-tts`)

## Audio Format

- PCM 16-bit little-endian
- 48kHz
- Stereo (2 channels)

## Quick Test

1. Start TsukiAI desktop app.
2. Start bridge: `npm start`.
3. Confirm bot joins configured voice channel.
4. Speak in the channel.
5. Verify logs show STT, LLM, and TTS playback flow.

## Troubleshooting

- Bot cannot join:
  - Check `DISCORD_TOKEN`, `GUILD_ID`, `VOICE_CHANNEL_ID`.
  - Verify bot permissions in the target channel.
- No speech recognized:
  - Lower `VAD_RMS_THRESHOLD` (for quiet microphones).
  - Try `STT_MODE=local` first to isolate cloud STT issues.
- No TTS playback:
  - Confirm C# app is reachable at `CSHARP_API_URL`.
  - Verify `/api/voice/process-binary` returns non-empty audio.

## Package Scripts

- `npm start` - start bridge
- `npm run dev` - watch mode
- `npm run test-token` - token sanity check
- `npm run test-tts` - TTS endpoint test
