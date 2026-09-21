import test from 'node:test';
import assert from 'node:assert/strict';
import { planMolabHybridGroups, playMolabHybridPipeline }
  from '../molab-hybrid-pipeline.js';

const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
const utterances = [
  'First sentence is played right away.',
  'Second sentence waits in GPU batch.',
  'Third sentence waits in GPU batch.',
  'Fourth sentence waits in GPU batch.',
  'Fifth sentence waits in GPU batch.',
  'Sixth sentence waits in GPU batch.',
  'Seventh sentence waits in GPU batch.',
];

test('first sentence stays single, remaining group into two or three', () => {
  assert.deepEqual(planMolabHybridGroups(utterances.join(' ')),
    [[utterances[0]], utterances.slice(1, 4), utterances.slice(4, 7)]);
  assert.deepEqual(planMolabHybridGroups(utterances.slice(0, 5).join(' ')),
    [[utterances[0]], utterances.slice(1, 3), utterances.slice(3, 5)]);
  assert.deepEqual(planMolabHybridGroups(utterances.slice(0, 3).join(' ')),
    [[utterances[0]], utterances.slice(1, 3)]);
  assert.deepEqual(planMolabHybridGroups(utterances.slice(0, 2).join(' ')),
    [[utterances[0]], [utterances[1]]]);
  assert.deepEqual(planMolabHybridGroups(''), []);
});

test('A1 playback begins before batch request, real groups are played in source order', async () => {
  const order = [];
  let active = 0;
  let maxActive = 0;
  const result = await playMolabHybridPipeline({
    text: utterances.join(' '),
    synthesize: async (text, index) => {
      order.push('single:' + index);
      return { audio: Buffer.from([index]), audioDurationSeconds: 1,
        generationMs: 100, requestMs: 150 };
    },
    synthesizeBatch: async (sentences, start) => {
      order.push('batch:' + start + ':' + sentences.length);
      active++;
      maxActive = Math.max(active, maxActive);
      await wait(start === 1 ? 25 : 1);
      active--;
      order.push('batch_ready:' + start);
      return {
        items: sentences.map((_, idx) => ({
          audio: Buffer.from([start + idx]), audioDurationSeconds: 1,
        })),
        generationMs: 200, requestMs: 250,
      };
    },
    play: async (pcm, index) => {
      assert.equal(pcm[0], index);
      order.push('play:' + index);
      await wait(8);
      order.push('played:' + index);
    },
  });
  assert.equal(result.played, utterances.length);
  assert.equal(result.batches, 3);
  assert.equal(result.groupedInferenceCalls, 2);
  assert.equal(result.singleInferenceCalls, 1);
  assert.equal(result.maxActiveJobs, 1);
  assert.ok(maxActive <= 1);
  assert.ok(order.indexOf('play:0') < order.indexOf('batch:1:3'));
  assert.ok(order.indexOf('batch:1:3') < order.indexOf('played:0'));
  assert.deepEqual(order.filter(x => x.startsWith('play:')),
    utterances.map((_, idx) => 'play:' + idx));
});

test('unsupported/failed GPU batch falls back to singles, not silent truncation', async () => {
  const calls = [];
  const events = [];
  const result = await playMolabHybridPipeline({
    text: utterances.slice(0, 4).join(' '),
    synthesize: async (_, index) => {
      calls.push('single:' + index);
      return { audio: Buffer.from([index]), audioDurationSeconds: 1,
        generationMs: 10 };
    },
    synthesizeBatch: async () => {
      calls.push('batch:unsupported');
      throw new Error('404 /tts-batch unavailable');
    },
    play: async (audio, index) => {
      calls.push('play:' + index);
      assert.equal(audio[0], index);
    },
    onProgress: item => events.push(item),
  });
  assert.equal(result.played, 4);
  assert.equal(result.groupedInferenceCalls, 1);
  assert.equal(result.singleInferenceCalls, 4);
  assert.equal(events.filter(item => item.stage === 'batch_fallback').length, 1);
  assert.equal(events.filter(item => item.stage === 'batch_synthesized').length, 0);
  assert.deepEqual(calls.filter(item => item.startsWith('play:')),
    ['play:0', 'play:1', 'play:2', 'play:3']);
});

test('incomplete GPU output falls back to singles, failed single never plays later audio', async () => {
  const played = [];
  await assert.rejects(() => playMolabHybridPipeline({
    text: utterances.slice(0, 4).join(' '),
    synthesizeBatch: async () => ({ items: [{ audio: Buffer.from([1]),
      audioDurationSeconds: 1 }] }),
    synthesize: async (_, idx) => {
      if (idx === 2) throw new Error('single sentence 3 unavailable');
      return { audio: Buffer.from([idx]), audioDurationSeconds: 1 };
    },
    play: async (_, idx) => played.push(idx),
  }), /single sentence 3 unavailable/);
  assert.deepEqual(played, [0]);
});

test('missing model time is null; per-GPU-call timing is not counted once per item', async () => {
  const events = [];
  const result = await playMolabHybridPipeline({
    text: utterances.slice(0, 4).join(' '),
    synthesize: async () => ({
      audio: Buffer.from([0]), audioDurationSeconds: 2, requestMs: 1000,
    }),
    synthesizeBatch: async () => ({
      items: [1, 2, 3].map(n => ({ audio: Buffer.from([n]),
        audioDurationSeconds: 2 })),
      requestMs: 1000, generationMs: 1500,
    }),
    play: async () => {},
    onProgress: item => events.push(item),
  });
  assert.equal(result.rtf, 0.25);
  assert.equal(events.find(e => e.stage === 'synthesized').generationMs, null);
  const batched = events.filter(e => e.stage === 'batch_synthesized');
  assert.equal(batched.length, 1);
  assert.equal(batched[0].size, 3);
  assert.equal(batched[0].rtf, 0.25);
  assert.equal(batched[0].audioDurationSeconds, 6);
});
