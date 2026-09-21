import assert from 'node:assert/strict';
import test from 'node:test';
import { planTtsUtterances, speechDurationLooksIncomplete, assemblePcmUtterances } from '../tts-integrity.js';
import { readWavDurationSeconds, voiceMetadataFromWav } from '../voice-message.js';

test('manual TTS preserves every word and final sentence across bounded generations', () => {
  const input = 'The first sentence is complete. ' +
    Array.from({ length: 55 }, (_, i) => 'word' + i).join(' ') +
    '. The final sentence must not disappear!';
  const segments = planTtsUtterances(input);
  assert.ok(segments.length >= 3);
  assert.ok(segments.every(part => part.length > 0 && part.length <= 140));
  assert.equal(segments.join(' '), input);
  assert.ok(segments.at(-1).endsWith('disappear!'));
});

test('custom TTS preserves a 280-character request rather than dropping its tail', () => {
  const text = 'Speak the complete line ' + 'with every word '.repeat(18);
  const input = text.slice(0, 280);
  const segments = planTtsUtterances(input);
  assert.equal(segments.join(' '), input.trim());
  assert.ok(segments.length > 1);
});

test('overlength TTS fails clearly instead of truncating text', () => {
  assert.throws(() => planTtsUtterances('x'.repeat(1201)), /no text was dropped/);
  assert.throws(() => planTtsUtterances('  '), /empty/);
});

test('obviously short generated audio is detected conservatively', () => {
  const phrase = 'One two three four five six seven eight nine ten eleven twelve.';
  assert.equal(speechDurationLooksIncomplete(phrase, 0.6), true);
  assert.equal(speechDurationLooksIncomplete(phrase, 4), false);
  assert.equal(speechDurationLooksIncomplete('Hello!', 0.6), false);
  assert.equal(speechDurationLooksIncomplete(phrase, 1.5), false,
    'normal fast speech must not be rejected');
  assert.equal(speechDurationLooksIncomplete('長い日本語の発話を最後まできちんと読み上げてください。', 0.5), true);
});

test('exact WAV duration does not substitute rounded/fallback voice message metadata', () => {
  const bytes = 2400; // 50 ms of 24 kHz mono PCM16.
  const wav = Buffer.alloc(44 + bytes);
  wav.write('RIFF', 0);
  wav.writeUInt32LE(wav.length - 8, 4);
  wav.write('WAVE', 8);
  wav.write('fmt ', 12);
  wav.writeUInt32LE(16, 16);
  wav.writeUInt16LE(1, 20);
  wav.writeUInt16LE(1, 22);
  wav.writeUInt32LE(24000, 24);
  wav.writeUInt32LE(48000, 28);
  wav.writeUInt16LE(2, 32);
  wav.writeUInt16LE(16, 34);
  wav.write('data', 36);
  wav.writeUInt32LE(bytes, 40);
  assert.equal(readWavDurationSeconds(wav), 0.05);
  assert.equal(voiceMetadataFromWav(wav, 1).durationSecs, 0.5);
  assert.equal(readWavDurationSeconds(Buffer.from('not a wav')), null);
  assert.equal(readWavDurationSeconds(wav.subarray(0, wav.length - 8)), null);
});

test('multiple TTS segments are assembled once in order with silence between words', () => {
  const left = Buffer.from([1, 0, 2, 0]);
  const right = Buffer.from([3, 0, 4, 0]);
  const pcm = assemblePcmUtterances([left, right]);
  assert.deepEqual(pcm.subarray(0, 4), left);
  assert.deepEqual(pcm.subarray(-4), right);
  assert.equal(pcm.length, 4 + 48000 * 0.1 * 4 + 4);
  assert.ok(pcm.subarray(4, -4).every(byte => byte === 0));
  assert.throws(() => assemblePcmUtterances([left, Buffer.alloc(0)]), /Invalid PCM/);
  assert.throws(() => assemblePcmUtterances([left, right], { maxAudioBytes: 100 }), /size limit/);
});

test('manual playback enqueues only one full, synthesized utterance', async () => {
  const { readFileSync } = await import('node:fs');
  const { fileURLToPath } = await import('node:url');
  const { dirname, join } = await import('node:path');
  const source = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'index.js'), 'utf8');
  const manual = source.slice(source.indexOf("const speech = await synthesizeSelectedSpeech(text);"),
    source.indexOf("segments_played: speech.segments"));
  assert.match(manual, /await enqueuePlayback\('manual-tts', speech\.pcm, \{ priority: 0 \}\)/);
  assert.doesNotMatch(manual, /for \(const segment/);
  assert.doesNotMatch(source, /manual-tts-' \+ \(segmentsPlayed/);
});
