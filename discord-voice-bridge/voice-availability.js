// Report a provider outage once per outage, and a recovery once it has returned.
// Sending is serialized so messages remain ordered; no raw provider errors,
// credentials, or user text are posted to Discord.
export function createVoiceAvailabilityNotifier({ send, log = () => {} }) {
  if (typeof send !== 'function') throw new TypeError('send is required');
  const statuses = new Map();
  let queued = Promise.resolve();

  function report(name, available, { down, up } = {}) {
    if (!name || typeof available !== 'boolean') {
      throw new TypeError('name and available are required');
    }
    const before = statuses.get(name);
    if (before === available) return queued;
    statuses.set(name, available);
    // Initial availability is not an outage recovery.
    if (available && before !== false) return queued;
    const content = available ? up : down;
    if (!content) return queued;
    queued = queued
      .then(() => send(content))
      .catch(error => { log('Voice status notification failed: ' + (error?.name || 'Error')); });
    return queued;
  }

  return { report, status: name => statuses.get(name) };
}
