import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const source = fs.readFileSync(path.join(
  path.dirname(fileURLToPath(import.meta.url)), '..', 'index.js'), 'utf8');

test('all relative bridge imports have deployable source files', () => {
  for (const match of source.matchAll(/from ['"](\.\/[^'"]+)['"]/g)) {
    const pathToModule = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', match[1]);
    assert.ok(fs.existsSync(pathToModule), 'Missing bridge module: ' + match[1]);
  }
});

test('selected voice runtime is exclusive, off does not synthesize or announce', () => {
  assert.match(source, /CONFIG\.TTS_PROVIDER === 'molab'/);
  assert.match(source, /CONFIG\.TTS_PROVIDER === 'colab'/);
  assert.match(source, /CONFIG\.TTS_PROVIDER === 'off'/);
  assert.match(source, /Selected Colab notebook unavailable/);
  assert.match(source, /Selected Molab notebook unavailable/);
  assert.doesNotMatch(source, /startMolabReadyAnnouncements/);
  assert.doesNotMatch(source, /createMolabReadyAnnouncer/);
  assert.doesNotMatch(source, /Molab is unavailable right now\. Voice replies will try the Colab fallback/);
});

test('all Discord TTS request surfaces select provider; C# text route never synthesizes', () => {
  assert.match(source, /const wav = await requestSelectedVoiceWav\(segment\);/);
  assert.match(source, /const speech = await synthesizeSelectedSpeech\(text\)/);
  assert.match(source, /const segments = planTtsUtterances\(text\)/);
  assert.match(source, /const speech = await synthesizeSelectedSpeech\(String\(reply\)\)/);
  assert.doesNotMatch(source, /requestSelectedVoiceWav\(limitDirectTtsText\(/);
  assert.match(source, /voice: false, \/\/ C# owns chat\/memory/);
  assert.match(source, /const triggeredVoice = wantsVoiceReply\(text\) && CONFIG\.TTS_PROVIDER !== 'off'/);
  assert.match(source, /const wantVoice = triggeredVoice/);
  assert.match(source, /if \(!await colabIsReady\(\)\) throw/);
  assert.match(source, /if \(!await molabIsReady\(\)\) throw/);
});
