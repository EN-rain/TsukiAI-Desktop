# Gate 3 native CPU backend preflight

> **HISTORICAL / SUPERSEDED:** This preflight targets the former Base/full-ICL
> path and is not an active production configuration.

This preflight is for the production Qwen3-TTS 12 Hz 0.6B **Base + full-ICL**
voice-clone path. A native backend is eligible only if it preserves the same
reference-audio + exact-reference-text conditioning semantics used by
`api.py` (`x_vector_only_mode=False`). A faster speaker-embedding-only clone is
not an equivalent Tsuki runtime.

## Current candidate order

This section ranks implementations by semantic eligibility for Tsuki's current
Base/full-ICL contract. It does not replace the optimization plan's precision
order. After a backend proves full-ICL parity at its native reference precision,
apply the plan's Q8/INT8 -> Q5 -> Q4/INT4 quality-gated sequence where that
backend actually supports those formats.

### 1. `qwen3-tts-rs` — first native experiment

Repository: `https://github.com/TrevorS/qwen3-tts-rs`

Observed upstream HEAD during this audit:

```text
711ceee07cad92673f86de8997bdf54c30caa49f
```

The project currently documents:

- CPU inference;
- Qwen3-TTS 0.6B Base support;
- voice cloning using either x-vector or ICL conditioning;
- streaming synthesis;
- English and Japanese support.

Those capabilities make it a semantic match worth testing. They do not prove
performance or quality on Azure AMD EPYC. The commit above is an audit pin, not
an approved production dependency. If the experiment is built later, record the
actual checked-out commit and refuse an unpinned moving branch.

At the pinned revision, upstream documents CPU components as F32 and does not
document an INT8/Q8 CPU model path. Use this candidate first to validate native
full-ICL semantics and measure whether its implementation is competitive; do not
count an F32 run as satisfying the plan's quantized-backend experiment.

### 2. `qwen3-tts.cpp` — blocked for the current Tsuki voice contract

Repository: `https://github.com/predict-woo/qwen3-tts.cpp`

Observed upstream HEAD during this audit:

```text
b3ba14077cf1b3e11b86e5f84aa9184605c89b28
```

Its converter supports the 0.6B Base model and exposes F16, Q8_0, and Q4_K
conversion paths. However, upstream issue #18 documents that reference-text
conditioning is not implemented. That means its current clone path cannot yet
replace Tsuki's full-ICL prompt without changing voice semantics. Keep it out of
the production benchmark matrix until that gap is fixed and independently
verified.

Do not treat a successful Q8 conversion as sufficient evidence. For Tsuki, the
backend must preserve the ICL reference text/codes as well as speaker identity.

## Azure B4as_v2 CPU-credit evidence

The current slow Base/full-ICL baseline should not be attributed to exhausted
B-series CPU credits. Azure Monitor showed `CPU Credits Remaining` staying at
approximately `2304` over the recent two-hour observation window. Azure lists
`2304` as the maximum banked-credit value for `Standard_B4as_v2`, so the VM was
not in a depleted-credit state during that observation window.

Earlier TTS workloads reached roughly 95% CPU, which confirms the inference path
can drive the VM hard, but high CPU utilization by itself does not prove credit
exhaustion. Treat the current ~40-100 second synthesis times as an inference
runtime/backend problem unless measurements from the exact benchmark interval
show the credit bank falling to zero or another host-level throttle.

Keep the optimization plan's B4as-vs-non-burstable/AWS comparison. CPU-credit
metrics still matter for long sustained-load testing because B-series VMs can
consume credits above their base CPU level; this finding only rules out current
credit exhaustion as the explanation for the measured baseline slowness.

## Required experiment order

Use the official Python worker as the correctness oracle. Run experiments in
this order:

1. build the pinned `qwen3-tts-rs` revision on an isolated worker;
2. use the same `Qwen/Qwen3-TTS-12Hz-0.6B-Base` model family;
3. preprocess the exact production Tsuki reference audio and transcript;
4. prove the native runtime is in ICL mode, not speaker-only mode;
5. synthesize the locked English, Japanese, and mixed-language quality subset;
6. run the same short/medium/long timing probes used by the Python baseline;
7. record cold start, warm start, generation seconds, audio duration, RTF,
   TTFA when streaming, peak RSS, swap, CPU, and failures;
8. complete a 20-request stability run;
9. run blind quality review before considering the backend eligible;
10. only after F32/F16-equivalent semantics pass should any quantized/native
    precision experiment be considered.

## Hard rejection rules

Reject the candidate immediately if any of these occur:

- reference text is ignored or the runtime falls back to x-vector-only cloning;
- generated sentence endings are truncated or missing;
- English/Japanese code switching regresses materially;
- Tsuki identity, accent, pacing, pronunciation, or prosody audibly regresses;
- sample rate/output framing does not match the existing `/tts` contract;
- swap is used during normal synthesis;
- repeated requests crash, deadlock, leak unbounded memory, or corrupt audio;
- the implementation cannot be pinned to an immutable source revision.

## Acceptance thresholds

The optimization plan's Gate 3 thresholds remain authoritative:

```text
strong candidate:          one short sentence <= 10 s, RTF <= 3
preferred production:      one short sentence <= 5 s,  RTF <= 1-2
```

Speed alone cannot pass the gate. Voice quality must be reference-equivalent
enough for the product and the complete-WAV Python worker remains the rollback
path until a replacement passes sustained testing.

## Streaming note

The official `qwen-tts` Python `generate_voice_clone()` API currently returns a
complete waveform; its `non_streaming_mode=False` option simulates streaming
text input rather than yielding generated PCM incrementally. A native backend
that truly emits audio before synthesis completes can therefore serve both Gate
3 and the later first-audio Gate 4, but Gate 3 quality/correctness must pass
before wiring streaming into the application.
