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
  assert.match(bridgeSource, /createDiscordPcmStream\(audioBuffer, \{ tailPaddingMs: 200 \}\)/);

  const subscribeIndex = bridgeSource.indexOf('connection.subscribe(audioPlayer)');
  const playIndex = bridgeSource.indexOf('audioPlayer.play(resource)');
  assert.ok(subscribeIndex >= 0, 'voice player must be subscribed');
  assert.ok(playIndex >= 0, 'voice player must be started');
  assert.ok(subscribeIndex < playIndex, 'the connection must be subscribed before play()');
});

test('all seven guild slash commands are flat, without /t subcommands', () => {
  for (const name of ['tjoin', 'tleave', 'tfocus', 'tunfocus', 'tfocuslist', 'tsayvc', 'tsayc']) {
    assert.match(bridgeSource, new RegExp("name:\\s*'" + name + "',\\s*\\n\\s*description:"));
    assert.match(bridgeSource, new RegExp("case '" + name + "':|interaction\\.commandName === '" + name + "'"));
  }
  assert.doesNotMatch(bridgeSource, /name:\s*'t',\s*\n\s*description:/);
  assert.doesNotMatch(bridgeSource, /interaction\.options\.getSubcommand\(/);
  assert.doesNotMatch(bridgeSource, /\/t (join|leave|focus|unfocus|focuslist)\b/);
});

test('/tsayvc and /tsayc use required inline text without a modal and separate audio destinations', () => {
  assert.match(bridgeSource, /name:\s*'text', description: 'What Tsuki should say in voice chat'/);
  assert.match(bridgeSource, /name:\s*'text', description: 'What Tsuki should say in this chat'/);
  assert.match(bridgeSource, /type:\s*3, required:\s*true, max_length:\s*DIRECT_TTS_MAX_CHARS/);
  assert.match(bridgeSource, /interaction\.options\.getString\('text', true\)/);
  assert.match(bridgeSource, /const speech = await synthesizeSelectedSpeech\(text\)/);
  assert.match(bridgeSource, /if \(isSayVc\) \{\s*await enqueuePlayback\('slash-tsayvc', speech\.pcm, \{ priority: 0 \}\);/);
  assert.match(bridgeSource, /\} else \{\s*await sendVoiceMessage\(interaction\.channelId, pcmToWav\(speech\.pcm\), 'Voice message from Tsuki'\);/);
  assert.match(bridgeSource, /if \(isSayVc && !currentConnection\)/);
  assert.doesNotMatch(bridgeSource, /showModal\(|isModalSubmit\(|ModalBuilder|TextInputBuilder/);
});

test('AI inference failures have distinct voice/text feedback and do not mark Molab TTS offline', () => {
  assert.match(bridgeSource, /llm:\s*\{\s*down:\s*'Speech was understood/);
  assert.match(bridgeSource, /await reportInferenceFailureVoiceTurn\(\);\s*error\.llmFailed = true/);
  assert.match(bridgeSource, /if \(!error\.llmFailed\) reportVoiceStatus\('voice', false\)/);
  assert.match(bridgeSource, /enqueuePlayback\('llm-unavailable-prompt'/);
  assert.match(bridgeSource, /lastInferenceFailureFeedbackAt < 30000/);
});

test('the raw PCM playback guard is wired into the player path', () => {
  assert.match(bridgeSource, /inspectDiscordPcm\(audioBuffer/);
  assert.match(bridgeSource, /sampleRate:\s*CONFIG\.SAMPLE_RATE/);
  assert.match(bridgeSource, /channels:\s*CONFIG\.CHANNELS/);
  assert.match(bridgeSource, /formatVoicePing\(connection\.ping\)/);
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

test('playback adds 200 ms of frame-aligned silence without altering voiced PCM', async () => {
  const voiced = Buffer.from([1, 2, 3, 4, 5, 6, 7, 8]);
  const chunks = [];
  for await (const chunk of createDiscordPcmStream(voiced, { tailPaddingMs: 200 })) {
    chunks.push(chunk);
  }
  assert.equal(chunks.length, 2);
  assert.strictEqual(chunks[0], voiced);
  assert.equal(chunks[1].length, 48000 * 0.2 * 4);
  assert.ok(chunks[1].every(value => value === 0));
  assert.throws(() => createDiscordPcmStream(voiced, { tailPaddingMs: 600 }), /padding/);
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
