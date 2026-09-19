#!/bin/sh
set -u

E=/opt/tsuki-qwen3-tts/staging/final-all450-v19/evidence
if test -f "$E/matrix.pid" && kill -0 "$(cat "$E/matrix.pid")" 2>/dev/null; then
    echo matrix=running
else
    echo matrix=stopped
fi
echo "wav_count=$(find "$E/raw" -type f -name '*.wav' | wc -l)"
if test -f "$E/matrix.log"; then
    tail -20 "$E/matrix.log"
fi
