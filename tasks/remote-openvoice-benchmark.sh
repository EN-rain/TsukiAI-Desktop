#!/usr/bin/env bash
set -eu

. /opt/openvoice/openvoice.env

request_case() {
  label="$1"
  actual_chars="$2"
  payload="$3"
  output="/tmp/openvoice-benchmark-${label}.wav"
  started_ns="$(date +%s%N)"

  curl --fail --silent --show-error \
    -H "X-Api-Key: ${OPENVOICE_API_KEY}" \
    -H 'Content-Type: application/json' \
    -X POST http://127.0.0.1:8000/tts \
    --data-binary "${payload}" \
    --output "${output}"

  finished_ns="$(date +%s%N)"
  generation_ms="$(( (finished_ns - started_ns) / 1000000 ))"
  audio_seconds="$(ffprobe -v error -show_entries format=duration \
    -of default=noprint_wrappers=1:nokey=1 "${output}")"
  rtf="$(awk -v generation_ms="${generation_ms}" -v audio_seconds="${audio_seconds}" \
    'BEGIN { if (audio_seconds > 0) printf "%.3f", generation_ms / 1000 / audio_seconds; else print "nan" }')"
  pid="$(systemctl show -p MainPID --value openvoice-api.service)"
  rss_kb="0"
  if [ -r "/proc/${pid}/status" ]; then
    rss_kb="$(awk '/^VmRSS:/ {print $2}' "/proc/${pid}/status")"
  fi
  bytes="$(stat -c '%s' "${output}")"
  printf 'case=%s actual_chars=%s generation_ms=%s audio_seconds=%s rtf=%s rss_kb=%s bytes=%s\n' \
    "${label}" "${actual_chars}" "${generation_ms}" "${audio_seconds}" "${rtf}" "${rss_kb}" "${bytes}"
  rm -f "${output}"
}

request_case en_50 64 '{"text":"Hello, where have you been? I have been waiting for you all day.","language":"EN","voice":"tsuki"}'
request_case en_100 120 '{"text":"Hello, where have you been? I have been waiting for you all day. The weather is calm, but we should leave before sunset.","language":"EN","voice":"tsuki"}'
request_case en_250 252 '{"text":"Hello, where have you been? I have been waiting for you all day. The weather is calm, but we should leave before sunset. Bring your notes, take the western road, and call me if anything changes. We have enough time to prepare, but we must not waste it.","language":"EN","voice":"tsuki"}'
request_case ja_50 18 '{"text":"\u3084\u3063\u3068\u6765\u305f\uff01\u305a\u3063\u3068\u5f85\u3063\u3066\u305f\u3093\u3060\u304b\u3089\u3002","language":"JA","voice":"tsuki"}'
request_case ja_100 35 '{"text":"\u3084\u3063\u3068\u6765\u305f\uff01\u305a\u3063\u3068\u5f85\u3063\u3066\u305f\u3093\u3060\u304b\u3089\u3002\u4eca\u65e5\u306f\u5929\u6c17\u3082\u3044\u3044\u3057\u3001\u4e00\u7dd2\u306b\u884c\u3053\u3046\u3002","language":"JA","voice":"tsuki"}'
request_case ja_250 82 '{"text":"\u3084\u3063\u3068\u6765\u305f\uff01\u305a\u3063\u3068\u5f85\u3063\u3066\u305f\u3093\u3060\u304b\u3089\u3002\u4eca\u65e5\u306f\u5929\u6c17\u3082\u3044\u3044\u3057\u3001\u4e00\u7dd2\u306b\u884c\u3053\u3046\u3002\u6e96\u5099\u3092\u3057\u3066\u3001\u897f\u306e\u9053\u3092\u901a\u3063\u3066\u3001\u4f55\u304b\u3042\u3063\u305f\u3089\u3059\u3050\u6559\u3048\u3066\u306d\u3002\u307e\u3060\u6642\u9593\u306f\u3042\u308b\u3051\u3069\u3001\u7126\u3089\u306a\u3044\u3068\u3044\u3051\u306a\u3044\u3002","language":"JA","voice":"tsuki"}'
