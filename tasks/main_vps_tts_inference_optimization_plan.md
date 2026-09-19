# TsukiAI Main VPS TTS Inference Optimization Plan

> **HISTORICAL / SUPERSEDED:** This document describes the former Base/full-ICL
> runtime. It must not be used to configure production. Use
> `voice/qwen3-tts/TRAINED_CUSTOM_VOICE_PRODUCTION_FIX_PLAN.md`.

## Purpose

This plan starts **after** the Main VPS cache work is already implemented and validated.

Current Main VPS target:

```text
Azure Malaysia West
Standard_B4as_v2
4 vCPU
16 GB RAM
CPU-only
Qwen3-TTS 0.6B
```

Current measured uncached English performance on the live Main VPS:

| Sentences | Generation time | Audio length | RTF |
|---:|---:|---:|---:|
| 1 | 39.9 s | 2.32 s | 17.19 |
| 2 | 48.2 s | 3.60 s | 13.39 |
| 3 | 65.9 s | 6.16 s | 10.70 |
| 4 | 82.2 s | 8.64 s | 9.52 |
| 5 | 100.5 s | 11.36 s | 8.85 |
| 6 | 122.8 s | 14.72 s | 8.34 |

Cache hits are already fast. The remaining bottleneck is **uncached inference**.

Primary objective:

```text
Normal short uncached TTS clause:
TARGET = 1–5 seconds complete generation time

Secondary realtime target:
USER-END → FIRST-AUDIO as low as practical

Sustained playback target:
RTF <= 1.0 preferred
```

This plan does **not** remove the cache layer. The cache remains the first path; inference optimization is the miss path.

---

# 1. Current Live Findings

Observed on the Main VPS:

```text
CPU:
AMD EPYC 7763
4 vCPU

RAM:
15 GiB usable
~7.2 GiB Qwen RSS while loaded/running
0 swap

Qwen runtime:
PyTorch CPU
float32
eager attention
4 intra-op threads
current inter-op policy can reach 4
one uvicorn worker
one Qwen synthesis worker
```

Current deployment configuration also reports:

```text
QWEN_TTS_MODEL=Qwen/Qwen3-TTS-12Hz-0.6B-Base
QWEN_TTS_CHECKPOINT_ID=Qwen/Qwen3-TTS-12Hz-0.6B-Base
QWEN_TTS_GENERATION_VERSION=full-icl-v1
```

This is the proven current production identity: Qwen3-TTS 0.6B Base with Tsuki's
full-ICL reference conditioning. Commit `789fd7c` introduced this architecture
and explicitly specified no Qwen training or fine-tuning; current Azure runtime
evidence matches it, and Azure/AWS object-storage inventories show no separate
trained Qwen artifact.

---

# 2. Non-Negotiable Rules

1. Preserve the working cache implementation.
2. Preserve the current complete-WAV endpoint as a fallback until replacement paths pass quality/reliability gates.
3. Do not retrain Tsuki as part of this optimization pass.
4. Treat Base/full-ICL as the authoritative current production identity unless a separately provenance-verified trained artifact is intentionally introduced later.
5. Every speed optimization must pass a Tsuki voice-quality comparison.
6. No optimization may introduce swap during normal TTS.
7. Do not add Helper A/B to compensate for a 40-second first sentence; optimize one worker first.
8. Prefer measured evidence over architecture guesses.
9. Keep one production rollback path at every stage.
10. Never expose TTS secrets or internal helper endpoints publicly.

---

# 3. Gate 0 — Establish the Truthful Production Identity

The original Qwen introduction commit, `789fd7c`, established the production
architecture as the official `Qwen/Qwen3-TTS-12Hz-0.6B-Base` model with Tsuki's
Japanese reference/transcript in full ICL mode and explicitly stated that no
training or fine-tuning is performed. Current live Azure evidence matches that
architecture. No separate trained Qwen checkpoint, LoRA, adapter, or trained
artifact has been found in the Azure deployment tree or the available Azure/AWS
object-storage inventories.

Therefore the current production classification is:

```text
model: Qwen/Qwen3-TTS-12Hz-0.6B-Base
conditioning: full ICL reference conditioning
reference required: yes
x_vector_only_mode: false
runtime LoRA: none
Qwen training/fine-tuning artifact: none
```

The later uncommitted optimization/realtime planning premise that a "finished
trained checkpoint" already existed had no artifact provenance. Treat that as a
planning defect, not as evidence that production is missing an expected model.

Required current-production evidence:

```text
/health model/checkpoint identity
generation version
source reference SHA-256 and matching live /health conditioning.reference_sha256
reference/ICL behavior
runtime LoRA state
audio-cache identity/version inputs
one-sentence through six-sentence baseline
```

Health and cache metadata must truthfully describe the deployed Base/full-ICL
runtime.

The original source reference has a read-only SHA-256 of:

```text
CE93BB53F197F0683D46AB4AD240E3D434EF6E2B2340190BE98DFE382A0F6530
```

This records the source reference bytes only. Do not claim live reference
equality until `/health` `conditioning.reference_sha256` matches this digest.

Required cache-key rule:

```text
checkpoint/model identity must be part of every audio cache key
```

The current live cache implementation satisfies this by including
`checkpoint_id`/`model_name` and `generation_version` in `build_cache_key`.

### Future trained-artifact gate

If a trained Qwen artifact is intentionally introduced later, Gate 0 must be
re-run before benchmarking that artifact. At that point classify it as one of:

```text
A. full fine-tuned checkpoint
B. base model + merged LoRA
C. base model + runtime LoRA
D. trained voice artifact that still requires full ICL/reference conditioning
```

Record its exact path/source, immutable checkpoint/build identity or digest,
runtime-LoRA requirement, reference/ICL requirement, `/health` identity, cache
identity, and a fresh baseline. Do not infer or fabricate those values.

### Gate 0 PASS

```text
/health reports the actual Base/full-ICL production identity
source reference SHA-256 is recorded and live /health conditioning.reference_sha256 matches it
cache identity includes the production checkpoint/model identity
reference/ICL behavior and runtime-LoRA=false are recorded accurately
one-sentence through six-sentence baseline is recorded
```

Current production does not require a trained Qwen artifact to pass Gate 0.

---

# 4. Gate 1 — PyTorch Low-Risk Optimization

This is the first performance pass because it keeps the current Python/PyTorch architecture.

## Variant matrix

Benchmark exactly these variants using the same text corpus and production model identity:

| Variant | dtype | Attention | Intra-op | Inter-op |
|---|---|---|---:|---:|
| A | FP32 | eager | 4 | current/reference |
| B | FP32 | SDPA | 4 | 1 |
| C | FP32 | SDPA | 3 | 1 |
| D | FP32 | SDPA | 2 | 1 |
| E | FP32 | eager | 4 | 1 |

Do not change sampling parameters during this matrix.

## Metrics

For every variant record:

```text
startup/model-load time
idle RSS
peak RSS
swap
CPU usage
1-sentence generation time
2-sentence generation time
3-sentence generation time
6-sentence generation time
audio duration
RTF
first-audio time if streaming becomes available
voice-quality result
```

## Selection rule

Choose the fastest variant that:

```text
keeps voice output equivalent to reference
uses zero swap
remains stable over repeated requests
shows no request corruption or deadlocks
```

### Gate 1 target

A useful win is at least:

```text
>= 15% lower one-sentence generation time
```

If SDPA/thread tuning gives only a small improvement, keep the best safe variant and continue.

---

# 5. Gate 2 — Token/Generation Budget Optimization

Current deployment allows:

```text
QWEN_TTS_MAX_NEW_TOKENS=2048
```

That may be unnecessarily high for short realtime clauses.

Do **not** blindly lower it globally.

Benchmark a safe matrix such as:

```text
2048  reference
1536
1024
768
512
```

Use short, medium and long TTS clauses.

Reject any value that causes:

```text
truncated speech
missing ending phonemes
premature stopping
quality degradation
unstable Japanese/English generation
```

If shorter token ceilings do not reduce runtime materially, revert and move on.

---

# 6. Gate 3 — Native/Quantized CPU Backend Benchmark

PyTorch FP32 is now the main suspect because RTF is ~8–17.

Do not replace it immediately. Build controlled experimental workers.

## Candidate order

```text
1. native/GGML INT8 or Q8
2. native/GGML Q5 if supported cleanly
3. native/GGML Q4 / INT4
4. PyTorch INT8 only if implementation quality is good
```

Avoid spending first-pass time on Intel-only acceleration for the Azure AMD EPYC VM.

## Requirements before benchmarking

Each candidate must prove:

```text
production model conversion succeeds
full-ICL voice-cloning semantics are preserved
English supported
Japanese supported
same output sample rate expected by TsukiAI
API can be wrapped behind existing /tts contract
```

The original production model/reference identity remains authoritative and untouched.

## Benchmark matrix

For each candidate:

```text
cold start
warm start
one short sentence
2 sentences
3 sentences
6 sentences
long detailed clause
repeated 20-request stability run
```

Record:

```text
runtime
RTF
peak RAM
CPU
output size
quality score
crashes/errors
```

### Gate 3 target

Strong candidate:

```text
1 sentence <= 10 sec
RTF <= 3
```

Preferred production candidate:

```text
1 sentence <= 5 sec
RTF <= 1–2
```

If a native backend reaches the target while preserving Tsuki's voice, it becomes the leading production runtime.

---

# 7. Voice Quality Gate

Every backend/precision change must be A/B compared against the known-good reference.

Use a fixed evaluation set containing:

```text
short English
long English
Japanese
mixed Japanese/English
numbers
names
questions
emotional sentence
fast punctuation-heavy sentence
```

Score:

```text
voice identity
Tsuki accent/style
pronunciation
prosody
pitch stability
rhythm
noise/artifacts
sentence endings
Japanese quality
English quality
```

Reject any backend that is faster but audibly damages Tsuki.

Do not choose the winner from speed alone.

---

# 8. Gate 4 — True First-Audio Streaming

Once the best inference backend is known, reduce perceived latency.

Current problem:

```text
Qwen generates complete waveform
→ WAV is encoded
→ C# receives full response
→ playback begins
```

Target:

```text
Qwen starts producing PCM/audio frames
        ↓
first safe audio buffer
        ↓
PLAY immediately
        +
continue generating later audio
```

## Preferred transport

Long-term:

```text
Main API / scheduler
↔ WebSocket or binary streaming HTTP
↔ TTS worker
```

Temporary benchmark transport may use chunked HTTP if simpler.

## Required streaming metrics

```text
request accepted at
first TTS frame generated at
first audio bytes sent at
first audio bytes received at
playback started at
synthesis completed at
underrun count
buffer depth over time
```

### Initial playback buffer tests

Benchmark:

```text
0 ms
250 ms
500 ms
750 ms
1000 ms
1500 ms
```

Choose the smallest buffer that avoids repeated audible starvation.

### Gate 4 PASS

```text
first audio starts before full synthesis completes
no duplicated/missing samples
no audible join artifacts
cancellation works
barge-in discards stale audio
```

---

# 9. Gate 5 — Clause-Level Streaming From the LLM

The LLM already has a streaming path.

Build:

```text
AssemblyAI end_of_turn
        ↓
streaming LLM
        ↓
stable clause/sentence chunker
        ↓
TTS clause #1 immediately
        ↓
PLAY #1
        +
TTS clause #2 while #1 plays
```

Do not send individual tokens to TTS.

Initial heuristic:

```text
first clause:
~6–12 spoken words

later clauses:
~12–25 spoken words
```

Use punctuation boundaries first.

The existing LLM length controls remain authoritative; do not create a second unrelated response-shortening system.

---

# 10. Gate 6 — Adaptive Pressure Mode

If the optimized worker still cannot keep audio ahead of playback, adapt response length for voice only.

## Normal

```text
existing Tsuki behavior
1–2 short sentences
<= normal project reply cap
```

## Pressure

Trigger if rolling metrics show:

```text
short-clause TTS > 5 sec repeatedly
or
buffer underruns
or
RTF > 1 with falling ready-audio buffer
```

Then temporarily request:

```text
1 short natural spoken sentence
~180–220 chars target
```

## Critical

If severe starvation continues:

```text
~100–160 chars
prefer cached/pregenerated speech
```

Return automatically to Normal when rolling performance recovers.

Do not use fake filler just to hide latency.

---

# 11. Gate 7 — VM Class Benchmark

`Standard_B4as_v2` is burstable and can become slower under sustained CPU usage.

After software tuning, benchmark the winning runtime on:

```text
A. current Azure B4as_v2
B. non-burstable Azure 4-vCPU / 16GB candidate
C. AWS m7i.xlarge or selected 4-vCPU / 16GiB candidate
```

Use the same checkpoint, runtime, quantization and benchmark text.

Measure at:

```text
fresh boot/warmup
30 min sustained load
2 h sustained load
```

For B-series also capture CPU-credit metrics.

Select based on sustained performance, not the first five minutes.

---

# 12. Helper A/B Gate

Do not enable production multi-worker scheduling until a single worker is reasonably fast.

Recommended enable threshold:

```text
one short sentence <= 10 sec
```

Preferred threshold:

```text
one short sentence <= 5 sec
```

Then the planned workers become useful:

```text
Main Qwen     → sentence #1
Helper A      → sentence #2
Helper B      → sentence #3
```

For 4+ sentences:

```text
next clause → first healthy idle worker
```

Playback must remain sequence-ordered regardless of completion order.

---

# 13. Central Scheduler Requirements

The Main VPS remains authoritative for:

```text
cache
single-flight
turn ID
sequence numbering
worker health
worker score
ordered playback
barge-in
cancellation
```

Worker score should consider:

```text
health
idle/busy state
rolling generation time
rolling RTF
queue depth
network latency
recent failures
```

Do not hardcode odd/even sentence assignment.

---

# 14. Cache Integration With Faster Backends

The existing cache remains in front of all workers.

```text
request
  ↓
RAM cache
  ↓ miss
disk cache
  ↓ miss
single-flight
  ↓
scheduler / TTS backend
```

Cache keys must add backend-sensitive parameters only when those parameters materially change audio.

Never mix audio generated by:

```text
different checkpoint
different voice version
different incompatible generation parameters
different output format/sample rate
```

Do not invalidate the whole cache merely because the serving implementation changed if produced audio semantics are proven identical and the key contract still matches.

---

# 15. Common Phrase Strategy

Keep the current small pre-cache.

Expand only from measured real usage.

Maintain counters for phrases that:

```text
occur frequently
are short
are voice-safe in multiple contexts
```

Do not pre-generate hundreds of speculative phrases.

A cache hit is excellent, but arbitrary new dialogue must still become fast enough to be usable.

---

# 16. Barge-In and Cancellation

Realtime production is not accepted without barge-in.

When human speech starts:

```text
stop current playback
cancel current LLM generation if obsolete
cancel/abort active TTS where backend supports it
clear queued old-turn clauses
clear queued old-turn audio
increment/new turn ID
```

Any audio arriving later with the old turn ID is discarded.

Completed cache entries remain valid.

---

# 17. Failure and Rollback Strategy

Keep these rollback layers:

```text
native/quantized backend fails
→ PyTorch optimized backend

PyTorch optimized fails
→ current PyTorch reference

streaming endpoint fails
→ complete-WAV /tts

helper fails
→ remaining workers

all helpers fail
→ Main worker only
```

No optimization may remove the known-good fallback until the replacement has passed sustained testing.

---

# 18. Benchmark Corpus

Create a deterministic benchmark corpus with at least:

```text
10 very short phrases
10 one-sentence replies
10 two-sentence replies
10 three-sentence replies
5 long/detail replies
5 Japanese replies
5 mixed-language replies
```

Every backend uses the exact same texts.

Do not benchmark with previously cached audio when measuring inference.

Use a dedicated benchmark namespace or force-cache-miss mode.

---

# 19. Benchmark Result Schema

For every run record:

```text
timestamp
host/cloud/region
VM SKU
CPU model
backend
backend version
checkpoint ID
precision/quantization
attention implementation
intra-op threads
inter-op threads
text ID
character count
word count
generation time
audio duration
RTF
TTFA
peak RSS
swap
CPU average/peak
cache state
success/failure
quality result
```

Save both CSV and Markdown summaries.

---

# 20. Decision Matrix

Weight production selection approximately:

| Criterion | Weight |
|---|---:|
| USER-END → FIRST-AUDIO | 25% |
| Short-clause generation time | 20% |
| Sustained RTF / underrun resistance | 20% |
| Voice quality | 20% |
| Reliability | 10% |
| RAM/CPU efficiency | 5% |

A backend with worse voice quality cannot win only because it is faster.

---

# 21. Immediate Execution Order

Execute in this order:

```text
1. close Gate 0 for the proven Base/full-ICL production identity
2. use the recorded 1–6 sentence baseline; rerun it if production identity/configuration changes
3. change inter-op to 1 and benchmark
4. benchmark FP32 SDPA with intra 4/3/2
5. test safe max_new_tokens reductions
6. select best PyTorch configuration
7. build one native/GGML experimental worker
8. benchmark INT8/Q8
9. benchmark Q5/Q4/INT4 if voice quality survives
10. select fastest quality-safe inference backend
11. implement true audio streaming
12. implement LLM clause → TTS pipeline
13. add adaptive pressure mode
14. benchmark B4as vs non-burstable Azure vs AWS candidate
15. only then activate Helper A/B production scheduling
16. run long-session reliability test
17. produce WINNER.md
```

---

# 22. Parallel Subagent Plan

The local orchestration agent is authorized to deploy visible Auvrynt/Codex subagents for this optimization work.

Suggested parallel roles:

```text
Agent A — production-identity/deployment archaeologist
Verify model/reference provenance, cache versioning, and health truthfulness.

Agent B — PyTorch CPU optimizer
Benchmark eager/SDPA and thread matrices.

Agent C — native backend researcher/implementer
Audit and prototype GGML/native INT8/Q4 paths.

Agent D — benchmark/evidence collector
Own deterministic corpus, metrics and CSV/Markdown results.

Agent E — audio quality reviewer
Blind/structured comparison of Tsuki voice identity and artifacts.

Agent F — streaming integration engineer
Implement true first-audio streaming behind compatibility fallback.

Agent G — reliability/security reviewer
Cancellation, barge-in, auth, secrets, resource bounds, rollback.

Agent H — cloud performance reviewer
Compare Azure burstable/non-burstable and AWS sustained CPU results.
```

Rules:

```text
parallel experiments must not silently overwrite production
production changes require benchmark evidence
all accepted changes must be reproducible
subagents report exact commands/config/results
```

---

# 23. Acceptance Gates

## Minimum acceptable improvement

```text
1 sentence <= 10 sec
quality preserved
zero swap
stable 20-request run
```

## Desired production target

```text
1 sentence <= 5 sec
USER-END → FIRST-AUDIO <= ~2–4 sec where streaming allows
RTF trending toward <= 1–2
no repeated underruns
```

## Ideal target

```text
uncached short clause 1–3 sec
RTF <= 1
first playable audio well before complete synthesis
```

Do not fake realtime by cutting a completed WAV after generation.

---

# 24. Stop Conditions

Stop an experiment immediately if it causes:

```text
swap thrashing
OOM
corrupt audio
voice identity regression
missing sentence endings
repeated crashes
cache corruption
stale-turn playback
security regression
```

Rollback to the previous accepted configuration and record the rejection reason.

---

# 25. Required Final Outputs

The optimization effort is complete only when it produces:

```text
WINNER.md
benchmark-results.csv
benchmark-results.md
backend-comparison.md
quality-comparison.md
rejected-experiments.md
production-config.md
rollback.md
```

`WINNER.md` must state:

```text
selected backend
selected precision
selected attention implementation
selected thread counts
selected VM class
checkpoint ID
one-sentence generation time
RTF
TTFA
peak RAM
quality result
fallback path
why it won
```

---

# 26. Final Principle

The cache solved repeat requests.

Now the job is to make **cache misses** fast enough for natural conversation.

The priority is:

```text
correct production model/reference identity
        ↓
fast single worker
        ↓
low first-audio latency
        ↓
stable realtime clause pipeline
        ↓
then multi-worker helpers
```

Do not use more VPSs as a substitute for fixing a 40-second first sentence.
