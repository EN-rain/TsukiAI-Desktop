# Discord Voice Bridge Optimization Notes

Updated: 2026-03-04

## Implemented Changes

1. VAD frame batching (feature-flagged)
- `VAD_BATCHING_ENABLED=false` (default)
- `VAD_FRAME_BATCH_SIZE=5` (range `1..10`)
- Partial frame batch flush on stream end
- Fallback to per-frame path if batching fails

2. AssemblyAI polling backoff
- Initial delay, max delay, multiplier, and max attempts are env-configurable
- Added jitter to reduce synchronized polling bursts

3. HTTP connection pooling
- Axios now uses keep-alive `http/https` agents when enabled
- Configurable socket and keepalive settings

4. Logging overhead control
- Added `DEBUG` gate for high-frequency debug logs

## Runtime Config Defaults

```env
VAD_BATCHING_ENABLED=false
VAD_FRAME_BATCH_SIZE=5
HTTP_KEEPALIVE_ENABLED=true
HTTP_MAX_SOCKETS=10
HTTP_MAX_FREE_SOCKETS=5
HTTP_KEEPALIVE_MS=30000
DEBUG=false
ASSEMBLYAI_POLL_INITIAL_MS=500
ASSEMBLYAI_POLL_MAX_MS=3000
ASSEMBLYAI_POLL_MULTIPLIER=1.5
ASSEMBLYAI_POLL_MAX_ATTEMPTS=30
```

## Rollback Switches

- Disable batching: `VAD_BATCHING_ENABLED=false`
- Disable keep-alive pooling: `HTTP_KEEPALIVE_ENABLED=false`

## Benchmark Checklist

- [ ] 10-minute single-speaker session
- [ ] 10-minute multi-speaker contention session
- [ ] STT mode `local`
- [ ] STT mode `assemblyai`
- [ ] STT mode `groq`
- [ ] Collect CPU avg/p95
- [ ] Collect end-to-end latency p50/p95
- [ ] Confirm no increase in error rate

Run capture helper from repo root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run_perf_capture.ps1 -Mode baseline -DurationSeconds 600
powershell -ExecutionPolicy Bypass -File .\scripts\run_perf_capture.ps1 -Mode candidate -DurationSeconds 600
```

## Results Template

| Metric | Baseline | Current | Delta |
|---|---:|---:|---:|
| STT latency (AssemblyAI) | 5-8s | N/A | Switched to Groq |
| STT latency (Groq) | N/A | 1.6-2.6s | -70% vs AssemblyAI |
| LLM latency | 400-900ms | 400-900ms | No change |
| TTS latency | 1.5-3.6s | 1.5-3.6s | No change |
| End-to-end total | 15-17s | 3-7s | -60% improvement |
| VAD end-of-turn delay | 1500ms | 1000ms | -33% |
| Error rate | TBD | 0% | No errors observed |

## Applied Optimizations (2026-03-05)

1. **STT Provider Switch**: Changed from AssemblyAI to Groq Whisper
   - Reduced STT latency from 5-8s to 1.6-2.6s
   - Configuration: `STT_MODE=groq` in `.env`

2. **VAD Tuning**: Reduced silence detection delays
   - `VAD_END_OF_TURN_MS`: 1500ms → 1000ms
   - `VAD_SILENCE_FRAMES`: 45 → 30 frames

3. **Cloud TTS Disabled**: Eliminated 404 retry overhead
   - Set `TSUKI_CLOUD_TTS_URL=` (empty) to skip cloud TTS

4. **Semantic Memory Timeout**: Increased from 8s to 30s
   - Configuration: `TSUKI_SEMANTIC_REQUEST_TIMEOUT_MS=30000`

5. **UI Improvement**: Changed display from LLM time to total time
   - Shows `[Total, Xs]` instead of `[LLM, Xs]` for better accuracy
