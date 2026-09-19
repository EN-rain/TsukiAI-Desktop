# TsukiAI TTS Master Benchmark & Implementation Selection Plan

## Purpose

This document is the **master comparison plan** for TsukiAI realtime TTS on the current target:

```text
4 vCPU
16 GB RAM
CPU-only
Qwen3-TTS 0.6B trained voice
AssemblyAI realtime STT
streaming LLM available
```

The goal is **not to guess which architecture is fastest**. The goal is to build several controlled variants, benchmark them on the same machine with the same trained checkpoint, and select the best production architecture from measured results.

The existing realtime implementation plan remains in:

```text
tasks/plan.md
```

That file is one candidate implementation path. This master document compares it against the sentence-pipeline approach, concurrency experiments, cache-first strategies, CPU-backend optimizations, and fallback modes.

---

# 1. Primary Performance Goal

For a normal short realtime TTS clause:

```text
TARGET COMPLETE TTS GENERATION TIME = 1–5 seconds
```

Interpretation:

```text
1–5 sec
→ acceptable target range for an uncached short clause

>5 sec
→ acceptable when the sentence/paragraph is genuinely long

>5 sec repeatedly for short clauses
→ remediation required
```

This target is separate from:

```text
TTFA = request → first playable audio
RTF  = synthesis seconds / generated audio seconds
```

Production selection must consider all three.

A plan does not win merely because total synthesis is fast if first-audio latency is poor or playback constantly starves.

---

# 2. Existing LLM Length Controls Confirmed in the Project

The project already contains multiple mechanisms that discourage long voice replies.

## Prompt-level control

`TsukiAI.Core/Services/PromptBuilder.cs` already injects:

```text
Respond in 1-2 short, natural sentences.
Keep under 360 characters.
Keep to at most 2 sentences unless user explicitly asks for details.
```

The normal remote inference path explicitly uses:

```text
oneToTwoSentences: true
```

## Generation-level control

Current defaults include:

```text
GenerationMaxTokens = 80
GenerationMaxReplyChars = 360
```

Intent tuning further limits output.

Examples:

```text
Command:
<= 80 tokens

Casual:
48–160 tokens

Question:
up to 256 tokens

Emotional support:
up to 220 tokens
```

## Post-generation hard limit

`ResponsePostProcessor.CleanAndValidate(...)` calls:

```text
EnforceLength(...)
```

with the normal 360-character maximum.

Therefore the system already has:

```text
prompt instruction
+
generation token cap
+
post-generation character cap
```

The realtime work should reuse this system rather than add a completely separate generic anti-long-response prompt.

---

# 3. Existing Qwen Text Limit

The current Python Qwen service defaults to:

```text
QWEN_TTS_MAX_TEXT_CHARS = 280
```

The LLM may produce up to roughly:

```text
360 characters
```

Do not simply increase Qwen to 360 and synthesize the entire reply in one call.

Preferred realtime behavior:

```text
LLM response
    ↓
natural sentence/clause splitter
    ↓
segment 1 <= Qwen limit
segment 2 <= Qwen limit
segment 3 <= Qwen limit
```

The first segment should normally be considerably shorter than 280 characters to improve first-play latency.

---

# 4. Benchmark Principle

Every candidate must be tested using:

```text
same VPS
same trained Qwen checkpoint
same voice settings
same reference/conditioning mode
same text set
same process startup state
same thread-count experiment matrix
same quality scoring process
```

Do not compare one plan cold and another plan warm.

Do not compare different voice-generation settings unless the plan explicitly changes them.

---

# 5. Candidate Plan 0 — Current Serial Baseline

This is the control group.

```text
AssemblyAI final
    ↓
complete LLM answer
    ↓
complete Qwen synthesis
    ↓
complete WAV/PCM conversion
    ↓
playback
```

## Why keep it

It establishes the amount of improvement achieved by every later plan.

## Expected strengths

- simplest path;
- best continuity/prosody for a full utterance;
- easiest to debug;
- current compatibility behavior.

## Expected weaknesses

- waits for full LLM;
- waits for full TTS;
- no compute/playback overlap;
- worst likely user-end-to-first-audio latency.

## Required measurements

```text
LLM total time
TTS total time
USER-END → FIRST-AUDIO
RTF
CPU peak/average
RAM peak
quality score
```

---

# 6. Candidate Plan A — Existing Realtime Plan (`tasks/plan.md`)

This is the current detailed realtime plan.

Core concept:

```text
AssemblyAI streaming STT
    ↓
streaming LLM
    ↓
realtime TTS path
    ↓
incremental playback
```

Its eventual target includes true Qwen PCM streaming, cancellation, barge-in, realtime buffering, and end-to-end observability.

## Test this as its own candidate

Do not assume it is automatically the production winner merely because it is the most realtime-oriented.

Measure engineering complexity as well as latency.

## Key success condition

True PCM streaming only counts if:

```text
first generated PCM chunk
<
full synthesis completion time
```

Generating the full WAV and slicing it afterward does not count.

---

# 7. Candidate Plan B — Sentence/Clause Pipeline (Recommended First Practical Test)

This is the approach discussed for a fast LLM producing 2 or more sentences.

Example LLM response:

```text
Sentence 1: "Yeah, I think we should go."
Sentence 2: "It'll probably be safer there."
```

Pipeline:

```text
LLM finishes or streams sentence 1
        ↓
TTS generate sentence 1
        ↓
PLAY sentence 1 immediately
        ↓
while sentence 1 is PLAYING:
        ↓
TTS generate sentence 2
        ↓
sentence 2 waits in ready-audio queue
        ↓
when sentence 1 ends:
        ↓
PLAY sentence 2 immediately
```

Important:

```text
sentence 2 does NOT wait for sentence 1 playback to finish before generating
```

It waits only for the single Qwen synthesis worker to finish generation #1.

## Architecture

```text
Streaming LLM
      ↓
Sentence/Clause Splitter
      ↓
TTS Text Queue
      ↓
ONE Qwen inference worker
      ↓
Ready Audio Queue
      ↓
Playback
```

## CPU behavior

Only one Qwen synthesis runs at a time.

Playback of sentence 1 is cheap, so most CPU can be reused for synthesis of sentence 2.

## RAM behavior

Very small increase beyond the loaded model.

Queued PCM clips are normally only KB to a few MB.

## Why this may win on 4 vCPU

It overlaps:

```text
LLM generation
TTS generation
playback
```

without forcing two Qwen generations to fight over the same four cores.

---

# 8. Candidate Plan C — Two Simultaneous TTS Jobs

This is an explicit experiment, not a recommended default.

Concept:

```text
Sentence 1 → Qwen job A
Sentence 2 → Qwen job B

run both simultaneously
```

Potential implementations:

```text
C1. two concurrent generations on one model instance
C2. two independent Qwen model processes
C3. two worker threads/tasks if backend safely supports it
```

## Main risk

On 4 vCPU, both generations may compete for the same CPU resources.

Possible result:

```text
single-worker:
S1 = 3 sec
S2 = 3 sec

parallel workers:
S1 = 5–7 sec
S2 = 5–7 sec
```

This is hardware-dependent, so benchmark rather than assume.

## RAM risk

If two independent model processes are required:

```text
model memory may be duplicated
```

This could make 16 GB RAM uncomfortable and increase swap risk.

## PASS condition

Only keep Plan C if it improves:

```text
USER-END → FIRST-AUDIO
AND
second-sentence readiness
```

without causing:

```text
higher underruns
excessive RAM
swap
quality regression
or worse p95 latency
```

If it only increases CPU utilization but not user-visible speed, reject it.

---

# 9. Candidate Plan D — Hybrid Single-Worker Pipeline + Short First Clause

This is Plan B with latency-biased chunk sizing.

Instead of equal chunks:

```text
first clause: very short
later clauses: larger
```

Example:

```text
"Yeah, probably."
"I think heading back to the village is safer right now."
```

Goal:

```text
get first audible response quickly
then hide larger TTS generation behind playback
```

## Initial benchmark ranges

```text
first segment:
6–12 spoken words

later segments:
12–25 spoken words
```

These are benchmark starting points, not permanent hard limits.

## What to measure

- first audio latency;
- total turn latency;
- prosody quality;
- gap between sentence 1 and 2;
- Qwen calls per turn;
- total CPU time.

---

# 10. Candidate Plan E — Cache-First Realtime

This plan focuses on **avoiding inference**.

Lookup path:

```text
request
  ↓
RAM audio cache?
  ├── hit → play immediately
  ↓ miss
Disk audio cache?
  ├── hit → play immediately
  ↓ miss
Qwen generation
  ↓
cache result
```

Use both:

```text
whole-response cache
+
segment/sentence cache
```

## Additional optimizations

- common phrase bank;
- pregenerated greetings;
- battle barks;
- confirmations;
- predictable NPC lines;
- likely next-line prefetch;
- cache warming after deployment.

## Expected outcome

For repeated text, this plan should beat all raw-inference plans because:

```text
no TTS inference occurs
```

## Important

Measure cache-hit and cache-miss performance separately.

Do not hide a slow cache-miss path behind an artificially high hit-rate test.

---

# 11. Candidate Plan F — Adaptive Pressure Mode

If the normal realtime path cannot keep short clauses near the 1–5 second goal, dynamically reduce spoken-output size.

This must be **voice-path-specific**, not a permanent reduction in general text-chat quality.

## Normal mode

Reuse current project behavior:

```text
1–2 short sentences
<= 360 chars
existing token profiles
```

## Pressure mode

Suggested experiment:

```text
1 short sentence
~180–220 chars
~48–64 tokens where intent permits
```

## Critical mode

Suggested experiment:

```text
1 very short sentence
~100–160 chars
cache/pregenerated speech preferred
```

## Activation metrics

Use rolling:

```text
short-clause generation time
RTF
ready-audio buffer duration
pending TTS queue depth
underrun count
```

Example policy:

```text
Healthy:
short TTS <= 5 sec
and buffer stable
→ NORMAL

Slow:
short TTS > 5 sec
or repeated low buffer
→ PRESSURE

Severe:
RTF far > 1
or repeated underruns
→ CRITICAL
```

Return to NORMAL automatically when conditions recover.

---

# 12. Candidate Plan G — True Incremental Qwen PCM Streaming

This is the most aggressive realtime plan.

Desired behavior:

```text
Qwen starts generation
    ↓
PCM chunk 1 emitted
    ↓
PLAY
    ↓
Qwen continues generating PCM chunk 2, 3, 4...
```

This can reduce TTFA further than waiting for one complete clause.

## Requirement

Do not call this plan implemented unless:

```text
first PCM arrives before generation completes
```

## Risks

- third-party backend complexity;
- compatibility with trained checkpoint;
- cancellation complexity;
- chunk-boundary glitches;
- backend maintenance;
- possible CPU throughput loss from small chunk sizes.

## Compare against Plan B/D

It may have lower TTFA but worse engineering complexity or throughput.

The winner should be chosen by measurements, not architectural elegance.

---

# 13. Candidate Plan H — CPU Backend Optimization

Use the same realtime architecture while changing only raw inference backend/settings.

Test individually:

```text
H0. current FP32 + eager baseline
H1. PyTorch CPU thread tuning
H2. SDPA if supported
H3. IPEX if Intel CPU and supported
H4. INT8
H5. torch.compile warm path
H6. OpenVINO
H7. ONNX Runtime
H8. native optimized backend if compatible
```

Do not combine all optimizations immediately.

Change one variable at a time so the winning change is identifiable.

## Quality gate

Every optimization must preserve:

```text
Tsuki identity
Japanese-influenced English delivery
intelligibility
prosody
vowels/consonants
line endings
low noise
stability
```

Reject a faster backend if voice quality becomes noticeably worse.

---

# 14. Candidate Plan I — Thread Count Matrix

The current service supports PyTorch thread control.

Test:

```text
2 inference threads
3 inference threads
4 inference threads
```

with:

```text
interop threads = 1
```

Reason:

With 4 total vCPU, 3 Qwen threads may produce better **end-to-end** performance because one vCPU remains available for:

```text
.NET API
Node Discord bridge
AssemblyAI networking
audio conversion
OS scheduling
```

Record both raw Qwen speed and full-system speed.

The configuration with the fastest Qwen-only benchmark is not necessarily the fastest voice-agent configuration.

---

# 15. Candidate Plan J — Prefetch / Speculation

Use already-known information to begin useful work before the player explicitly needs audio.

Examples:

```text
AssemblyAI partial transcript
→ safe context/memory/tool preparation

NPC proximity
→ likely greeting pregeneration

current dialogue line playing
→ generate likely next line

choice focus/hover
→ prefetch most probable branch
```

Do not commit spoken responses based on unstable AssemblyAI partial text.

Authoritative conversational response still starts from:

```text
AssemblyAI end_of_turn
```

---

# 16. Candidate Plan K — Stronger Hardware Escape Hatch

This is not a software failure.

If the 4-vCPU machine cannot meet the target after software optimization:

```text
KEEP:
AssemblyAI
streaming LLM
sentence splitter
cache
barge-in
turn IDs
Discord bridge
.NET API

MOVE ONLY:
Qwen inference
```

Compare:

```text
current 4-vCPU VPS
vs
higher-clock/faster CPU instance
vs
small GPU instance
```

This gives a final reference for the maximum improvement obtainable from hardware.

---

# 17. Controlled Benchmark Text Set

Use the same text for all plans.

## Very short

```text
"Okay."
```

## Short

```text
"Yeah, I think we should go."
```

## Two short sentences

```text
"Yeah, I think we should go. It'll probably be safer there."
```

## Medium

```text
"I think we should head back to the village before it gets dark."
```

## Two medium sentences

```text
"I think we should head back to the village before it gets dark. We can figure out what to do next once we're there."
```

## Long

Use a fixed 30–50 word line.

## Paragraph

Use a fixed 60–80 word sample.

The >5 second allowance should be interpreted in context of these longer categories.

---

# 18. Real Conversation Test Set

Synthetic text is not enough.

Also run at least 20 realistic voice turns covering:

```text
casual chat
question
command
emotional-support style response
short factual reply
two-sentence response
explicit request for detail
interruption/barge-in
repeated cached phrase
cache miss
English
Japanese if production requires it
```

---

# 19. Warm vs Cold Testing

For each candidate record:

```text
cold first request
warm request #1
warm request #2
warm request #3
warm request #4
warm request #5
```

Production ranking should primarily use **warm** results because the service is intended to stay persistent.

Cold-start time should still be reported separately.

---

# 20. Metrics Per Test

Record:

```text
STT finalization timestamp
LLM request timestamp
LLM first-token timestamp
LLM first stable clause timestamp
TTS request start
TTS completion
first playable audio
playback start
playback finish
```

Derived:

```text
LLM TTFT
USER-END → FIRST-AUDIO
TTS generation seconds
output audio duration
RTF
sentence-2 ready-before-sentence-1-finished? yes/no
inter-sentence playback gap
CPU average
CPU peak
RAM baseline
RAM peak
swap usage
cache hit/miss
underrun count
cancellation latency
```

---

# 21. CPU Measurement

Measure whole-system CPU, not only Qwen.

Record processes separately where practical:

```text
Python/Qwen
.NET API
Node/Discord bridge
other Tsuki services
```

For Plan C especially, observe:

```text
context switching
CPU saturation
per-job slowdown
```

100% CPU usage is not automatically good if first-audio latency gets worse.

---

# 22. RAM Measurement

Record:

```text
idle VPS RAM
Qwen loaded/warm RAM
peak TTS RAM
queued-audio RAM
second-worker RAM if tested
swap used
```

A plan fails the 16 GB target if it requires regular swapping.

Audio queues themselves should normally consume only a few MB; model/runtime duplication is the main memory risk.

---

# 23. Quality Score

Speed cannot be the only criterion.

Score each sample from 1–5 for:

```text
voice identity
accent preservation
intelligibility
prosody
natural joins
noise/artifacts
sentence ending quality
consistency between chunks
```

Keep a reference audio sample from the current high-quality full synthesis path.

---

# 24. Reliability Score

Also test:

```text
20 consecutive turns
50 consecutive turns
rapid short requests
user interruption
cancel while generating
network reconnect
AssemblyAI reconnect
LLM streaming fallback
TTS fallback
cache corruption/miss
process restart
```

A plan that is 300 ms faster but frequently deadlocks or plays stale audio should lose.

---

# 25. Engineering Complexity Score

Assign each candidate:

```text
1 = simple
2 = low complexity
3 = moderate
4 = high
5 = very high
```

Consider:

```text
new dependencies
third-party forks
custom streaming protocol
cancellation complexity
maintenance burden
debuggability
checkpoint compatibility
```

Complexity should not dominate performance, but close-performing plans should favor the simpler one.

---

# 26. Benchmark Result Table

Use this table for each candidate:

| Plan | Short TTS | USER-END → FIRST-AUDIO | RTF | CPU Avg/Peak | RAM Peak | Gaps/Underruns | Quality | Complexity |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 0 Serial baseline | | | | | | | | |
| A Existing realtime plan | | | | | | | | |
| B Sentence pipeline | | | | | | | | |
| C Dual simultaneous TTS | | | | | | | | |
| D Short-first hybrid | | | | | | | | |
| E Cache-first | | | | | | | | |
| F Pressure mode | | | | | | | | |
| G True PCM streaming | | | | | | | | |
| H Optimized CPU backend | | | | | | | | |
| J Prefetch/speculation | | | | | | | | |
| K Faster hardware reference | | | | | | | | |

---

# 27. Production Winner Rules

The selected production design should satisfy, in priority order:

## Mandatory

```text
stable output
trained Tsuki voice preserved
no repeated playback starvation
no regular swapping
barge-in/cancellation works
no stale audio after interrupted turns
```

## Latency preference

For normal short uncached clauses:

```text
1–5 sec complete TTS generation target
```

Then minimize:

```text
USER-END → FIRST-AUDIO
```

## Continuous realtime preference

Prefer:

```text
RTF <= 1.0
```

when practical.

## Tie-breaker

If two plans are within approximately 10–15% of each other in user-visible latency and quality:

```text
choose the simpler, more reliable architecture
```

---

# 28. Recommended Test Order

Do not start with the hardest implementation.

## Round 1 — establish reality

```text
Plan 0 baseline
Thread matrix 2/3/4
```

## Round 2 — highest-value architecture comparison

```text
Plan B sentence pipeline
Plan D short-first hybrid
Plan E cache-first
```

These are likely to provide the largest practical gains with relatively low complexity.

## Round 3 — user's concurrency experiment

```text
Plan C dual simultaneous TTS
```

Test it empirically and keep it only if the 4-vCPU server actually benefits.

## Round 4 — pressure/fallback behavior

```text
Plan F adaptive pressure mode
```

## Round 5 — raw inference optimization

```text
Plan H CPU backend variants
```

## Round 6 — advanced realtime

```text
Plan G true PCM streaming
```

Only do the full backend integration if its measured benefit justifies the complexity compared with Plan B/D.

## Round 7 — hardware ceiling

```text
Plan K stronger hardware reference
```

Only necessary if current hardware still misses the target.

---

# 29. Likely Best Composite Architecture to Validate

The most promising combined production architecture to test is:

```text
AssemblyAI streaming STT
        ↓
end_of_turn
        ↓
streaming LLM
        ↓
short first stable clause
        ↓
RAM/Disk TTS cache
        ↓ miss
ONE persistent Qwen worker
        ↓
play first clause
        ↓
generate next clause while current audio plays
        ↓
ready-audio queue
        ↓
seamless next-clause playback
```

plus:

```text
3-vs-4 Qwen thread benchmark
adaptive pressure mode
single-flight cache misses
prefetch
barge-in
turn cancellation
```

But this is a hypothesis. The benchmark decides whether it wins.

---

# 30. Special Two-Sentence Acceptance Test

This directly tests the user's proposed behavior.

Input:

```text
"Yeah, I think we should go. It'll probably be safer there."
```

Required timeline instrumentation:

```text
T0 LLM output/first sentence available
T1 TTS #1 starts
T2 TTS #1 finishes
T3 audio #1 playback starts
T4 TTS #2 starts
T5 TTS #2 finishes
T6 audio #1 playback finishes
T7 audio #2 playback starts
```

Ideal condition:

```text
T4 ≈ T3
```

Meaning TTS #2 starts immediately when #1 begins playback.

Best case:

```text
T5 < T6
```

Meaning sentence #2 is completely generated **before sentence #1 finishes playing**.

Then:

```text
T7 ≈ T6
```

and the listener hears no noticeable gap.

Record this test for:

```text
single Qwen worker
vs
dual simultaneous Qwen jobs
```

This gives a direct answer about which design is actually faster on the real VPS.

---

# 31. Decision Outputs After Benchmarking

At the end of testing, produce:

```text
1. WINNER.md
2. benchmark-results.csv
3. benchmark-results.md
4. representative audio samples
5. CPU/RAM charts
6. rejected-plans.md with measured reasons
7. final implementation checklist
```

`WINNER.md` should state:

```text
selected plan
measured latency
measured RTF
CPU/RAM use
quality score
why it beat alternatives
fallback/remediation mode
```

---

# 32. Final Rule

Do not lock the project into an architecture before measurement.

The production implementation will be selected from controlled evidence:

```text
fastest user-visible response
+
stable continuous playback
+
acceptable CPU/RAM
+
preserved Tsuki voice quality
+
manageable implementation complexity
```

The 4-vCPU server will decide whether sentence pipelining, dual generation, true PCM streaming, or a hybrid strategy is actually best.

---

# 33. 26-Day / $190 Credit Deployment Budget

Budget target:

```text
$190 total credit
26 days
normal active window = 10-16 hours/day
```

Use the user's stable hostname as the public entry point:

```text
tsukihime.dpns.org -> Main TsukiAI gateway / reverse proxy
```

Keep helper Qwen workers private when possible. Prefer WireGuard/private addressing between Main and Helper; the public domain should not expose raw Qwen worker ports. If subdomains are supported, optional names such as `tts-main.tsukihime.dpns.org` and `tts-my.tsukihime.dpns.org` may be used for TLS/service identity while access remains restricted.

The following planning figures reuse the current approximate hourly rates already being evaluated in this benchmark and MUST be replaced by the authoritative Azure portal/retail rate for the exact subscription, OS, and region before deployment:

```text
Main B4as_v2 4 vCPU / 16 GB ~= $0.189/hour
8 GB Helper B4als_v2 4 vCPU / 8 GB ~= $0.167/hour
16 GB Helper B4as_v2 4 vCPU / 16 GB ~= $0.189/hour
```

### Cost over 26 days

| Configuration | Approx combined rate | 10 h/day | 13 h/day | 16 h/day | Fits $190 at 16 h/day? |
|---|---:|---:|---:|---:|---:|
| Main + 8 GB Helper | $0.356/h | $92.56 | $120.33 | $148.10 | YES |
| Main + same 16 GB Helper | $0.378/h | $98.28 | $127.76 | $157.25 | YES |
| Main + 2 x 8 GB Helpers | $0.523/h | $135.98 | $176.77 | $217.57 | NO |

Do not plan to spend the full $190 on VM compute. Keep approximately $20-30 available for managed disks, snapshots/backups, public IPs, outbound traffic, pricing variation, and mistakes during benchmarking.

With a $30 reserve, approximate maximum daily active windows are:

```text
Main + 8 GB Helper        ~= 17.3 h/day
Main + same 16 GB Helper  ~= 16.3 h/day
Main + 2 x 8 GB Helpers   ~= 11.8 h/day
```

### Recommended production candidate under this budget

Start with:

```text
MAIN
4 vCPU / 16 GB
TsukiAI + scheduler + Qwen Worker A

HELPER
4 vCPU / 16 GB if available and price remains close to the 8 GB helper
Qwen Worker B

TOTAL
8 vCPU
32 GB across two independent machines
2 independent Qwen workers
```

Reason: at the current planning rates, the 16 GB helper costs only about $0.022/hour more than the 8 GB helper, or about $9.15 extra across the worst-case 416 active hours. That buys substantially more RAM headroom and the B4as_v2 has a higher documented baseline CPU percentage than B4als_v2 after burst credits are exhausted.

The cheaper `Main + 8 GB Helper` remains the fallback if the exact regional price gap is larger or the 8 GB worker proves completely swap-free under the trained model.

Do NOT run `Main + 2 x 8 GB Helpers` for the full 16-hour daily window under a $190 total-credit cap. Use a third worker only as an on-demand burst worker after measuring how many hours/day the two-worker system actually needs extra capacity.

### Third-worker burst policy

If three-worker testing wins for long responses, run Worker C only when useful:

```text
normal 1-2 sentence Tsuki reply
-> two workers maximum

3+ sentence / detailed reply and queue pressure high
-> start/use Worker C if already warm and budget allows

idle / low queue pressure
-> Worker C deallocated
```

A cold-start VM/model load is too slow to start only after each individual long response. If Worker C is used, schedule it in known high-use periods rather than power-cycling it per utterance.

### B-series warning

B4as_v2 and B4als_v2 are burstable VMs. CPU credits and post-credit baseline performance MUST be part of the benchmark. A TTS benchmark performed only with a fresh credit bank can overestimate sustained production speed.

Record:

```text
Azure CPU credits remaining/consumed
Qwen clause wall time
RTF
CPU utilization
RAM / swap
```

Also benchmark at least one non-burstable 4-vCPU candidate such as D4as_v5 if it is available at a competitive exact regional price. The final winner must be based on measured sustained TTS performance per dollar, not VM family name alone.
