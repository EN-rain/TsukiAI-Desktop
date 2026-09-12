#!/usr/bin/env bash
set -eu

api_key="$(openssl rand -hex 32)"

install -o azureuser -g azureuser -m 600 /dev/null /opt/openvoice/openvoice.env
install -d -o azureuser -g azureuser -m 750 \
  /opt/openvoice/cache/huggingface \
  /opt/openvoice/cache/nltk \
  /opt/openvoice/models/checkpoints_v2/base_speakers/ses \
  /tmp/numba-cache
printf '%s\n' \
  "OPENVOICE_API_KEY=${api_key}" \
  'OPENVOICE_ROOT=/opt/openvoice' \
  'OPENVOICE_MODEL_DIR=/opt/openvoice/models/checkpoints_v2' \
  'OPENVOICE_VOICE_REGISTRY=/opt/openvoice/voice_registry.json' \
  'OPENVOICE_REFERENCE_WAV=/opt/openvoice/voices/references/tsuki.wav' \
  'OPENVOICE_EMBEDDING_DIR=/opt/openvoice/voices/embeddings' \
  'OPENVOICE_BASE_SPEAKER_DIR=/opt/openvoice/models/checkpoints_v2/base_speakers/ses' \
  'OPENVOICE_BASE_SPEAKER_EN=EN-NEWEST' \
  'OPENVOICE_BASE_SPEAKER_JA=JP' \
  'OPENVOICE_SPEED_EN=1.0' \
  'OPENVOICE_SPEED_JA=1.0' \
  'OPENVOICE_OUTPUT_SAMPLE_RATE=24000' \
  'OPENVOICE_OUTPUT_DIR=/tmp/openvoice-output' \
  'OPENVOICE_VOICE_ID=tsuki' \
  'OPENVOICE_DEVICE=cpu' \
  'OPENVOICE_MAX_TEXT_CHARS=400' \
  'OPENVOICE_INFERENCE_WAIT_SECONDS=30' \
  'OPENVOICE_TORCH_THREADS=4' \
  'OPENVOICE_LOG_LEVEL=INFO' \
  'NUMBA_DISABLE_CACHING=1' \
  'NUMBA_CACHE_DIR=/tmp/numba-cache' \
  'HOME=/opt/openvoice' \
  'XDG_CACHE_HOME=/opt/openvoice/cache' \
  'HF_HOME=/opt/openvoice/cache/huggingface' \
  'TRANSFORMERS_CACHE=/opt/openvoice/cache/huggingface' \
  'NLTK_DATA=/opt/openvoice/cache/nltk' \
  > /opt/openvoice/openvoice.env

chown azureuser:azureuser /opt/openvoice/openvoice.env

if [ ! -f /opt/openvoice/.venv/lib/python3.10/site-packages/unidic/dicdir/mecabrc ]; then
  /opt/openvoice/.venv/bin/python -m unidic download
fi
if [ ! -d /opt/openvoice/cache/nltk/taggers/averaged_perceptron_tagger_eng ]; then
  HOME=/opt/openvoice NLTK_DATA=/opt/openvoice/cache/nltk \
    /opt/openvoice/.venv/bin/python -m nltk.downloader \
    -d /opt/openvoice/cache/nltk \
    averaged_perceptron_tagger_eng averaged_perceptron_tagger
fi

install -o root -g root -m 644 \
  /opt/openvoice/systemd/openvoice-api.service.example \
  /etc/systemd/system/openvoice-api.service
sed -i 's/<VM_USER>/azureuser/' /etc/systemd/system/openvoice-api.service

systemctl daemon-reload
systemctl enable openvoice-api.service
systemctl restart openvoice-api.service
echo service_started
