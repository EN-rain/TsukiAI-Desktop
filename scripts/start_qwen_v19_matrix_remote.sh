#!/usr/bin/env bash
set -euo pipefail

TEXT_URL="${1:?text URL is required}"
PROMPTS_URL="${2:?accent prompt URL is required}"
MANIFEST_URL="${3:?reference manifest URL is required}"

ROOT="/opt/tsuki-qwen3-tts/staging/final-all450-v19"
EVIDENCE="$ROOT/evidence"
TEXT_FILE="$EVIDENCE/tsuki_ref_text.txt"
PROMPTS_FILE="$EVIDENCE/english_accent_prompts.json"
MANIFEST_FILE="$EVIDENCE/supplied_reference_manifest.json"
RUNNER="$ROOT/evidence-runner.py"
EXPECTED_TEXT_SHA="bd3e849d943b80e75f62243c85c3d75dabe2716c2d1b12535b39960d9406949e"
EXPECTED_PROMPTS_SHA="9651ca36a7902b7e202f1738429e906359301465e2d609341eaf7751a1362e5d"
EXPECTED_MANIFEST_SHA="b15f45e481926094afd6e56f54b844fc76b13a3ac3c2b2b599035b098201e4fc"

test -x "$RUNNER"
mkdir -p "$EVIDENCE/raw"

curl -fsSL --retry 4 --retry-delay 3 --connect-timeout 30 --max-time 120 \
  "$TEXT_URL" -o "$TEXT_FILE.part"
mv "$TEXT_FILE.part" "$TEXT_FILE"
curl -fsSL --retry 4 --retry-delay 3 --connect-timeout 30 --max-time 120 \
  "$PROMPTS_URL" -o "$PROMPTS_FILE.part"
mv "$PROMPTS_FILE.part" "$PROMPTS_FILE"
curl -fsSL --retry 4 --retry-delay 3 --connect-timeout 30 --max-time 120 \
  "$MANIFEST_URL" -o "$MANIFEST_FILE.part"
mv "$MANIFEST_FILE.part" "$MANIFEST_FILE"

test "$(sha256sum "$TEXT_FILE" | awk '{print $1}')" = "$EXPECTED_TEXT_SHA"
test "$(sha256sum "$PROMPTS_FILE" | awk '{print $1}')" = "$EXPECTED_PROMPTS_SHA"
test "$(sha256sum "$MANIFEST_FILE" | awk '{print $1}')" = "$EXPECTED_MANIFEST_SHA"

if [[ -f "$EVIDENCE/matrix.pid" ]] && kill -0 "$(cat "$EVIDENCE/matrix.pid")" 2>/dev/null; then
  echo "matrix_already_running pid=$(cat "$EVIDENCE/matrix.pid")"
  exit 0
fi

rm -f "$EVIDENCE/matrix.pid" "$EVIDENCE/matrix.log" \
  "$EVIDENCE/reference_matrix.json" "$EVIDENCE/reference-matrix.zip"

nohup /opt/tsuki-qwen3-tts/venv/bin/python "$RUNNER" \
  --base-url http://127.0.0.1:8102 \
  --env-file "$ROOT/qwen-final.env" \
  --japanese-text-file "$TEXT_FILE" \
  --accent-prompts-file "$PROMPTS_FILE" \
  --reference-manifest "$MANIFEST_FILE" \
  --evidence-dir "$EVIDENCE" > "$EVIDENCE/matrix.log" 2>&1 < /dev/null &
pid=$!
printf '%s\n' "$pid" > "$EVIDENCE/matrix.pid"
echo "matrix_started pid=$pid"
