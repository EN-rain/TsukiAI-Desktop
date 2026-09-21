import test from 'node:test';
import assert from 'node:assert/strict';

import {
  batchMolabVoiceSentences,
  countMolabWords,
  playMolabSentencePipeline,
  splitMolabVoiceSentences,
} from '../molab-voice-pipeline.js';

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const separate = [
  'First batch contains enough words that it must remain a separate generation request.',
  'Second batch contains enough words that it must remain a separate generation request.',
  'Third batch contains enough words that it must remain a separate generation request.',
  'Fourth batch contains enough words that it must remain a separate generation request.',
  'Fifth batch contains enough words that it must remain a separate generation request.',
  'Sixth batch contains enough words that it must remain a separate generation request.',
];
const sixBatches = separate.join(' ');

test('splits natural punctuation and long clauses without breaking common abbreviations', () => {
  assert.deepEqual(
    splitMolabVoiceSentences('Dr. Tsuki has 3.14 apples. Hello! 元気ですか？Next.'),
    ['Dr. Tsuki has 3.14 apples.', 'Hello!', '元気ですか？', 'Next.'],
  );
  assert.deepEqual(splitMolabVoiceSentences(''), []);
  assert.deepEqual(splitMolabVoiceSentences('one two three four', 7),
    ['one two', 'three', 'four']);
  assert.deepEqual(splitMolabVoiceSentences('Hello, lovely world!', 8),
    ['Hello,', 'lovely', 'world!']);
  const longText = '今日は、' + '日'.repeat(290) + '終わりです。';
  const parts = splitMolabVoiceSentences(longText);
  assert.ok(parts.length > 1 && parts.every(part => part.length <= 280));
  assert.equal(parts.join(''), longText);
});

test('groups adjacent short sentences into one WAV job, leaves medium and long independent', () => {
  assert.deepEqual(
    batchMolabVoiceSentences('Hey! I am back! What are we doing today?'),
    ['Hey! I am back! What are we doing today?'],
  );
  assert.deepEqual(
    batchMolabVoiceSentences('Hello! ' + separate[0] + ' ' + separate[1]),
    ['Hello! ' + separate[0], separate[1]],
  );
  assert.deepEqual(batchMolabVoiceSentences(sixBatches), separate);
  const long = 'This rather lengthy sentence is designed to contain more than twenty distinct words and must stay in its own synthesis batch even when tiny nearby sentences would otherwise be grouped together.';
  assert.ok(countMolabWords(long) > 20);
  assert.deepEqual(batchMolabVoiceSentences('Hi! ' + long + ' Bye!'),
    ['Hi!', long, 'Bye!']);
  assert.deepEqual(batchMolabVoiceSentences('こんにちは！お元気ですか？'), ['こんにちは！お元気ですか？']);
});

test('A1 plays while later batches synthesize, at most three active and ordered', async () => {
  const events = [];
  let active = 0;
  let maximum = 0;
  const result = await playMolabSentencePipeline({
    text: sixBatches,
    synthesize: async (_batch, index) => {
      active++;
      maximum = Math.max(maximum, active);
      events.push('start:' + index);
      await delay(index === 0 ? 5 : index === 1 ? 35 : 7);
      active--;
      events.push('done:' + index);
      return {
        audio: Buffer.from([index]),
        audioDurationSeconds: index === 0 ? 10 : 3,
        generationMs: 500,
      };
    },
    play: async (_audio, index) => {
      events.push('play:' + index);
      await delay(20);
      events.push('end:' + index);
    },
  });
  assert.equal(result.sentences, 6);
  assert.equal(result.batches, 6);
  assert.equal(result.played, 6);
  assert.ok(maximum > 1 && maximum <= 3);
  assert.equal(result.maxActiveJobs, maximum);
  assert.deepEqual(events.filter(event => event.startsWith('play:')),
    ['play:0', 'play:1', 'play:2', 'play:3', 'play:4', 'play:5']);
  assert.ok(events.indexOf('start:1') < events.indexOf('end:0'),
    'A2 must start while A1 plays');
  assert.ok(events.indexOf('play:1') > events.indexOf('end:0'),
    'A2 must not interrupt A1');
});

test('long A1 feeds up to three jobs; short A1 prioritizes A2 before further batches', async () => {
  const longActive = [];
  await playMolabSentencePipeline({
    text: sixBatches,
    synthesize: async (_batch, index, { activeJobs }) => {
      if (index > 0) longActive.push(activeJobs);
      await delay(index === 0 ? 1 : 10);
      return { audio: Buffer.from([index]), audioDurationSeconds: index === 0 ? 12 : 4, generationMs: 400 };
    },
    play: async () => { await delay(15); },
  });
  assert.ok(Math.max(...longActive) === 3);
  const shortActive = [];
  await playMolabSentencePipeline({
    text: sixBatches,
    synthesize: async (_batch, index, { activeJobs }) => {
      if (index > 0) shortActive.push(activeJobs);
      await delay(index === 0 ? 1 : 15);
      return { audio: Buffer.from([index]), audioDurationSeconds: 1.5, generationMs: 700 };
    },
    play: async () => { await delay(2); },
  });
  assert.equal(shortActive[0], 1, 'A2 gets priority during a short first batch');
});

test('measured generation ms, audio duration, active jobs, and rolling RTF are reported', async () => {
  const events = [];
  const result = await playMolabSentencePipeline({
    text: 'Hey! Welcome back. How have you been?',
    synthesize: async () => ({
      audio: Buffer.alloc(192000 * 2),
      audioDurationSeconds: 2,
      generationMs: 1200,
      requestMs: 1510,
    }),
    play: async () => {},
    onProgress: event => events.push(event),
  });
  assert.equal(result.sentences, 3);
  assert.equal(result.batches, 1);
  assert.equal(result.rtf, 0.6);
  assert.equal(events.find(event => event.stage === 'synthesized').requestMs, 1510);
  assert.equal(events.find(event => event.stage === 'synthesized').audioDurationSeconds, 2);
  assert.equal(events.find(event => event.stage === 'synthesized').rtf, 0.6);
  assert.equal(events.find(event => event.stage === 'playing').activeJobs, 0);
});

test('missing generation timer never relabels request duration as model time', async () => {
  const events = [];
  const result = await playMolabSentencePipeline({
    text: 'Good morning.',
    synthesize: async () => ({ audio: Buffer.alloc(192000),
      audioDurationSeconds: 1, requestMs: 1350 }),
    play: async () => {},
    onProgress: event => events.push(event),
  });
  const measured = events.find(event => event.stage === 'synthesized');
  assert.equal(measured.generationMs, null);
  assert.equal(measured.requestMs, 1350);
  assert.equal(measured.rtf, null);
  assert.equal(measured.rollingRtf, null);
  assert.equal(result.rtf, null);
});

test('no more than three concurrent jobs and no chat-voice routing', async () => {
  const calls = [];
  await assert.rejects(() => playMolabSentencePipeline({
    text: sixBatches,
    maxConcurrent: 4,
    synthesize: () => calls.push('synth'),
    play: () => {},
  }), RangeError);
  assert.equal(calls.length, 0);
});

test('synthesis failure does not play later batches out of order', async () => {
  const played = [];
  await assert.rejects(
    () => playMolabSentencePipeline({
      text: sixBatches,
      synthesize: async (_batch, index) => {
        if (index === 1) throw new Error('batch 2 unavailable');
        return { audio: Buffer.from([index]), audioDurationSeconds: 10 };
      },
      play: async (_audio, index) => played.push(index),
    }),
    /batch 2 unavailable/,
  );
  assert.deepEqual(played, [0]);
});
