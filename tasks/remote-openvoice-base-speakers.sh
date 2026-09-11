#!/usr/bin/env bash
set -eu

. /opt/openvoice/openvoice.env

base_dir="${OPENVOICE_BASE_SPEAKER_DIR:?OPENVOICE_BASE_SPEAKER_DIR is required}"
install -d -o azureuser -g azureuser -m 750 "${base_dir}"

download_embedding() {
  local filename="$1"
  local url="$2"
  local target="${base_dir}/${filename}"
  local temp_file
  temp_file="$(mktemp "${base_dir}/.${filename}.XXXXXX")"
  trap 'rm -f "${temp_file:-}"' EXIT

  curl --fail --location --retry 3 --silent --show-error \
    --output "${temp_file}" "${url}"

  /opt/openvoice/.venv/bin/python - "${temp_file}" "${filename}" <<'PY'
import sys

import torch

path, label = sys.argv[1:]
try:
    value = torch.load(path, map_location="cpu", weights_only=True)
except TypeError:
    value = torch.load(path, map_location="cpu")
if not hasattr(value, "shape") or value.ndim != 3:
    raise SystemExit(f"{label}: unexpected speaker embedding")
print(f"validated {label} shape={tuple(value.shape)}")
PY

  chown azureuser:azureuser "${temp_file}"
  chmod 644 "${temp_file}"
  mv -f "${temp_file}" "${target}"
  trap - EXIT
  printf 'installed %s sha256=' "${target}"
  sha256sum "${target}" | cut -d' ' -f1
}

download_embedding \
  en-au.pth \
  'https://huggingface.co/myshell-ai/OpenVoiceV2/resolve/main/base_speakers/ses/en-au.pth?download=true'
download_embedding \
  jp.pth \
  'https://huggingface.co/myshell-ai/OpenVoiceV2/resolve/main/base_speakers/ses/jp.pth?download=true'
