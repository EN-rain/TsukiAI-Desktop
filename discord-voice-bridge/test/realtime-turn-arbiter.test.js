import test from 'node:test';
import assert from 'node:assert/strict';
import { createRealtimeTurnArbiter, isMeaningfulVoiceTranscript }
  from '../realtime-turn-arbiter.js';

test('punctuation, noise and empty finals cannot lock a speaker', () => {
  const turn = createRealtimeTurnArbiter();
  for (const sound of ['', ' ', '.', '?!', 'a', 'uh', 'um!', 'hmm']) {
    assert.equal(turn.claim('one', sound).accepted, false);
    assert.equal(turn.snapshot().active, false);
  }
  assert.equal(isMeaningfulVoiceTranscript('はい'), true);
  assert.equal(isMeaningfulVoiceTranscript('Hi'), true);
});

test('first meaningful finalized transcript wins, not first speaking event', () => {
  const lock = createRealtimeTurnArbiter();
  const first = lock.claim('speaker-B', 'Hello, Tsuki.');
  assert.equal(first.accepted, true);
  assert.equal(first.owner, true);
  assert.equal(lock.canCapture('speaker-B'), true);
  assert.equal(lock.canCapture('speaker-A'), false);
  assert.equal(lock.claim('speaker-A', 'I began talking earlier.').accepted, false);
  const same = lock.claim('speaker-B', 'Follow up.');
  assert.equal(same.accepted, true);
  assert.equal(same.token, first.token);
  assert.equal(lock.release('speaker-A', first.token), false);
  assert.equal(lock.release('speaker-B', first.token), true);
  assert.equal(lock.canCapture('speaker-A'), true);
  assert.equal(lock.claim('speaker-A', 'Now it is my turn.').accepted, true);
});

test('disconnect/reset invalidates old releases and new speaker can claim', () => {
  const arbiter = createRealtimeTurnArbiter();
  const old = arbiter.claim('one', 'First turn.');
  arbiter.reset();
  assert.equal(arbiter.snapshot().active, false);
  const next = arbiter.claim('two', 'Second turn.');
  assert.equal(next.accepted, true);
  assert.equal(arbiter.release('one', old.token), false);
  assert.equal(arbiter.claim('three', 'Third turn.').accepted, false);
  assert.equal(arbiter.release('two', next.token), true);
  assert.equal(arbiter.claim('three', 'Third turn.').accepted, true);
});
