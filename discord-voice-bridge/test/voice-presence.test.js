import test from 'node:test';
import assert from 'node:assert/strict';
import {
  countHumanVoiceMembers,
  countEligibleHumanVoiceMembers,
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

test('when manual focus is active, only focused human members enable realtime', () => {
  const members = new Map([
    ['bot', { id: 'bot', user: { bot: true } }],
    ['focused-human', { id: 'focused-human', user: { bot: false } }],
    ['unfocused-human', { id: 'unfocused-human', user: { bot: false } }],
  ]);

  assert.equal(countEligibleHumanVoiceMembers(members, new Set(['focused-human'])), 1);
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
