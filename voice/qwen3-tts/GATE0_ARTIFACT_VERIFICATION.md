# Gate 0 production identity verification

> **HISTORICAL / SUPERSEDED:** This records the former Base/full-ICL identity.
> It is provenance only and is not a production approval for the trained
> Tsukihime checkpoint. Use `TRAINED_CUSTOM_VOICE_PRODUCTION_FIX_PLAN.md`.

Gate 0 must describe the model that actually exists in production. The earlier
assumption that a separate "finished trained checkpoint" must exist was a
planning defect introduced by later uncommitted realtime/optimization plans; it
does not match the provenance of the Qwen deployment.

Commit `789fd7c` introduced Qwen3-TTS using the official 0.6B Base model plus
Tsuki's Japanese full-ICL reference conditioning and explicitly stated that no
training or fine-tuning is performed. Current Azure deployment evidence matches
that architecture. Azure and AWS object-storage inventories also show no
separate trained Qwen artifact.

Repository evidence supports only these claims:

- `789fd7c` established `Qwen/Qwen3-TTS-12Hz-0.6B-Base` as the Qwen runtime;
- the active runtime is Qwen3-TTS 12 Hz 0.6B Base inference;
- the worker always uses the Japanese reference/transcript in full ICL mode with
  `x_vector_only_mode=False`;
- the worker does not load a runtime LoRA;
- the Qwen production path has no trained/fine-tuned Qwen artifact;
- `PHASE2-HANDOFF.md` documents a separate GPT-SoVITS effort whose training did
  not complete, so it is not evidence of a trained Qwen deployment artifact;
- available Azure/AWS object-storage inventories contain no separate trained
  Qwen artifact.

## Reconciled live Azure evidence

Observed on `b4as-test` under `/opt/tsuki-qwen3-tts` on 2026-09-14:

- the deployed model is the Qwen3-TTS 0.6B Base/full-ICL path;
- no trained Tsuki Qwen checkpoint, LoRA, adapter, or other trained model
  artifact was found under that deployment tree;
- the live `/health` response reports the Base checkpoint rather than a distinct
  trained Tsuki checkpoint;
- the live worker still requires full ICL/reference conditioning;
- the live authoritative `cache.py` `build_cache_key` includes the following
  cache-sensitive inputs: `checkpoint_id`/`model_name`, `generation_version`,
  `language`, `max_new_tokens`, `output_format`, `reference_sha256`,
  `sample_rate`, `text`, and `voice_version`;
- therefore the observed live Base deployment does satisfy Gate 0's rule that
  checkpoint/model identity participates in every final-audio cache key;
- the live deployment source also contains `cache.py`, `queueing.py`, and
  `ram_cache.py`, while those files are absent from this checkout.

This evidence establishes the truthful **current production classification**:

```text
model: Qwen/Qwen3-TTS-12Hz-0.6B-Base
conditioning: full ICL reference conditioning
reference required: yes
x_vector_only_mode: false
runtime LoRA: none
trained/fine-tuned Qwen artifact: none
```

Gate 0 for current production is an identity/provenance gate for this
Base/full-ICL configuration. A trained-artifact classification is not required
unless a trained Qwen artifact is intentionally introduced in the future.

## Current-production Gate 0 evidence

Gate 0 passes for the current Base/full-ICL production identity when all of the
following are recorded together:

1. `/health` reports the actual Base model/checkpoint identity and generation
   version;
2. the exact Tsuki source reference-audio SHA-256 is recorded and the live
   `/health` `conditioning.reference_sha256` matches it;
3. the authoritative audio-cache identity includes the deployed
   checkpoint/model identity and generation version;
4. the full-ICL/reference behavior and absence of runtime LoRA are recorded;
5. the one-through-six-sentence baseline is recorded for that same production
   identity/configuration.

The original source reference from the Qwen introduction/deployment workflow
still exists at `C:\Users\LENOVO\Downloads\tsuki_25s_clip.mp3`. A read-only
SHA-256 computation on 2026-09-14 produced:

```text
CE93BB53F197F0683D46AB4AD240E3D434EF6E2B2340190BE98DFE382A0F6530
```

This is the **source reference digest**. It is not, by itself, proof that the
live worker is using byte-identical reference audio. The remaining Gate 0
reference-evidence check is precise: live `/health`
`conditioning.reference_sha256` must equal
`CE93BB53F197F0683D46AB4AD240E3D434EF6E2B2340190BE98DFE382A0F6530`.

The existing live evidence already establishes the Base/full-ICL architecture,
the `/health` Base identity, and cache-key identity participation. The Main VPS
optimization plan already records the current one-through-six-sentence baseline.

## Live Main VPS verification / evidence refresh

Run these commands on the Main VPS. They deliberately avoid printing the API
key.

1. Read only the model-identity fields from the service environment:

   ```bash
   sudo grep -E '^QWEN_TTS_(MODEL|CHECKPOINT_ID|GENERATION_VERSION)=' /opt/tsuki-qwen3-tts/qwen.env
   ```

2. Query the protected worker health response without putting the API key in
   the command line or output:

   ```bash
   sudo python - <<'PY'
   import json
   from pathlib import Path
   from urllib.request import Request, urlopen

   values = {}
   for line in Path("/opt/tsuki-qwen3-tts/qwen.env").read_text().splitlines():
       line = line.strip()
       if line and not line.startswith("#") and "=" in line:
           key, value = line.split("=", 1)
           values[key.strip()] = value.strip().strip('"').strip("'")

   request = Request(
       "http://127.0.0.1:8100/health",
       headers={"X-Api-Key": values["QWEN_TTS_API_KEY"]},
   )
   with urlopen(request, timeout=10) as response:
       health = json.load(response)

   keep = (
       "status", "engine", "model", "checkpoint_id", "checkpoint_id_source",
       "generation_version", "audio_cache_namespace", "conditioning",
       "runtime_lora_loaded",
   )
   print(json.dumps({key: health.get(key) for key in keep}, indent=2, sort_keys=True))
   PY
   ```

3. If an immutable local model snapshot/directory identity is desired for the
   evidence bundle, resolve the exact local model directory. If `QWEN_TTS_MODEL` is already an
   absolute directory, use it directly. If it is a Hugging Face repository ID,
   resolve the already-downloaded snapshot without network access:

   ```bash
   python - <<'PY'
   import os
   from pathlib import Path
   from huggingface_hub import snapshot_download

   model = os.environ["QWEN_TTS_MODEL"]
   path = Path(model).expanduser()
   if not path.is_dir():
       path = Path(snapshot_download(repo_id=model, local_files_only=True))
   print(path.resolve())
   print(f"snapshot_or_directory_id={path.resolve().name}")
   PY
   ```

4. Optionally fingerprint that resolved local directory with the checked-in
   deterministic digester. This is useful provenance evidence for the Base
   snapshot but is not a substitute for the required reference SHA-256.
   Substitute the path printed by step 3:

   ```bash
   cd /opt/tsuki-qwen3-tts/server
   python artifact_digest.py /exact/resolved/model/directory
   ```

5. If the deployed architecture changes or an adapter is suspected, inspect the
   resolved artifact for LoRA/adapter metadata without assuming its presence:

   ```bash
   find /exact/resolved/model/directory -maxdepth 2 -type f \
     \( -name 'adapter_config.json' -o -name 'adapter_model.safetensors' -o -name '*lora*' \) -print
   ```

6. The 2026-09-14 live inspection has already verified that the authoritative
   Main VPS `cache.py` `build_cache_key` includes `checkpoint_id`/`model_name`
   and `generation_version`, along with language, token ceiling, output format,
   reference SHA-256, sample rate, text, and voice version. Repeat this check
   after any cache-source reconciliation or deployment change and compare it
   against the checked-in source.

## Smallest source-control reconciliation for live cache modules

The current checkout cannot reproduce or review the deployed cache/queue
behavior because `cache.py`, `queueing.py`, and `ram_cache.py` exist only on the
live worker. The smallest safe reconciliation is:

1. copy those three exact live source files from `b4as-test` into
   `voice/qwen3-tts/` without refactoring them during import;
2. record a SHA-256 for each live file before transfer and confirm the checked-in
   copy has the same hash, so source control first captures what is actually
   deployed;
3. add focused local tests for the imported public contracts, beginning with a
   `build_cache_key` test proving every currently observed cache-sensitive field
   changes the key when varied independently;
4. run import/compile and those focused tests before making any behavioral
   changes;
5. only after that parity point, review or refactor cache/queue behavior in a
   separate bounded change and deploy it through the normal source-controlled
   path.

Do not rewrite the live modules from memory or recreate them from the field list
above. Their exact deployed source is the reconciliation input.

### Minimal `build_cache_key` test contract once parity is proven

Do not add an executable cache-key test until the exact live `cache.py` is in
this checkout and its SHA-256 matches the live copy. The function signature and
any normalization rules must come from that imported source rather than being
guessed from deployment observations.

Once parity is proven, the first focused test file should exercise only the
existing `build_cache_key` contract:

1. construct one valid baseline call using the imported function's real
   signature and realistic fixed values;
2. assert two identical calls produce exactly the same key;
3. vary exactly one observed cache-sensitive input at a time and assert the key
   changes for each of:
   - checkpoint identity (`checkpoint_id`, or `model_name` when the deployed
     function uses model name as the fallback identity);
   - `generation_version`;
   - `language`;
   - `max_new_tokens`;
   - `output_format`;
   - `reference_sha256`;
   - `sample_rate`;
   - `text`;
   - `voice_version`;
4. if the imported implementation explicitly supports both an explicit
   `checkpoint_id` and a `model_name` fallback, add one focused assertion that
   changing the fallback model identity changes the key when no explicit
   checkpoint ID is supplied;
5. do not assert undocumented normalization, ordering, hash algorithm, digest
   length, or exception behavior unless those are visible in the exact imported
   source.

This test is intentionally about identity and field sensitivity only. Queueing,
RAM-cache eviction, disk layout, TTLs, concurrency, and performance belong to
separate tests after source parity is established.

### Evidence still required before importing or testing the live modules

The 2026-09-14 checkout inspection still does not contain `cache.py`,
`queueing.py`, or `ram_cache.py` under `voice/qwen3-tts/`, and no source-control
record in this Gate 0 work contains the live SHA-256 values for those files.
Before the reconciliation can proceed, capture the exact deployed bytes plus a
hash manifest on `b4as-test`, then verify the transferred copies locally.

On the live worker:

```bash
cd /opt/tsuki-qwen3-tts/server
sha256sum cache.py queueing.py ram_cache.py
```

After copying those exact three files into `voice/qwen3-tts/`, verify the local
copies without editing them first:

```powershell
Get-FileHash voice/qwen3-tts/cache.py -Algorithm SHA256
Get-FileHash voice/qwen3-tts/queueing.py -Algorithm SHA256
Get-FileHash voice/qwen3-tts/ram_cache.py -Algorithm SHA256
```

Each local digest must equal the corresponding live digest byte-for-byte before
adding the focused cache-key tests or making any production-source change.

## Conditional future trained-artifact gate

If a trained Qwen checkpoint, merged LoRA, runtime LoRA, adapter, or other
trained voice artifact is intentionally introduced later, reopen Gate 0 before
benchmarking or production rollout. Record its exact path/source, immutable
checkpoint/build identity or digest, artifact type, runtime-LoRA requirement,
reference/ICL requirement, `/health` identity, cache identity, and a fresh
one-through-six-sentence baseline. Do not infer these values from a plan or
model label.

For the current Base/full-ICL deployment, no trained artifact is missing from
Gate 0. Cache-key participation is verified for the live Base deployment and
must be revalidated after source reconciliation or any production identity,
reference, or cache-key change.
