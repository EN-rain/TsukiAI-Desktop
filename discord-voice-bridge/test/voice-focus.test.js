import test from 'node:test';
import assert from 'node:assert/strict';
import { shouldAcceptVoiceStart } from '../voice-focus.js';

test('an empty manual focus list allows any human speaker', () => {
  assert.equal(shouldAcceptVoiceStart({
    userId: 'user-1',
    manualFocusList: new Set(),
    assemblyRealtimeRequested: true,
    focusedUserId: null,
    turnGateClosed: false,
  }), true);
});

test('a non-listed user is rejected when manual focus is active', () => {
  assert.equal(shouldAcceptVoiceStart({
    userId: 'user-2',
    manualFocusList: new Set(['user-1']),
    assemblyRealtimeRequested: true,
    focusedUserId: null,
    turnGateClosed: false,
  }), false);
});

test('multiple listed users can start separate AssemblyAI realtime captures', () => {
  const focusList = new Set(['user-1', 'user-2']);

  assert.equal(shouldAcceptVoiceStart({
    userId: 'user-2',
    manualFocusList: focusList,
    assemblyRealtimeRequested: true,
    focusedUserId: 'user-1',
    turnGateClosed: true,
  }), true);
});

test('non-realtime mode keeps the single-user focus and turn gate', () => {
  const focusList = new Set(['user-1', 'user-2']);

  assert.equal(shouldAcceptVoiceStart({
    userId: 'user-2',
    manualFocusList: focusList,
    assemblyRealtimeRequested: false,
    focusedUserId: 'user-1',
    turnGateClosed: false,
  }), false);

  assert.equal(shouldAcceptVoiceStart({
    userId: 'user-1',
    manualFocusList: focusList,
    assemblyRealtimeRequested: false,
    focusedUserId: null,
    turnGateClosed: true,
  }), false);
});
