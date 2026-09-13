# TsukiAI Voice Chat

TsukiAI is a .NET 8 + WPF desktop voice assistant with a local HTTP API, multi-provider LLM support, semantic memory, and Discord voice integration through a Node bridge.

## Core Capabilities

- Voice pipeline: STT -> LLM -> TTS
- Local + remote inference support
- Multi-provider model routing configuration (Cerebras, Groq, Gemini, GitHub Models, Mistral, etc.)
- Semantic memory integration (Supermemory or Chroma fallback)
- Discord voice bridge (`discord-voice-bridge/`)
- Resilience improvements:
  - retry + circuit-breaker on key outbound HTTP calls
  - correlation IDs across request flow
  - bounded background queues for memory write-back
  - file-backed Groq STT key rotation for Discord, API, and desktop microphone paths

## Tech Stack

- .NET 8
- WPF
- ASP.NET Core minimal host (local API for bridge and tooling)
- Node.js bridge for Discord voice I/O
- Qwen3-TTS 0.6B CPU service for full-ICL English and Japanese character TTS

## Repository Layout

```text
TsukiAI.Core/                Core services and models
TsukiAI.VoiceChat/           WPF app and local HTTP API
discord-voice-bridge/        Discord voice sidecar (Node.js)
scripts/                     Utility scripts (including semantic memory helpers)
```

## Prerequisites

- Windows 10/11
- .NET SDK 8.0+
- Node.js 18+ (for Discord bridge)

Optional:
- API keys for cloud providers / cloud STT / translation

## Setup

1. Restore/build:

```bash
dotnet build TsukiAI.sln
```

2. Configure environment:

- Copy `.env.example` to `.env` at repository root.
- Fill required values (examples):
  - `TSUKI_REMOTE_INFERENCE_URL`
  - `TSUKI_REMOTE_INFERENCE_API_KEY`
  - `TSUKI_ASSEMBLYAI_API_KEY` (C#-side fallback, if used)
  - `TSUKI_DEEPL_API_KEY` (optional)

For Groq rotation, set `TSUKI_GROQ_API_KEYS_FILE` or `GROQ_KEYS_HOST_PATH` to
a newline-separated key file outside Git. Discord/API/desktop STT and LLM chat
all rotate the same configured pool when a key is rejected or rate-limited.

For long-term memory, set `TSUKI_SEMANTIC_MEMORY_ENABLED=true`, then set
`TSUKI_SUPERMEMORY_API_KEY` for the Discord/API process and
`TSUKI_DESKTOP_SUPERMEMORY_API_KEY` separately for the desktop app. Discord
text and Discord voice use the same per-user memory container; desktop voice
uses a separate desktop container. Empty keys preserve the Chroma/local
fallback.

3. Run app:

```bash
dotnet run --project TsukiAI.VoiceChat/TsukiAI.VoiceChat.csproj
```

For the Azure deployment, provision the private Qwen3-TTS service described
in [voice/qwen3-tts/README.md](voice/qwen3-tts/README.md), then set
`TSUKI_QWEN_TTS_URL` and `TSUKI_QWEN_TTS_API_KEY` in the root `.env` and run:

```bash
docker compose up -d --build
```

The Japanese Tsuki reference is processed once on the VM into a cached full-ICL
voice prompt. Runtime requests contain only text and target language; no
training or fine-tuning is used.

The Discord voice bridge can use the realtime path documented in
`discord-voice-bridge/README.md`: Discord PCM -> AssemblyAI Streaming v3 ->
TsukiAI LLM -> private Qwen3-TTS -> Discord playback. AssemblyAI is enabled
only while the bot is connected and at least one human is in the voice channel;
its external key file is rotated without committing secrets.

Normal Discord text chat goes through `/api/chat/discord`; the bridge sends the
Discord user ID so text and voice memory are synchronized for that user.

## Testing

```bash
dotnet build TsukiAI.sln -c Release
dotnet test TsukiAI.Core.Tests/TsukiAI.Core.Tests.csproj
cd discord-voice-bridge && npm test
cd ../web && npm run build
```

The .NET test project covers the Qwen3-TTS synthesis response contract.

## Discord Voice Bridge

For the Azure Qwen3-TTS deployment, see
[voice/qwen3-tts/README.md](voice/qwen3-tts/README.md) and
[tasks/plan.md](tasks/plan.md).

See [discord-voice-bridge/README.md](discord-voice-bridge/README.md) for bridge setup and `.env` keys.

## Troubleshooting

- Build fails:
  - ensure .NET 8 SDK is installed
  - run `dotnet restore` then `dotnet build`
- Bridge has no audio:
  - verify bot permissions and voice channel IDs
  - verify `CSHARP_API_URL` in bridge `.env`
- TTS/STT issues:
  - verify the private Qwen3-TTS `/health` endpoint and API key
  - validate local API is reachable on `http://localhost:5000`
