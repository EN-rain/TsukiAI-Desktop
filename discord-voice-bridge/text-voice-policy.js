// Voice in text chat is independent of text-reply eligibility.
// Match complete words/phrases to avoid "voice" matching "invoice".
export const DEFAULT_TEXT_VOICE_KEYWORDS = 'voice,say it,say this,say that,speak,talk,sing,read it';

export function parseTextVoiceKeywords(value = DEFAULT_TEXT_VOICE_KEYWORDS) {
  return String(value)
    .split(',')
    .map(keyword => keyword.trim().toLocaleLowerCase())
    .filter(Boolean);
}

export function wantsTextVoiceReply(text, mode, keywords) {
  if (mode === 'off') return false;
  if (mode !== 'keywords') return false;
  const haystack = String(text || '').toLocaleLowerCase();
  const terms = Array.isArray(keywords)
    ? keywords.map(value => String(value).trim().toLocaleLowerCase()).filter(Boolean)
    : parseTextVoiceKeywords(keywords);
  return terms.some(term => {
    const start = haystack.indexOf(term);
    if (start === -1) return false;
    // Walk occurrences: a substring inside another word must not trigger.
    let at = start;
    while (at !== -1) {
      const before = at === 0 ? '' : haystack[at - 1];
      const after = haystack[at + term.length] || '';
      if (!/[\p{L}\p{N}_]/u.test(before) &&
          !/[\p{L}\p{N}_]/u.test(after)) return true;
      at = haystack.indexOf(term, at + 1);
    }
    return false;
  });
}
