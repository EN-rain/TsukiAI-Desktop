# Gate 1 Base/full-ICL benchmark on Azure Main

> **HISTORICAL / SUPERSEDED:** This benchmark is diagnostic provenance for the
> former Base/full-ICL runtime and cannot select a production voice.

The current production identity is intentionally
`Qwen/Qwen3-TTS-12Hz-0.6B-Base` with full ICL conditioning,
`x_vector_only_mode=false`, and no runtime LoRA. There is no trained/fine-tuned
Qwen artifact required for this production identity.

The benchmark runner has two evidence scopes. Use
`gate0_verified_production_identity` after the Gate 0 identity attestation is
complete and matches live `/health`. Those rows are emitted with
`gate0_qualified=true` and may participate in production Gate 1/2 decisions.
Use `base_full_icl_rehearsal` before that attestation exists; those rows remain
useful timing evidence but cannot select a production winner.

`evidence_valid=true` means only that the synthesis succeeded and the cache-miss
evidence is usable. It never means Gate 0 passed.

## Preconditions

1. Use a maintenance window because each variant requires a Qwen worker restart.
2. Run from `/opt/tsuki-qwen3-tts/server` with the benchmark tooling from this
   checkout deployed there.
3. Keep the upstream complete-WAV fallback/cache path unchanged. Benchmark the
   private worker directly at `http://127.0.0.1:8100`.
4. Decide whether this run is qualified production evidence or an unqualified
   rehearsal. Qualified runs require the identity attestation described below.
5. Do not enable shell tracing (`set -x`) while the API key is in the shell
   environment.

## Capture the starting state

Open a root shell, load the existing service environment without printing it,
and save only non-secret health fields:

```bash
cd /opt/tsuki-qwen3-tts/server
set -a
. /opt/tsuki-qwen3-tts/qwen.env
set +a

python - <<'PY' > /tmp/qwen-health-before-rehearsal.json
import json, os
from urllib.request import Request, urlopen

req = Request("http://127.0.0.1:8100/health", headers={"X-Api-Key": os.environ["QWEN_TTS_API_KEY"]})
with urlopen(req, timeout=10) as response:
    health = json.load(response)
keep = (
    "status", "engine", "model", "checkpoint_id", "checkpoint_id_source",
    "generation_version", "mode", "runtime_lora_loaded", "conditioning", "runtime",
)
print(json.dumps({key: health.get(key) for key in keep}, indent=2, sort_keys=True))
PY

cat /tmp/qwen-health-before-rehearsal.json
```

Stop if the health output no longer describes the known Base/full-ICL runtime.
The benchmark runner will also reject such a mismatch.

## Apply one reversible Gate 1 variant

The canonical matrix is `benchmark_variants.json`. A-E now explicitly set
attention, intra-op threads, inter-op threads, and `max_new_tokens=2048` so Gate
1 changes only attention/threading. Sampling stays fixed in `api.py`.

Create one temporary environment file in `/run` from the selected variant. This
file contains tuning values only; it never contains the API key. Replace
`gate1-A` with B, C, D, or E for later runs.

```bash
export BENCHMARK_VARIANT=gate1-A
python - <<'PY'
import json, os
from pathlib import Path

variant_id = os.environ["BENCHMARK_VARIANT"]
payload = json.loads(Path("benchmark_variants.json").read_text())
variant = next(item for item in payload["variants"] if item["id"] == variant_id)
if variant["gate"] != 1:
    raise SystemExit("rehearsal procedure accepts Gate 1 variants only")
Path("/run/tsuki-qwen-gate1-rehearsal.env").write_text(
    "".join(f"{key}={value}\n" for key, value in sorted(variant["env"].items())),
    encoding="utf-8",
)
PY

mkdir -p /run/systemd/system/qwen3-tts.service.d
cat > /run/systemd/system/qwen3-tts.service.d/90-gate1-rehearsal.conf <<'EOF'
[Service]
EnvironmentFile=/run/tsuki-qwen-gate1-rehearsal.env
EOF

systemctl daemon-reload
systemctl restart qwen3-tts
```

If the deployed unit has a different service name, use its actual name for the
two `systemctl` commands and the drop-in directory. Do not edit the persistent
`qwen.env` for a rehearsal.

## Run and label the benchmark

Find the worker PID if resource sampling is desired, then run the locked corpus
directly against the private worker. The variant check compares `/health` with
the canonical matrix before any samples are collected.

```bash
export QWEN_PID="$(systemctl show -p MainPID --value qwen3-tts)"

python benchmark_evidence.py \
  --base-url http://127.0.0.1:8100 \
  --cache-mode direct-inference \
  --cloud azure \
  --region malaysiawest \
  --vm-sku Standard_B4as_v2 \
  --variant-id "$BENCHMARK_VARIANT" \
  --evidence-scope gate0_verified_production_identity \
  --gate0-attestation /opt/tsuki-qwen3-tts/evidence/gate0-production-identity.json \
  --resource-pid "$QWEN_PID" \
  --save-audio \
  --output-dir "benchmark-output/gate0-production-identity/$BENCHMARK_VARIANT"
```

Before the attestation is complete, run the same command with
`--evidence-scope base_full_icl_rehearsal`, omit `--gate0-attestation`, and use a
clearly named rehearsal output directory. The runner will emit
`gate0_qualified=false` and the decision helper will refuse a production winner.

Repeat the apply/run sequence for `gate1-A`, `gate1-B`, `gate1-C`, `gate1-D`,
and `gate1-E`. A is the eager 4/4 reference; B-D are SDPA 4/1, 3/1, and 2/1;
E is eager 4/1. The fixed `ld-05` long/detail corpus item is the six-sentence
Gate 1 timing probe; it remains inside the locked 55-row corpus rather than
creating a second ad-hoc text set.

For the repeated-request part of the Gate 1 rehearsal, run one fixed
one-sentence item twenty times under the same variant and keep it in a separate
output directory:

```bash
python benchmark_evidence.py \
  --base-url http://127.0.0.1:8100 \
  --cache-mode direct-inference \
  --cloud azure \
  --region malaysiawest \
  --vm-sku Standard_B4as_v2 \
  --variant-id "$BENCHMARK_VARIANT" \
  --evidence-scope gate0_verified_production_identity \
  --gate0-attestation /opt/tsuki-qwen3-tts/evidence/gate0-production-identity.json \
  --resource-pid "$QWEN_PID" \
  --text-id os-01 \
  --repetitions 20 \
  --output-dir "benchmark-output/gate0-production-identity/$BENCHMARK_VARIANT-stability20"
```

Gate 2 remains a separate experiment after a Gate 1 runtime is selected. Its
token variants intentionally set only `QWEN_TTS_MAX_NEW_TOKENS`; use the same
verified production identity, corpus, sampling profile, and external quality
gate when comparing Gate 2 results.

## Roll back the temporary runtime override

After the final rehearsal variant, remove only the `/run` files created above,
reload systemd, restart the worker, and compare health with the captured starting
state:

```bash
rm -f /run/systemd/system/qwen3-tts.service.d/90-gate1-rehearsal.conf
rm -f /run/tsuki-qwen-gate1-rehearsal.env
systemctl daemon-reload
systemctl restart qwen3-tts

python - <<'PY'
import json, os
from urllib.request import Request, urlopen

req = Request("http://127.0.0.1:8100/health", headers={"X-Api-Key": os.environ["QWEN_TTS_API_KEY"]})
with urlopen(req, timeout=10) as response:
    health = json.load(response)
print(json.dumps({
    "model": health.get("model"),
    "checkpoint_id": health.get("checkpoint_id"),
    "mode": health.get("mode"),
    "runtime": health.get("runtime"),
}, indent=2, sort_keys=True))
PY
```

The restored runtime must match the pre-rehearsal configuration before the
maintenance window is closed.

## Gate 0 production-identity attestation

The runner accepts `--evidence-scope gate0_verified_production_identity` only
when `--gate0-attestation` points to a JSON file containing all of the following:

```json
{
  "gate0_status": "passed",
  "model": "Qwen/Qwen3-TTS-12Hz-0.6B-Base",
  "checkpoint_id": "<exact /health checkpoint_id>",
  "reference_sha256": "<exact 64-hex /health conditioning.reference_sha256>",
  "conditioning": {
    "mode": "full-icl",
    "x_vector_only_mode": false
  },
  "runtime_lora_loaded": false,
  "generation_version": "<exact /health generation_version>"
}
```

The attested model and checkpoint must exactly match `/health`. The live
reference hash must itself be a real 64-hex SHA-256, health must report
`mode=full-icl`, `conditioning.x_vector_only_mode=false`, and
`runtime_lora_loaded=false`, and `generation_version` must be present and match
the attestation. No trained-artifact type or trained-artifact digest is required.
The benchmark row records the SHA-256 of the attestation file itself so the
decision helper can require reference and candidate evidence to use the same
verified production identity.
