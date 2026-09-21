// First meaningful *final transcript* wins a single Discord voice reply turn.
// AssemblyAI captures remain per-speaker; the bridge serializes accepted turns.
// No text, transcript, speaker ID or audio is persisted by this arbiter.
export function isMeaningfulVoiceTranscript(rawText) {
  const text = String(rawText || '').trim();
  // Reject punctuation-only and one-character noise; accept English/CJK words.
  if (text.length < 2 || !/[\p{L}\p{N}]/u.test(text)) return false;
  // A hesitation alone should not make the room ignore a real sentence.
  return !/^(?:uh+|um+|erm+|hmm+|mm+|eh+|ah+)\W*$/iu.test(text);
}

export function createRealtimeTurnArbiter() {
  let owner = null;
  let sequence = 0;
  return {
    claim(userId, transcript) {
      if (!userId || !isMeaningfulVoiceTranscript(transcript)) {
        return { accepted: false, reason: 'no_text' };
      }
      if (owner && owner.userId !== userId) {
        return { accepted: false, reason: 'busy' };
      }
      if (owner) return { accepted: true, token: owner.token, owner: false };
      owner = { userId, token: ++sequence };
      return { accepted: true, token: owner.token, owner: true };
    },
    owns(userId, token) {
      return owner?.userId === userId && owner.token === token;
    },
    canCapture(userId) {
      return !owner || owner.userId === userId;
    },
    release(userId, token) {
      if (!this.owns(userId, token)) return false;
      owner = null;
      return true;
    },
    reset() {
      owner = null;
      sequence += 1;
    },
    snapshot() {
      return { active: Boolean(owner) };
    },
  };
}
