#!/usr/bin/env bash
set -eu

. /opt/openvoice/openvoice.env

unauth_code="$(curl --silent --output /tmp/openvoice-unauth.out \
  --write-out '%{http_code}' \
  -X POST http://127.0.0.1:8000/tts \
  -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{"text":"Hello","language":"EN"}
JSON
)"
test "${unauth_code}" = 401

voices="$(curl --fail --silent \
  -H "X-Api-Key: ${OPENVOICE_API_KEY}" \
  http://127.0.0.1:8000/voices)"
printf 'unauth_code=%s voices=%s\n' "${unauth_code}" "${voices}"

curl --fail --silent --show-error \
  -H "X-Api-Key: ${OPENVOICE_API_KEY}" \
  -H 'Content-Type: application/json' \
  -X POST http://127.0.0.1:8000/tts \
  --data-binary @- \
  --output /tmp/openvoice-test-en.wav <<'JSON'
{"text":"Hello, where are you?","language":"EN","voice":"tsuki"}
JSON

stat -c 'en_bytes=%s' /tmp/openvoice-test-en.wav
sha256sum /tmp/openvoice-test-en.wav
ffprobe -v error \
  -show_entries format=duration:stream=sample_rate,channels,codec_name \
  -of default=noprint_wrappers=1 /tmp/openvoice-test-en.wav

curl --fail --silent --show-error \
  -H "X-Api-Key: ${OPENVOICE_API_KEY}" \
  -H 'Content-Type: application/json' \
  -X POST http://127.0.0.1:8000/tts \
  --data-binary @- \
  --output /tmp/openvoice-test-ja.wav <<'JSON'
{"text":"やっと来た！ずっと待ってたんだから。","language":"JA","voice":"tsuki"}
JSON

stat -c 'ja_bytes=%s' /tmp/openvoice-test-ja.wav
sha256sum /tmp/openvoice-test-ja.wav
ffprobe -v error \
  -show_entries format=duration:stream=sample_rate,channels,codec_name \
  -of default=noprint_wrappers=1 /tmp/openvoice-test-ja.wav

rm -f /tmp/openvoice-unauth.out /tmp/openvoice-test-en.wav /tmp/openvoice-test-ja.wav
