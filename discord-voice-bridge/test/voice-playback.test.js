import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { createDiscordPcmStream, inspectDiscordPcm } from '../voice-playback.js';

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const bridgeSource = fs.readFileSync(path.join(testDirectory, '..', 'index.js'), 'utf8');

test('voice playback subscribes before starting raw PCM and keeps a subscriber playable', () => {
  assert.match(bridgeSource, /createAudioPlayer\(\{[\s\S]*noSubscriber:\s*NoSubscriberBehavior\.Play/);
  assert.match(bridgeSource, /createDiscordPcmStream\(audioBuffer\)/);

  const subscribeIndex = bridgeSource.indexOf('connection.subscribe(audioPlayer)');
  const playIndex = bridgeSource.indexOf('audioPlayer.play(resource)');
  assert.ok(subscribeIndex >= 0, 'voice player must be subscribed');
  assert.ok(playIndex >= 0, 'voice player must be started');
  assert.ok(subscribeIndex < playIndex, 'the connection must be subscribed before play()');
});

test('the registered guild command is /t and no active handler text points to /tsuki', () => {
  assert.match(bridgeSource, /name:\s*'t',\s*\n\s*description:/);
  assert.match(bridgeSource, /interaction\.commandName !== 't'/);
  assert.doesNotMatch(bridgeSource, /\/tsuki\b/);
});

test('the raw PCM playback guard is wired into the player path', () => {
  assert.match(bridgeSource, /inspectDiscordPcm\(audioBuffer/);
  assert.match(bridgeSource, /sampleRate:\s*CONFIG\.SAMPLE_RATE/);
  assert.match(bridgeSource, /channels:\s*CONFIG\.CHANNELS/);
});

test('the playback guard rejects empty, silent, and misaligned PCM', () => {
  assert.throws(() => inspectDiscordPcm(Buffer.alloc(0)), /empty/);
  assert.throws(() => inspectDiscordPcm(Buffer.alloc(3)), /frame-aligned/);
  assert.throws(() => inspectDiscordPcm(Buffer.alloc(4)), /silent/);
});

test('the playback guard reports non-silent stereo PCM accurately', () => {
  const pcm = Buffer.alloc(8);
  pcm.writeInt16LE(1000, 0);
  pcm.writeInt16LE(-2000, 2);
  pcm.writeInt16LE(3000, 4);
  pcm.writeInt16LE(-4000, 6);

  const stats = inspectDiscordPcm(pcm);
  assert.equal(stats.bytes, 8);
  assert.equal(stats.frames, 2);
  assert.equal(stats.durationSecs, 2 / 48000);
  assert.equal(stats.peak, 4000);
  assert.equal(Math.round(stats.rms), 2739);
});

test('raw PCM is exposed as one binary stream chunk', async () => {
  const pcm = Buffer.from([1, 2, 3, 4]);
  const chunks = [];
  for await (const chunk of createDiscordPcmStream(pcm)) {
    chunks.push(chunk);
  }

  assert.equal(chunks.length, 1);
  assert.strictEqual(chunks[0], pcm);
});
