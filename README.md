# TsukiAI Voice Chat

TsukiAI is a .NET 8 + WPF desktop voice assistant with a local HTTP API, multi-provider LLM support, semantic memory, and Discord voice integration through a Node bridge.

## Core Capabilities

- Voice pipeline: STT -> LLM -> TTS
- Local + remote inference support
- Multi-provider model routing configuration (Cerebras, Groq, Gemini, GitHub Models, Mistral, etc.)
- Semantic memory integration (Chroma-backed service)
- Discord voice bridge (`discord-voice-bridge/`)
- Resilience improvements:
  - retry + circuit-breaker on key outbound HTTP calls
  - correlation IDs across request flow
  - bounded background queues for memory write-back

## Tech Stack

- .NET 8
- WPF
- ASP.NET Core minimal host (local API for bridge and tooling)
- Node.js bridge for Discord voice I/O

## Repository Layout

```text
TsukiAI.Core/                Core services and models
TsukiAI.Core.Tests/          Core tests
TsukiAI.VoiceChat/           WPF app and local HTTP API
TsukiAI.VoiceChat.Tests/     Voice/runtime tests
discord-voice-bridge/        Discord voice sidecar (Node.js)
scripts/                     Utility scripts (including semantic memory helpers)
```

## Prerequisites

- Windows 10/11
- .NET SDK 8.0+
- Node.js 18+ (for Discord bridge)

Optional:
- VOICEVOX runtime for local TTS
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
  - `TSUKI_ASSEMBLYAI_API_KEY` (if used)
  - `TSUKI_DEEPL_API_KEY` (optional)

3. Run app:

```bash
dotnet run --project TsukiAI.VoiceChat/TsukiAI.VoiceChat.csproj
```

## Testing

```bash
dotnet test TsukiAI.sln
```

## Discord Voice Bridge

See [discord-voice-bridge/README.md](discord-voice-bridge/README.md) for bridge setup and `.env` keys.

## Troubleshooting

- Build fails:
  - ensure .NET 8 SDK is installed
  - run `dotnet restore` then `dotnet build`
- Bridge has no audio:
  - verify bot permissions and voice channel IDs
  - verify `CSHARP_API_URL` in bridge `.env`
- TTS/STT issues:
  - check configured provider keys and endpoint URLs
  - validate local API is reachable on `http://localhost:5000`
