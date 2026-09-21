// Globally serializes the bot's Discord text turns, but does not silently
// discard the next eligible speaker while LLM/TTS is busy.
export function createTextTurnQueue({ capacity = 8 } = {}) {
  if (!Number.isInteger(capacity) || capacity < 1 || capacity > 32)
    throw new RangeError('Invalid pending text capacity');
  let active = false;
  const waiters = [];
  function claim() {
    if (!active) { active = true; return Promise.resolve(true); }
    if (waiters.length >= capacity) return Promise.resolve(false);
    return new Promise(resolve => { waiters.push(resolve); });
  }
  function release() {
    if (!active) throw new Error('No active text turn');
    const next = waiters.shift();
    if (next) next(true);
    else active = false;
  }
  function snapshot() { return { active, pending: waiters.length }; }
  return { claim, release, snapshot };
}
