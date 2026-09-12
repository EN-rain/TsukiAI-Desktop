// Keep Discord voice-message mastering conservative: the OpenVoice WAVs are
// already valid PCM, so only apply the measured delivery gain and a true-peak
// ceiling before Opus encoding.
export const DISCORD_VOICE_AUDIO_FILTER = 'volume=5dB,alimiter=limit=0.95';
