#!/usr/bin/env bash
set -euo pipefail

BASE=/opt/tsuki-qwen-zero-shot
VENV="$BASE/venv"
OMNI_REF="${VLLM_OMNI_REF:-v0.28.0}"
SOURCE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ "$(id -u)" -ne 0 ]]; then
  echo "Run this installer as root." >&2
  exit 1
fi

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y --no-install-recommends \
  build-essential ca-certificates curl ffmpeg git libsndfile1 python3-dev python3-venv

if ! id qwen >/dev/null 2>&1; then
  useradd --system --home-dir "$BASE" --shell /usr/sbin/nologin qwen
fi

install -d -o qwen -g qwen "$BASE" "$BASE/adapter" "$BASE/reference" \
  "$BASE/voices" "$BASE/huggingface"

if [[ ! -x "$VENV/bin/python" ]]; then
  python3 -m venv "$VENV"
fi
"$VENV/bin/python" -m pip install --upgrade pip setuptools wheel
"$VENV/bin/python" -m pip install 'uv>=0.8,<1'
"$VENV/bin/uv" pip install --python "$VENV/bin/python" \
  'vllm==0.28.0' --torch-backend=auto
"$VENV/bin/uv" pip install --python "$VENV/bin/python" \
  'vllm-omni==0.28.0' \
  'fastapi>=0.115,<1' 'httpx>=0.28,<1' 'uvicorn[standard]>=0.34,<1'

if [[ ! -d "$BASE/vllm-omni/.git" ]]; then
  rm -rf "$BASE/vllm-omni.tmp"
  git clone --depth 1 --branch "$OMNI_REF" \
    https://github.com/vllm-project/vllm-omni.git "$BASE/vllm-omni.tmp"
  mv "$BASE/vllm-omni.tmp" "$BASE/vllm-omni"
else
  git -C "$BASE/vllm-omni" fetch --depth 1 origin "$OMNI_REF"
  git -C "$BASE/vllm-omni" checkout --detach FETCH_HEAD
fi

cp "$BASE/vllm-omni/vllm_omni/deploy/qwen3_tts.yaml" \
  "$BASE/vllm-omni/vllm_omni/deploy/qwen3_tts_ibm.yaml"
python3 - "$BASE/vllm-omni/vllm_omni/deploy/qwen3_tts_ibm.yaml" "$BASE/voices" <<'PY'
from pathlib import Path
import sys

config_path = Path(sys.argv[1])
voice_dir = sys.argv[2]
text = config_path.read_text(encoding="utf-8")
needle = "async_chunk: true\n"
if "custom_voice_dir:" not in text:
    text = text.replace(needle, needle + f"custom_voice_dir: {voice_dir}\n", 1)
# The L4 has enough memory for the Base model, but these limits avoid reserving
# the high-throughput default cache for a single-voice, latency-sensitive VM.
text = text.replace("gpu_memory_utilization: 0.3", "gpu_memory_utilization: 0.20")
text = text.replace("max_num_seqs: 64", "max_num_seqs: 4")
config_path.write_text(text, encoding="utf-8")
PY

install -o qwen -g qwen -m 0644 "$SOURCE_DIR/adapter.py" "$BASE/adapter/adapter.py"
install -o root -g root -m 0644 \
  "$SOURCE_DIR/systemd/qwen3-tts-zero-shot.service" \
  /etc/systemd/system/qwen3-tts-zero-shot.service
install -o root -g root -m 0644 \
  "$SOURCE_DIR/systemd/qwen3-tts-zero-shot-adapter.service" \
  /etc/systemd/system/qwen3-tts-zero-shot-adapter.service

install -d -o root -g root -m 0750 /etc/tsukis-bedroom
if [[ ! -e /etc/tsukis-bedroom/qwen-zero-shot.env ]]; then
  install -o root -g root -m 0600 /dev/null /etc/tsukis-bedroom/qwen-zero-shot.env
fi

cat > "$BASE/runtime-versions.txt" <<EOF
vllm_omni_ref=$(git -C "$BASE/vllm-omni" rev-parse HEAD)
vllm_version=$($VENV/bin/python -c 'import vllm; print(vllm.__version__)')
vllm_omni_version=$($VENV/bin/python -c 'import importlib.metadata; print(importlib.metadata.version("vllm-omni"))')
python_version=$($VENV/bin/python --version)
EOF
if command -v nvidia-smi >/dev/null 2>&1; then
  nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader \
    > "$BASE/gpu-identity.txt"
fi
chown qwen:qwen "$BASE/runtime-versions.txt" "$BASE/gpu-identity.txt" 2>/dev/null || true

systemctl daemon-reload
systemctl enable qwen3-tts-zero-shot.service qwen3-tts-zero-shot-adapter.service
echo "IBM Qwen3-TTS zero-shot runtime installed; reference/profile and API key are still required before startup."
