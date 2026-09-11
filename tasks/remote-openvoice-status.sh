#!/usr/bin/env bash
set -eu

systemctl is-active openvoice-api.service
api_key="$(awk -F= '$1 == "OPENVOICE_API_KEY" { print $2 }' /opt/openvoice/openvoice.env)"
curl --fail --silent \
  -H "X-Api-Key: ${api_key}" \
  http://127.0.0.1:8000/health
printf '\n'
free -h
df -h /
stat -c '%a %U:%G %n' \
  /opt/openvoice/openvoice.env \
  /opt/openvoice/cache \
  /opt/openvoice/cache/nltk
