import test from 'node:test';
import assert from 'node:assert/strict';
import {
  countHumanVoiceMembers,
  shouldEnableAssemblyRealtime,
} from '../voice-presence.js';

test('counts only human members in a voice channel', () => {
  const members = new Map([
    ['bot', { user: { bot: true } }],
    ['human-one', { user: { bot: false } }],
    ['human-two', { user: { bot: false } }],
  ]);

  assert.equal(countHumanVoiceMembers(members), 2);
});

test('AssemblyAI realtime is disabled when the bot is alone', () => {
  assert.equal(shouldEnableAssemblyRealtime({
    isAssemblyMode: true,
    hasVoiceConnection: true,
    humanMemberCount: 0,
  }), false);
});

test('AssemblyAI realtime is enabled for a connected bot with a human present', () => {
  assert.equal(shouldEnableAssemblyRealtime({
    isAssemblyMode: true,
    hasVoiceConnection: true,
    humanMemberCount: 1,
  }), true);
});

test('AssemblyAI realtime stays disabled when the bot is not connected', () => {
  assert.equal(shouldEnableAssemblyRealtime({
    isAssemblyMode: true,
    hasVoiceConnection: false,
    humanMemberCount: 2,
  }), false);
});
