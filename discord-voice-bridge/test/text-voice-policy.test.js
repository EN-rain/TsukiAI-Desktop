import test from 'node:test';
import assert from 'node:assert/strict';
import {
  DEFAULT_TEXT_VOICE_KEYWORDS,
  parseTextVoiceKeywords,
  wantsTextVoiceReply,
} from '../text-voice-policy.js';

test('text-chat voice options are exclusive', () => {
  const terms = parseTextVoiceKeywords(DEFAULT_TEXT_VOICE_KEYWORDS);
  assert.equal(wantsTextVoiceReply('hello there', 'all', terms), false);
  assert.equal(wantsTextVoiceReply('please speak now', 'all', terms), false);
  assert.equal(wantsTextVoiceReply('hello there', 'keywords', terms), false);
  assert.equal(wantsTextVoiceReply('please speak now', 'keywords', terms), true);
  assert.equal(wantsTextVoiceReply('please speak now', 'off', terms), false);
  assert.equal(wantsTextVoiceReply('please speak now', 'unsupported', terms), false);
  assert.equal(wantsTextVoiceReply('please speak now', 'keywords', []), false);
});

test('keyword checks match whole words/phrases rather than substrings', () => {
  const terms = parseTextVoiceKeywords(DEFAULT_TEXT_VOICE_KEYWORDS);
  assert.equal(wantsTextVoiceReply('This invoice was paid', 'keywords', terms), false);
  assert.equal(wantsTextVoiceReply('We are talking about it', 'keywords', terms), false);
  assert.equal(wantsTextVoiceReply('Please, SAY IT!', 'keywords', terms), true);
  assert.equal(wantsTextVoiceReply('say this "You have reached your limit"', 'keywords', terms), true);
  assert.equal(wantsTextVoiceReply('someone says this', 'keywords', terms), false);
  assert.equal(wantsTextVoiceReply('Read it aloud', 'keywords', terms), true);
  assert.equal(wantsTextVoiceReply('do not read items', 'keywords', terms), false);
  assert.equal(wantsTextVoiceReply('voice message', 'keywords', terms), true);
});

test('bridge uses the mode and configured keyword list when deciding chat audio', async () => {
  const fs = await import('node:fs');
  const source = fs.readFileSync(new URL('../index.js', import.meta.url), 'utf8');
  assert.match(source, /const triggeredVoice = wantsVoiceReply\(text\) && CONFIG\.TTS_PROVIDER !== 'off'/);
  assert.match(source, /const wantVoice = triggeredVoice/);
  assert.match(source, /CONFIG\.TEXT_REPLY_MODE !== 'any' && !mentioned && !repliedToBot/);
  assert.ok(source.indexOf("if (engagement.decision !== 'reply') return;") <
    source.lastIndexOf("const triggeredVoice = wantsVoiceReply(text)"));
  assert.match(source, /return wantsTextVoiceReply\(text, TEXT_VOICE_MODE, TEXT_VOICE_KEYWORDS\)/);
  assert.match(source, /process\.env\.TEXT_VOICE_KEYWORDS \|\| DEFAULT_TEXT_VOICE_KEYWORDS/);
});
