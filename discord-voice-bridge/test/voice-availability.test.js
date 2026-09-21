import test from 'node:test';
import assert from 'node:assert/strict';
import { createVoiceAvailabilityNotifier } from '../voice-availability.js';

const texts = { down: 'Voice not available right now.', up: 'Voice is available again.' };

test('one outage notice and one recovery; no duplicate alerts while down', async () => {
  const notices = [];
  const reporter = createVoiceAvailabilityNotifier({ send: async text => notices.push(text) });
  await reporter.report('voice', true, texts);
  assert.deepEqual(notices, []);
  await reporter.report('voice', false, texts);
  await reporter.report('voice', false, texts);
  await reporter.report('voice', true, texts);
  await reporter.report('voice', true, texts);
  assert.deepEqual(notices, [texts.down, texts.up]);
});

test('Molab down does not claim the whole voice path is down', async () => {
  const notices = [];
  const reporter = createVoiceAvailabilityNotifier({ send: async text => notices.push(text) });
  await reporter.report('molab', false, { down: 'Molab unavailable. Trying Colab.', up: 'Molab ready.' });
  await reporter.report('voice', true, texts);
  assert.deepEqual(notices, ['Molab unavailable. Trying Colab.']);
  assert.equal(reporter.status('voice'), true);
});

test('overlapping transitions are sent in order', async () => {
  const notices = [];
  let release;
  const held = new Promise(resolve => { release = resolve; });
  const reporter = createVoiceAvailabilityNotifier({
    send: async message => {
      if (message === texts.down) await held;
      notices.push(message);
    },
  });
  const first = reporter.report('voice', false, texts);
  const second = reporter.report('voice', true, texts);
  release();
  await Promise.all([first, second]);
  assert.deepEqual(notices, [texts.down, texts.up]);
});

test('failure to send an alert does not break later notifications', async () => {
  const notices = [];
  const reporter = createVoiceAvailabilityNotifier({
    send: async message => {
      if (message === texts.down) throw new Error('Discord offline');
      notices.push(message);
    },
  });
  await reporter.report('voice', false, texts);
  await reporter.report('voice', true, texts);
  assert.deepEqual(notices, [texts.up]);
});
