// Discord voice channels have a text chat attached to the channel itself.
// Accept the configured text channel and the chat of the voice channel in
// which Tsuki is currently joined; never read unrelated channels/servers.
export function acceptsDiscordChatChannel({
  channelId,
  channelType,
  guildId,
  configuredGuildId,
  configuredTextId,
  activeVoiceId,
  configuredVoiceId,
  guildVoiceType,
}) {
  if (!guildId || guildId !== configuredGuildId) return false;
  if (configuredTextId && channelId === configuredTextId) return true;
  const selectedVoiceId = activeVoiceId || configuredVoiceId;
  return Boolean(selectedVoiceId && channelId === selectedVoiceId &&
    channelType === guildVoiceType);
}
