# TsukiAI — Helper A + Helper B Deployment Plan

## Scope

This plan covers **only the two helper VPS workers**. The Main VPS cache work is being handled separately by the local agent and remains the authoritative cache/scheduler/control plane.

Target topology:

```text
PUBLIC
https://tsukihime.dpns.org
        |
        v
MAIN — Azure Malaysia West
4 vCPU / 16 GB
TsukiAI + reverse proxy + central cache + scheduler + Qwen Worker A
        |
        +------------------------------+
        |                              |
        v                              v
HELPER A                         HELPER B
Azure Malaysia West             AWS ap-southeast-1 (Singapore)
4 vCPU / 16 GB                  4 vCPU / 16 GiB
Qwen Worker B                   Qwen Worker C
```

This document must not replace the Main VPS cache plan. Helpers are compute workers. Main owns cache authority, request ordering, turn cancellation, worker selection, and public ingress.

---

# 1. Deployment Goals

Both helpers must provide independent Qwen3-TTS 0.6B inference capacity using the **same finished trained Tsuki checkpoint**.

Primary goals:

```text
1. Keep one Qwen model resident per helper.
2. Allow Main to dispatch sentence/segment jobs concurrently.
3. Return audio to Main with minimum overhead.
4. Preserve exact Tsuki voice identity across all workers.
5. Never expose helper inference endpoints publicly unless explicitly required.
6. Fail safely: if either helper fails, Main can continue with remaining workers.
7. Benchmark actual speed/RAM/RTF before declaring either helper production-ready.
```

---

# 2. Helper Roles

## Helper A — Azure Malaysia West

```text
Cloud: Azure
Region: Malaysia West
CPU: 4 vCPU
RAM: 16 GB
Role: Qwen Worker B
Network: same Azure region as Main where possible
```

Preferred use:

```text
Main cache miss
    ↓
Scheduler selects idle worker
    ↓
Helper A handles sentence/segment job
```

Because Helper A is in the same Azure region as Main, prefer a private Azure network path rather than public Internet exposure.

## Helper B — AWS Singapore

```text
Cloud: AWS
Region: ap-southeast-1 (Singapore)
Preferred instance family: non-burstable general-purpose x86_64
Initial candidate: m7i.xlarge
CPU: 4 vCPU
RAM: 16 GiB
Role: Qwen Worker C
Network: encrypted private overlay from Main
```

Helper B is cross-cloud and must be treated as an independent worker with measured RTT and bandwidth.

---

# 3. Public/Private Network Design

Only Main should be public-facing.

```text
https://tsukihime.dpns.org
        ↓
Main reverse proxy
        ↓
TsukiAI API / scheduler
        ↓
internal helper requests
```

Do **not** expose Helper A or Helper B `/tts` endpoints directly to the public Internet.

## Helper A path

Preferred:

```text
Main Azure Malaysia West
        ↓ private Azure VNet/private IP
Helper A Azure Malaysia West
```

Use NSG/firewall rules so only Main can access Helper A's TTS port.

## Helper B path

Preferred:

```text
Main Azure Malaysia West
        ↓ WireGuard or Tailscale/private overlay
Helper B AWS Singapore
```

Helper B should accept TTS traffic only from the private tunnel/overlay address or a tightly allowlisted Main source.

The custom domain remains on Main. No public helper subdomain is required.

---

# 4. Worker API Contract

Each helper should expose only the minimum private API required by Main.

Required:

```text
GET  /health
POST /tts
```

Later candidate:

```text
POST /tts/stream
```

Do not require true PCM streaming for first deployment. The first goal is stable independent sentence/segment jobs.

Health response should eventually report:

```text
healthy
worker_id
checkpoint_id
voice_version
backend
precision
model_loaded
warmup_complete
active_jobs
last_generation_ms
rolling_rtf
process_rss_mb
swap_used_mb
```

Never expose API keys, model paths containing secrets, or full environment dumps.

---

# 5. Exact Model Consistency Requirement

Main, Helper A, and Helper B must use the same:

```text
trained checkpoint
checkpoint/build hash
voice version
reference/conditioning state if still required
language configuration
sample rate
codec/output format
generation parameters
normalization rules
```

The scheduler must reject a helper as unhealthy/incompatible if its checkpoint ID or voice version differs from Main's expected version.

This prevents sentence-to-sentence voice drift such as:

```text
sentence #1 sounds like Tsuki v3
sentence #2 sounds like older Tsuki v2
```

---

# 6. Worker Process Design

Each helper starts with:

```text
ONE Qwen process
ONE trained model resident in RAM
ONE active synthesis job at a time
```

Do not run two simultaneous Qwen generations on one 4-vCPU helper during initial production deployment.

The system-level parallelism comes from having multiple independent machines:

```text
Main Worker A   → sentence #1
Helper A Worker B → sentence #2
Helper B Worker C → sentence #3
```

This is preferable to forcing two heavy Qwen jobs onto the same 4-vCPU VM.

---

# 7. CPU Threading Baseline

Benchmark rather than hard-code forever.

## Helper A

Start tests with:

```text
PyTorch intra-op threads: 3 and 4
PyTorch inter-op threads: 1
OMP_NUM_THREADS: match tested intra-op value
MKL_NUM_THREADS: match tested intra-op value where applicable
```

Because Helper A is mostly dedicated to Qwen, 4 threads may win. Keep 3 as a comparison because runtime/network/OS overhead can make 4 slower in some cases.

## Helper B

For m7i.xlarge or equivalent 4-vCPU x86_64 instance:

```text
PyTorch intra-op threads: 3 and 4
PyTorch inter-op threads: 1
```

Keep the winner based on real wall time, RTF, and sustained tests.

---

# 8. Backend Benchmark Matrix

Do not change backends on both helpers at once before establishing a reference.

Reference configuration:

```text
PyTorch
FP32
CPU
current trained checkpoint
```

Then test, in order:

```text
A. FP32 + eager — reference
B. FP32 + SDPA — first optimization
C. INT8/quantized PyTorch path if compatible
D. native/GGML-style backend only after checkpoint conversion and voice-quality validation
```

Each backend must preserve Tsuki voice identity.

Reject any faster backend that materially damages:

```text
pronunciation
accent/prosody
voice timbre
pitch stability
Japanese-influenced English delivery
line-ending quality
noise/artifacts
```

---

# 9. Helper Cache Policy

Main remains the authoritative cache.

```text
Main RAM cache
Main disk cache
Main single-flight dedupe
```

Helpers do **not** need an authoritative persistent audio cache.

Optional later helper optimization:

```text
small RAM-only hot cache
64–256 MB
```

This is allowed only after Qwen peak RAM is measured and zero swap is confirmed.

Rules:

```text
1. Main checks cache before dispatching to helper.
2. Main prevents duplicate synthesis via single-flight.
3. Helper may return a local RAM hit if it has one.
4. Main stores the returned result in its authoritative cache.
5. Helpers do not independently own cache versioning policy.
```

---

# 10. Sentence/Segment Scheduling

For a three-sentence response:

```text
sentence #1 → fastest available worker
sentence #2 → next idle worker
sentence #3 → next idle worker
```

Do not permanently hard-map:

```text
#1 always Main
#2 always Helper A
#3 always Helper B
```

The scheduler should eventually use measured worker state:

```text
health
busy/idle
rolling generation time
rolling RTF
queue depth
network RTT
recent errors
```

The fastest healthy idle worker should normally receive the next highest-priority segment.

---

# 11. Ordered Playback

Every request from Main must carry at least:

```text
turn_id
sequence_number
text
language
checkpoint_id
voice_version
priority
```

Example:

```text
turn=abc123 seq=0 → Main Worker
turn=abc123 seq=1 → Helper A
turn=abc123 seq=2 → Helper B
```

Completion order does not control playback order.

If generation completes:

```text
#2
#1
#3
```

Main still plays:

```text
#1 → #2 → #3
```

---

# 12. Rolling Look-Ahead

Do not synthesize an unlimited paragraph ahead of playback.

Starting target:

```text
currently playing: 1 segment
ready:             up to 1–2 segments
generating:        up to number of healthy workers
unscheduled:       text only
```

For long detailed answers, keep a bounded look-ahead so CPU is not wasted if the user interrupts Tsuki.

---

# 13. Barge-In and Cancellation

When a new user turn interrupts current playback:

```text
Main stops playback
Main invalidates old turn_id
Main cancels queued old-turn jobs
Helpers stop active old-turn jobs where backend permits
Late old-turn audio is rejected by Main
```

A helper returning old audio after cancellation must not cause it to be played.

Turn IDs are authoritative.

---

# 14. Timeout and Retry Policy

Do not let a dead helper block the voice pipeline.

Starting policy to benchmark:

```text
connect timeout: short
health timeout: 1–2 s
TTS request timeout: comfortably above measured p99 generation time
```

If Helper A or B fails:

```text
mark temporarily unhealthy
requeue unfinished segment
send to another healthy worker
continue conversation
```

Avoid retry storms. Use bounded retry/backoff and temporary circuit breaking after repeated failures.

---

# 15. Helper A Security

Helper A should use:

```text
private Azure IP
NSG allow only Main → TTS port
no public TTS listener if private routing is available
service account/non-root runtime
firewall default-deny inbound
secrets outside repository
```

---

# 16. Helper B Security

Helper B should use:

```text
AWS Security Group default-deny inbound
SSH restricted to admin source or private management overlay
TTS port restricted to WireGuard/Tailscale/private source
service account/non-root runtime
secrets outside repository
```

Do not put helper API credentials into Git.

---

# 17. Service Management

Each helper should run Qwen through a supervised service such as systemd or a container with an explicit restart policy.

Desired behavior:

```text
boot
→ network ready
→ model service starts
→ trained checkpoint loads
→ voice conditioning loads/builds
→ warmup synthesis runs
→ /health reports ready
```

On failure:

```text
process exits
→ supervisor restarts with bounded policy
→ worker reports unhealthy until warmup succeeds again
```

Do not report healthy merely because the HTTP process is listening.

---

# 18. Warmup

Each helper performs one controlled warmup after model load.

Measure separately:

```text
cold model load time
first synthesis time
warm synthesis time
```

Production scheduler should only send normal traffic after warmup passes.

---

# 19. RAM Acceptance Gate

For each helper, record:

```text
RSS after model load
RSS after warmup
peak RSS short clause
peak RSS long clause
swap usage
OOM events
```

PASS:

```text
zero swap during normal synthesis
comfortable free-memory margin
no OOM
no repeated allocator pressure
```

16 GB was selected to give the current FP32 PyTorch implementation safer headroom.

---

# 20. CPU Acceptance Gate

Record:

```text
per-process CPU
system CPU
steal time if observable
load average
TTS generation milliseconds
RTF
```

Run sustained tests, not just a fresh-instance single request.

For Azure burstable sizes, also record relevant CPU-credit behavior if the selected Helper A SKU uses credits.

For AWS, prefer non-burstable M-family for Helper B so sustained TTS is not dependent on burst credits.

---

# 21. Network Acceptance Gate

Measure from Main:

```text
Main → Helper A RTT
Main → Helper B RTT
/TTS request round-trip excluding synthesis if possible
packet loss
jitter
throughput for returned PCM/audio
```

Network latency is expected to be much smaller than CPU TTS generation time, but it must be measured rather than assumed.

---

# 22. Performance Targets

Current working target for an uncached normal short clause:

```text
1–5 seconds generation time
```

Over 5 seconds can be acceptable for genuinely long text, but repeatedly exceeding 5 seconds for normal short clauses triggers remediation/testing.

Record:

```text
p50
p90
p95
p99
generation wall time
RTF
```

Do not call the helper realtime-ready based only on one good sample.

---

# 23. Three-Worker Expected Behavior

If one short clause takes approximately 2–5 seconds on each worker, three independent workers can process the first three independent clauses concurrently:

```text
0 s:
Worker A → #1
Worker B → #2
Worker C → #3

~2–5 s:
#1 ready
#2 ready
#3 ready
```

This does **not** make #1 itself three times faster.

The benefit is multi-sentence throughput and keeping future audio ready before playback reaches it.

---

# 24. Helper A vs Helper B Benchmark

Use the exact same corpus on both helpers.

Minimum corpus:

```text
10 very short lines
10 normal one-sentence lines
10 two-clause lines
5 long/detail lines
English
Japanese if Tsuki uses both
```

For every line record:

```text
worker ID
cloud/region
instance type
backend
precision
threads
text length
generation_ms
audio_duration_ms
RTF
peak RSS
CPU
swap
audio hash/metadata
quality notes
```

---

# 25. Worker Selection Score

After measurements exist, Main may use a score such as:

```text
worker unavailable → reject
worker busy        → queue penalty
higher rolling RTF → penalty
higher recent latency → penalty
cross-cloud RTT    → small penalty
recent errors      → large penalty
```

Do not over-weight network RTT when TTS itself takes seconds.

---

# 26. Failure Scenarios to Test

Test all of these deliberately:

```text
Helper A offline
Helper B offline
both helpers offline
helper returns 500
helper times out
helper model not loaded
helper wrong checkpoint version
helper produces corrupt/empty audio
network tunnel drops
Main cancels while helper is generating
late result arrives after turn cancellation
helpers finish out of order
```

Main must remain functional in degraded mode.

---

# 27. Deployment Order

Recommended order:

```text
1. Freeze/identify trained checkpoint ID.
2. Build reproducible helper Qwen service package/container.
3. Deploy Helper A in Azure Malaysia West.
4. Verify private Main↔Helper A connectivity.
5. Run load/warmup/quality/RAM/thread benchmarks.
6. Deploy Helper B in AWS Singapore.
7. Establish WireGuard/Tailscale/private route.
8. Run the same benchmark corpus.
9. Compare Helper A vs Helper B.
10. Integrate both into Main scheduler.
11. Test 2-worker and 3-worker dispatch.
12. Test ordered playback.
13. Test cancellation/barge-in.
14. Run sustained multi-hour test.
15. Keep the measured winner settings per worker.
```

---

# 28. Subagent Orchestration — Explicitly Authorized

The local Auvrynt/Codex agent is explicitly authorized to deploy multiple visible subagents for this work and should do so where parallel work improves speed or independent verification.

Use **visible, titled worker chats** through the Auvrynt Codex orchestration flow. Do not use hidden fallback workers.

The prime/local agent owns final integration and should create an orchestration run for the helper deployment work, then deploy specialists such as:

```text
Worker 1 — Azure Helper A implementation
Role: implementation
Task: deployment/service/network/bootstrap for Azure Malaysia helper.

Worker 2 — AWS Helper B implementation
Role: implementation
Task: deployment/service/network/bootstrap for AWS Singapore helper.

Worker 3 — Qwen runtime/performance researcher
Role: research
Task: verify best compatible CPU backend/threading/precision options for the trained checkpoint.

Worker 4 — Benchmark/test engineer
Role: test
Task: build deterministic corpus, measurement scripts, RAM/CPU/RTF collection, sustained tests.

Worker 5 — Security reviewer
Role: security-review
Task: firewall, tunnel, secrets, public exposure, least privilege, API auth review.

Worker 6 — Integration/reliability reviewer
Role: integration or review
Task: scheduler contract, ordering, health, failover, cancellation, wrong-version rejection.
```

The local prime agent may deploy additional subagents as needed. There is no requirement to stop at six if more independent workstreams are useful.

## Subagent rules

```text
1. Each worker receives a narrow scope.
2. Parallel workers must not edit the same file blindly.
3. Prime agent supervises and integrates results.
4. Each worker reports RESULT / CHANGES / VALIDATION / BLOCKERS.
5. Testing/review workers should independently validate implementation workers.
6. Do not let a worker expose credentials or copy secrets into reports.
7. Do not ask the user for routine permission already granted for this deployment plan.
8. Stop only for genuinely missing credentials/provider access or irreversible external actions requiring user approval.
```

## Suggested Auvrynt orchestration pattern

The local agent should use the available Auvrynt Codex orchestration capabilities to:

```text
create one run
spawn multiple visible agents in parallel
monitor the aggregate run
steer workers when needed
collect final results
integrate accepted work
run final independent validation
complete the run only after all accepted work is terminal and reviewed
```

This plan explicitly permits autonomous Dev → QA loops.

---

# 29. Local-Agent Execution Prompt

The local agent can use the following as its execution directive:

```text
Read tasks/helper_a_b_deployment_plan.md completely and treat it as the helper deployment authority.

The Main VPS cache implementation is being handled separately. Do not replace or redesign the Main cache unless integration requires a bounded compatibility change.

Implement and validate Helper A (Azure Malaysia West, 4 vCPU/16 GB) and Helper B (AWS ap-southeast-1 Singapore, 4 vCPU/16 GiB) as independent Qwen3-TTS workers for the same finished trained Tsuki checkpoint.

You are explicitly authorized to use Auvrynt Codex orchestration and deploy as many visible specialist subagents as useful. Split Azure implementation, AWS implementation, benchmarking, security, and integration/reliability into parallel workers where practical. Require RESULT / CHANGES / VALIDATION / BLOCKERS from workers and independently validate before accepting changes.

Preserve the public architecture: tsukihime.dpns.org terminates on Main. Helpers remain private worker endpoints. Main owns authoritative cache, scheduler, ordering, cancellation, and failover.

Do not invent credentials, checkpoint paths, private IPs, AWS/Azure account identifiers, or secrets. Where provider credentials or exact infrastructure IDs are missing, prepare the deployment artifacts/configuration and report the exact blocker rather than guessing.

Benchmark before locking runtime settings: FP32 eager reference, FP32 SDPA, 3-vs-4 threads, RAM/RTF/wall time, and sustained behavior. Add INT8/native backend experiments only if compatible and voice-quality-safe.

Continue autonomously through implementation, testing, QA, and integration until the helper system is concrete and reviewable or a real external-access blocker is reached.
```

---

# 30. Definition of Done

Helper deployment is complete only when:

```text
Helper A is reachable privately from Main.
Helper B is reachable through the approved encrypted/private path from Main.
Both load the same trained checkpoint/version.
Both complete warmup successfully.
Both pass short/normal/long benchmark corpus.
Peak RAM and swap are measured.
2/3/4-thread or at least 3/4-thread comparison is measured.
RTF and generation wall time are recorded.
Main can dispatch concurrent independent segments.
Main preserves playback order.
Main rejects late/cancelled-turn audio.
Main survives either helper failing.
No helper TTS endpoint is unintentionally public.
No secrets are committed.
An independent test/security/reliability review passes.
```

The production settings must be chosen from measured evidence, not assumptions.
