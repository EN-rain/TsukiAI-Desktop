#!/bin/sh
set -u

set -a
. /opt/tsuki-qwen3-tts/qwen.env
set +a

i=1
while [ "$i" -le 12 ]; do
    body="$(curl -sS --max-time 20 \
        -H "X-Api-Key: $QWEN_TTS_API_KEY" \
        http://127.0.0.1:8100/health 2>/dev/null || true)"
    if printf '%s' "$body" | grep -q ready; then
        printf '%s\n' "$body"
        exit 0
    fi
    printf 'poll=%s service=%s\n' "$i" "$(systemctl is-active qwen3-tts.service || true)"
    i=$((i + 1))
    sleep 10
done

journalctl --no-pager -u qwen3-tts.service -n 80
exit 1
