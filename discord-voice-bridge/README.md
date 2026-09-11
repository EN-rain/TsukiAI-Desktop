# TsukiAI Discord Voice Bridge

Node.js sidecar for Discord voice capture/playback. In AssemblyAI mode the
runtime path is:

```text
Discord user speaks
  -> Discord.js receiver decodes PCM
  -> AssemblyAI v3 realtime WebSocket
  -> partial + final Turn events
  -> TsukiAI C# API (LLM)
  -> private OpenVoice V2 CPU service
  -> Discord bot playback
```

Discord text mentions use `/api/chat/discord`. The bridge sends the same
Discord user ID for text and voice requests, so their long-term memory is
shared per user while desktop memory remains separate.

## What It Does

- Joins a Discord voice channel as a bot
- Captures human speech; AssemblyAI v3 handles realtime endpointing in
  `STT_MODE=assemblyai` (the other STT modes retain local RMS VAD)
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

# STT mode: azure | groq | assemblyai | local
STT_MODE=assemblyai
STT_FALLBACK_MODE=groq
AZURE_SPEECH_KEY=your-azure-speech-key
AZURE_SPEECH_REGION=southeastasia
# Azure's REST recognizer uses one locale per request.
AZURE_STT_LANGUAGE=en-US
# Preferred: comma- or newline-separated keys; failed keys are rotated out.
GROQ_API_KEYS=
# Optional newline-separated key file. The Docker deployment mounts
# discord-voice-bridge/groq.keys at this container path.
GROQ_API_KEYS_FILE=/run/secrets/tsuki-groq-keys
# Legacy single-key fallback when GROQ_API_KEYS is empty.
GROQ_API_KEY=
# Newline-separated AssemblyAI v3 keys. Keep this file outside Git.
ASSEMBLYAI_KEYS_FILE=C:\path\to\assembly.txt
ASSEMBLYAI_API_KEYS=
# Legacy single-key fallback when the file/list is empty.
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
- `STT_MODE=assemblyai` streams 16 kHz mono PCM to AssemblyAI v3 and handles
  partial and final `Turn` events. The AssemblyAI key file is read locally and
  never sent to the C# API.
- AssemblyAI realtime is presence-gated: it stays disabled when the bot is not
  in voice, or when the bot is alone. It opens/accepts speech only after a
  human member is present, and closes active sessions when the last human
  leaves.
- `ASSEMBLYAI_KEYS_FILE` accepts newline/comma-separated keys. Failed,
  unauthorized, rate-limited, network, and timeout connections rotate to the
  next key; rejected keys are temporarily cooled down.
- The current provider contract is AssemblyAI Streaming v3; this bridge does
  not use the legacy upload/poll endpoint.
- `STT_MODE=azure` sends Discord PCM to Azure Speech. The bridge converts it to
  16 kHz mono WAV and never sends the Azure key to the C# API.
- Set `AZURE_STT_LANGUAGE=ja-JP` for Japanese voice input or `en-US` for English.
  The REST endpoint is intentionally configured for one locale at a time; use
  the setting that matches the current voice channel.
- `STT_FALLBACK_MODE=groq` is attempted only if the primary cloud request fails.
- `STT_MODE=local` uses C# Whisper via `/api/voice/stt`.
- `STT_MODE=groq` uses batch cloud STT in Node, then sends text to C# for LLM/TTS.
- `GROQ_API_KEYS` accepts comma- or newline-separated keys. Groq Whisper tries
  the current key first and rotates to another key on authentication, rate-limit,
  network, timeout, or server errors. Rejected keys are temporarily cooled down.
- For Docker deployments, `GROQ_API_KEYS_FILE` is preferred for long lists. The
  mounted key file is read at startup, excluded from the image, and mounted
  read-only. The API and desktop C# STT paths can use the same file through
  `TSUKI_GROQ_API_KEYS_FILE` or `GROQ_KEYS_HOST_PATH`.
- For Docker deployments, set `ASSEMBLYAI_KEYS_HOST_PATH` in the root `.env` to
  the host-side `assembly.txt`. Compose mounts it read-only at
  `/run/secrets/assemblyai-keys`.

## Slash Commands

The bridge registers one grouped command. Members need **Manage Channels**:

```text
/tsuki join [channel_id]
/tsuki leave [channel_id]
/tsuki say destination:vc text:"Hello from Tsuki"
/tsuki say destination:c text:"Hello in the chat"
/tsuki focus user_id:<user-id>
/tsuki unfocus user_id:<user-id>
/tsuki focuslist
```

`destination:vc` speaks in the currently joined voice channel. `destination:c`
sends a Discord voice message in the text channel where the command was used.
Direct speech is limited to 280 characters and uses the C# TTS pipeline. The
pipeline has one backend: the private OpenVoice V2 CPU service configured with
`TSUKI_OPENVOICE_URL` and `TSUKI_OPENVOICE_API_KEY`.

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
