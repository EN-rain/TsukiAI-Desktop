import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {
  AssemblyKeyPool,
  loadAssemblyApiKeys,
  parseAssemblyApiKeys,
} from '../assembly-key-pool.js';
import {
  AssemblyRealtimeSession,
  createPcm48StereoTo16Mono,
} from '../assemblyai-realtime.js';

test('parses newline, comma, assignment, and bearer-style AssemblyAI keys', () => {
  assert.deepEqual(
    parseAssemblyApiKeys('\nASSEMBLYAI_API_KEY=first\nsecond, Bearer third\n# ignored\n'),
    ['first', 'second', 'third'],
  );
});

test('prefers the AssemblyAI key file over the environment fallback', () => {
  const keyFile = 'assembly-rotation-test.keys';
  try {
    fs.writeFileSync(keyFile, 'file-one\nfile-two\n');
    assert.deepEqual(loadAssemblyApiKeys(keyFile, 'env-fallback'), ['file-one', 'file-two']);
  } finally {
    fs.rmSync(keyFile, { force: true });
  }
});

test('moves a failed AssemblyAI key out of the next rotation', () => {
  const pool = new AssemblyKeyPool(['first', 'second', 'third']);

  assert.deepEqual(pool.candidates(), ['first', 'second', 'third']);
  pool.markFailure('first', 401);
  assert.deepEqual(pool.candidates(), ['second', 'third']);

  pool.markSuccess('second');
  assert.deepEqual(pool.candidates(), ['second', 'third']);
});

test('retries realtime connection with the next AssemblyAI key', async () => {
  const pool = new AssemblyKeyPool(['bad-key', 'good-key']);
  const attemptedKeys = [];
  const transcriberFactory = (apiKey) => {
    attemptedKeys.push(apiKey);
    const listeners = new Map();
    return {
      on(event, listener) {
        listeners.set(event, listener);
      },
      async connect() {
        if (apiKey === 'bad-key') {
          const error = new Error('401 Not Authorized');
          error.statusCode = 401;
          listeners.get('error')?.(error);
          throw error;
        }
        listeners.get('open')?.({ id: 'session-good', expires_at: 0 });
        return { id: 'session-good', expires_at: 0 };
      },
      sendAudio() {},
      async close() {},
    };
  };

  const session = new AssemblyRealtimeSession({ keyPool: pool, transcriberFactory });
  await session.connect();
  await session.close();

  assert.deepEqual(attemptedKeys, ['bad-key', 'good-key']);
  assert.deepEqual(pool.candidates(), ['good-key']);
});

test('cools an AssemblyAI key that fails after the socket opened', async () => {
  const pool = new AssemblyKeyPool(['first-key', 'second-key']);
  const listenersByKey = new Map();
  const transcriberFactory = (apiKey) => {
    const listeners = new Map();
    listenersByKey.set(apiKey, listeners);
    return {
      on(event, listener) {
        listeners.set(event, listener);
      },
      async connect() {
        listeners.get('open')?.({ id: apiKey, expires_at: 0 });
        return { id: apiKey, expires_at: 0 };
      },
      sendAudio() {},
      async close() {},
    };
  };

  const firstSession = new AssemblyRealtimeSession({ keyPool: pool, transcriberFactory });
  await firstSession.connect();
  const error = new Error('401 Not Authorized');
  error.statusCode = 401;
  listenersByKey.get('first-key').get('error')(error);
  await firstSession.close();

  const secondSession = new AssemblyRealtimeSession({ keyPool: pool, transcriberFactory });
  await secondSession.connect();
  await secondSession.close();

  assert.equal(listenersByKey.has('second-key'), true);
});

test('emits partial and final transcript data and returns the final text', async () => {
  const partials = [];
  const finals = [];
  const listeners = new Map();
  const transcriber = {
    on(event, listener) {
      listeners.set(event, listener);
    },
    async connect() {
      listeners.get('open')?.({ id: 'session-1', expires_at: 0 });
      return { id: 'session-1', expires_at: 0 };
    },
    sendAudio() {
      listeners.get('turn')?.({
        type: 'Turn',
        turn_order: 0,
        end_of_turn: false,
        transcript: 'hello',
        language_code: 'en',
        end_of_turn_confidence: 0.8,
      });
    },
    async close() {
      listeners.get('turn')?.({
        type: 'Turn',
        turn_order: 0,
        end_of_turn: true,
        transcript: 'Hello Tsuki.',
        language_code: 'en',
        end_of_turn_confidence: 0.99,
      });
    },
  };

  const session = new AssemblyRealtimeSession({
    keyPool: new AssemblyKeyPool(['only-key']),
    transcriberFactory: () => transcriber,
    onPartial: (event) => partials.push(event.transcript),
    onFinal: (event) => finals.push(event.transcript),
  });

  await session.connect();
  session.sendAudio(Buffer.alloc(640));
  const result = await session.close();

  assert.deepEqual(partials, ['hello']);
  assert.deepEqual(finals, ['Hello Tsuki.']);
  assert.equal(result.text, 'Hello Tsuki.');
  assert.equal(result.language, 'en');
});

test('downsamples split Discord PCM chunks to 16 kHz mono', () => {
  const convert = createPcm48StereoTo16Mono();
  const first = Buffer.alloc(2 * 4);
  const second = Buffer.alloc(4);

  for (let frame = 0; frame < 2; frame++) {
    first.writeInt16LE(1000, frame * 4);
    first.writeInt16LE(3000, frame * 4 + 2);
  }
  first.copy(second, 0, 0, 0);
  second.writeInt16LE(1000, 0);
  second.writeInt16LE(3000, 2);

  assert.equal(convert(first).length, 0);
  const output = convert(second);
  assert.equal(output.length, 2);
  assert.equal(output.readInt16LE(0), 2000);
});
