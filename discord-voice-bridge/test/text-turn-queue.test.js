import test from 'node:test';
import assert from 'node:assert/strict';
import { createTextTurnQueue } from '../text-turn-queue.js';

test('FIFO waiters run after the active text turn releases', async () => {
  const q = createTextTurnQueue({ capacity: 2 });
  assert.equal(await q.claim(), true);
  const order = [];
  const b = q.claim().then(granted => order.push(['b', granted]));
  const c = q.claim().then(granted => order.push(['c', granted]));
  assert.deepEqual(q.snapshot(), { active: true, pending: 2 });
  assert.equal(await q.claim(), false);
  q.release();
  await b;
  assert.deepEqual(order, [['b', true]]);
  q.release();
  await c;
  assert.deepEqual(order, [['b', true], ['c', true]]);
  q.release();
  assert.deepEqual(q.snapshot(), { active: false, pending: 0 });
});

test('claim can be reused when idle and rejects invalid bounds or release', async () => {
  assert.throws(() => createTextTurnQueue({ capacity: 0 }), RangeError);
  const q = createTextTurnQueue();
  assert.throws(() => q.release(), /No active/);
  assert.equal(await q.claim(), true);
  q.release();
  assert.equal(await q.claim(), true);
  q.release();
});
