import test from 'node:test';
import assert from 'node:assert/strict';
import { voiceSession, validateVoiceTarget } from '../voice-control.js';

const guild = '1307370943416500314';
const other = '346070665184542720';
const channel = '1307370944255229956';
const connection = { joinConfig: { guildId: guild, channelId: channel },
  state: { status: 'ready' } };

test('reports real connected channel instead of configured channel', () => {
  const state = voiceSession(connection, guild);
  assert.deepEqual(state, {
    guild_id: guild, channel_id: channel, connected: true,
    configured_guild_id: guild,
  });
  assert.equal(validateVoiceTarget({ guild_id: guild }, state), null);
  assert.equal(validateVoiceTarget({ guild_id: other }, state)?.status, 400);
});

test('disconnecting or joining is never reported as connected', () => {
  const disconnected = voiceSession(null, guild);
  assert.equal(disconnected.connected, false);
  assert.equal(disconnected.channel_id, '');
  assert.equal(validateVoiceTarget({ guild_id: guild }, disconnected)?.status, 409);
  assert.equal(voiceSession({
    ...connection, state: { status: 'connecting' },
  }, guild).connected, false);
});

test('rejects malformed or different-server voice commands', () => {
  const state = voiceSession(connection, guild);
  for (const payload of [null, [], {}, { guild_id: 13 },
    { guild_id: other }, { guild_id: '../etc' }]) {
    assert.equal(validateVoiceTarget(payload, state)?.status, 400);
  }
  assert.equal(validateVoiceTarget({ guild_id: guild },
    voiceSession({ ...connection, joinConfig: { guildId: other, channelId: channel } },
      guild))?.status, 409);
});
