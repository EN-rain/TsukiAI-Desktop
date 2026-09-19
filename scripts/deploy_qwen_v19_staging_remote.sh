#!/usr/bin/env bash
set -euo pipefail

# Deploy the exact v19 Kaggle artifact into a new staging tree.  The three
# arguments are short-lived read-only SAS URLs supplied by the operator.
ARTIFACT_URL="${1:?artifact URL is required}"
RUNTIME_URL="${2:?runtime URL is required}"
RUNNER_URL="${3:?runner URL is required}"

ROOT="/opt/tsuki-qwen3-tts/staging/final-all450-v19"
ARCHIVE="$ROOT/checkpoint-artifact.zip"
MANIFEST="$ROOT/artifact/ARTIFACT_MANIFEST.json"
MODEL="$ROOT/artifact/tsukiai_tsukihime_qwen06b/checkpoints/checkpoint-epoch-0/model.safetensors"
EXPECTED_ARCHIVE_SHA="d5e09d12a40d5b3a587478f63f2e5c79dcd74ab25f651386dbe37901c60cf66a"
EXPECTED_MODEL_SHA="a2005807a1e73b09ae75957bd26dd4e424d9558eb6b400dd36a02d6a2dcc2c00"
EXPECTED_MANIFEST_SHA="2807fb996c73c7d9149fe0b1992be47255f218731e1bc07d17fa2f9d5a7a2d35"
EXPECTED_RUNTIME_SHA="95dc95246c1928b895fb6bb053989494d0fd2aa2be9d6c8292bafbd9e29c4faf"
EXPECTED_RUNNER_SHA="cf935cc5cc7056101eedd18ca53d4cdebd5beb900d304f5de37221f5170186e2"
CHECKPOINT_ID="tsuki-qwen3-tts-final-all450-alignment-pr278-lr2e-6-epoch1-seed42:checkpoint-epoch-0:0194ed64608ce71a96b38c7c84927ff966b9863d01f7f74996b1785c1ac3ed38"
UNIT="/etc/systemd/system/qwen3-tts-v19-staging.service"

if [[ -e "$UNIT" ]]; then
  echo "v19 systemd unit already exists; refusing to overwrite"
  exit 20
fi

mkdir -p "$ROOT"

if [[ ! -f "$ARCHIVE" ]]; then
  curl -fsSL --retry 4 --retry-delay 3 --connect-timeout 30 --max-time 3600 \
    "$ARTIFACT_URL" -o "$ARCHIVE.part"
  mv "$ARCHIVE.part" "$ARCHIVE"
fi
actual_archive_sha="$(sha256sum "$ARCHIVE" | awk '{print $1}')"
[[ "$actual_archive_sha" == "$EXPECTED_ARCHIVE_SHA" ]]

if [[ ! -f "$MANIFEST" ]]; then
  mkdir -p "$ROOT/artifact"
  python3 - "$ARCHIVE" "$ROOT/artifact" <<'PY'
import sys
from pathlib import Path
from zipfile import ZipFile

archive = Path(sys.argv[1])
destination = Path(sys.argv[2]).resolve()
with ZipFile(archive) as zf:
    for member in zf.infolist():
        target = (destination / member.filename).resolve()
        if target != destination and destination not in target.parents:
            raise SystemExit(f"unsafe archive member: {member.filename}")
    zf.extractall(destination)
PY
fi

actual_model_sha="$(sha256sum "$MODEL" | awk '{print $1}')"
actual_manifest_sha="$(sha256sum "$MANIFEST" | awk '{print $1}')"
[[ "$actual_model_sha" == "$EXPECTED_MODEL_SHA" ]]
[[ "$actual_manifest_sha" == "$EXPECTED_MANIFEST_SHA" ]]

RUNTIME_ZIP="$ROOT/runtime-bundle-posix.zip"
if [[ ! -f "$RUNTIME_ZIP" ]]; then
  curl -fsSL --retry 4 --retry-delay 3 --connect-timeout 30 --max-time 120 \
    "$RUNTIME_URL" -o "$RUNTIME_ZIP.part"
  mv "$RUNTIME_ZIP.part" "$RUNTIME_ZIP"
fi
actual_runtime_sha="$(sha256sum "$RUNTIME_ZIP" | awk '{print $1}')"
[[ "$actual_runtime_sha" == "$EXPECTED_RUNTIME_SHA" ]]
if [[ ! -f "$ROOT/bundle-fixed/server/api.py" ]]; then
  mkdir -p "$ROOT/bundle-fixed"
  python3 - "$RUNTIME_ZIP" "$ROOT/bundle-fixed" <<'PY'
import sys
from pathlib import Path
from zipfile import ZipFile

archive = Path(sys.argv[1])
destination = Path(sys.argv[2]).resolve()
with ZipFile(archive) as zf:
    for member in zf.infolist():
        target = (destination / member.filename).resolve()
        if target != destination and destination not in target.parents:
            raise SystemExit(f"unsafe archive member: {member.filename}")
    zf.extractall(destination)
PY
fi
test -f "$ROOT/bundle-fixed/server/api.py"

RUNNER="$ROOT/evidence-runner.py"
if [[ ! -f "$RUNNER" ]]; then
  curl -fsSL --retry 4 --retry-delay 3 --connect-timeout 30 --max-time 120 \
    "$RUNNER_URL" -o "$RUNNER.part"
  mv "$RUNNER.part" "$RUNNER"
fi
actual_runner_sha="$(sha256sum "$RUNNER" | awk '{print $1}')"
[[ "$actual_runner_sha" == "$EXPECTED_RUNNER_SHA" ]]
chmod 0755 "$RUNNER"

api_key=""
for source in \
  /opt/tsuki-qwen3-tts/staging/final-all450-v18/qwen-final.env \
  /opt/tsuki-qwen3-tts/qwen.env; do
  if [[ -z "$api_key" && -f "$source" ]]; then
    api_key="$(sed -n 's/^QWEN_TTS_API_KEY=//p' "$source" | head -n 1)"
  fi
done
[[ -n "$api_key" ]]

ENV_FILE="$ROOT/qwen-final.env"
umask 077
{
  printf '%s\n' "QWEN_TTS_API_KEY=$api_key"
  printf '%s\n' "QWEN_TTS_MODE=trained-custom"
  printf '%s\n' "QWEN_TTS_DEPLOYMENT_STAGE=staging"
  printf '%s\n' "QWEN_TTS_APPROVAL_STATUS=APPROVED_FOR_STAGING"
  printf '%s\n' "QWEN_TTS_MODEL=$ROOT/artifact/tsukiai_tsukihime_qwen06b/checkpoints/checkpoint-epoch-0"
  printf '%s\n' "QWEN_TTS_APPROVED_MANIFEST=$MANIFEST"
  printf '%s\n' "QWEN_TTS_CHECKPOINT_ID=$CHECKPOINT_ID"
  printf '%s\n' "QWEN_TTS_GENERATION_VERSION=final-all450-v19-kaggle-gpu-v1"
  printf '%s\n' "QWEN_TTS_SPEAKER=tsukihime"
  printf '%s\n' "QWEN_TTS_DEVICE=cpu"
  printf '%s\n' "QWEN_TTS_ATTN_IMPLEMENTATION=eager"
  printf '%s\n' "QWEN_TTS_THREADS=4"
  printf '%s\n' "QWEN_TTS_INTRA_OP_THREADS=4"
  printf '%s\n' "QWEN_TTS_INTER_OP_THREADS=4"
  printf '%s\n' "QWEN_TTS_MAX_NEW_TOKENS=2048"
  printf '%s\n' "QWEN_TTS_CUSTOM_MAX_NEW_TOKENS=2048"
  printf '%s\n' "QWEN_TTS_MAX_TEXT_CHARS=280"
  printf '%s\n' "QWEN_TTS_PORT=8102"
} > "$ENV_FILE"
chmod 0600 "$ENV_FILE"

mkdir -p "$ROOT/numba-cache" "$ROOT/evidence/raw"
chown -R azureuser:azureuser "$ROOT/bundle-fixed" "$ROOT/numba-cache" "$ROOT/evidence" "$RUNNER"

cat > "$UNIT" <<EOF
[Unit]
Description=Tsuki Qwen3-TTS v19 trained custom candidate (staging only)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=azureuser
Group=azureuser
WorkingDirectory=$ROOT/bundle-fixed/server
EnvironmentFile=$ENV_FILE
Environment=NUMBA_CACHE_DIR=$ROOT/numba-cache
Environment=PYTHONUNBUFFERED=1
ExecStart=/opt/tsuki-qwen3-tts/venv/bin/uvicorn api:app --host 127.0.0.1 --port 8102 --workers 1
Restart=on-failure
RestartSec=10
TimeoutStartSec=1800
TimeoutStopSec=30
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/opt/tsuki-qwen3-tts /tmp

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl start qwen3-tts-v19-staging.service
echo "v19_tree=$ROOT"
echo "archive_sha256=$actual_archive_sha"
echo "model_sha256=$actual_model_sha"
echo "manifest_sha256=$actual_manifest_sha"
echo "runtime_bundle_sha256=$actual_runtime_sha"
echo "runner_sha256=$actual_runner_sha"
echo "v19_service=$(systemctl is-active qwen3-tts-v19-staging.service || true)"
