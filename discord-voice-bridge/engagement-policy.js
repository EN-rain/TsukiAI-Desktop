import { isMeaningfulVoiceTranscript } from './realtime-turn-arbiter.js';

// Ephemeral Discord-only addressee policy. No transcript or speaker ID is
// persisted; observe/abstain never authorizes inference or audio generation.
const MODES = new Set(['wake_only', 'context_aware', 'proactive']);
const REASONS = new Set(['direct', 'follow_up', 'proactive', 'addressed_other',
  'background', 'noise', 'uncertain']);
const DECISIONS = new Set(['reply', 'observe', 'abstain']);
const CONTINUATION = /^(?:and\b|also\b|but\b|what about\b|how about\b|why\b|how\b|what\b|when\b|where\b|which\b|who\b|can you\b|could you\b|would you\b|do you\b|is it\b|does it\b|tell me more\b|go on\b|explain\b|that\b|it\b|more\b)/iu;
const REDIRECT = /^(?:hey|hi|yo|hello|thanks|thank you|sorry|listen|look|ask|tell|okay|ok)\s+@?[\p{L}\p{N}_-]+\s*[,!:]/iu;
const THIRD_PERSON = /^(?:said|says|told|was|were|is|has|had|did|does|went|thinks|thought|looks|seems|would|could|will)\b/iu;
const escapeRegex = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
function aliasesOf(value) {
  const names = Array.isArray(value) ? value : String(value || '').split(',');
  return [...new Set(names.map(name => String(name).trim().toLocaleLowerCase())
    .filter(name => name.length >= 2 && name.length <= 30))].slice(0, 8);
}
function decision(value, reason, userId, contextIds = [], confidence = 1) {
  return { decision: value, reason, confidence,
    target_user_id: value === 'reply' ? userId : null,
    context_turn_ids: [...contextIds] };
}
export function validateEngagementClassification(result, userId, minimum = 0.85) {
  if (!result || typeof result !== 'object' || !DECISIONS.has(result.decision) ||
      !REASONS.has(result.reason) || !Number.isFinite(result.confidence) ||
      result.confidence < 0 || result.confidence > 1 ||
      !Array.isArray(result.context_turn_ids) ||
      result.context_turn_ids.length > 12 ||
      !result.context_turn_ids.every(id => typeof id === 'string' && id.length <= 30) ||
      (result.decision === 'reply' &&
        (result.target_user_id !== userId || result.confidence < minimum ||
         !['direct', 'follow_up', 'proactive'].includes(result.reason))) ||
      (result.decision !== 'reply' && result.target_user_id != null)) {
    return null;
  }
  return result;
}

// Only explicitly selected, still-live turns are allowed into an authorized
// reply. Never forward the complete observed room buffer, raw Discord IDs, or
// the current utterance twice. The recipient must treat these as untrusted data.
export function selectReplyContext(policy, input, outcome) {
  if (outcome?.decision !== 'reply' ||
      outcome.target_user_id !== input.userId ||
      !Array.isArray(outcome.context_turn_ids)) return [];
  const currentId = outcome.context_turn_ids.at(-1);
  const allowed = new Set(outcome.context_turn_ids.slice(0, -1));
  if (!allowed.size || !currentId) return [];
  return policy.recent(input).filter(turn =>
    allowed.has(turn.id) && turn.id !== currentId &&
    typeof turn.text === 'string' && turn.text.length > 0)
    .slice(-4).map(turn => ({
      speaker: turn.userId === input.userId ? 'current user' : 'another speaker',
      text: turn.text.replace(/\s+/gu, ' ').trim().slice(0, 500),
    }));
}

export function createEngagementPolicy({
  mode = 'context_aware', aliases = ['tsuki', 'tsukihime'],
  followupMs = 10000, roomTtlMs = 30000, maxTurns = 12,
  now = () => Date.now(),
} = {}) {
  if (!MODES.has(mode) || !Number.isInteger(followupMs) ||
      followupMs < 0 || followupMs > 30000 ||
      !Number.isInteger(roomTtlMs) || roomTtlMs < 1000 ||
      !Number.isInteger(maxTurns) || maxTurns < 1 || maxTurns > 12) {
    throw new RangeError('Invalid engagement policy settings');
  }
  const names = aliasesOf(aliases);
  if (!names.length) throw new RangeError('At least one addressed name required');
  const rooms = new Map();
  let sequence = 0;
  let revision = 0;
  function roomKey(guildId, channelId, transport) {
    return `${transport}:${guildId}:${channelId}`;
  }
  function purge(room, time) {
    room.turns = room.turns.filter(turn => time - turn.at <= roomTtlMs &&
      time >= turn.at);
    if (room.followup && (time - room.followup.at > followupMs ||
      time < room.followup.at)) room.followup = null;
  }
  function getRoom(guildId, channelId, transport, time) {
    const key = roomKey(guildId, channelId, transport);
    if (!rooms.has(key)) rooms.set(key, { turns: [], followup: null });
    const room = rooms.get(key);
    purge(room, time);
    return room;
  }
  function remember(room, userId, content, time) {
    const id = String(++sequence);
    room.turns.push({ id, userId, text: content.slice(0, 500), at: time });
    if (room.turns.length > maxTurns) room.turns.splice(0, room.turns.length - maxTurns);
    return id;
  }
  function addressed(text) {
    const value = text.trim().toLocaleLowerCase();
    for (const name of names) {
      const safe = escapeRegex(name);
      // Address at the start, or greeting/name at the end. Third-person
      // mentions of Tsuki never become a directive on their own.
      const start = value.match(new RegExp(`^(?:(?:hey|hi|hello|yo)\\s+)?${safe}(?![\\p{L}\\p{N}_])([\\s\\S]*)$`, 'iu'));
      if (start && !THIRD_PERSON.test(start[1].replace(/^[\s,!:?.]+/u, ''))) {
        return true;
      }
      if (new RegExp(`(?:^|[,!?]\\s*)${safe}[!?.,\\s]*$`, 'iu').test(value)) {
        return true;
      }
    }
    return false;
  }
  function decide({ guildId, channelId, transport, userId, text,
    mentioned = false, repliedToBot = false, addressedOther = false,
    focusAllowed = true, enabled = true, at = now() }) {
    if (!enabled || !focusAllowed || !guildId || !channelId || !userId ||
        !['voice', 'chat'].includes(transport)) {
      return decision('abstain', 'uncertain', userId, [], 0);
    }
    const normalized = String(text || '').trim().slice(0, 1000);
    if (!isMeaningfulVoiceTranscript(normalized)) {
      return decision('abstain', 'noise', userId);
    }
    const room = getRoom(guildId, channelId, transport, at);
    const id = remember(room, userId, normalized, at);
    const turnIds = [id];
    if (addressedOther || (!mentioned && !repliedToBot &&
      REDIRECT.test(normalized) && !addressed(normalized))) {
      if (room.followup?.userId === userId) room.followup = null;
      return decision('observe', 'addressed_other', userId, turnIds);
    }
    if (mentioned || repliedToBot || addressed(normalized)) {
      return decision('reply', 'direct', userId, turnIds);
    }
    const following = room.followup;
    if (mode !== 'wake_only' && followupMs > 0 && following?.userId === userId &&
        at - following.at <= followupMs && at >= following.at &&
        CONTINUATION.test(normalized)) {
      return decision('reply', 'follow_up', userId, [following.turnId, id].filter(Boolean));
    }
    // Proactive needs an independently verified classifier; opening a
    // room-question to everybody must not silently activate it.
    return decision('observe', 'background', userId, turnIds, 0.5);
  }
  function completed({ guildId, channelId, transport, userId, at = now() }) {
    if (!guildId || !channelId || !userId || !['voice', 'chat'].includes(transport)) return;
    const room = getRoom(guildId, channelId, transport, at);
    // An unrelated speaker may have spoken while Tsuki was composing her
    // reply. Never associate their turn with the reply recipient.
    const original = room.turns.findLast(turn => turn.userId === userId);
    room.followup = { userId, at, turnId: original?.id || null };
  }
  function recent({ guildId, channelId, transport, at = now() }) {
    return getRoom(guildId, channelId, transport, at).turns.map(turn => ({ ...turn }));
  }
  function followupEligible({ guildId, channelId, transport, userId, at = now() }) {
    const room = getRoom(guildId, channelId, transport, at);
    return mode !== 'wake_only' && followupMs > 0 &&
      room.followup?.userId === userId && at - room.followup.at <= followupMs;
  }
  function invalidateFollowup({ guildId, channelId, transport }) {
    const room = rooms.get(roomKey(guildId, channelId, transport));
    if (room) room.followup = null;
  }
  function reset() { rooms.clear(); revision++; }
  function snapshot() { return { rooms: rooms.size, revision }; }
  return { mode, decide, completed, recent, followupEligible,
    invalidateFollowup, reset, snapshot };
}
