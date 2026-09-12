import assert from 'node:assert/strict';
import test from 'node:test';
import { DISCORD_VOICE_AUDIO_FILTER } from '../audio-encoding.js';

test('Discord voice-message encoding preserves API mastering and applies only a peak ceiling', () => {
  assert.equal(DISCORD_VOICE_AUDIO_FILTER, 'alimiter=limit=0.95');
  assert.doesNotMatch(DISCORD_VOICE_AUDIO_FILTER, /volume=/);
});
