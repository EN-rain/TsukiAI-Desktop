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
- OpenVoice V2 CPU service for English and Japanese character TTS

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
  - `TSUKI_ASSEMBLYAI_API_KEY` (if used)
  - `TSUKI_DEEPL_API_KEY` (optional)

3. Run app:

```bash
dotnet run --project TsukiAI.VoiceChat/TsukiAI.VoiceChat.csproj
```

For the Azure deployment, provision the private OpenVoice V2 service described
in [voice/openvoice-api/README.md](voice/openvoice-api/README.md), then set
`TSUKI_OPENVOICE_URL` and `TSUKI_OPENVOICE_API_KEY` in the root `.env` and run:

```bash
docker compose up -d --build
```

The OpenVoice reference audio is processed once on the VM into a cached speaker
embedding. Runtime requests contain only text and language.

## Testing

```bash
dotnet build TsukiAI.sln -c Release
dotnet test TsukiAI.Core.Tests/TsukiAI.Core.Tests.csproj
cd discord-voice-bridge && npm test
cd ../web && npm run build
```

The .NET test project covers the OpenVoice synthesis response contract.

## Discord Voice Bridge

For the Azure OpenVoice deployment, see
[voice/openvoice-api/README.md](voice/openvoice-api/README.md) and
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
  - verify the private OpenVoice `/health` endpoint and API key
  - validate local API is reachable on `http://localhost:5000`
