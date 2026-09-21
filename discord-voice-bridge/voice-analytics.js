// Observed, process-lifetime voice metrics. No fabricated historical values,
// no transcripts, user identifiers, estimates or inferred cache hit counts.
const STAGES = new Set(['stt', 'llm', 'tts']);
const DROPS = new Set(['busy', 'unheard', 'focus', 'cooldown', 'no_text', 'other']);

export function createVoiceAnalytics(now = () => Date.now()) {
  const startedAt = new Date(now()).toISOString();
  const measurements = Object.fromEntries([...STAGES].map(stage => [
    stage, { attempts: 0, successes: 0, failures: 0, durationMsTotal: 0 },
  ]));
  const drops = Object.fromEntries([...DROPS].map(reason => [reason, 0]));
  let completedReplies = 0;
  let generatedAudioSeconds = 0;
  let ttsRequestMsTotal = 0;
  let ttsRequestSamples = 0;

  function record(stage, { durationMs = null, success = true,
    audioSeconds = null, requestMs = null } = {}) {
    if (!STAGES.has(stage)) throw new RangeError('Invalid analytics stage');
    const metric = measurements[stage];
    metric.attempts += 1;
    if (success) {
      metric.successes += 1;
      if (Number.isFinite(durationMs) && durationMs >= 0) {
        metric.durationMsTotal += durationMs;
        // Averages are over *timed* observations, not every attempt.
        metric.timedSamples = (metric.timedSamples || 0) + 1;
      }
      if (stage === 'tts') {
        if (Number.isFinite(audioSeconds) && audioSeconds > 0) {
          generatedAudioSeconds += audioSeconds;
        }
        if (Number.isFinite(requestMs) && requestMs >= 0) {
          ttsRequestMsTotal += requestMs;
          ttsRequestSamples += 1;
        }
      }
    } else {
      metric.failures += 1;
    }
  }
  function drop(reason) {
    if (!DROPS.has(reason)) throw new RangeError('Invalid drop reason');
    drops[reason] += 1;
  }
  function replyCompleted() { completedReplies += 1; }
  function snapshot() {
    const stages = {};
    for (const [stage, item] of Object.entries(measurements)) {
      stages[stage] = {
        attempts: item.attempts, successes: item.successes, failures: item.failures,
        timed_samples: item.timedSamples || 0,
        avg_ms: item.timedSamples ? +(item.durationMsTotal / item.timedSamples).toFixed(1) : null,
      };
    }
    return {
      since: startedAt,
      window: 'current bridge process only',
      sources: 'observed completed work; no persisted history',
      stages, completed_replies: completedReplies, drops: { ...drops },
      tts: {
        avg_request_ms: ttsRequestSamples ? +(ttsRequestMsTotal / ttsRequestSamples).toFixed(1) : null,
        request_samples: ttsRequestSamples,
        generated_audio_seconds: +generatedAudioSeconds.toFixed(3),
        // Full-ICL reference prompt is prepared at model load. No phrase-audio
        // cache is implemented, so we never claim any audio-cache hits.
        audio_cache: 'not implemented',
      },
    };
  }
  return { record, drop, replyCompleted, snapshot };
}
