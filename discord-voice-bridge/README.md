# TsukiAI Discord Voice Bridge

## Discord voice and chat engagement (workspace implementation)

Engagement gating runs after finalized AssemblyAI/fallback transcription and
before any voice speaker claim, LLM call, or TTS request. Only direct bot address,
a short same-speaker follow-up after a successfully delivered Tsuki reply, or
an opt-in classified room invitation can authorize a reply. Unrelated speakers
do not own the AssemblyAI turn lock. A separate gate applies to allowed Discord
text chats before `/api/chat/discord`; a keyword selects TTS **only after** the
chat reply was authorized. Text `mention` mode still requires an actual @mention
or a confirmed reply to the bot, regardless of chat context mode.

Bridge `.env` settings: `VOICE_ENGAGEMENT_MODE` and `CHAT_ENGAGEMENT_MODE` are
`wake_only`, `context_aware` (source defaults), or `proactive`; use
`ENGAGEMENT_ALIASES=tsuki,tsukihime` and `ENGAGEMENT_FOLLOWUP_MS=10000` (0–30000).
The optional `ENGAGEMENT_CLASSIFIER_MODEL` must name an explicitly configured
Groq JSON-compatible chat model; leaving it empty disables classifier requests
and makes ambiguous/proactive speech abstain. The classifier uses a strict 1.2 s
runtime deadline and separate Groq key rotation. Never send transcript bodies to
metrics. `/analytics` exposes process-lifetime engagement decision counts and
classifier timing/failures; it does not report production classification accuracy.
Controls writes these settings through the existing authenticated config job,
restarting the bridge. Read `docs/CONTEXT_AWARENESS_VOICE_CHAT_PLAN.md` for
acceptance tests and outstanding consented live Discord validation. Neither
local code changes nor synthetic tests constitute a VPS deployment.

**Independent LLM selection.** The dashboard's LLM provider is separate from
`TSUKI_TTS_PROVIDER`: choose `api` (existing Groq/remote API provider), `molab`,
or `colab`. The two notebook selections use authenticated
`lukey03/Qwen3.5-9B-abliterated` `/v1/chat/completions` through the C# API,
which injects the existing Tsuki persona and `assets/Modelfile` style anchor as
the system prompt. The same persona remains active on `api`. Set the matching
private `TSUKI_LLM_API_KEY` in the API's root `.env` and the notebook runtime;
set `TSUKI_ENABLE_TEXT_LLM=1` in the selected notebook only when GPU resources
allow the separate 9B model alongside TTS. Do not place the key in a frontend,
commit it, or confuse notebook `/health` for LLM readiness: use `/v1/health` and
`/v1/models`, and verify an actual authenticated inference before claiming the
model is ready. Unavailable selected providers fail clearly, without silent
cross-provider fallback.

The engagement gate passes only the decision's selected short-lived prior
speaker-attributed `contextTurns` to the C# text/voice reply. C# revalidates
and quotes these as low-priority user-context JSON; unrelated observed speech
and raw Discord IDs are never sent as reply history or persisted to memory.
A focus/channel/disconnect change invalidates pending replies. See the plan
for staged, consented real-speaker accuracy and live notebook/VPS acceptance.

Node.js sidecar for Discord voice capture/playback. In AssemblyAI mode the
runtime path is:

```text
Discord user speaks
  -> Discord.js receiver decodes PCM
  -> AssemblyAI v3 realtime WebSocket
  -> partial + final Turn events
  -> TsukiAI C# API (LLM)
  -> private IBM Qwen3-TTS 0.6B trained Tsukihime custom-voice service
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

The bridge registers seven top-level commands (no grouped `/t` command). Members need **Manage Channels**:

```text
/tjoin [channel_id]
/tleave [channel_id]
/tsayvc text:<what to say>
/tsayc text:<what to say>
/tfocus user_id:<user-id>
/tunfocus user_id:<user-id>
/tfocuslist
```

`/tsayvc` takes required inline `text` and **speaks in the connected Discord
voice channel** using the normal audio player. Tsuki must already be connected;
use `/tjoin` if necessary. `/tsayc` takes the same inline `text` argument and
sends a native Discord voice message in the chat channel where it was invoked.
No modal opens: type `/tsayvc hello, person` or `/tsayc hello, person`, then
submit from Discord's slash-command input. Each command uses only the selected
Molab or Colab TTS runtime (or fails visibly if Off/unavailable), with no
provider fallback. Both require Manage Channels and limit text to 280 characters.

## Molab manual notebook and custom Cloudflare domain

Molab is a manually started notebook runtime, like Colab, **not** an API
provider. There is no Molab-issued URL or API key. The separate, locally-managed
Cloudflare tunnel and DNS route are:

- Hostname: `molab-tts.tsukihime.dpdns.org`
- Tunnel: `tsuki-molab-qwen-custom`
- Cloudflare tunnel ID: `ce98db7a-1f14-4e56-a016-e5bd2f5676ea`
- Molab origin: `http://127.0.0.1:8100`

The hostname is baked into `discord-voice-bridge/index.js`; no
`MOLAB_VOICE_TTS_URL` or `MOLAB_VOICE_TTS_API_KEY` environment variables
exist. Colab uses its existing, different tunnel and hostname.

Before manually uploading the notebook to Molab, open the private Cloudflare
tunnel credential JSON on your PC at
`C:\Users\LENOVO\.cloudflared\ce98db7a-1f14-4e56-a016-e5bd2f5676ea.json`.
Copy its **full JSON contents** into
`CLOUDFLARE_TUNNEL_CREDENTIALS_JSON = r'''PASTE_PRIVATE_CLOUDFLARE_TUNNEL_JSON_HERE'''`
in `marimo_epoch0_service.py`, replacing the placeholder between the
triple quotes. The notebook validates the tunnel ID and writes the credential
privately in Molab's runtime for cloudflared. There is no separate JSON upload.
Do not share or commit the populated notebook. This is a Cloudflare credential,
not a Molab API key. If you leave the variable unfilled, TTS serves locally
but the custom domain tunnel does not start.
The Molab and Colab custom domains remain independent notebook endpoints.
Controls polls only the currently selected TTS provider. If that provider is
offline, voice-channel TTS stays unavailable until it returns; no other TTS
provider is tried automatically.

The VPS bridge reuses its existing `DISCORD_TOKEN`, `TEXT_CHANNEL_ID` and
`VOICE_CHANNEL_ID`. When the Molab health endpoint becomes ready, the VPS bot
posts text plus a native voice message in `TEXT_CHANNEL_ID`, currently
`1545789527556104393`. The voice channel setting is for spoken conversation,
not the startup chat announcement. The notebook receives no Discord secrets.

**Security:** the Molab `/tts` endpoint has no application API key. Anyone
with access to the public hostname may request synthesis unless you add
Cloudflare-side access restrictions compatible with the VPS.

Only **Discord voice-channel replies** use this duration-aware batching ladder:

1. C# handles the LLM, persona and memory with POST /api/voice/process,
   audio=false and molabVoice=true. Only this path can return up to 1600
   characters; regular C#/Colab and text-chat voice replies retain 280.
2. Split naturally at sentences/clauses; combine adjacent 1–8-word sentences
   until roughly 9–20 words or 180 characters. Keep medium/long units separate;
   every Molab request is at most 280 characters.
3. Generate the first batch alone for low time-to-first-audio. As it starts
   playing, dynamically start 1/2/3 subsequent jobs based on ready audio:
   under 3s / 3–8s / 8–15s. Over 15s, pause new work except the next
   required batch to protect against an underrun.
4. Record word count, character count, server-reported generation ms, network
   request ms, WAV/PCM audio duration, concurrent request count, measured
   real-time factor and playback gaps. A rolling RTF and characters-per-second
   estimate inform expected generation time for later batches.
5. Play every generated batch strictly in original order. At most three
   in-flight synthesis requests share Molab's ONE model; never send the
   combined batch path through Colab or text-chat voice.

**Discord text-chat voice messages, /tsayvc, /tsayc, and /play-tts** also honor
`TSUKI_TTS_PROVIDER`. Molab makes one direct `/tts` request; Colab uses the
existing C# `/api/voice/test-tts` route; Off does not synthesize. The
280-character single-request limit remains for chat and direct messages;
longer voice-channel Molab replies alone use duration-aware batching.
`/api/chat/discord` handles text and memory with `voice=false`; the bridge
then synthesizes through the selected provider. Text remains available if
the selected TTS provider is offline. Do not configure Molab in
TSUKI_QWEN_TTS_WORKERS: that C# worker pool affects other API consumers.

The three slots refer to requests to one Molab model, not three separately
loaded models. Actual 3-job GPU throughput and memory must be checked on Molab.
Do not commit credentials to the source or .env files.

## Strict TTS provider selection

`TSUKI_TTS_PROVIDER` is one of `molab`, `colab`, or `off`. The dashboard at
`controls.tsukihime.dpdns.org` owns this setting and recreates only the bridge
when it changes. There is **no automatic TTS fallback**:

- `molab`: the voice-channel path checks only the standalone Molab endpoint and
  uses the duration-aware Molab batching ladder. If Molab is unavailable, the
  turn stays without TTS; Colab is never tried.
- `colab`: the voice-channel path checks only the standalone Colab endpoint and
  uses the existing C#/Colab `process-binary` route. If Colab is unavailable,
  Molab is never tried.
- `off`: all Discord TTS is intentionally disabled; text chat still works.

Provider readiness is displayed in Controls, which polls only the selected
provider. The bridge no longer sends Molab-ready startup announcements or a
sample voice message. A failed attempted voice turn may still emit the generic
`Voice not available right now` status notice; it never claims a fallback was
attempted. STT fallback behavior is separate and unchanged.

## Add this bot to a server

Invite the existing bot from Discord Developer Portal > your app >
Installation / OAuth2, with `bot` and `applications.commands` scopes.
Grant View Channel, Send Messages, Attach Files, Read Message History,
Connect, Speak, Use Voice Activity, and Use Application Commands. Do not
grant Administrator. Enable Message Content Intent in Developer Portal >
Bot for the mention-triggered text channel. The server member invoking
`/t` must have Manage Channels; the bot itself does not need that permission.

This bridge currently serves one configured guild. When switching servers,
update `GUILD_ID`, `VOICE_CHANNEL_ID`, and `TEXT_CHANNEL_ID` on the VPS,
then recreate the bridge. Merely inviting it to another guild does not make
it start listening there.

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
