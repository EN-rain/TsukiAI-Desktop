import test from 'node:test';
import assert from 'node:assert/strict';
import { createEngagementPolicy, selectReplyContext, validateEngagementClassification }
  from '../engagement-policy.js';
import { createRealtimeTurnArbiter } from '../realtime-turn-arbiter.js';

const input = (userId, text, extra = {}) => ({
  guildId: 'guild', channelId: 'voice', transport: 'voice', userId, text, ...extra,
});
test('the first unrelated speaker cannot claim the reply lock', () => {
  const policy = createEngagementPolicy();
  const lock = createRealtimeTurnArbiter();
  const observed = policy.decide(input('a', 'Can you give this to Sarah?'));
  assert.equal(observed.decision, 'observe');
  assert.equal(lock.snapshot().active, false);
  const engaged = policy.decide(input('b', 'Tsuki, can you answer?'));
  assert.equal(engaged.reason, 'direct');
  assert.equal(lock.claim('b', 'Tsuki, can you answer?').owner, true);
  assert.equal(lock.claim('a', 'And me?').accepted, false);
});
test('name boundaries and third-person mentions cannot force replies', () => {
  const p = createEngagementPolicy();
  for (const text of ['Tsukishima is here', 'I think Tsuki said that', 'Tsuki said that.',
    'Tsuki is nice', 'Hello Sarah, can you come over?']) {
    assert.notEqual(p.decide(input('a', text)).decision, 'reply', text);
  }
  for (const text of ['Tsuki, are you here?', 'Hey Tsuki! Answer?', 'Can you help, Tsuki?',
    'Tsuki?']) {
    assert.equal(p.decide(input('a', text)).reason, 'direct', text);
  }
});
test('only a completed reply opens a same-user same-room timed follow-up', () => {
  let time = 100_000;
  const p = createEngagementPolicy({ now: () => time });
  p.decide(input('a', 'Tsuki, tell me something'));
  assert.equal(p.decide(input('a', 'And what about it?')).decision, 'observe');
  p.completed({ guildId: 'guild', channelId: 'voice', transport: 'voice', userId: 'a' });
  assert.equal(p.decide(input('b', 'And what about it?')).decision, 'observe');
  assert.equal(p.decide(input('a', 'And what about it?')).reason, 'follow_up');
  assert.equal(p.decide(input('a', 'And what about it?', { channelId: 'other' })).decision, 'observe');
  assert.equal(p.decide(input('a', 'And what about it?', { guildId: 'other' })).decision, 'observe');
  assert.equal(p.decide(input('a', 'And what about it?', { transport: 'chat' })).decision, 'observe');
  time += 10_001;
  assert.equal(p.decide(input('a', 'And what about it?')).decision, 'observe');
});
test('reply follow-up context attaches the original speaker turn, not an interleaved stranger', () => {
  const p = createEngagementPolicy();
  const owner = p.decide(input('a', 'Tsuki, help with my quest'));
  p.decide(input('b', 'Sarah, can you help with the quest?'));
  p.completed({ guildId: 'guild', channelId: 'voice', transport: 'voice', userId: 'a' });
  const follow = p.decide(input('a', 'And what next?'));
  assert.equal(follow.reason, 'follow_up');
  assert.deepEqual(follow.context_turn_ids, [owner.context_turn_ids[0], follow.context_turn_ids.at(-1)]);
  assert.notEqual(follow.context_turn_ids[0], p.recent(input('a', '')).at(-2)?.id);
});

test('only relevant prior untrusted turns, never overheard room history or Discord IDs, reach inference', () => {
  const p = createEngagementPolicy();
  const first = input('a', 'Tsuki, help with a quest');
  const direct = p.decide(first);
  assert.deepEqual(selectReplyContext(p, first, direct), []);
  p.decide(input('b', 'I am talking to Sarah about another game'));
  p.completed(first);
  const next = input('a', 'And what about the reward?');
  const follow = p.decide(next);
  assert.deepEqual(selectReplyContext(p, next, follow), [
    { speaker: 'current user', text: 'Tsuki, help with a quest' },
  ]);
  assert.deepEqual(selectReplyContext(p, input('b', 'And what?'), follow), []);
  p.reset();
  assert.deepEqual(selectReplyContext(p, next, follow), []);
});

test('selected historical context normalizes line breaks before the C# boundary', () => {
  const p = createEngagementPolicy();
  const original = input('a', 'Tsuki, first line\nsecond line');
  p.decide(original);
  p.completed(original);
  const next = input('a', 'And how?');
  const result = p.decide(next);
  assert.deepEqual(selectReplyContext(p, next, result), [
    { speaker: 'current user', text: 'Tsuki, first line second line' },
  ]);
});

test('explicit redirection, focus and reset close the follow-up', () => {
  const p = createEngagementPolicy();
  p.completed({ guildId: 'guild', channelId: 'voice', transport: 'voice', userId: 'a' });
  assert.equal(p.decide(input('a', 'Hey Sarah, look!')).reason, 'addressed_other');
  assert.equal(p.decide(input('a', 'And why?')).decision, 'observe');
  assert.equal(p.decide(input('a', 'Tsuki, help', { focusAllowed: false })).decision, 'abstain');
  p.completed({ guildId: 'guild', channelId: 'voice', transport: 'voice', userId: 'a' });
  p.reset();
  assert.equal(p.decide(input('a', 'And why?')).decision, 'observe');
});
test('noise, wake_only and proactive without classifier fail closed', () => {
  const p = createEngagementPolicy({ mode: 'wake_only' });
  assert.equal(p.decide(input('a', 'um!')).reason, 'noise');
  p.completed({ guildId: 'guild', channelId: 'voice', transport: 'voice', userId: 'a' });
  assert.equal(p.decide(input('a', 'And why?')).decision, 'observe');
  const proactive = createEngagementPolicy({ mode: 'proactive' });
  assert.equal(proactive.decide(input('a', 'Does anyone know the answer?')).decision, 'observe');
});
test('classifier schema rejects invalid targets, confidence, reason and identifiers', () => {
  const good = { decision: 'reply', reason: 'follow_up', confidence: 0.95,
    target_user_id: 'a', context_turn_ids: ['1'] };
  assert.deepEqual(validateEngagementClassification(good, 'a'), good);
  for (const mutation of [{ confidence: NaN }, { confidence: 0.5 },
    { target_user_id: 'b' }, { reason: 'override' }, { context_turn_ids: ['x'.repeat(31)] },
    { decision: 'execute' }]) {
    assert.equal(validateEngagementClassification({ ...good, ...mutation }, 'a'), null);
  }
});
