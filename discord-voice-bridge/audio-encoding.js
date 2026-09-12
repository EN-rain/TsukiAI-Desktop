// OpenVoice masters its WAV before this bridge sees it. Keep only a true-peak
// ceiling here so Discord's Opus conversion cannot add gain or re-introduce hiss.
export const DISCORD_VOICE_AUDIO_FILTER = 'alimiter=limit=0.95';
