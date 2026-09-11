import test from 'node:test';
import assert from 'node:assert/strict';
import {
  normalizeAzureLocale,
  pcmToWav16kMono,
  parseAzureResponse,
} from '../azure-speech.js';

test('normalizes supported Azure Speech locale aliases', () => {
  assert.equal(normalizeAzureLocale('ja'), 'ja-JP');
  assert.equal(normalizeAzureLocale('Japanese'), 'ja-JP');
  assert.equal(normalizeAzureLocale('en'), 'en-US');
  assert.equal(normalizeAzureLocale('en-GB'), 'en-GB');
  assert.equal(normalizeAzureLocale('auto', 'ja-JP'), 'ja-JP');
});

test('converts Discord stereo PCM to 16 kHz mono WAV', () => {
  // Six 48 kHz stereo frames become two 16 kHz mono frames.
  const pcm = Buffer.alloc(6 * 2 * 2);
  for (let frame = 0; frame < 6; frame++) {
    pcm.writeInt16LE(1000, frame * 4);
    pcm.writeInt16LE(3000, frame * 4 + 2);
  }

  const wav = pcmToWav16kMono(pcm, 48000, 2);
  assert.equal(wav.subarray(0, 4).toString('ascii'), 'RIFF');
  assert.equal(wav.subarray(8, 12).toString('ascii'), 'WAVE');
  assert.equal(wav.readUInt16LE(22), 1);
  assert.equal(wav.readUInt32LE(24), 16000);
  assert.equal(wav.readUInt16LE(34), 16);
  assert.equal(wav.readUInt32LE(40), 4);
  assert.equal(wav.readInt16LE(44), 2000);
  assert.equal(wav.readInt16LE(46), 2000);
});

test('parses Azure detailed recognition response', () => {
  const result = parseAzureResponse({
    RecognitionStatus: 'Success',
    DisplayText: '  Hello, Tsuki.  ',
    NBest: [{ Confidence: 0.934 }],
  }, 'en-US');

  assert.deepEqual(result, {
    text: 'Hello, Tsuki.',
    language: 'en-US',
    confidence: 0.934,
  });
});

test('returns an empty result when Azure recognizes no speech', () => {
  assert.deepEqual(
    parseAzureResponse({ RecognitionStatus: 'NoMatch', DisplayText: '' }, 'ja-JP'),
    { text: '', language: 'ja-JP', confidence: 0 },
  );
});
