import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { GroqKeyPool, loadGroqApiKeys, parseGroqApiKeys } from '../groq-key-pool.js';
import { transcribeAudio } from '../groq-whisper.js';

test('parses newline, comma, and assignment-style Groq key lists', () => {
  assert.deepEqual(
    parseGroqApiKeys('\nGROQ_API_KEY=gsk_first\ngsk_second,gsk_third\n# ignored\n'),
    ['gsk_first', 'gsk_second', 'gsk_third'],
  );
});

test('prefers a configured key file over the single-key fallback', () => {
  const keyFile = 'groq-rotation-test.keys';
  try {
    fs.writeFileSync(keyFile, 'file-one\nfile-two\n');
    assert.deepEqual(loadGroqApiKeys(keyFile, 'env-fallback'), ['file-one', 'file-two']);
  } finally {
    fs.rmSync(keyFile, { force: true });
  }
});

test('moves failed keys behind available keys', () => {
  const pool = new GroqKeyPool(['first', 'second', 'third']);

  assert.deepEqual(pool.candidates(), ['first', 'second', 'third']);

  pool.markFailure('first', 403);
  assert.deepEqual(pool.candidates(), ['second', 'third']);

  pool.markSuccess('second');
  assert.deepEqual(pool.candidates(), ['second', 'third']);
});

test('tries the next Groq key when the current key is rejected', async () => {
  const pool = new GroqKeyPool(['key-one', 'key-two']);
  const authorizationHeaders = [];
  const httpClient = {
    post: async (_url, _form, options) => {
      authorizationHeaders.push(options.headers.Authorization);
      if (authorizationHeaders.length === 1) {
        const error = new Error('forbidden');
        error.response = { status: 403 };
        throw error;
      }

      return { data: { text: 'hello Tsuki', language: 'en' } };
    },
  };

  const result = await transcribeAudio(pool, Buffer.alloc(8), 48000, 'auto', httpClient);

  assert.equal(result.text, 'hello Tsuki');
  assert.deepEqual(authorizationHeaders, ['Bearer key-one', 'Bearer key-two']);
});
