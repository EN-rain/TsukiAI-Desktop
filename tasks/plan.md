# TsukiAI Real-Time Voice Plan

> **HISTORICAL / SUPERSEDED:** This plan predates the trained-custom production
> requirement. Its Base/full-ICL and reference-prompt instructions are not an
> active deployment contract. Use `voice/qwen3-tts/TRAINED_CUSTOM_VOICE_PRODUCTION_FIX_PLAN.md`.

> This remains the detailed realtime implementation candidate. The cross-plan benchmark and production-selection master plan is `tasks/tts_master_benchmark_plan.md`.

## Goal

Make Tsuki's voice path real-time in both directions:

```text
Discord / local microphone
        ↓
AssemblyAI streaming STT
        ↓
finalized turn text
        ↓
Tsuki LLM pipeline
        ↓
Qwen3-TTS full-ICL voice clone
        ↓
REAL streaming PCM chunks
        ↓
playback / Discord voice output immediately
```

The AssemblyAI side is already streaming. The Qwen side is not. The current Qwen service waits for the entire waveform, creates a complete WAV, and only then returns it. This plan changes Qwen to true incremental audio generation and playback while preserving Tsuki's existing cloned voice.

---

## Current code-grounded state

### AssemblyAI realtime STT

`discord-voice-bridge/assemblyai-realtime.js` already:

- opens an AssemblyAI streaming transcriber session;
- continuously sends PCM while a Discord user is speaking;
- receives partial turns through `onPartial`;
- receives finalized end-of-turn text through `onFinal`;
- rotates AssemblyAI keys on connection failures;
- keeps fallback transcription support;
- sends the finalized transcript to the LLM exactly once.

Keep this behavior.

Do **not** invoke the LLM for every partial transcript. Partials are only for live UI/status. `end_of_turn` remains the authoritative trigger for one AI response.

### Current Qwen TTS

`voice/qwen3-tts/api.py` currently does:

```text
POST /tts
   ↓
generate_voice_clone(...)
   ↓
wait for the complete waveform
   ↓
encode one complete WAV
   ↓
return response
```

`TsukiAI.Core/Voice/QwenTtsClient.cs` then reads the whole response into memory before returning a `byte[]`.

`TsukiAI.VoiceChat/Services/TtsPlaybackService.cs` expects a complete WAV before playback starts.

`TsukiAI.Api/Hubs/VoiceHub.cs` sends completed voice audio as one Base64 blob.

All of these must change for genuine streaming TTS.

---

## Important Qwen constraint

Qwen3-TTS is designed around low-latency streaming, but the current official `qwen-tts` Python inference API used by this project does not expose true incremental audio streaming through the existing `generate_voice_clone()` call.

Do not treat `non_streaming_mode=False` as proof of real generated-audio streaming. In the current upstream source that option simulates streaming text input rather than giving Tsuki actual PCM chunks while inference is still running.

Implementation order:

1. evaluate a real incremental Qwen3-TTS implementation that supports the 0.6B Base model and voice cloning;
2. audit its source changes against upstream;
3. pin the exact commit/version if accepted;
4. preserve Tsuki's cached full-ICL prompt;
5. prove that the first PCM chunk is produced before synthesis finishes;
6. only then wire it into Tsuki.

Preferred capability to look for:

```text
stream_generate_pcm(...)
stream_generate_voice_clone(...)
```

Do not silently depend on an unpinned moving third-party branch. Vendor or pin the audited implementation and document the delta from official Qwen.

---

## Real-time definition

For Tsuki, "real-time TTS" means:

- first playable audio is produced before the whole utterance has finished synthesizing;
- playback begins while later audio is still being generated;
- audio is not generated completely and then merely sliced afterward;
- the producer can sustain playback without constant buffer starvation;
- cancellation stops active generation and playback quickly;
- the full-ICL Tsuki voice remains consistent with the existing output.

### Required metrics

Record:

- TTFA: request accepted → first valid PCM chunk;
- first-play latency: request accepted → first audio reaches playback;
- synthesis wall time;
- generated audio duration;
- RTF;
- playback buffer duration;
- underrun count;
- total stream chunks;
- cancellation latency.

Use this repository's RTF convention:

```text
RTF = synthesis_wall_time / generated_audio_duration
```

Interpretation:

```text
RTF < 1.0 = faster than real-time
RTF = 1.0 = exactly real-time
RTF > 1.0 = slower than playback speed
```

Do not mix this with repositories that define throughput using the inverse ratio.

---

## Azure CPU feasibility gate

Target deployment remains:

```text
Azure Standard_B4as_v2
4 vCPU
16 GiB RAM
CPU-only Qwen3-TTS 0.6B
```

Streaming transport cannot compensate for an inference engine that is permanently slower than playback.

The real Azure VM must be benchmarked before calling the work complete.

### Production PASS target

The user-facing target for the warmed CPU-only Qwen service is:

- **normal short TTS clause/request: 1-5 seconds generation time** on an uncached miss;
- **over 5 seconds is acceptable for genuinely long sentences/paragraphs**, but long text must not be the normal realtime voice path;
- first spoken clause should begin as early as possible instead of waiting for the full assistant reply;
- sustained RTF should preferably remain <= 1.0 once playback is underway;
- no repeated audible starvation;
- cancellation/barge-in must stop obsolete speech quickly.

Do not confuse the **1-5 second per-clause generation target** with TTFA. With true incremental PCM streaming, TTFA should still be pushed toward roughly <= 1 second where the backend allows it. With clause-level streaming, the first short clause may need to finish before playback begins, so the first-clause wall time is the more useful Stage-1 metric.

### Existing LLM length controls already protect the realtime path

The repository already has several independent length guards; do not add a second unrelated prompt system without using these existing controls.

`TsukiAI.Core/Services/PromptBuilder.cs` already injects:

```text
Respond in 1-2 short, natural sentences.
Keep under 360 characters.
Keep to at most 2 sentences unless user explicitly asks for details.
```

`RemoteInferenceClient.BuildSystemPrompt(...)` explicitly calls that builder with:

```text
oneToTwoSentences: true
```

Generation also has hard token limits:

```text
GenerationMaxTokens default = 80
casual chat = 48-160 tokens
command = <= 80 tokens
question = normally at least 120 but capped at 256
emotional support = normally at least 100 but capped at 220
```

Finally, `ResponsePostProcessor.CleanAndValidate(...)` applies `EnforceLength(...)`, with the default `GenerationMaxReplyChars = 360`, so even if the model ignores the prompt the visible reply is still hard-capped.

This means Tsuki already has the short-response behavior needed for realtime voice. Reuse it rather than inventing a new generic "be short" prompt.

### Important current length mismatch

The Python Qwen service currently defaults to:

```text
QWEN_TTS_MAX_TEXT_CHARS = 280
```

while the LLM/post-processor allows up to:

```text
360 characters
```

Do not simply raise the Qwen limit and send the whole response as one request. The realtime pipeline should split the response at natural punctuation and keep each TTS clause below the Qwen limit, preferably much shorter for the first clause.

A 360-character assistant reply can therefore remain valid while being spoken as multiple small TTS jobs:

```text
LLM reply <= 360 chars
        ↓
clause 1 <= Qwen limit → TTS → play immediately
clause 2 <= Qwen limit → TTS while clause 1 plays
clause 3 <= Qwen limit → queue/play
```

### Remediation ladder if 4 vCPU cannot keep up

Apply this ladder in order. Do not jump directly to new hardware before measuring the cheaper fixes.

#### Level A — short clause takes 1-5 seconds

This is within the target. Keep the current server and optimize perceived latency:

- send the first stable LLM clause to TTS immediately;
- use the existing 1-2-sentence / 360-character LLM limits;
- make the first clause shorter than later clauses;
- generate clause N+1 while clause N is playing;
- use RAM/disk TTS cache and pregenerated common phrases;
- keep one persistent Qwen worker.

#### Level B — short clause repeatedly exceeds 5 seconds, but RTF is near 1.0

Keep the VPS and reduce overhead/load:

- benchmark Qwen with 2, 3, and 4 PyTorch inference threads; 3 may leave one vCPU for .NET/Node/network/audio work;
- benchmark clause sizes instead of blindly making them tiny; very small jobs can increase fixed per-request overhead;
- add a modest initial playback buffer;
- eliminate unnecessary WAV encode/decode/resample steps from the hot path;
- ensure the trained checkpoint and voice conditioning are already loaded and warm;
- use final-audio cache, single-flight duplicate suppression, and predictive prefetch.

#### Level C — short clause still exceeds 5 seconds under normal load

Enable a **voice-only pressure mode** rather than changing all Tsuki text chat globally.

Normal voice mode can keep the existing policy:

```text
1-2 short sentences
<= 360 chars
existing intent token limits
```

Pressure mode should temporarily request approximately:

```text
1 short natural sentence
~180-220 chars maximum
~48-64 generation tokens where compatible with intent
```

Critical pressure mode can use approximately:

```text
1 very short sentence
~100-160 chars maximum
cache/pregenerated phrases preferred
```

These are runtime tuning targets to benchmark, not new global defaults. Detailed user requests should still be answered correctly; if the answer must be longer, stream it as several short spoken clauses instead of synthesizing one huge TTS request.

Pressure mode should be triggered by measured values such as:

```text
rolling TTS clause wall time
rolling RTF
ready-audio buffer seconds
pending TTS queue depth
underrun count
```

and should recover automatically when load returns to normal.

#### Level D — optimize raw CPU inference

If Level C is still not enough, benchmark one change at a time:

- CPU SDPA where supported;
- Intel IPEX if the actual host CPU supports it;
- INT8 inference with strict voice-quality comparison;
- `torch.compile` only if warm measurements justify it;
- OpenVINO / ONNX only if the trained Qwen graph exports correctly and output remains equivalent.

Reject any optimization that materially damages Tsuki voice identity, Japanese-influenced English delivery, intelligibility, or stability.

#### Level E — hardware escape hatch

If a **normal short clause still cannot reliably approach the 1-5 second target** after the above work, or sustained uncached RTF remains far above 1.0, keep the realtime protocol and move **only the Qwen inference service** to a faster CPU/GPU target.

AssemblyAI, the LLM pipeline, Discord bridge, caching, chunking, barge-in, and turn protocol do not need to be redesigned.

Never fake realtime by generating an entire long WAV first and slicing it afterward.

---

## Non-negotiable voice contract

- Architecture remains Qwen3-TTS 0.6B; production inference must load the **finished trained deployment artifact** derived from the 0.6B base model, with its exact checkpoint/build hash recorded before cache rollout.
- Keep full ICL voice cloning.
- Keep `x_vector_only_mode=False`.
- Keep the exact Tsuki Japanese reference audio.
- Keep the exact Japanese reference transcript.
- Build/cache the ICL prompt once and reuse it for every request.
- English uses `language="English"`.
- Japanese uses `language="Japanese"`.
- Training is already complete; this pass must **not retrain** the voice. It should deploy, benchmark, cache, and optimize the finished trained artifact.
- No replacement voice.
- Do not switch back to OpenVoice or another TTS engine.
- Keep the current complete-WAV `/tts` endpoint during migration as an explicit fallback until the streaming path passes all gates.

---

## Qwen service API

Keep:

```text
GET  /health
POST /tts
```

Add:

```text
POST /tts/stream
```

The Python service remains private:

```text
127.0.0.1:8100
```

It must never be exposed directly to the public internet.

### `/tts/stream` request

```json
{
  "text": "...",
  "language": "English"
}
```

Auth remains:

```text
X-Api-Key: <secret>
```

### `/tts/stream` response

Use binary HTTP streaming for the private service-to-service hop.

Recommended content type:

```text
application/octet-stream
```

Headers must be sent before audio starts:

```text
X-Qwen-Engine: qwen3-tts-12hz-0.6b-base
X-Qwen-Mode: full-icl-streaming
X-Audio-Format: pcm_s16le
X-Audio-Sample-Rate: <actual model sample rate>
X-Audio-Channels: 1
X-Correlation-ID: <correlation id>
```

Do not send each chunk as a mini-WAV.

Streaming data path:

```text
Qwen float PCM chunk
        ↓
finite-value validation
        ↓
clamp to -1..1
        ↓
PCM16 little-endian conversion
        ↓
HTTP stream chunk
```

---

## Python Qwen changes

Primary file:

`voice/qwen3-tts/api.py`

Implement a true streaming path that:

1. reuses the existing cached Tsuki full-ICL prompt;
2. starts Qwen generation in an inference worker;
3. receives incremental waveform chunks from the streaming generator;
4. converts each chunk to PCM16 immediately;
5. pushes chunks into a bounded async queue;
6. exposes the queue through FastAPI `StreamingResponse`;
7. applies backpressure;
8. detects client disconnects;
9. propagates cancellation into the generator when possible;
10. never buffers the whole utterance in the real-time path;
11. logs TTFA, chunk count, emitted bytes, total runtime, emitted duration, and correlation ID.

### Bounded buffering

Never use an unbounded chunk list.

Initial queue depth to benchmark:

```text
4-12 chunks
```

### Chunk duration experiment

Benchmark at least:

```text
40 ms
80 ms
120 ms
200 ms
```

Choose the smallest chunk size that does not materially damage CPU throughput.

---

## .NET Qwen client changes

Keep the compatibility API:

```csharp
Task<byte[]> SynthesizeWavAsync(...)
```

Add a streaming abstraction conceptually equivalent to:

```csharp
IAsyncEnumerable<TtsAudioChunk> StreamPcmAsync(...)
```

Each `TtsAudioChunk` should carry:

```text
PCM bytes
sample rate
channels
sequence number
isFinal
correlationId
```

Primary implementation:

`TsukiAI.Core/Voice/QwenTtsClient.cs`

Requirements:

- request `/tts/stream`;
- use `HttpCompletionOption.ResponseHeadersRead`;
- validate format/sample-rate/channel headers;
- read directly from the response stream;
- yield chunks as soon as bytes arrive;
- never collect the real-time stream in one `MemoryStream`;
- honor cancellation immediately;
- enforce cumulative safety limits;
- report TTFA;
- use `/tts` fallback only for explicitly-classified streaming failures.

---

## Desktop real-time playback

Primary file:

`TsukiAI.VoiceChat/Services/TtsPlaybackService.cs`

Add buffered PCM playback.

Recommended NAudio flow:

```text
Qwen stream
    ↓
IAsyncEnumerable<TtsAudioChunk>
    ↓
streaming resampler if needed
    ↓
BufferedWaveProvider
    ↓
WaveOutEvent
    ↓
selected Tsuki output device
```

Requirements:

- start playback after a small configurable initial buffer;
- continuously append PCM chunks;
- never recreate `WaveOutEvent` for each chunk;
- expose buffer milliseconds;
- expose underrun count;
- honor `VoiceChatOutputDeviceNumber`;
- preserve local speaker output;
- preserve VRChat virtual-cable output;
- support immediate stop/cancel on barge-in.

---

## Discord real-time playback

For Discord voice-channel speaking, stop waiting for a completed TTS file.

Target path:

```text
Qwen PCM chunks
      ↓
progressive resample/channel conversion if required
      ↓
Node Readable stream
      ↓
@discordjs/voice AudioResource
      ↓
Discord voice channel
```

Discord users should hear the beginning of Tsuki's response while Qwen is still synthesizing the rest.

Keep Discord voice-message attachment generation separate because uploads require a finalized artifact.

Two output modes must remain distinct:

```text
LIVE VOICE CHANNEL
    = real-time PCM stream

VOICE MESSAGE FILE
    = completed encoded file
```

---

## AssemblyAI timing behavior

Keep the existing control flow:

```text
partial transcript
    → display/log only

final end_of_turn transcript
    → one LLM request
```

Add timestamps under the same correlation ID for:

```text
speech first frame
AssemblyAI connected
first STT partial
final STT end_of_turn
LLM request start
LLM first token when available
LLM final text
Qwen stream request start
Qwen first PCM chunk
first playback frame
playback complete
```

This gives one authoritative end-to-end latency trace.

---

## Optional phase 2 — streaming LLM into streaming TTS

Do this only after Qwen audio streaming itself is stable.

The current `VoiceHub` already notes that token-level inference streaming can be layered on later.

Do **not** send single LLM tokens to TTS.

Use a text segmenter:

```text
LLM token stream
      ↓
text accumulator
      ↓
complete sentence / stable clause
      ↓
Qwen streaming TTS
```

Good boundaries:

```text
. ! ?
。！？
```

Commas may be used only after a sufficiently long stable clause.

Maintain one ordered playback queue so sentence 2 can never play before sentence 1.

Required phase-1 latency win remains:

```text
final AssemblyAI turn
      ↓
LLM final reply
      ↓
Qwen starts playing BEFORE Qwen finishes synthesizing
```

---

## Barge-in

When `VoiceBargeInEnabled=true` and AssemblyAI detects a new user speech turn while Tsuki is speaking:

1. stop current playback;
2. cancel the active Qwen HTTP stream;
3. cancel Qwen generation where supported;
4. clear not-yet-played PCM;
5. keep AssemblyAI listening to the new user turn;
6. generate exactly one response when that new turn becomes final.

Old audio must never resume afterward.

---

## Web control additions

All modifiable controls belong on the web control plane, not the desktop WPF UI.

Add:

- real-time TTS enabled;
- full-WAV fallback enabled;
- initial playback buffer ms;
- barge-in enabled;
- AssemblyAI realtime status;
- first-partial STT latency;
- final-turn STT latency;
- Qwen health;
- Qwen mode: `full-icl-streaming` / fallback;
- Qwen TTFA;
- Qwen RTF;
- active stream state;
- chunk count;
- playback buffer ms;
- underrun count;
- current correlation ID.

Secrets remain server-side and must never be returned to the browser.

---

## Parallel subagent execution

This implementation must be heavily parallelized.

Deploy at least **12 subagents** when supported; target **16+ specialized subagents**.

### Wave A — reconnaissance

**A1 — Qwen upstream agent**
- inspect current official Qwen3-TTS source and streaming behavior;
- document exactly what upstream exposes.

**A2 — streaming implementation audit agent**
- inspect candidate true-streaming implementation;
- diff it against upstream;
- identify risky/unrelated changes.

**A3 — Azure benchmark agent**
- benchmark the current B4as_v2 baseline;
- capture full-WAV wall time, duration, RTF, CPU/RAM.

**A4 — AssemblyAI agent**
- verify partial/final behavior;
- verify final turns cannot execute twice;
- add latency instrumentation plan.

### Wave B — streaming runtime

**B1 — Python inference agent**
- implement/audit incremental Qwen voice-clone generation.

**B2 — Python transport agent**
- implement `/tts/stream`, auth, headers, backpressure, disconnect handling.

**B3 — .NET streaming client agent**
- implement incremental response reading.

**B4 — .NET voice contract agent**
- extend `ITtsClient` and pipeline interfaces without breaking fallback callers.

**B5 — desktop playback agent**
- implement `BufferedWaveProvider` playback and interruption.

**B6 — audio-format agent**
- implement/test incremental resampling and channel conversion.

### Wave C — integrations

**C1 — Discord live playback agent**
- pipe Qwen PCM into `@discordjs/voice` while generation is active.

**C2 — Discord attachment agent**
- preserve completed voice-message generation separately.

**C3 — VRChat/local device agent**
- preserve output-device and virtual-cable behavior.

**C4 — barge-in agent**
- wire cancellation across AssemblyAI turn detection, Qwen, and playback.

### Wave D — control/telemetry

**D1 — web control agent**
- add streaming controls and health displays.

**D2 — observability agent**
- add correlation-ID timing, TTFA, RTF, buffer, underrun metrics.

### Wave E — independent reviewers

**E1 — audio quality reviewer**
- compare streaming vs current output for Tsuki voice identity, accent, glitches, joins, silence.

**E2 — reliability reviewer**
- test disconnects, cancellation, repeated turns, concurrency, key rotation, and task leaks.

**E3 — security reviewer**
- audit dependency pinning, API keys, public exposure, body limits, and browser secret leakage.

**E4 — final integration reviewer**
- reject any implementation that produces chunks only after full synthesis.

The orchestrator owns integration/merges. Parallel agents should use separate worktrees/branches when available.

---

## Merge gates

### Gate 0 — baseline

Record before modifications:

- existing AssemblyAI first-partial latency;
- existing AssemblyAI final-turn latency;
- current Qwen complete-WAV TTFA;
- current Qwen runtime;
- generated audio duration;
- current Qwen RTF;
- current end-to-end final-STT → first-play latency;
- audio sample for quality comparison.

### Gate 1 — true Qwen streaming in isolation

PASS only if:

- first PCM arrives before synthesis completes;
- full-ICL Tsuki prompt is used;
- PCM is valid/non-silent;
- order is correct;
- cancellation works.

### Gate 2 — real Azure performance

PASS only if:

- B4as_v2 can sustain acceptable playback;
- no repeated audible buffer starvation;
- TTFA/RTF are recorded.

If this fails, report the CPU limit explicitly and move inference to a faster target rather than hiding it.

### Gate 3 — .NET + desktop

PASS only if the desktop starts playing before the Qwen request finishes.

### Gate 4 — Discord

PASS only if Discord hears Tsuki while Qwen is still generating later chunks.

### Gate 5 — control plane

PASS only if the web UI can control streaming and display authoritative health/latency metrics without exposing secrets.

### Gate 6 — regression

Verify:

- AssemblyAI realtime STT remains functional;
- partial transcript never causes duplicate LLM requests;
- STT fallback remains functional;
- `/tts` fallback remains functional;
- Tsuki voice identity is preserved;
- English works;
- Japanese works;
- desktop speakers work;
- VRChat routing works;
- Discord live voice works;
- barge-in works;
- no zombie Qwen streams remain after cancellation;
- no secrets leak.

---

## Storage hygiene and automatic cleanup

Storage growth must be bounded by design. Real-time audio chunks are ephemeral runtime data and must not accumulate on disk.

### Streaming audio lifecycle

- AssemblyAI realtime STT PCM frames remain in memory only.
- Clear pending AssemblyAI audio buffers immediately on normal close, error, timeout, cancellation, and barge-in.
- Release Discord fallback `rawAudioChunks` as soon as the turn completes or fallback is no longer possible.
- Qwen `/tts/stream` must stream PCM directly from producer to consumer; do not create one file per chunk.
- Desktop playback must feed chunks directly into `BufferedWaveProvider`; consumed audio must be released by the bounded playback buffer.
- Discord live playback must pipe PCM/Opus through streams; do not persist live chunks unless the completed voice-message feature explicitly needs a final attachment.
- Browser streaming playback must use bounded in-memory queues and release played buffers.
- Cancellation must immediately discard queued but unplayed TTS audio.
- Any emergency spill-to-disk implementation must use a dedicated temp directory and delete files in `finally` blocks on success, failure, disconnect, restart, and cancellation.

### Qwen service files

Keep permanently only:

- the Qwen model/cache required to run the service;
- the original Tsuki reference audio;
- the exact reference transcript;
- the cached full-ICL voice prompt (`tsuki_icl_prompt.pt`);
- configuration/secrets stored by the deployment contract.

Do not retain normal synthesis WAV/PCM outputs on disk.

`/opt/tsuki-qwen3-tts/outputs/` is test/diagnostic storage only. Automated tests must overwrite deterministic benchmark filenames or remove generated samples after validation. Add a cleanup job that removes diagnostic audio older than 24 hours unless explicitly marked to keep.

### Logs

The existing .NET `DevLog` already rotates at approximately 2 MiB and retains only one `.1` archive. Preserve that bounded behavior.

Add bounded logging for every other runtime component:

- Docker `json-file` logging: `max-size: 10m`, `max-file: 3` per container, or an equivalent bounded logging driver;
- Qwen service logs: rotate by size/day and retain at most 3-7 files;
- Discord bridge logs: rotate and retain at most 3-7 files if redirected to disk;
- never write raw PCM, WAV payloads, API keys, auth cookies, or full binary request bodies to logs.

### Conversation and memory storage

Existing bounded behavior to preserve:

- shared conversation history is capped at 1000 messages;
- per-user Discord text history is capped at 60 turns;
- Discord text history uses `TSUKI_MEMORY_RETENTION_DAYS` (default 30 days);
- HTTP Chroma semantic memory has a daily retention purge;
- temporary settings/history/state files are deleted after atomic replacement;
- settings keep only the single `.bak` backup rather than timestamped backups.

Fix the current retention gaps:

- `ChromaSqliteSemanticMemoryService.DeleteOlderThanAsync()` is currently a no-op; implement real age-based deletion/compaction or explicitly disable local persistent semantic memory in deployments where retention cannot be enforced;
- Supermemory's current adapter cannot perform date-filtered deletion through the documented API, so the UI/health report must state that remote provider retention is provider-controlled rather than pretending local cleanup occurred.

### Docker and deployment cleanup

The application must not run destructive global Docker prune commands automatically. Instead use bounded, targeted cleanup:

- remove stopped Tsuki containers after successful replacement;
- remove old unreferenced Tsuki images beyond the newest 2 deployment versions;
- use multi-stage builds and `.dockerignore` so `bin/`, `obj/`, `node_modules/`, test outputs, local caches, and development artifacts are not copied into production images;
- do not persist container-layer audio/temp files;
- periodically report Docker image/container/log disk usage to the control plane;
- expose a safe manual `Clean deployment cache` action that previews exactly what will be removed before deletion.

### Desktop/dev-machine cleanup

Development artifacts such as `.NET bin/obj`, `web/dist`, package caches, and `node_modules` are not runtime audio chunks and should not be deleted while the developer is actively using them. Provide a separate safe maintenance command that can remove rebuildable artifacts only when requested.

Never auto-delete:

- source code;
- `.env`/secret configuration;
- Tsuki's reference audio/transcript;
- the cached Qwen full-ICL prompt;
- active conversation/memory data within the configured retention window;
- currently deployed binaries/images needed for rollback.

### Storage health limits

Expose storage metrics on the web control panel:

- VPS free disk space;
- Tsuki data-directory size;
- Qwen model/cache size;
- diagnostic-output size;
- Docker image/container/log size where available;
- desktop Tsuki data-directory size;
- last cleanup time and bytes reclaimed.

Recommended alerts:

- warning below 20% free disk;
- critical below 10% free disk;
- diagnostic/temp directory warning above 1 GiB;
- refuse new diagnostic captures when disk is critical, while keeping normal voice runtime operational where possible.

### Storage validation gate

PASS only if a stress test of at least 100 voice turns shows no linear growth from transient audio chunks after garbage collection/stream disposal, and all persistent categories remain within their configured bounds. Measure disk usage before, during, and after the test. A completed/cancelled turn must leave zero per-turn PCM/WAV chunk files behind.

---

## Required tests

### Python

- cached full-ICL prompt reuse;
- first chunk emitted before generator completion;
- ordered PCM chunks;
- finite/non-silent PCM validation;
- bounded queue/backpressure;
- disconnect cancellation;
- API auth;
- max text length;
- English/Japanese normalization.

### .NET

- streaming metadata parsing;
- incremental network reads;
- cumulative safety limits;
- cancellation;
- fallback classification;
- playback buffering;
- underrun metrics;
- selected output-device routing;
- streaming resample correctness.

### Node / Discord

- AssemblyAI partial does not invoke LLM;
- AssemblyAI final invokes LLM once;
- live Qwen PCM reaches the Discord voice player incrementally;
- barge-in cancels playback;
- AssemblyAI key rotation still works;
- completed voice-message upload path still works.

### End-to-end scenarios

1. short English response;
2. long English response;
3. short Japanese response;
4. long Japanese response;
5. user interrupts Tsuki mid-sentence;
6. AssemblyAI connection/key rotation;
7. Qwen stream disconnect;
8. Qwen streaming unavailable → explicit `/tts` fallback;
9. ten sequential turns with no leaked tasks/sockets;
10. real Discord voice-channel test on Azure.

---

## Service layout

```text
/opt/tsuki-qwen3-tts/
├── venv/
├── server/
├── reference/
│   ├── tsuki_25s_clip.mp3
│   ├── tsuki_ref_text.txt
│   └── tsuki_icl_prompt.pt
├── outputs/
├── vendor/      # only if a pinned/audited streaming implementation is required
└── qwen.env
```

---

## Final target

```text
                  ┌──────────────────────┐
                  │ Tsuki Web Controls   │
                  └──────────┬───────────┘
                             │
                             ▼
                      TsukiAI.Api / Azure
                             │
        ┌────────────────────┼────────────────────┐
        │                    │                    │
        ▼                    ▼                    ▼
AssemblyAI realtime      Tsuki LLM          Qwen3-TTS realtime
STT WebSocket                               full-ICL voice
        │                                         │
partial/final                               PCM chunks
        │                                         │
        └── finalized turn ───────────────────────┘
                                                  │
                                                  ▼
                                      streaming audio router
                                                  │
                         ┌────────────────────────┼────────────────────────┐
                         ▼                        ▼                        ▼
                  desktop speaker          VRChat cable            Discord voice
```

Success means Tsuki no longer waits for a complete Qwen WAV before speaking.
