import test from 'node:test';
import assert from 'node:assert/strict';
import { createEngagementClassifier } from '../engagement-classifier.js';
import { GroqKeyPool } from '../groq-key-pool.js';

const input = { text: 'And what about that?',
  transportType: 'voice', userId: 'alice', followupAllowed: true,
  proactiveAllowed: false, turns: [{ id: '1', userId: 'alice',
    text: 'Tsuki, help me' }] };
const response = object => ({ choices: [{ message: { content: JSON.stringify(object) } }] });
const answer = { decision: 'reply', reason: 'follow_up', confidence: 0.96,
  target_user_id: 'speaker', context_turn_ids: ['1'] };
const pool = () => new GroqKeyPool(['test-key']);
test('missing opt-in classifier model never calls a provider', async () => {
  let calls = 0;
  const c = createEngagementClassifier({ keyPool: pool(), transport: () => { calls++; } });
  assert.equal(c.configured, false);
  assert.equal((await c.classify(input)).decision, 'abstain');
  assert.equal(calls, 0);
});
test('classifier sends short attributed untrusted data and maps speaker only', async () => {
  const outcomes = [];
  const c = createEngagementClassifier({ model: 'explicit-test-model',
    keyPool: pool(), onOutcome: v => outcomes.push(v),
    transport: async request => {
      assert.equal(request.messages[0].role, 'system');
      assert.equal(request.messages[1].role, 'user');
      assert.doesNotMatch(request.messages[0].content, /Tsuki, help me/);
      const user = JSON.parse(request.messages[1].content);
      assert.deepEqual(user.recent_turns, [{ id: '1', speaker: 'speaker',
        text: 'Tsuki, help me' }]);
      assert.equal(user.followup_allowed, true);
      assert.equal(user.proactive_allowed, false);
      return response(answer);
    } });
  const result = await c.classify(input);
  assert.equal(result.decision, 'reply');
  assert.equal(result.target_user_id, 'alice');
  assert.equal(outcomes[0].result, 'reply');
});
test('stale classification, wrong reason/turn id and uncertain outputs fail closed', async () => {
  for (const [raw, overrides] of [
    [{ ...answer, reason: 'proactive' }, {}],
    [{ ...answer, context_turn_ids: ['not-provided'] }, {}],
    [{ ...answer, confidence: 0.5 }, {}],
    [answer, { revisionCurrent: () => false }],
    [{ ...answer, target_user_id: 'somebody-else' }, {}],
  ]) {
    const c = createEngagementClassifier({ model: 'model', keyPool: pool(),
      transport: async () => response(raw) });
    assert.equal((await c.classify({ ...input, ...overrides })).decision, 'abstain');
  }
});
test('classifier network failures remain fail-closed and measured without raw text', async () => {
  const outcomes = [];
  const c = createEngagementClassifier({ model: 'model', keyPool: pool(),
    onOutcome: value => outcomes.push(value),
    transport: async () => { throw new Error('transcript should stay private'); } });
  assert.equal((await c.classify(input)).decision, 'abstain');
  assert.deepEqual(Object.keys(outcomes[0]).sort(), ['elapsedMs', 'result']);
  assert.equal(outcomes[0].result, 'failure');
});
