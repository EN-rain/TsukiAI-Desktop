# TsukiAI Discord Voice Bridge

Node.js sidecar that handles Discord voice I/O and bridges to your C# application for STT/LLM/TTS processing.

## Why This Exists

Discord voice in .NET libraries has compatibility issues with Discord's new DAVE/E2EE requirements. Node.js has the most mature and battle-tested Discord voice implementation.

## Architecture

```
Discord Voice Channel
    ↓ (user speaks)
Node.js Bot (this)
    ↓ (PCM audio)
C# App HTTP API (/api/voice/stt)
    ↓ (transcribed text)
C# App (Whisper STT)
    ↓ (text)
C# App (LLM processing)
    ↓ (response text)
C# App (VoiceVox TTS)
    ↓ (audio bytes)
Node.js Bot (this)
    ↓ (plays audio)
Discord Voice Channel
```

## Setup

### Quick Setup (Recommended)

1. Open TsukiAI Desktop app
2. Go to Settings → Voice Chat Settings
3. Enter:
   - Discord Bot Token
   - Guild ID (your Discord server ID)
   - Voice Channel ID
4. Click Save

The app automatically creates/updates the `.env` file!

### Manual Setup

If you prefer to edit `.env` directly:

1. Create `discord-voice-bridge/.env`:
   ```env
   DISCORD_TOKEN=your_bot_token_here
   GUILD_ID=your_server_id_here
   VOICE_CHANNEL_ID=your_channel_id_here
   CSHARP_API_URL=http://localhost:5000
   ```

2. See [GET_DISCORD_TOKEN.md](GET_DISCORD_TOKEN.md) for detailed instructions

### Install Dependencies

```bash
cd discord-voice-bridge
npm install
```

### Run the Bridge

```bash
npm start
```

Or use the batch file:
```bash
start.bat
```

## C# API Endpoints Required

Your C# application needs to expose these HTTP endpoints:

### POST /api/voice/stt

Receives audio for speech-to-text transcription.

**Request**:
- Headers: `Content-Type: application/octet-stream`, `X-User-Id: <discord_user_id>`
- Body: Raw PCM audio bytes (48kHz, stereo)

**Response**:
```json
{
  "text": "transcribed text here"
}
```

### POST /api/voice/process

Receives text, processes with LLM, generates TTS, returns audio.

**Request**:
```json
{
  "userId": "discord_user_id",
  "text": "user message"
}
```

**Response**:
- Content-Type: `application/octet-stream`
- Body: Raw PCM audio bytes (48kHz, stereo) or WAV file

## Audio Format

Discord uses:
- Sample Rate: 48kHz
- Channels: 2 (stereo)
- Format: PCM signed 16-bit little-endian

Your C# app should:
- Accept PCM audio in this format for STT
- Return PCM audio in this format for TTS playback

## Testing

1. Start your C# application (ensure API endpoints are running)
2. Start this Node.js bridge: `npm start`
3. Bot should join Discord voice channel
4. Speak in the voice channel
5. Bot should transcribe, process, and respond with TTS

## Troubleshooting

### Bot doesn't join voice
- Check `DISCORD_TOKEN` is valid
- Check `GUILD_ID` and `VOICE_CHANNEL_ID` are correct
- Ensure bot has "Connect" and "Speak" permissions

### No audio received from users
- Ensure `selfDeaf: false` in `joinVoiceChannel`
- Check Discord voice region (some regions may have issues)

### TTS doesn't play
- Check audio format from C# app matches Discord requirements
- Verify `CSHARP_API_URL` is correct
- Check C# app logs for errors

### Connection drops
- Discord voice connections can be unstable
- Bot will attempt to reconnect automatically
- Check network/firewall settings

## Development

Run with auto-reload:
```bash
npm run dev
```

## Dependencies

- `discord.js` - Discord API client
- `@discordjs/voice` - Discord voice support
- `@discordjs/opus` - Opus audio codec
- `prism-media` - Audio processing
- `libsodium-wrappers` - Encryption for voice
- `axios` - HTTP client for C# API

## License

Same as TsukiAI project
