import test from 'node:test';
import assert from 'node:assert/strict';
import { acceptsDiscordChatChannel } from '../voice-chat-policy.js';
import { readFileSync } from 'node:fs';

const bridgeSource = readFileSync(new URL('../index.js', import.meta.url), 'utf8');

const context = {
  guildId: '11111111111111111',
  configuredGuildId: '11111111111111111',
  configuredTextId: '22222222222222222',
  configuredVoiceId: '33333333333333333',
  guildVoiceType: 2,
};

test('the configured text channel and the configured voice attached chat both work', () => {
  assert.equal(acceptsDiscordChatChannel({
    ...context, channelId: context.configuredTextId, channelType: 0,
  }), true);
  assert.equal(acceptsDiscordChatChannel({
    ...context, channelId: context.configuredVoiceId, channelType: 2,
  }), true);
});

test('only the currently joined voice chat is accepted when the bot joins another channel', () => {
  assert.equal(acceptsDiscordChatChannel({
    ...context, activeVoiceId: '44444444444444444', channelId: context.configuredVoiceId, channelType: 2,
  }), false);
  assert.equal(acceptsDiscordChatChannel({
    ...context, activeVoiceId: '44444444444444444', channelId: '44444444444444444', channelType: 2,
  }), true);
});

test('reject unrelated channels, foreign guilds, or a spoofed channel type', () => {
  assert.equal(acceptsDiscordChatChannel({
    ...context, channelId: '55555555555555555', channelType: 2,
  }), false);
  assert.equal(acceptsDiscordChatChannel({
    ...context, channelId: context.configuredVoiceId, channelType: 0,
  }), false);
  assert.equal(acceptsDiscordChatChannel({
    ...context, guildId: '66666666666666666', channelId: context.configuredTextId, channelType: 0,
  }), false);
  assert.equal(acceptsDiscordChatChannel({
    ...context, guildId: '66666666666666666', channelId: context.configuredVoiceId, channelType: 2,
  }), false);
});

test('Discord message handler uses the allowlist and retains reply/voice modes', () => {
  assert.match(bridgeSource, /client\.on\('messageCreate', async \(message\) => \{/);
  assert.match(bridgeSource, /acceptsDiscordChatChannel\(\{/);
  assert.match(bridgeSource, /guildVoiceType: ChannelType\.GuildVoice/);
  assert.match(bridgeSource, /CONFIG\.TEXT_REPLY_MODE !== 'any' && !mentioned && !repliedToBot/);
  assert.match(bridgeSource, /const gate = chatEngagement.decide\(input\)/);
  assert.match(bridgeSource, /if \(engagement.decision !== 'reply' \|\| !sessionValid\(\)\) return;/);
  assert.match(bridgeSource, /const triggeredVoice = wantsVoiceReply\(text\) && CONFIG\.TTS_PROVIDER !== 'off'/);
  assert.match(bridgeSource, /const wantVoice = triggeredVoice/);
  assert.match(bridgeSource, /\[VOICE CHAT\] Missing channel permissions/);
});

test('text inference failure is reported separately from TTS and never uses the misleading brain-hiccup reply', () => {
  assert.match(bridgeSource, /const reply = response\?\.data\?\.text;\s*reportVoiceStatus\('llm', true\)/);
  assert.match(bridgeSource, /console\.error\('\[TEXT\] Turn failed:', turnError\?\.message\);\s*\/\/ An inference HTTP 502 is not evidence that Molab\/Colab TTS is down\.\s*reportVoiceStatus\('llm', false\)/);
  assert.match(bridgeSource, /I received your message, but my AI reply service is unavailable right now\./);
  assert.doesNotMatch(bridgeSource, /my brain hiccuped/);
});

test('with no separate text channel, only the selected voice channel chat is accepted', () => {
  assert.equal(acceptsDiscordChatChannel({
    ...context, configuredTextId: '', channelId: context.configuredVoiceId, channelType: 2,
  }), true);
  assert.equal(acceptsDiscordChatChannel({
    ...context, configuredTextId: '', channelId: context.configuredTextId, channelType: 0,
  }), false);
  assert.equal(acceptsDiscordChatChannel({
    ...context, configuredTextId: '', channelId: '55555555555555555', channelType: 2,
  }), false);
  assert.equal(acceptsDiscordChatChannel({
    ...context, configuredTextId: '', guildId: '66666666666666666', channelId: context.configuredVoiceId, channelType: 2,
  }), false);
});
