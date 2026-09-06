# discord-voice-bridge: Node service that joins a Discord voice channel and
# drives the TsukiAI API for the STT -> LLM -> TTS pipeline.
FROM node:22-slim

WORKDIR /app

# Build tools for the native audio deps (@discordjs/opus, sodium-native) if no
# prebuilt binary matches; usually a no-op on debian.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 make g++ ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY package.json package-lock.json ./
RUN npm ci --omit=dev --no-audit --no-fund

# Known-good static ffmpeg (ffmpeg-static's opus encoder produces static
# artifacts in this container). FFMPEG_PATH points the bridge at it.
COPY --from=mwader/static-ffmpeg:6.1 /ffmpeg /usr/local/bin/ffmpeg
ENV FFMPEG_PATH=/usr/local/bin/ffmpeg

COPY . .

# Secrets come from the compose env_file, never from the image.
CMD ["node", "index.js"]
