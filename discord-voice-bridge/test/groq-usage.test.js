import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import axios from 'axios';
import { GroqUsageRecorder, installGroqUsageInterceptor } from '../groq-usage.js';

test('usage recorder persists separate Groq attempts across service recreation without credentials', () => {
  const directory = mkdtempSync(path.join(tmpdir(), 'groq-usage-'));
  const key = 'gsk_fake_test_credentials_12345';
  try {
    const recorder = new GroqUsageRecorder(directory);
    recorder.recordAttempt(key, 'stt');
    new GroqUsageRecorder(directory).recordAttempt(key, 'llm');
    const contents = readFileSync(path.join(directory, 'groq-usage-bridge.jsonl'), 'utf8');
    const rows = contents.trim().split('\n').map(line => JSON.parse(line));
    assert.equal(rows.filter(row => row.type === 'tracking_started').length, 1);
    assert.deepEqual(rows.slice(1).map(row => row.kind), ['stt', 'llm']);
    assert.equal(rows[1].key_id.length, 64);
    assert.equal(rows[1].key_id, rows[2].key_id);
    assert.equal(rows[1].key_mask, 'gsk_...12345');
    assert.doesNotMatch(contents, /gsk_fake_test_credentials_12345/);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('axios interceptor counts each Groq network dispatch including repeated calls and skips other hosts', async () => {
  const directory = mkdtempSync(path.join(tmpdir(), 'groq-usage-'));
  try {
    const api = axios.create({ adapter: async config => ({
      data: {}, status: 200, statusText: 'OK', headers: {}, config,
    }) });
    const recorder = new GroqUsageRecorder(directory);
    installGroqUsageInterceptor(api, recorder);
    for (let count = 0; count < 2; count++) {
      await api.post('https://api.groq.com/openai/v1/audio/transcriptions', {}, {
        headers: { Authorization: 'Bearer gsk_fake_test_credentials_12345' },
      });
    }
    await api.post('https://api.groq.com/openai/v1/chat/completions', {}, {
      headers: { Authorization: 'Bearer gsk_fake_test_credentials_12345' },
    });
    await api.post('https://example.com/elsewhere', {});
    const rows = readFileSync(recorder.file, 'utf8').trim().split('\n').map(JSON.parse);
    assert.deepEqual(rows.slice(1).map(row => row.kind), ['stt', 'stt', 'llm']);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
