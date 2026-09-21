// Pure checks shared by the bridge's loopback-only dashboard voice controls.
export function voiceSession(connection, configuredGuildId, readyStatus = 'ready') {
  const guildId = connection?.joinConfig?.guildId || '';
  const channelId = connection?.joinConfig?.channelId || '';
  const connected = Boolean(guildId && channelId && connection?.state?.status === readyStatus);
  return {
    guild_id: connected ? guildId : '',
    channel_id: connected ? channelId : '',
    connected,
    configured_guild_id: configuredGuildId,
  };
}

export function validateVoiceTarget(data, session, requireConnection = true) {
  if (!data || typeof data !== 'object' || Array.isArray(data) ||
      typeof data.guild_id !== 'string' ||
      !/^[0-9]{17,20}$/.test(data.guild_id) ||
      data.guild_id !== session.configured_guild_id) {
    return { status: 400, error: 'Select the active configured Discord server.' };
  }
  if (requireConnection && (!session.connected || session.guild_id !== data.guild_id)) {
    return { status: 409, error: 'Tsuki is not connected to voice in this server.' };
  }
  return null;
}
