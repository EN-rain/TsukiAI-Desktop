import assert from 'node:assert/strict';
import test from 'node:test';
import { createVoiceAnalytics } from '../voice-analytics.js';

test('no samples means no fabricated timing or audio-cache hits', () => {
  const analytics = createVoiceAnalytics(() => Date.parse('2026-09-20T00:00:00Z'));
  const result = analytics.snapshot();
  assert.equal(result.since, '2026-09-20T00:00:00.000Z');
  for (const stage of ['stt', 'llm', 'tts']) {
    assert.equal(result.stages[stage].avg_ms, null);
    assert.equal(result.stages[stage].timed_samples, 0);
  }
  assert.equal(result.tts.audio_cache, 'not implemented');
  assert.equal(result.tts.avg_request_ms, null);
});

test('averages use observed successful timed samples, not failed or unmeasured work', () => {
  const analytics = createVoiceAnalytics(() => 0);
  analytics.record('stt', { durationMs: 120 });
  analytics.record('stt', { durationMs: 80 });
  analytics.record('stt', { success: false });
  analytics.record('stt'); // realtime STT has no measured elapsed duration
  analytics.record('llm', { durationMs: 1600 });
  analytics.record('tts', { durationMs: 4000, requestMs: 4400, audioSeconds: 2.5 });
  analytics.record('tts', { durationMs: 2000, requestMs: 2200, audioSeconds: 1.5 });
  analytics.record('tts', { success: false });
  analytics.drop('busy');
  analytics.drop('cooldown');
  analytics.replyCompleted();
  const result = analytics.snapshot();
  assert.deepEqual(result.stages.stt, {
    attempts: 4, successes: 3, failures: 1, timed_samples: 2, avg_ms: 100,
  });
  assert.equal(result.stages.llm.avg_ms, 1600);
  assert.equal(result.stages.tts.avg_ms, 3000);
  assert.equal(result.tts.avg_request_ms, 3300);
  assert.equal(result.tts.generated_audio_seconds, 4);
  assert.equal(result.tts.audio_cache, 'not implemented');
  assert.equal(result.completed_replies, 1);
  assert.equal(result.drops.busy, 1);
  assert.equal(result.drops.cooldown, 1);
});

test('invalid stages and reasons fail rather than corrupt metrics', () => {
  const analytics = createVoiceAnalytics();
  assert.throws(() => analytics.record('cached_tts'), /Invalid analytics stage/);
  assert.throws(() => analytics.drop('unknown_kind'), /Invalid drop reason/);
});
