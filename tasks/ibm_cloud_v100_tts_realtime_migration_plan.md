# TsukiAI — IBM Cloud V100 TTS Realtime Migration Plan

> **HISTORICAL / SUPERSEDED:** This document describes the former Base/full-ICL
> runtime. It must not be used to configure production. Use
> `voice/qwen3-tts/TRAINED_CUSTOM_VOICE_PRODUCTION_FIX_PLAN.md`.

## Status

Planning document only. This file does **not** provision IBM Cloud resources, change production routing, or modify the current Azure worker.

Created for the following fixed target:

```text
Cloud: IBM Cloud VPC
Region priority: Osaka (jp-osa) first
Region fallback: Tokyo (jp-tok)
Profile: gx2-8x64x1v100
CPU: 8 vCPU / 4 physical cores
RAM: 64 GiB
GPU: 1× NVIDIA V100 PCIe 16 GB
Network cap: 16 Gbps
OS: Ubuntu
Workload: Qwen3-TTS 0.6B Base, Tsuki full-ICL voice cloning
```

The migration is **TTS-compute-only** at first. The existing Azure Main remains the cache/scheduler/control plane and rollback path until IBM passes the realtime and quality gates below.

---

# 1. Executive Decision

The current Azure CPU worker is not a realtime TTS platform.

Current production evidence:

```text
Qwen/Qwen3-TTS-12Hz-0.6B-Base
full ICL
x_vector_only_mode=false
runtime LoRA=false
attention=SDPA
4 intra-op / 1 inter-op
max_new_tokens=2048
```

Latest production smoke:

| Probe | Wall | Audio | RTF |
|---|---:|---:|---:|
| 1 sentence | 43.988 s | 3.440 s | 12.786 |
| 2 sentences | 58.130 s | 5.840 s | 9.952 |

The migration objective is therefore not another CPU tuning pass. It is:

```text
move Qwen inference onto the IBM V100
        +
keep the model resident in VRAM
        +
keep Tsuki full-ICL conditioning identical
        +
start TTS from streaming LLM clauses
        +
prove realtime behavior with measured gates
```

No cutover is allowed merely because CUDA is working. IBM becomes production only after it passes the latency, RTF, quality, stability, networking, and rollback gates in this document.

---

# 2. Authoritative Voice Identity

Do **not** revive the old assumption that Tsuki uses a separately trained Qwen checkpoint.

Authoritative production identity:

```text
model: Qwen/Qwen3-TTS-12Hz-0.6B-Base
conditioning: full ICL reference conditioning
reference required: yes
x_vector_only_mode: false
runtime LoRA: none
trained/fine-tuned Qwen artifact: none
```

Reference audio SHA-256:

```text
CE93BB53F197F0683D46AB4AD240E3D434EF6E2B2340190BE98DFE382A0F6530
```

The IBM worker must use the exact same reference bytes and exact reference transcript.

Any IBM deployment that silently changes to x-vector-only, a different reference, a different model revision, a different language behavior, or a LoRA is a failed migration even if it is faster.

---

# 3. IBM Cloud Capacity and Region Gate

IBM currently lists `gx2-8x64x1v100` as:

```text
8 vCPU / 4 cores
64 GiB RAM
1× NVIDIA V100 PCIe 16 GB
16 Gbps bandwidth cap
```

IBM documentation also states that GPU profile availability must be verified in the target zone before provisioning. Do not assume a profile is currently allocatable merely because the region exists.

## Region selection procedure

### First choice — Osaka

```text
region = jp-osa
profile = gx2-8x64x1v100
```

Check every available Osaka VPC zone exposed to the account.

Accept Osaka only if all of the following are true:

```text
profile is selectable
account GPU quota permits one instance
capacity is currently available
Ubuntu image is compatible
the account-specific hourly estimate is acceptable
```

### Fallback — Tokyo

If Osaka fails because of profile availability, quota, or capacity:

```text
region = jp-tok
profile = gx2-8x64x1v100
```

Do not change to a larger GPU profile just to force the deployment through. The V100 profile is the budget target.

Do not provision Osaka and Tokyo simultaneously during the first migration.

---

# 4. Cost Gate Before Provisioning

The user intends to preserve the IBM $200 trial credit and prefers roughly 8–10 hours of daily uptime over the 30-day credit window.

Before clicking Create, record the actual IBM account/region quote for:

```text
gx2-8x64x1v100
boot volume
floating/public IP if billed
outbound traffic assumptions
```

Required calculations:

```text
8 h/day × 30 days = 240 runtime hours
10 h/day × 30 days = 300 runtime hours

$200 / 240 h = $0.833/h absolute ceiling
$200 / 300 h = $0.667/h absolute ceiling
```

Leave a reserve for storage/networking/mistakes. A practical compute target is lower than those absolute ceilings.

If the real IBM quote cannot support the desired runtime, record that fact before deployment. Do not hide the cost problem behind the free-credit label.

When the daily session is finished, stop the VSI through the IBM control plane rather than merely shutting Linux down from inside the guest. Persistent storage may continue to incur charges even while compute is stopped.

---

# 5. Target Architecture

Initial migration topology:

```text
Tsuki voice pipeline / public ingress
              |
              v
Azure Main control plane
  - cache authority
  - single-flight
  - turn IDs
  - cancellation
  - scheduler
  - existing rollback TTS
              |
              | private encrypted cross-cloud path
              v
IBM Cloud Osaka (preferred)
        or Tokyo (fallback)
              |
              v
Ubuntu gx2-8x64x1v100
1× V100 16 GB
Qwen3-TTS 0.6B Base
full-ICL prompt cached
model resident in VRAM
```

Do not move the cache authority to IBM during the first pass. Moving compute and control-plane state at the same time increases the blast radius for no useful latency benefit.

The IBM worker is a private inference worker, not a public application server.

---

# 6. Network Design

Preferred cross-cloud path:

```text
Azure Main
    |
    | WireGuard/private encrypted overlay
    v
IBM V100 worker
```

Requirements:

```text
IBM TTS port must not be open to the general Internet
only Azure Main may call the worker
retain API-key authentication even on the private tunnel
restrict SSH to the operator source address where possible
no API keys in shell history, Git, benchmark CSV, or logs
```

The existing `QwenTtsClient` already supports a configurable remote endpoint through:

```text
TSUKI_QWEN_TTS_URL
TSUKI_QWEN_TTS_API_KEY
```

Therefore the complete-WAV migration does not require hardcoding an IBM address into C#.

Recommended cutover shape:

```text
TSUKI_QWEN_TTS_URL=http://<ibm-private-overlay-ip>:8100
```

Do not use a raw IBM public IP as the final production configuration.

---

# 7. Ubuntu / V100 Bootstrap

Use a supported Ubuntu LTS image. Prefer Ubuntu 22.04 LTS for the first deployment unless the exact IBM image/driver matrix proves Ubuntu 24.04 is equally safe for this V100 stack.

IBM's current V100 Linux guidance requires at least:

```text
NVIDIA driver >= 535
CUDA >= 12.2
```

Provisioning is not complete until all of these pass:

```bash
nvidia-smi
```

and:

```python
import torch
assert torch.cuda.is_available()
print(torch.cuda.get_device_name(0))
print(torch.cuda.get_device_capability(0))
print(torch.version.cuda)
```

Expected hardware identity:

```text
NVIDIA Tesla V100 / V100 PCIe
compute capability 7.0
~16 GB VRAM
```

Create the deployment under the same logical root used by the Azure service:

```text
/opt/tsuki-qwen3-tts/
  server/
  reference/
  cache/
  numba-cache/
  backups/
  venv/
```

Before installing Python packages, capture the current Azure package versions for the Qwen stack. Keep the Qwen package/model behavior constant and change only the PyTorch runtime from CPU to CUDA wherever possible.

---

# 8. V100-Specific Runtime Rules

The current checked-in worker is explicitly CPU-only. `voice/qwen3-tts/api.py` currently rejects any `QWEN_TTS_DEVICE` other than `cpu` and hardcodes:

```text
device_map="cpu"
dtype=torch.float32
```

That code must be generalized before IBM deployment.

## Required GPU runtime

```text
QWEN_TTS_DEVICE=cuda:0
QWEN_TTS_DTYPE=float16
QWEN_TTS_ATTN_IMPLEMENTATION=sdpa
QWEN_TTS_MAX_NEW_TOKENS=2048 initially
```

### Why FP16

V100 has Tensor Cores for FP16. It is not a native BF16-generation GPU. Do not copy a modern A100/L4/H100 BF16 configuration onto V100 and assume it is optimal.

### Why SDPA first

The current worker already has a proven SDPA path. V100 is pre-Ampere hardware, so do not make FlashAttention 2 a migration dependency. Benchmark it only if a specific build explicitly supports V100; otherwise stay on PyTorch SDPA/eager.

### GPU runtime startup gate

The service must refuse to report ready if `QWEN_TTS_DEVICE` requests CUDA but any of these are false:

```text
torch.cuda.is_available() == true
requested CUDA device exists
GPU name is recorded
VRAM is detected
model parameters are actually on CUDA
full-ICL prompt tensors are on the model device
```

Never silently fall back to CPU on the IBM GPU worker. A silent CPU fallback would recreate the original latency problem while pretending the migration succeeded.

---

# 9. Required Source Changes

Do these as a bounded GPU-enablement slice before realtime pipeline integration.

## `voice/qwen3-tts/runtime_config.py`

Add explicit validated runtime settings for:

```text
device: cpu | cuda | cuda:0
dtype: float32 | float16
```

Keep current CPU defaults so Azure rollback remains valid.

Do not make V100-specific values global defaults.

## `voice/qwen3-tts/api.py`

Replace the CPU-only startup guard with explicit CPU/CUDA branches.

CPU branch must retain current behavior.

CUDA branch must load approximately as:

```python
Qwen3TTSModel.from_pretrained(
    model_name,
    device_map="cuda:0",
    dtype=torch.float16,
    attn_implementation="sdpa",
)
```

Exact call syntax must be verified against the pinned installed `qwen-tts` package before deployment.

The cached full-ICL prompt already moves saved prompt tensors to `model.device`; retain that property and test it explicitly on CUDA.

## `/health`

Add truthful runtime fields such as:

```text
device
cuda_available
gpu_name
gpu_compute_capability
cuda_runtime
dtype
attention_implementation
model_load_seconds
VRAM allocated/reserved if available
```

A production check should be able to prove from `/health` alone that IBM is not doing CPU inference.

## Systemd example

Update or add an IBM-specific environment example containing no secrets:

```text
QWEN_TTS_DEVICE=cuda:0
QWEN_TTS_DTYPE=float16
QWEN_TTS_ATTN_IMPLEMENTATION=sdpa
QWEN_TTS_MAX_NEW_TOKENS=2048
```

Keep the service at one model process. Do not start multiple Qwen processes on a single 16 GB V100 until VRAM measurements prove it is safe and useful.

---

# 10. Identity and Cache Isolation on IBM

The first IBM GPU deployment may produce slightly different audio because FP16/GPU numerics differ from the current FP32 CPU worker.

Do not mix experimental IBM output into the existing production audio namespace until quality equivalence is accepted.

Use a distinct provisional generation identity, for example:

```text
QWEN_TTS_GENERATION_VERSION=full-icl-v100-fp16-v1
```

The exact final value can change, but the principle is mandatory.

Required startup attestation:

```text
model identity matches Base 0.6B
model revision/checkpoint identity recorded
reference SHA matches exactly
reference transcript matches exactly
x_vector_only_mode=false
runtime LoRA=false
full ICL prompt loaded
CUDA device active
FP16 recorded
```

Do not reuse cached audio across CPU/GPU generation identities until A/B evidence proves doing so is semantically safe.

---

# 11. GPU Warmup and Residency

The desired runtime is persistent inference, not model-per-request execution.

Boot sequence:

```text
start service
  ↓
load Qwen once into V100 VRAM
  ↓
load/build Tsuki full-ICL prompt once
  ↓
run one private warmup clause
  ↓
mark warmup_complete=true
  ↓
accept normal traffic
```

After startup, verify with `nvidia-smi` that the Qwen process remains resident in VRAM between requests.

No request path may reload the model or rebuild the reference prompt.

---

# 12. IBM GPU Benchmark Gate

Do not start realtime pipeline work until the raw V100 inference result is known.

Use the existing deterministic corpus and the exact same locked probes used for Azure:

```text
1 sentence
2 sentences
3 sentences
6 sentences
Japanese
mixed Japanese/English
```

For the core 1/2/3/6 probes, run:

```text
1 cold startup result
1 warmup result
at least 5 warm cache-miss inference repetitions per probe
```

Record:

```text
model load seconds
VRAM idle after load
peak VRAM
host RSS
GPU utilization
GPU power/clock if available
request wall time
audio duration
RTF
HTTP/network overhead
output sample rate
RMS/peak
errors/OOMs
```

## Hard GPU proof

The benchmark is invalid unless all are true:

```text
nvidia-smi shows the service process using VRAM
/health reports CUDA
model parameters are on cuda:0
GPU utilization rises during synthesis
no silent CPU fallback occurs
```

## Performance gates

Minimum sustainable gate:

```text
RTF <= 1.0 on warm short and medium probes
```

Preferred:

```text
RTF <= 0.6
```

Conversational short-clause target:

```text
6–12 spoken word clause complete generation <= 2.0 s
preferred <= 1.0–1.5 s
```

If the V100 cannot reach RTF <= 1 after a reasonable FP16/SDPA optimization pass, do not label the V100 deployment realtime. Escalate to a newer GPU rather than redefining the target.

---

# 13. V100 Optimization Order

After the first correct FP16/SDPA baseline, optimize one variable at a time.

Order:

```text
1. FP16 + SDPA baseline
2. FP16 + eager comparison
3. safe PyTorch compile experiment if supported and stable
4. codec/decoder compile experiment if the installed Qwen version supports it
5. CUDA graph experiment only if V100 compatibility is proven
6. batching/concurrency only after single-request latency is acceptable
```

Do not burn time retesting CPU thread counts on the GPU worker.

Do not lower `max_new_tokens` merely because the move is to GPU; the existing Gate 2 evidence already showed no reliable benefit from the lower global caps.

---

# 14. What “Realtime” Means Here

Do not fake this definition.

There are two different levels:

## Level A — Conversational realtime via short clauses

The LLM emits a stable first clause early, the GPU synthesizes that short clause quickly, and playback begins while the LLM continues producing the rest of the answer.

This can feel realtime even if each individual TTS request returns a complete short WAV.

Target:

```text
user end-of-turn
  ↓
LLM partial text
  ↓
first stable 6–12 word clause
  ↓
IBM V100 TTS
  ↓
playback starts <= ~2 s after clause is ready
  ↓
next clause synthesized while previous audio plays
```

## Level B — True intra-clause audio streaming

The TTS backend emits audio bytes before synthesis of that clause is complete.

Target:

```text
Qwen starts generation
  ↓
first PCM frames produced
  ↓
frames sent immediately
  ↓
playback starts
  +
remaining frames continue arriving
```

The project must never claim Level B merely because the HTTP client uses `ResponseHeadersRead` or because a finished WAV is sliced into pieces.

---

# 15. Important Upstream Qwen Streaming Constraint

The current official `generate_voice_clone()` Python API still returns a completed waveform tuple. Its `non_streaming_mode` option is documented as simulating streaming text input and is **not by itself proof of true streaming output generation**.

Therefore:

```text
GPU migration != true streaming automatically
```

The first IBM objective is to get raw generation below realtime (RTF < 1). Once that is true, integrate clause-level LLM→TTS overlap immediately because the repo already contains the standalone `StreamingClauseChunker`.

True PCM streaming is a separate acceptance gate and must use a backend/path that can prove audio is produced before full synthesis finishes.

---

# 16. Clause-Level Realtime Integration

The repo already contains:

```text
TsukiAI.Core/Voice/StreamingClauseChunker.cs
TsukiAI.Core.Tests/StreamingClauseChunkerTests.cs
```

It is intentionally not wired into production yet.

The current `VoiceConversationPipeline` waits for the full LLM reply and then calls TTS once. That must change for realtime voice turns.

## Required pipeline sequence

```text
AssemblyAI end_of_turn
        ↓
IInferenceClient.ChatWithEmotionStreamingAsync(...)
        ↓ cumulative partial text
StreamingClauseChunker.AppendPartial(...)
        ↓
first stable clause
        ↓
IBM V100 TTS request starts immediately
        ↓
play clause #1
        +
LLM keeps generating
        +
clause #2 goes to TTS while clause #1 plays
```

At the end of LLM generation:

```text
StreamingClauseChunker.Flush()
```

Any final remainder becomes the last clause.

## Ordering

Every clause gets:

```text
turn_id
sequence_number
```

Playback must stay sequence ordered even if later implementation allows more than one TTS request in flight.

## Cancellation / barge-in

When the user begins a new turn:

```text
cancel old LLM stream
cancel queued IBM TTS clauses
abort active IBM request where possible
stop current playback
clear old-turn audio
reject late audio whose turn_id is stale
```

The existing per-user cancellation ownership in `VoiceConversationPipeline` must remain the authority.

---

# 17. True PCM Streaming Phase

Only begin this after the V100 proves RTF < 1.

Preferred service contract:

```text
POST /tts/stream
```

Preferred payload remains:

```json
{
  "text": "...",
  "language": "English"
}
```

Preferred response for the first implementation:

```text
chunked binary PCM16
24 kHz
mono
explicit metadata headers/frame header
```

Raw framed PCM is simpler for progressive playback than pretending a complete RIFF/WAV file exists before its final size is known.

The C# client should expose a streaming API rather than buffering the entire response into `byte[]` first.

Potential bounded design:

```text
IStreamingTtsClient
  -> IAsyncEnumerable<AudioChunk>
```

The audio pipeline then converts/resamples chunks incrementally to the playback format.

## True-streaming proof

Instrument these timestamps:

```text
request accepted
first model audio frame produced
first response audio bytes written
first response audio bytes received by C#
first playback buffer submitted
full synthesis complete
```

PASS requires:

```text
first_audio_produced < synthesis_complete
first_audio_received < synthesis_complete
playback_started < synthesis_complete
```

If those inequalities are not true, it is not true TTS streaming.

---

# 18. Streaming Backend Compatibility Gate

Do not blindly replace the official Qwen package with a community “fast streaming” fork.

Any candidate must first prove:

```text
V100 / compute capability 7.0 supported
installed CUDA/PyTorch combination supports V100
Qwen 0.6B Base supported
full-ICL voice clone supported
ref_text is actually used
x_vector_only_mode remains false
English works
Japanese works
Tsuki reference identity remains stable
24 kHz output remains correct
cancellation does not corrupt subsequent requests
```

Many modern optimized paths assume Ampere/Ada/Hopper or recent CUDA graph/FlashAttention features. V100 is older Volta hardware. Compatibility must be measured, not assumed.

If no trustworthy V100-compatible true-streaming backend passes, retain the official model path and use short-clause overlap for conversational realtime. Do not claim Level B streaming until a real PCM-yielding path exists.

---

# 19. C# Files Expected to Change During Realtime Integration

Expected bounded production files:

```text
TsukiAI.Core/Voice/QwenTtsClient.cs
TsukiAI.Core/Voice/VoiceConversationPipeline.cs
TsukiAI.Core/Voice/StreamingClauseChunker.cs   # only if integration reveals a proven narrow need
TsukiAI.Core/Voice/ITtsClient.cs or a new IStreamingTtsClient.cs
TsukiAI.Core/Voice/AudioProcessingService.cs   # only if incremental PCM conversion is needed
```

Expected tests:

```text
Qwen client streamed-response parsing
clause ordering
first-clause dispatch before final LLM reply
turn cancellation
stale-turn audio discard
partial-response disconnect
IBM health failover
complete-WAV fallback
```

Do not remove the existing `SynthesizeWavAsync()` path. It is the rollback/fallback path.

---

# 20. IBM Worker API / Health Contract

Minimum worker endpoints:

```text
GET  /health
POST /tts
```

After true streaming is implemented:

```text
POST /tts/stream
```

Health should include enough evidence to reject a misconfigured worker:

```text
status
worker_id
region
model
checkpoint_id
generation_version
reference_sha256
mode=full-icl
x_vector_only_mode=false
runtime_lora_loaded=false
device=cuda:0
gpu_name
gpu_compute_capability
dtype=float16
attention_implementation
model_loaded
warmup_complete
model_load_seconds
VRAM allocated/reserved
active_jobs
rolling_generation_ms
rolling_rtf
```

Do not expose API keys, raw environment dumps, or secret paths.

---

# 21. Concurrency Strategy for One V100

Do not assume that “GPU” means unlimited simultaneous inference.

First production configuration:

```text
one resident Qwen model
one active synthesis lane
bounded clause queue
```

This avoids VRAM fragmentation/OOM and gives predictable latency.

After single-lane realtime is proven, benchmark:

```text
1 active request
2 concurrent requests
```

Measure both latency and VRAM. Keep concurrency=1 if the second request damages TTFA/RTF enough to make both conversations worse.

The initial goal is one excellent realtime Tsuki conversation, not maximum aggregate throughput.

---

# 22. Quality Gate

GPU speed does not override Tsuki voice quality.

A/B IBM V100 FP16 output against the accepted Azure full-ICL reference using the fixed evaluation set:

```text
short English
long English
Japanese
mixed English/Japanese
numbers
names
questions
emotion-heavy line
punctuation-heavy line
```

Review:

```text
voice identity
accent/style
pronunciation
prosody
pitch
rhythm
noise/artifacts
sentence endings
Japanese quality
English quality
```

Reject the GPU precision/backend if it materially degrades Tsuki.

---

# 23. Reliability / Soak Gate

Before cutover, run at least:

```text
20-request sequential stability test
mixed EN/JA test
cancellation test
network disconnect/reconnect test
worker restart test
```

Preferred soak after cutover candidate is stable:

```text
2 h continuous active test
then one normal 8–10 h daily operating window
```

Reject for:

```text
CUDA OOM
GPU reset
memory growth without recovery
stuck generation lock
corrupted WAV/PCM
near-silent output
reference identity drift
uncaught disconnects
stale-turn playback
```

---

# 24. Cutover Sequence

Do not make IBM the only worker on day one.

## Stage 1 — sidecar

```text
Azure production remains unchanged
IBM worker receives benchmark traffic only
```

## Stage 2 — shadow/manual traffic

```text
selected real clauses copied to IBM for timing/quality comparison
IBM audio not automatically played unless explicitly testing
```

## Stage 3 — primary TTS, Azure fallback

```text
cache miss
  ↓
IBM V100 primary
  ↓ failure/timeout
Azure optimized CPU fallback
```

## Stage 4 — realtime clause pipeline

Enable streaming LLM partials and first-clause dispatch only after IBM short-clause latency is stable.

## Stage 5 — true PCM streaming

Enable `/tts/stream` only after the strict first-audio proof passes.

Do not decommission the Azure Qwen service during the IBM trial period.

---

# 25. Failure Policy

Fallback order:

```text
IBM true stream fails
  -> IBM complete short-clause /tts

IBM worker fails
  -> Azure optimized Qwen /tts

IBM networking fails
  -> Azure optimized Qwen /tts

IBM CUDA unavailable on restart
  -> mark IBM unhealthy; DO NOT silently infer on IBM CPU
```

A health failure must remove IBM from scheduling until it passes a new warm health check.

---

# 26. Rollback

Rollback must require configuration changes, not code surgery.

Keep:

```text
Azure Qwen service unchanged and runnable
existing complete-WAV client path
existing production API key material private
current Azure reference/prompt/cache files
```

Rollback action:

```text
TSUKI_QWEN_TTS_URL -> current Azure worker path
restart/reload application configuration
verify /health
run one known short smoke line
```

Do not delete IBM evidence when rolling back; failed evidence is needed to decide whether the hardware or software path was wrong.

---

# 27. Evidence Files to Produce

During execution, add:

```text
voice/qwen3-tts/IBM_V100_HOST_BASELINE.md
voice/qwen3-tts/IBM_V100_GPU_RESULTS_<date>.md
voice/qwen3-tts/IBM_V100_REALTIME_RESULTS_<date>.md
```

CSV/JSON benchmark rows should continue using the existing benchmark-evidence tooling where compatible.

Record at minimum:

```text
region + zone
profile
gpu model
driver
CUDA runtime
PyTorch version
qwen-tts version
model revision
reference SHA
precision
attention backend
warm/cold state
TTFA where meaningful
runtime
audio duration
RTF
VRAM
GPU utilization
quality verdict
```

Do not put credentials or public secrets into evidence files.

---

# 28. Implementation Order

Execute in this exact order unless new measured evidence proves a reason to change it:

```text
1. Record IBM hourly quote + quota.
2. Check Osaka gx2-8x64x1v100 availability.
3. If unavailable, check Tokyo.
4. Provision one Ubuntu V100 VSI.
5. Install/verify NVIDIA driver + CUDA.
6. Capture current Azure Python/Qwen package versions.
7. Make the Qwen worker device/dtype configurable with CPU rollback preserved.
8. Add truthful GPU fields to /health.
9. Deploy same Base/full-ICL identity to IBM.
10. Verify exact reference SHA and model identity.
11. Verify model and prompt are resident on cuda:0.
12. Warm the worker once.
13. Run controlled V100 1/2/3/6 benchmark.
14. Perform FP16 SDPA/eager bounded comparison.
15. Require RTF < 1 before calling the hardware realtime-capable.
16. Wire existing StreamingClauseChunker into the LLM partial-response path.
17. Dispatch first stable clause to IBM immediately.
18. Preserve complete-WAV fallback and cancellation.
19. Measure user-end/LLM-clause/TTS/playback latency end-to-end.
20. Attempt true PCM streaming only through a V100/full-ICL-compatible path.
21. Run quality + stability + barge-in gates.
22. Promote IBM to primary with Azure fallback.
23. Run one normal 8–10 h operating window.
24. Produce final IBM migration evidence and decision.
```

---

# 29. Definition of “Realtime PASS”

The migration is **not complete** merely because the service runs on a V100.

## Required for conversational realtime

```text
Qwen verified on cuda:0
model stays resident in VRAM
full-ICL identity preserved
short-clause warm generation <= 2.0 s
RTF <= 1.0, preferred <= 0.6
first stable LLM clause dispatched before full reply completes
next clause may synthesize while prior audio plays
barge-in/cancellation works
no stale audio after a new turn
quality accepted
```

## Required before claiming true audio streaming

```text
first model audio frame produced before full synthesis completion
first audio bytes received before full synthesis completion
playback begins before full synthesis completion
no post-hoc WAV slicing masquerading as streaming
```

Preferred end-user latency targets:

```text
LLM first stable clause: as early as provider allows
IBM short-clause TTS: <= 1.0–1.5 s preferred
first playback after clause ready: <= 2.0 s hard target
steady playback: no starvation, RTF < 1
```

---

# 30. Stop Conditions

Stop optimization and escalate rather than wasting trial credit if any of these remain true after the bounded V100 pass:

```text
warm RTF > 1.0
short 6–12 word clause routinely > 3 s
quality materially worse in FP16
V100 streaming backend requires unsupported modern-GPU features
VRAM instability/OOM under one request
network path adds enough latency to erase the GPU gain
```

If that happens, the correct next move is a newer inference GPU (L4/A10/A100-class depending price), not another round of CPU thread/token tweaking.

---

# 31. Non-Goals

Do not do these during the IBM V100 migration:

```text
no Qwen retraining
no fabricated trained checkpoint
no x-vector-only downgrade
no helper A/B expansion before the single V100 is fast
no cache redesign
no model-per-request loading
no public unauthenticated TTS endpoint
no fake streaming
no deleting Azure rollback
no lowering global token caps without new quality evidence
no multi-GPU design
```

---

# 32. Final Expected Architecture

If all gates pass:

```text
User speech ends
      ↓
STT final turn
      ↓
LLM streaming response
      ↓
StreamingClauseChunker
      ↓ first stable clause
Azure Main cache/scheduler
      ↓ cache miss
IBM Osaka V100 (Tokyo fallback region)
Qwen3-TTS 0.6B Base / FP16 / full ICL / CUDA
      ↓
first audio
      ↓
play immediately
      +
next LLM clause → next TTS job
      ↓
ordered continuous playback
```

Azure remains the rollback worker until the IBM deployment has enough operational evidence to remove it deliberately.

The success criterion is not “IBM works.” The success criterion is **Tsuki responds with the same voice, on the V100, at sustainable realtime speed, with measured first-audio latency and a tested rollback path.**
