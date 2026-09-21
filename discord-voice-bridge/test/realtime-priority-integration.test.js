import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { matchesMolabHealth } from '../molab-health.js';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const source = readFileSync(join(root, 'index.js'), 'utf8');
const notebookCompat = { status: 'ready', mode: 'trained-base-full-icl',
  checkpoint_id: 'epoch-0', voice_concurrency_limit: 3 };
const batchNotebook = { ...notebookCompat, voice_concurrency_limit: 1,
  gpu_batch_size_limit: 3, gpu_batch_endpoint: '/tts-batch' };

test('live bridge gate claims finalized speech after manual focus, releases and resets', () => {
  const start = source.indexOf('async function processRealtimeTranscript(');
  const end = source.indexOf('function handleAssemblyRealtimeUserAudio(', start);
  const handler = source.slice(start, end);
  assert.ok(start >= 0 && end > start);
  const focus = handler.indexOf('manualFocusList.size && !manualFocusList.has(userId)');
  const gate = handler.indexOf('voiceEngagement.decide(input)');
  const replyCheck = handler.indexOf("if (engagement.decision !== 'reply' ||");
  const claim = handler.indexOf('realtimeTurnArbiter.claim(userId, text)');
  assert.ok(focus >= 0 && gate > focus && replyCheck > gate && claim > replyCheck,
    'manual focus and engagement must precede atomic arbitration');
  assert.match(handler, /if \(claim\.owner\) stopNonOwnerAssemblyRealtimeCaptures\(userId\)/);
  assert.match(handler, /realtimeTurnArbiter\.release\(userId, claim\.token\)/);
  assert.match(source, /!realtimeTurnArbiter\.canCapture\(userId\)/);
  assert.match(source, /function leaveVoice\(guildId\) \{\s*realtimePendingTranscripts\.clear\(\);\s*realtimeTurnArbiter\.reset\(\)/);
  assert.match(source, /stopAssemblyRealtimeCaptures\('voice channel has no human listeners'\)/);
  assert.match(source, /realtimeTurnArbiter\.reset\(\)/);
});

test('legacy STT also gates fallback transcripts before LLM and releases non-addressed focus', () => {
  const start = source.indexOf('async function sendAudioForSTT(');
  const end = source.indexOf('async function molabIsReady(', start);
  const handler = source.slice(start, end);
  assert.ok(start >= 0 && end > start);
  const gate = handler.indexOf('voiceEngagement.decide(input)');
  const resolver = handler.indexOf('resolveEngagement(voiceEngagement, gate, input');
  const authorized = handler.indexOf("if (engagement.decision === 'reply'");
  const llm = handler.indexOf('await processWithLLM(userId, text,');
  assert.ok(gate >= 0 && resolver > gate && authorized > resolver && llm > authorized,
    'legacy STT must obtain engagement authorization before inference');
  const finalize = source.slice(source.indexOf('async function finalizeTurn('), start);
  assert.match(finalize, /if \(focusedUserId === userId\) focusedUserId = null;/);
});

test('asynchronous voice replies cannot enqueue playback for a disconnected or newly focused session', () => {
  assert.match(source, /async function processWithLLM\(userId, text, validTurn = \(\) => true, contextTurns = \[\]\)/);
  assert.match(source, /if \(!validTurn\(\)\) return false;/);
  assert.match(source, /play: \(pcm, index\) => \{\s*if \(!validTurn\(\)\) throw invalidVoiceTurn\(\)/);
  assert.match(source, /if \(next\.validTurn && !next\.validTurn\(\)\) throw invalidVoiceTurn\(\)/);
  assert.match(source, /realtimeTurnArbiter\.owns\(userId, claim\.token\) &&/);
});

test('Molab identity accepts exact legacy and new batch notebooks only', () => {
  assert.equal(matchesMolabHealth(notebookCompat), true);
  assert.equal(matchesMolabHealth(batchNotebook), true);
  assert.equal(matchesMolabHealth({ ...batchNotebook, checkpoint_id: 'future' }), false);
  assert.equal(matchesMolabHealth({ ...batchNotebook, gpu_batch_size_limit: 2 }), false);
  assert.equal(matchesMolabHealth({ ...notebookCompat, voice_concurrency_limit: 1 }), false);
});

test('bridge first uses single /tts, then true GPU /tts-batch, preserving fallback', () => {
  assert.match(source, /playMolabHybridPipeline\(\{/);
  assert.match(source, /endpoint \+ '\/tts-batch'/);
  assert.match(source, /texts: sentences, languages/);
  assert.match(source, /data\?\.mode !== 'gpu-batch'/);
  assert.match(source, /event\.stage === 'batch_synthesized'/);
  assert.match(source, /event\.stage === 'batch_fallback'/);
});
