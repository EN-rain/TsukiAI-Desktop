import 'dotenv/config';
import http from 'http';
import https from 'https';
import {
  ChannelType,
  Client,
  GatewayIntentBits,
  MessageFlags,
  PermissionsBitField,
  Routes,
} from 'discord.js';
import {
  joinVoiceChannel,
  createAudioPlayer,
  createAudioResource,
  AudioPlayerStatus,
  NoSubscriberBehavior,
  StreamType,
  VoiceConnectionStatus,
  EndBehaviorType,
  entersState,
  getVoiceConnection
} from '@discordjs/voice';
import axios from 'axios';
import { spawn as spawnProcess } from 'child_process';
import ffmpegPath from 'ffmpeg-static';
import axiosRetry from 'axios-retry';
import prism from 'prism-media';
import { GroqKeyPool, loadGroqApiKeys } from './groq-key-pool.js';
import { GroqUsageRecorder, installGroqUsageInterceptor } from './groq-usage.js';
import { AssemblyKeyPool, loadAssemblyApiKeys } from './assembly-key-pool.js';
import {
  AssemblyRealtimeSession,
  createPcm48StereoTo16Mono,
  transcribeAudio as transcribeAssemblyAudio,
} from './assemblyai-realtime.js';
import {
  countEligibleHumanVoiceMembers,
  shouldEnableAssemblyRealtime,
} from './voice-presence.js';
import { shouldAcceptVoiceStart } from './voice-focus.js';
import { createRealtimeTurnArbiter } from './realtime-turn-arbiter.js';
import { createEngagementPolicy, selectReplyContext } from './engagement-policy.js';
import { createEngagementClassifier } from './engagement-classifier.js';
import { createVoiceAnalytics } from './voice-analytics.js';
import { DISCORD_VOICE_AUDIO_FILTER } from './audio-encoding.js';
import { createDiscordPcmStream, inspectDiscordPcm } from './voice-playback.js';
import { voiceMetadataFromWav, readWavDurationSeconds } from './voice-message.js';
import { playMolabHybridPipeline } from './molab-hybrid-pipeline.js';
import { planTtsUtterances, speechDurationLooksIncomplete, assemblePcmUtterances } from './tts-integrity.js';
import { matchesMolabHealth } from './molab-health.js';
import { createVoiceAvailabilityNotifier } from './voice-availability.js';
import { acceptsDiscordChatChannel } from './voice-chat-policy.js';
import { createTextTurnQueue } from './text-turn-queue.js';
import { voiceSession, validateVoiceTarget } from './voice-control.js';
import { DEFAULT_TEXT_VOICE_KEYWORDS, parseTextVoiceKeywords, wantsTextVoiceReply } from './text-voice-policy.js';

const DEBUG_MODE = (process.env.DEBUG || 'false').toLowerCase() === 'true';
function debugLog(...args) {
  if (DEBUG_MODE) {
    console.log(...args);
  }
}

// Configure axios retry with exponential backoff
axiosRetry(axios, {
  retries: 3,
  retryDelay: axiosRetry.exponentialDelay,
  retryCondition: (error) => {
    // Retry on network errors, 5xx, 429, and timeouts
    return axiosRetry.isNetworkOrIdempotentRequestError(error) ||
           error.response?.status === 429 ||
           (error.response?.status >= 500 && error.response?.status < 600);
  },
  onRetry: (retryCount, error, requestConfig) => {
    console.log(`[HTTP Retry] attempt=${retryCount}, url=${requestConfig.url}, error=${error.message}`);
  }
});
// Register after the retry policy: every outbound Groq request and retry is
// persisted with a key fingerprint before dispatch to the provider.
installGroqUsageInterceptor(axios, new GroqUsageRecorder());

// Configuration (trim token - copy/paste often adds newlines or spaces)
function boundedInt(raw, fallback, minimum, maximum) {
  const value = Number.parseInt(String(raw ?? ''), 10);
  return Number.isFinite(value) ? Math.max(minimum, Math.min(maximum, value)) : fallback;
}

function boundedFloat(raw, fallback, minimum, maximum) {
  const value = Number.parseFloat(String(raw ?? ''));
  return Number.isFinite(value) ? Math.max(minimum, Math.min(maximum, value)) : fallback;
}

const BRIDGE_HTTP_PORT = boundedInt(process.env.BRIDGE_HTTP_PORT, 3001, 1, 65535);
const HTTP_KEEPALIVE_ENABLED = (process.env.HTTP_KEEPALIVE_ENABLED || 'true').toLowerCase() === 'true';
const HTTP_MAX_SOCKETS = boundedInt(process.env.HTTP_MAX_SOCKETS, 10, 1, 100);
const HTTP_MAX_FREE_SOCKETS = boundedInt(process.env.HTTP_MAX_FREE_SOCKETS, 5, 0, HTTP_MAX_SOCKETS);
const HTTP_KEEPALIVE_MS = boundedInt(process.env.HTTP_KEEPALIVE_MS, 30000, 1000, 300000);
const configuredGroqKeys = loadGroqApiKeys(
  (process.env.GROQ_API_KEYS_FILE || '').trim(),
  process.env.GROQ_API_KEYS || process.env.GROQ_API_KEY,
);
const configuredAssemblyKeys = loadAssemblyApiKeys(
  (process.env.ASSEMBLYAI_KEYS_FILE || '').trim(),
  process.env.ASSEMBLYAI_API_KEYS || process.env.ASSEMBLYAI_API_KEY,
);
const CONFIG = {
  DISCORD_TOKEN: (process.env.DISCORD_TOKEN || 'YOUR_BOT_TOKEN').trim(),
  GUILD_ID: process.env.GUILD_ID || 'YOUR_GUILD_ID',
  VOICE_CHANNEL_ID: process.env.VOICE_CHANNEL_ID || 'YOUR_VOICE_CHANNEL_ID',
  TEXT_CHANNEL_ID: (process.env.TEXT_CHANNEL_ID || '').trim(),
  // 'mention' (default): reply only when @-mentioned. 'any': treat every
  // non-bot message in the channel as input.
  TEXT_REPLY_MODE: (process.env.TEXT_REPLY_MODE || 'mention').trim().toLowerCase(),
  VOICE_ENGAGEMENT_MODE: (process.env.VOICE_ENGAGEMENT_MODE || 'context_aware').trim().toLowerCase(),
  CHAT_ENGAGEMENT_MODE: (process.env.CHAT_ENGAGEMENT_MODE || 'context_aware').trim().toLowerCase(),
  ENGAGEMENT_ALIASES: (process.env.ENGAGEMENT_ALIASES || 'tsuki,tsukihime').trim(),
  ENGAGEMENT_FOLLOWUP_MS: boundedInt(process.env.ENGAGEMENT_FOLLOWUP_MS, 10000, 0, 30000),
  ENGAGEMENT_CLASSIFIER_MODEL: (process.env.ENGAGEMENT_CLASSIFIER_MODEL || '').trim(),
  CSHARP_API_URL: (process.env.CSHARP_API_URL || 'http://localhost:5000').trim(),
  CSHARP_API_KEY: (process.env.CSHARP_API_KEY || '').trim(),
  // Molab is a manually started notebook, like Colab. Supply its distinct
  // custom Cloudflare hostname here after the DNS/tunnel is configured.
  // There is no Molab-issued URL or API key and no VPS Molab env variable.
  MOLAB_CUSTOM_DOMAIN: 'molab-tts.tsukihime.dpdns.org',
  COLAB_CUSTOM_DOMAIN: 'colab-tts.tsukihime.dpdns.org',
  // Strict voice routing: exactly one TTS provider, never automatic fallback.
  TTS_PROVIDER: (process.env.TSUKI_TTS_PROVIDER || 'molab').trim().toLowerCase(),
  ASSEMBLYAI_API_KEY: configuredAssemblyKeys[0] || '',
  ASSEMBLYAI_API_KEYS: configuredAssemblyKeys,
  ASSEMBLYAI_KEYS_FILE: (process.env.ASSEMBLYAI_KEYS_FILE || '').trim(),
  // GROQ_API_KEY remains the first-key compatibility value; runtime STT uses
  // the pool so a rejected or rate-limited key rotates to the next one.
  GROQ_API_KEY: configuredGroqKeys[0] || '',
  GROQ_API_KEYS: configuredGroqKeys,
  GROQ_API_KEYS_FILE: (process.env.GROQ_API_KEYS_FILE || '').trim(),
  AZURE_SPEECH_KEY: (process.env.AZURE_SPEECH_KEY || '').trim(),
  AZURE_SPEECH_REGION: (process.env.AZURE_SPEECH_REGION || '').trim(),
  AZURE_STT_LANGUAGE: (process.env.AZURE_STT_LANGUAGE || 'en-US').trim(),
  STT_MODE: (process.env.STT_MODE || 'groq').trim().toLowerCase(), // 'azure', 'assemblyai', 'groq', or 'local'
  STT_FALLBACK_MODE: (process.env.STT_FALLBACK_MODE || 'groq').trim().toLowerCase(),
  STT_LANGUAGE: (process.env.STT_LANGUAGE || 'auto').trim().toLowerCase(),
  USE_CLOUD_STT: (process.env.USE_CLOUD_STT || 'false').toLowerCase() === 'true',
  SAMPLE_RATE: 48000, // Discord voice sample rate
  CHANNELS: 2, // Stereo
  FRAME_SIZE: 960, // 20ms at 48kHz
};
const GROQ_KEY_POOL = new GroqKeyPool(CONFIG.GROQ_API_KEYS);
const ASSEMBLY_KEY_POOL = new AssemblyKeyPool(CONFIG.ASSEMBLYAI_API_KEYS);

// USE_CLOUD_STT is the feature gate. A local setting must never silently turn
// into a cloud request just because STT_MODE has a stale provider value.
if (!CONFIG.USE_CLOUD_STT) {
  CONFIG.STT_MODE = 'local';
  CONFIG.STT_FALLBACK_MODE = 'local';
}

const assemblyRealtimeRequested = CONFIG.USE_CLOUD_STT
  && CONFIG.STT_MODE === 'assemblyai'
  && ASSEMBLY_KEY_POOL.size > 0;

if (HTTP_KEEPALIVE_ENABLED) {
  const httpAgent = new http.Agent({
    keepAlive: true,
    keepAliveMsecs: HTTP_KEEPALIVE_MS,
    maxSockets: HTTP_MAX_SOCKETS,
    maxFreeSockets: HTTP_MAX_FREE_SOCKETS,
  });
  const httpsAgent = new https.Agent({
    keepAlive: true,
    keepAliveMsecs: HTTP_KEEPALIVE_MS,
    maxSockets: HTTP_MAX_SOCKETS,
    maxFreeSockets: HTTP_MAX_FREE_SOCKETS,
  });
  axios.defaults.httpAgent = httpAgent;
  axios.defaults.httpsAgent = httpsAgent;
  console.log('[HTTP] Keep-alive agents enabled');
}

// Determine STT mode and load appropriate module
let transcribeAudio = null;
let sttModeName = 'Unavailable (no local transcriber installed)';

if (CONFIG.STT_MODE === 'azure' && CONFIG.AZURE_SPEECH_KEY.length > 10 && CONFIG.AZURE_SPEECH_REGION) {
  console.log('[INFO] Cloud STT enabled (Azure Speech)');
  sttModeName = 'Azure Speech';
  try {
    const azureModule = await import('./azure-speech.js');
    transcribeAudio = azureModule.transcribeAudio;
  } catch (error) {
    console.error('[ERROR] Failed to load Azure Speech module:', error.message);
  }
} else if (CONFIG.STT_MODE === 'assemblyai' && ASSEMBLY_KEY_POOL.size > 0) {
  console.log(`[INFO] AssemblyAI realtime v3 enabled (${ASSEMBLY_KEY_POOL.size} keys)`);
  sttModeName = 'AssemblyAI realtime v3';
} else if (CONFIG.STT_MODE === 'assemblyai' && ASSEMBLY_KEY_POOL.size === 0) {
  console.log('[INFO] ✅ Cloud STT enabled (AssemblyAI)');
  console.error('[ERROR] AssemblyAI realtime requested but no API keys were loaded');
  sttModeName = 'AssemblyAI unavailable';
  transcribeAudio = transcribeAssemblyAudio;
} else if (CONFIG.STT_MODE === 'groq' && GROQ_KEY_POOL.size > 0) {
  console.log(`[INFO] ✅ Cloud STT enabled (Groq Whisper, ${GROQ_KEY_POOL.size} keys)`);
  sttModeName = 'Groq Whisper';
  try {
    const groqModule = await import('./groq-whisper.js');
    transcribeAudio = groqModule.transcribeAudio;
  } catch (error) {
    console.error('[ERROR] Failed to load Groq Whisper module:', error.message);
    process.exit(1);
  }
} else {
  console.warn('[STT] No configured transcriber. Local STT is not installed; cloud transcription will NOT run while USE_CLOUD_STT=false.');
  sttModeName = 'Unavailable (no local transcriber installed)';
}

// ── VAD & chunking settings ────────────────────────────────────────────
// Load an optional second cloud provider for outage fallback. This stays lazy
// with respect to requests: it does not make any network call at startup.
let fallbackTranscriber = null;
let fallbackApiKey = '';
let fallbackModeName = '';
let activeSttMode = CONFIG.STT_MODE;

if (CONFIG.STT_FALLBACK_MODE !== CONFIG.STT_MODE) {
  try {
    if (CONFIG.STT_FALLBACK_MODE === 'azure' && CONFIG.AZURE_SPEECH_KEY.length > 10 && CONFIG.AZURE_SPEECH_REGION) {
      const azureFallback = await import('./azure-speech.js');
      fallbackTranscriber = azureFallback.transcribeAudio;
      fallbackApiKey = CONFIG.AZURE_SPEECH_KEY;
      fallbackModeName = 'Azure Speech';
    } else if (CONFIG.STT_FALLBACK_MODE === 'assemblyai' && ASSEMBLY_KEY_POOL.size > 0) {
      fallbackTranscriber = transcribeAssemblyAudio;
      fallbackApiKey = ASSEMBLY_KEY_POOL;
      fallbackModeName = 'AssemblyAI realtime v3';
    } else if (CONFIG.STT_FALLBACK_MODE === 'groq' && GROQ_KEY_POOL.size > 0) {
      const groqFallback = await import('./groq-whisper.js');
      fallbackTranscriber = groqFallback.transcribeAudio;
      fallbackApiKey = GROQ_KEY_POOL;
      fallbackModeName = 'Groq Whisper';
    }
  } catch (error) {
    console.error('[STT] Failed to load fallback provider:', error.message);
  }
}

// If the selected primary is not configured, use the configured fallback as
// the active cloud provider instead of silently dropping to C# STT.
if (!transcribeAudio && fallbackTranscriber) {
  transcribeAudio = fallbackTranscriber;
  activeSttMode = CONFIG.STT_FALLBACK_MODE;
  sttModeName = fallbackModeName + ' (fallback)';
  console.warn('[STT] Primary provider unavailable; using ' + sttModeName);
}

const VAD = {
  // RMS threshold to consider a frame as speech (0–32767 scale for 16-bit PCM)
  RMS_SPEECH_THRESHOLD: boundedInt(process.env.VAD_RMS_THRESHOLD, 300, 0, 32767),
  // How many consecutive silent frames (20 ms each) before we finalize a segment
  // 20 frames * 20 ms = 400 ms silence cutoff
  SILENCE_FRAMES_CUTOFF: boundedInt(process.env.VAD_SILENCE_FRAMES, 20, 1, 1000),
  // Hard max for a single audio segment in seconds
  MAX_SEGMENT_SEC: boundedFloat(process.env.VAD_MAX_SEGMENT_SEC, 12, 0.5, 60),
  // Max total turn length in seconds (across all segments before sending to LLM)
  MAX_TURN_SEC: boundedFloat(process.env.VAD_MAX_TURN_SEC, 30, 1, 300),
  // End-of-turn silence: if no new speech for this many ms, finalize the whole turn
  END_OF_TURN_MS: boundedInt(process.env.VAD_END_OF_TURN_MS, 650, 100, 10000),
  // Per-user cooldown in ms (prevent rapid-fire triggers)
  USER_COOLDOWN_MS: boundedInt(process.env.VAD_USER_COOLDOWN_MS, 2000, 1000, 60000),
  // Minimum segment size in bytes to bother sending for STT (avoids tiny pops)
  MIN_SEGMENT_BYTES: boundedInt(process.env.VAD_MIN_SEGMENT_BYTES, 7680, 1, 1024 * 1024), // ~40 ms stereo 48 kHz
};
const VAD_BATCHING_ENABLED = (process.env.VAD_BATCHING_ENABLED || 'true').toLowerCase() === 'true';
const VAD_FRAME_BATCH_SIZE = boundedInt(process.env.VAD_FRAME_BATCH_SIZE, 8, 1, 10);

// C# integration is active whenever CSHARP_API_URL is set to any non-empty value.
// Previously this excluded the default localhost:5000 URL which is the correct address
// for TsukiAI running locally — that check was wrong and caused standalone mode.
const hasCSharpIntegration = !!(process.env.CSHARP_API_URL && process.env.CSHARP_API_URL.trim().length > 0);

if (hasCSharpIntegration) {
  console.log('[INFO] C# API URL configured:', CONFIG.CSHARP_API_URL);
  // Web API behind cookie auth: headless clients authenticate with X-Api-Key.
  if (CONFIG.CSHARP_API_KEY) {
    axios.defaults.headers.common['X-Api-Key'] = CONFIG.CSHARP_API_KEY;
    console.log('[INFO] C# API key auth enabled');
  }
  console.log('[INFO] Full STT->LLM->TTS pipeline enabled');
} else {
  console.log('[INFO] Running in standalone mode - C# integration disabled');
  console.log('[INFO] This bot will join voice but not process audio yet');
}

console.log('[VAD] Config:', JSON.stringify(VAD, null, 2));
console.log(`[VAD] Batching: enabled=${VAD_BATCHING_ENABLED}, frame_batch_size=${VAD_FRAME_BATCH_SIZE}`);

// Create Discord client
const client = new Client({
  intents: [
    GatewayIntentBits.Guilds,
    GatewayIntentBits.GuildVoiceStates,
    GatewayIntentBits.GuildMessages,
    GatewayIntentBits.MessageContent,
  ],
});

// Only publish state changes, never repeated warnings for every failing turn.
// A provider outage is distinct from the complete voice path being unavailable.
const voiceStatusMessages = {
  stt: {
    down: 'Voice not available right now. Speech recognition failed.',
    up: 'Speech recognition is available again.',
  },
  assemblyai: {
    down: 'AssemblyAI is unavailable right now. Trying the configured STT fallback.',
    up: 'AssemblyAI is available again.',
  },
  llm: {
    down: 'Speech was understood, but the AI reply service failed. Check inference provider access; Molab TTS may still be online.',
    up: 'AI replies are available again.',
  },
  voice: {
    down: 'Voice not available right now. Voice generation or playback failed.',
    up: 'Voice is available again.',
  },
  chatVoice: {
    down: 'Voice not available right now for text-chat replies. Text replies will continue.',
    up: 'Voice messages in text chat are available again.',
  },
};
const voiceAvailability = createVoiceAvailabilityNotifier({
  send: async content => {
    if (!client.isReady()) return;
    // When a separate text channel is omitted, use the voice channel's chat.
    const channelId = CONFIG.TEXT_CHANNEL_ID || currentConnection?.joinConfig?.channelId || CONFIG.VOICE_CHANNEL_ID;
    if (!isValidSnowflake(channelId)) return;
    const channel = await client.channels.fetch(channelId);
    if (!channel || channel.guildId !== CONFIG.GUILD_ID || typeof channel.send !== 'function') {
      throw new Error('Voice status channel unavailable');
    }
    await channel.send({ content, allowedMentions: { parse: [] } });
    console.log('[VOICE STATUS] ' + content);
  },
  log: message => console.warn('[VOICE STATUS] ' + message),
});
function reportVoiceStatus(component, available) {
  const messages = voiceStatusMessages[component];
  if (!messages) return;
  void voiceAvailability.report(component, available, messages);
}

// When every STT route fails, say a short fixed retry prompt through the
// strictly selected TTS runtime. Do not speak on every error or at startup.
let lastUnheardTurnFeedbackAt = 0;
const UNHEARD_TURN_COOLDOWN_MS = 30000;
async function reportUnheardVoiceTurn() {
  reportVoiceStatus('stt', false);
  if (!currentConnection || CONFIG.TTS_PROVIDER === 'off') return;
  const now = Date.now();
  if (now - lastUnheardTurnFeedbackAt < UNHEARD_TURN_COOLDOWN_MS) return;
  lastUnheardTurnFeedbackAt = now;
  try {
    const wav = await requestSelectedVoiceWav(
      'I could not understand that. Please try speaking again.',
    );
    const pcm = await wavToDiscordPcm(wav);
    await enqueuePlayback('stt-retry-prompt', pcm, { priority: 0 });
    reportVoiceStatus('voice', true);
  } catch (error) {
    console.warn('[STT] Voice retry prompt unavailable: ' + error.message);
    reportVoiceStatus('voice', false);
  }
}

// A working Molab/Colab tunnel does not imply the AI reply API is working.
// Give a short, rate-limited spoken error without fabricating an AI answer.
let lastInferenceFailureFeedbackAt = 0;
async function reportInferenceFailureVoiceTurn() {
  reportVoiceStatus('llm', false);
  if (!currentConnection || CONFIG.TTS_PROVIDER === 'off') return;
  const now = Date.now();
  if (now - lastInferenceFailureFeedbackAt < 30000) return;
  lastInferenceFailureFeedbackAt = now;
  try {
    const wav = await requestSelectedVoiceWav(
      'I heard you, but my reply service is unavailable right now.',
    );
    await enqueuePlayback('llm-unavailable-prompt', await wavToDiscordPcm(wav), { priority: 0 });
    reportVoiceStatus('voice', true);
  } catch (error) {
    console.warn('[LLM] Voice outage prompt unavailable:', error.message);
    reportVoiceStatus('voice', false);
  }
}

client.on('voiceStateUpdate', (oldState, newState) => {
  if (!assemblyRealtimeRequested) return;
  const channelId = currentConnection?.joinConfig?.channelId;
  if (!channelId || (oldState.channelId !== channelId && newState.channelId !== channelId)) return;

  // Discord updates the channel member cache as part of the state event. A
  // next-turn callback observes the post-event membership, including joins
  // and leaves, before toggling AssemblyAI usage.
  setTimeout(() => syncAssemblyRealtimePresence('voice membership changed'), 0);
});

// Audio player for TTS playback
const audioPlayer = createAudioPlayer({
  behaviors: {
    // Keep consuming the resource while a connection transitions to Ready;
    // playTTSAudio still waits for Ready before starting the resource.
    noSubscriber: NoSubscriberBehavior.Play,
  },
});
const playbackQueue = [];
let playbackWorkerActive = false;
let playbackSequence = 0;
let audioPlayerConnection = null;

// Track active voice connections
let currentConnection = null;
// AssemblyAI realtime is a presence-gated feature: no human in the bot's
// channel means no realtime session and no provider usage.
let assemblyRealtimeEnabled = false;
const activeAssemblyCaptures = new Map();
// Legacy non-realtime focus mode: track which user is currently being processed.
// AssemblyAI realtime uses independent per-user sessions instead.
let focusedUserId = null;
// Persistent manual focus list (via /focus, cleared via /unfocus).
// When non-empty, ONLY these users are listened to.
const manualFocusList = new Set();
const voiceAnalytics = createVoiceAnalytics();
function focusSnapshot() {
  const channel = getCurrentVoiceChannel();
  const members = channel?.members ? [...channel.members.values()]
    .filter(member => !member.user?.bot).map(member => ({
      id: member.id, name: String(member.displayName || member.user?.username || member.id).slice(0, 80),
      focused: manualFocusList.has(member.id),
    })) : [];
  return { guild_id: CONFIG.GUILD_ID, channel_id: channel?.id || null,
    mode: manualFocusList.size ? 'selected users only' : 'everyone',
    user_ids: [...manualFocusList], members };
}
// Legacy input gate for the single-speaker STT path. Realtime captures are
// admitted per user and reject new audio at the capture layer while playback
// is active.
let turnGateClosed = false;
// Track users with an active audio capture to avoid duplicate subscriptions.
const activeAudioCaptures = new Set();

// Per-user state: cooldown timestamps and active turn data
const userState = new Map();
// Bounded pending transcripts prevent lost speech during slow LLM/TTS replies.
const realtimePendingTranscripts = new Map();
const realtimeTurnArbiter = createRealtimeTurnArbiter();
const voiceEngagement = createEngagementPolicy({
  mode: CONFIG.VOICE_ENGAGEMENT_MODE,
  aliases: CONFIG.ENGAGEMENT_ALIASES,
  followupMs: CONFIG.ENGAGEMENT_FOLLOWUP_MS,
});
const chatEngagement = createEngagementPolicy({
  mode: CONFIG.CHAT_ENGAGEMENT_MODE,
  aliases: CONFIG.ENGAGEMENT_ALIASES,
  followupMs: CONFIG.ENGAGEMENT_FOLLOWUP_MS,
});
const engagementMetrics = {
  since: new Date().toISOString(), voice: {}, chat: {},
  classifier: { attempts: 0, failures: 0, replies: 0, timed_samples: 0,
    duration_ms_total: 0 },
};
const engagementClassifier = createEngagementClassifier({
  model: CONFIG.ENGAGEMENT_CLASSIFIER_MODEL,
  keyPool: new GroqKeyPool(CONFIG.GROQ_API_KEYS),
  transport: async ({ model, key, deadlineMs, messages }) => {
    const response = await axios.post('https://api.groq.com/openai/v1/chat/completions', {
      model, messages, temperature: 0, max_tokens: 120,
      response_format: { type: 'json_object' },
    }, { headers: { Authorization: 'Bearer ' + key }, timeout: deadlineMs,
      signal: AbortSignal.timeout(deadlineMs),
      'axios-retry': { retries: 0 }, maxContentLength: 4096 });
    return response.data;
  },
  onOutcome: ({ result, elapsedMs }) => {
    const item = engagementMetrics.classifier;
    item.attempts++;
    if (result === 'failure') item.failures++;
    if (result === 'reply') item.replies++;
    if (Number.isFinite(elapsedMs) && elapsedMs >= 0) {
      item.timed_samples++;
      item.duration_ms_total += elapsedMs;
    }
  },
});
async function resolveEngagement(policy, gate, input, transport, validSession) {
  if (gate.decision !== 'observe' || gate.reason !== 'background' ||
      policy.mode === 'wake_only' || !engagementClassifier.configured) return gate;
  const followupAllowed = policy.followupEligible(input);
  const proactiveAllowed = policy.mode === 'proactive' &&
    (input.text.includes('?') || /\b(?:anyone|everybody|somebody)\b/iu.test(input.text));
  if (!followupAllowed && !proactiveAllowed) return gate;
  const revision = policy.snapshot().revision;
  return engagementClassifier.classify({
    text: input.text, transportType: transport, userId: input.userId,
    turns: policy.recent(input), followupAllowed, proactiveAllowed,
    revisionCurrent: () => policy.snapshot().revision === revision && validSession(),
  });
}
function measureEngagement(transport, reason) {
  const counts = engagementMetrics[transport];
  counts[reason] = (counts[reason] || 0) + 1;
}
function resetEngagement() { voiceEngagement.reset(); chatEngagement.reset(); }

// Cache of known bot user IDs to avoid repeated lookups and spam logs
const knownBots = new Set();
// Cache of known human user IDs to skip bot check entirely
const knownHumans = new Set();

function getCurrentVoiceChannel() {
  const channelId = currentConnection?.joinConfig?.channelId;
  if (!channelId) return null;
  return client.guilds.cache.get(CONFIG.GUILD_ID)?.channels.cache.get(channelId) || null;
}

function getCurrentHumanMemberCount() {
  return countEligibleHumanVoiceMembers(
    getCurrentVoiceChannel()?.members,
    manualFocusList,
  );
}

function stopAssemblyRealtimeCapture(capture, reason) {
  if (capture.aborted) return;
  capture.aborted = true;
  try {
    capture.audioStream?.destroy();
    capture.decoder?.destroy();
  } catch {
    // The stream may already be closed.
  }
  void capture.finish?.(reason);
}

function stopAssemblyRealtimeCaptures(reason) {
  for (const capture of activeAssemblyCaptures.values()) {
    stopAssemblyRealtimeCapture(capture, reason);
  }
}

function stopNonOwnerAssemblyRealtimeCaptures(userId) {
  for (const [speakerId, capture] of activeAssemblyCaptures.entries()) {
    if (speakerId !== userId) {
      stopAssemblyRealtimeCapture(capture, 'another speaker owns the current reply turn');
    }
  }
}

function stopUnfocusedAssemblyRealtimeCaptures(reason) {
  if (manualFocusList.size === 0) return;

  for (const [userId, capture] of activeAssemblyCaptures.entries()) {
    if (!manualFocusList.has(userId)) {
      stopAssemblyRealtimeCapture(capture, reason);
    }
  }
}

function syncAssemblyRealtimePresence(reason) {
  if (!assemblyRealtimeRequested) return;

  const humanMemberCount = getCurrentHumanMemberCount();
  const enabled = shouldEnableAssemblyRealtime({
    isAssemblyMode: assemblyRealtimeRequested,
    hasVoiceConnection: Boolean(currentConnection?.joinConfig?.channelId),
    humanMemberCount,
  });

  stopUnfocusedAssemblyRealtimeCaptures('user is no longer on the manual focus list');

  if (enabled === assemblyRealtimeEnabled) return;
  assemblyRealtimeEnabled = enabled;
  console.log(`[ASSEMBLY] Realtime ${enabled ? 'enabled' : 'disabled'} (${humanMemberCount} human member(s), ${reason})`);

  if (!enabled) {
    stopAssemblyRealtimeCaptures('voice channel has no human listeners');
    focusedUserId = null;
    turnGateClosed = false;
    userState.clear();
    realtimePendingTranscripts.clear();
    realtimeTurnArbiter.reset();
    resetEngagement();
  }
}

/**
 * Get or create per-user state
 */
function getUserState(userId) {
  if (!userState.has(userId)) {
    userState.set(userId, {
      lastResponseAt: 0,       // timestamp of last completed response
      turnSegments: [],         // collected PCM buffers for this turn
      turnStartedAt: 0,        // when the current turn started
      turnTotalBytes: 0,       // total bytes across all segments in this turn
      endOfTurnTimer: null,    // timer to finalize the turn after silence
      processing: false,       // true while this user's turn is being processed
    });
  }
  return userState.get(userId);
}

/**
 * Calculate RMS (root mean square) volume of a 16-bit PCM buffer (stereo).
 * Returns a value 0–32767.
 */
function calcRMS(pcmBuf) {
  const sampleCount = pcmBuf.length / 2; // 16-bit = 2 bytes per sample
  if (sampleCount === 0) return 0;
  let sumSq = 0;
  for (let i = 0; i < pcmBuf.length - 1; i += 2) {
    const sample = pcmBuf.readInt16LE(i);
    sumSq += sample * sample;
  }
  return Math.sqrt(sumSq / sampleCount);
}

/**
 * Finalize a user's turn: concatenate all collected segments and send for STT → LLM → TTS
 */
async function finalizeTurn(userId) {
  const state = getUserState(userId);

  // Clear any pending end-of-turn timer
  if (state.endOfTurnTimer) {
    clearTimeout(state.endOfTurnTimer);
    state.endOfTurnTimer = null;
  }

  const segments = state.turnSegments;
  state.turnSegments = [];
  state.turnTotalBytes = 0;
  state.turnStartedAt = 0;

  if (segments.length === 0) return;

  const combined = Buffer.concat(segments);
  if (combined.length < VAD.MIN_SEGMENT_BYTES) {
    voiceAnalytics.drop('unheard');
    debugLog(`[TURN] User ${userId}: discarding tiny turn (${combined.length} bytes)`);
    return;
  }

  // Check cooldown
  const now = Date.now();
  if (now - state.lastResponseAt < VAD.USER_COOLDOWN_MS) {
    voiceAnalytics.drop('cooldown');
    debugLog(`[TURN] User ${userId}: cooldown active, skipping`);
    return;
  }

  if (state.processing) {
    voiceAnalytics.drop('busy');
    debugLog(`[TURN] User ${userId}: already processing, skipping`);
    return;
  }

  state.processing = true;
  // Close the input gate for this whole turn: STT -> LLM -> TTS playback.
  // Everyone else (and this user too) is filtered until the reply finishes.
  turnGateClosed = true;
  try {
    debugLog(`[TURN] User ${userId}: finalizing turn (${segments.length} segment(s), ${combined.length} bytes)`);
    await sendAudioForSTT(userId, combined);
    state.lastResponseAt = Date.now();
  } finally {
    state.processing = false;
    turnGateClosed = false;
    // Legacy STT may finish with an observed (non-addressed) transcript.
    // A speaking-event focus is never allowed to survive that outcome.
    if (focusedUserId === userId) focusedUserId = null;
  }
}

/**
 * Add a completed segment to a user's turn and manage end-of-turn timing
 */
function addSegmentToTurn(userId, segmentBuffer) {
  const state = getUserState(userId);
  const now = Date.now();

  // Start a new turn if needed
  if (state.turnSegments.length === 0) {
    state.turnStartedAt = now;
  }

  state.turnSegments.push(segmentBuffer);
  state.turnTotalBytes += segmentBuffer.length;

  // Check max turn duration
  const turnDurationSec = (now - state.turnStartedAt) / 1000;
  if (turnDurationSec >= VAD.MAX_TURN_SEC) {
    debugLog(`[VAD] User ${userId}: max turn duration reached (${turnDurationSec.toFixed(1)}s), forcing finalize`);
    finalizeTurn(userId);
    return;
  }

  // Reset end-of-turn timer: wait for more segments or finalize after END_OF_TURN_MS
  if (state.endOfTurnTimer) {
    clearTimeout(state.endOfTurnTimer);
  }
  state.endOfTurnTimer = setTimeout(() => {
    debugLog(`[VAD] User ${userId}: end-of-turn silence (${VAD.END_OF_TURN_MS}ms), finalizing turn`);
    finalizeTurn(userId);
  }, VAD.END_OF_TURN_MS);
}

function decodeErrorPayload(payload) {
  try {
    if (!payload) return '';
    if (Buffer.isBuffer(payload)) return payload.toString('utf8');
    if (payload instanceof ArrayBuffer) return Buffer.from(payload).toString('utf8');
    if (ArrayBuffer.isView(payload)) return Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength).toString('utf8');
    if (payload?.type === 'Buffer' && Array.isArray(payload?.data)) {
      return Buffer.from(payload.data).toString('utf8');
    }
    if (typeof payload === 'string') return payload;
    return JSON.stringify(payload);
  } catch {
    return String(payload);
  }
}

/**
 * Send audio to STT service (AssemblyAI, Groq Whisper, or C# Whisper)
 */
async function sendAudioForSTT(userId, audioBuffer) {
  const sttStartedAt = Date.now();
  let transcribed = false;
  try {
    console.log(`[STT] Transcribing ${audioBuffer.length} bytes from user ${userId}`);

    let text, language, confidence;

    if (transcribeAudio) {
      // Use the configured cloud STT chain (Azure, AssemblyAI, or Groq).
      console.log(`[STT] Using ${sttModeName}...`);
      const apiKey = activeSttMode === 'azure'
        ? CONFIG.AZURE_SPEECH_KEY
        : activeSttMode === 'assemblyai'
          ? ASSEMBLY_KEY_POOL
          : GROQ_KEY_POOL;
      let result;
      try {
        result = await transcribeAudio(apiKey, audioBuffer, CONFIG.SAMPLE_RATE, CONFIG.STT_LANGUAGE);
      } catch (primaryError) {
        if (!fallbackTranscriber || activeSttMode !== CONFIG.STT_MODE) {
          throw primaryError;
        }
        console.warn('[STT] ' + sttModeName + ' failed; trying ' + fallbackModeName + ':', primaryError.message);
        result = await fallbackTranscriber(fallbackApiKey, audioBuffer, CONFIG.SAMPLE_RATE, CONFIG.STT_LANGUAGE);
      }
      if (!result.text?.trim() && fallbackTranscriber && activeSttMode === CONFIG.STT_MODE) {
        console.warn('[STT] ' + sttModeName + ' returned no text; trying ' + fallbackModeName);
        result = await fallbackTranscriber(fallbackApiKey, audioBuffer, CONFIG.SAMPLE_RATE, CONFIG.STT_LANGUAGE);
      }
      text = result.text;
      language = result.language;
      confidence = result.confidence;
    } else {
      // /api/voice/stt is Groq CLOUD Whisper, not an offline C# model.
      // Fail closed rather than sending private audio to Groq under a
      // misleading "local" / USE_CLOUD_STT=false configuration.
      console.warn('[STT] No transcription backend enabled. Audio was not uploaded.');
      voiceAnalytics.drop('stt_disabled');
      return;
    }

    // Note: Transcription is logged by the configured provider
    console.log(`[STT] Transcription complete (${language}, ${(confidence ?? 0).toFixed(2)})`);

    if (text && text.trim().length > 0) {
      voiceAnalytics.record('stt', { durationMs: Date.now() - sttStartedAt });
      transcribed = true;
      reportVoiceStatus('stt', true);
      const sessionConnection = currentConnection;
      const voiceChannelId = sessionConnection?.joinConfig?.channelId;
      const revision = voiceEngagement.snapshot().revision;
      if (voiceChannelId) {
        const input = {
          guildId: CONFIG.GUILD_ID, channelId: voiceChannelId,
          transport: 'voice', userId, text,
          focusAllowed: !manualFocusList.size || manualFocusList.has(userId),
        };
        const gate = voiceEngagement.decide(input);
        const engagement = gate.decision === 'reply' ? gate :
          await resolveEngagement(voiceEngagement, gate, input, 'voice', () =>
            currentConnection === sessionConnection &&
            (!manualFocusList.size || manualFocusList.has(userId)));
        measureEngagement('voice', engagement.reason);
        if (engagement.decision === 'reply' &&
            currentConnection === sessionConnection &&
            voiceEngagement.snapshot().revision === revision &&
            (!manualFocusList.size || manualFocusList.has(userId))) {
          const contextTurns = selectReplyContext(voiceEngagement, input, engagement);
          const replied = await processWithLLM(userId, text, () =>
            currentConnection === sessionConnection &&
            voiceEngagement.snapshot().revision === revision &&
            (!manualFocusList.size || manualFocusList.has(userId)), contextTurns);
          if (replied && currentConnection === sessionConnection &&
              voiceEngagement.snapshot().revision === revision &&
              (!manualFocusList.size || manualFocusList.has(userId))) {
            voiceEngagement.completed({ guildId: CONFIG.GUILD_ID,
              channelId: voiceChannelId, transport: 'voice', userId });
          }
        }
      }
    } else {
      voiceAnalytics.record('stt', { success: false });
      voiceAnalytics.drop('no_text');
      // No text detected, release focus
      if (focusedUserId === userId) {
        focusedUserId = null;
        console.log(`[FOCUS] Released (no text detected)`);
      }
    }
  } catch (error) {
    if (!transcribed) {
      voiceAnalytics.record('stt', { success: false });
      await reportUnheardVoiceTurn();
    }
    console.error('[STT] Error:', error.message);
    // Release focus on error
    if (focusedUserId === userId) {
      focusedUserId = null;
      console.log(`[FOCUS] Released (STT error)`);
    }
  }
}

/**
 * Send text to C# app for LLM processing and get TTS audio back
 */
async function molabIsReady() {
  try {
    const response = await axios.get(
      'https://' + CONFIG.MOLAB_CUSTOM_DOMAIN + '/health',
      { timeout: 5000 },
    );
    return matchesMolabHealth(response.data);
  } catch {
    return false;
  }
}

async function colabIsReady() {
  try {
    const response = await axios.get(
      'https://' + CONFIG.COLAB_CUSTOM_DOMAIN + '/health',
      { timeout: 5000 },
    );
    return response.status === 200 && response.data?.status === 'ready';
  } catch {
    return false;
  }
}

function invalidVoiceTurn() {
  return Object.assign(new Error('Voice turn invalidated'), { code: 'VOICE_TURN_INVALIDATED' });
}

async function processMolabVoiceTurn(userId, text, validTurn = () => true, contextTurns = []) {
  // C# still owns persona/LLM/memory; do not synthesize there for Molab voice.
  let replyResponse;
  const llmStartedAt = Date.now();
  try {
    replyResponse = await axios.post(
      CONFIG.CSHARP_API_URL + '/api/voice/process',
      { userId: String(userId), text, memoryScope: 'discord', audio: false,
        molabVoice: true, contextTurns },
      { timeout: 180000, headers: CONFIG.CSHARP_API_KEY
        ? { 'X-Api-Key': CONFIG.CSHARP_API_KEY } : {} },
    );
  } catch (error) {
    voiceAnalytics.record('llm', { success: false });
    await reportInferenceFailureVoiceTurn();
    error.llmFailed = true;
    throw error;
  }
  const reply = String(replyResponse.data?.text || '').trim();
  voiceAnalytics.record('llm', {
    success: Boolean(reply), durationMs: Date.now() - llmStartedAt,
  });
  reportVoiceStatus('llm', true);
  if (!reply) {
    console.warn('[MOLAB VOICE] LLM returned no response text');
    return false;
  }
  if (!validTurn()) return false;
  console.log('[MOLAB VOICE] Reply received (chars=' + reply.length + ')');

  const endpoint = 'https://' + CONFIG.MOLAB_CUSTOM_DOMAIN;
  const health = await axios.get(endpoint + '/health', { timeout: 15000 });
  if (!matchesMolabHealth(health.data)) {
    throw new Error('Molab health does not attest to epoch-0 Base/full-ICL voice support');
  }

  const startedAt = Date.now();
  const result = await playMolabHybridPipeline({
    text: reply,
    maxChars: 140,
    synthesize: async (sentence, index) => {
      const language = /[\u3040-\u30ff\u3400-\u9fff]/u.test(sentence)
        ? 'Japanese' : 'English';
      for (let attempt = 1; attempt <= 2; attempt += 1) {
        const requestStarted = Date.now();
        const response = await axios.post(
          endpoint + '/tts',
          { text: sentence, language },
          {
            responseType: 'arraybuffer', timeout: 180000,
            maxContentLength: 16 * 1024 * 1024,
          },
        );
        const wav = Buffer.from(response.data);
        if (wav.length < 44 || wav.length > 16 * 1024 * 1024 ||
            wav.toString('ascii', 0, 4) !== 'RIFF' ||
            wav.toString('ascii', 8, 12) !== 'WAVE') {
          throw new Error('Molab sentence ' + (index + 1) + ' returned invalid WAV');
        }
        const requestMs = Date.now() - requestStarted;
        const pcm = await wavToDiscordPcm(wav);
        const duration = pcm.length / (48000 * 2 * 2);
        const incomplete = speechDurationLooksIncomplete(sentence, duration);
        console.log('[MOLAB COVERAGE] ' + JSON.stringify({
          batch: index + 1, chars: sentence.length, audioSeconds: +duration.toFixed(2),
          attempt, incomplete,
        }));
        if (incomplete) {
          if (attempt < 2) continue;
          throw new Error('Molab sentence ' + (index + 1) + ' audio appears incomplete after retry');
        }
        const measuredGenerationMs = Number(response.headers['x-tsuki-generation-ms']);
        return {
          audio: pcm, audioDurationSeconds: duration,
          generationMs: Number.isFinite(measuredGenerationMs) && measuredGenerationMs > 0
            ? measuredGenerationMs : null,
          requestMs,
        };
      }
      throw new Error('Molab TTS did not return a complete utterance');
    },
    synthesizeBatch: async (sentences, startIndex) => {
      const languages = sentences.map(sentence =>
        /[\u3040-\u30ff\u3400-\u9fff]/u.test(sentence) ? 'Japanese' : 'English');
      const requestStarted = Date.now();
      const response = await axios.post(endpoint + '/tts-batch', {
        texts: sentences, languages,
      }, {
        timeout: 180000, maxContentLength: 72 * 1024 * 1024,
      });
      const data = response.data;
      if (data?.mode !== 'gpu-batch' || data.batch_size !== sentences.length ||
          !Array.isArray(data.items) || data.items.length !== sentences.length) {
        throw new Error('Molab did not return a complete GPU batch');
      }
      const items = [];
      for (let index = 0; index < data.items.length; index += 1) {
        const encoded = data.items[index]?.audio_base64;
        if (typeof encoded !== 'string' || encoded.length > 24 * 1024 * 1024 ||
            !/^[A-Za-z0-9+/]+={0,2}$/.test(encoded)) {
          throw new Error('Molab GPU batch returned invalid audio base64');
        }
        const wav = Buffer.from(encoded, 'base64');
        if (wav.length < 44 || wav.length > 16 * 1024 * 1024 ||
            wav.toString('ascii', 0, 4) !== 'RIFF' ||
            wav.toString('ascii', 8, 12) !== 'WAVE') {
          throw new Error('Molab GPU batch returned an invalid WAV');
        }
        const pcm = await wavToDiscordPcm(wav);
        const duration = pcm.length / (48000 * 2 * 2);
        if (speechDurationLooksIncomplete(sentences[index], duration)) {
          throw new Error('Molab GPU batch audio appears incomplete for sentence ' +
            (startIndex + index + 1));
        }
        items.push({ audio: pcm, audioDurationSeconds: duration });
      }
      const measuredGenerationMs = Number(data.generation_ms);
      return {
        items, generationMs: Number.isFinite(measuredGenerationMs) &&
          measuredGenerationMs > 0 ? measuredGenerationMs : null,
        requestMs: Date.now() - requestStarted,
      };
    },
    play: (pcm, index) => {
      if (!validTurn()) throw invalidVoiceTurn();
      if (!currentConnection) throw new Error('Discord voice channel disconnected');
      return enqueuePlayback('molab-sentence-' + (index + 1), pcm,
        { priority: 1, validTurn });
    },
    onProgress: event => {
      if (event.stage === 'synthesized' || event.stage === 'batch_synthesized') {
        voiceAnalytics.record('tts', {
          durationMs: event.generationMs, requestMs: event.requestMs,
          audioSeconds: event.audioDurationSeconds,
        });
        console.log('[MOLAB METRICS] ' + JSON.stringify(event));
      } else if (event.stage === 'batch_fallback') {
        console.warn('[MOLAB GPU BATCH] Falling back to single utterances: ' +
          event.reason);
      } else if (event.stage === 'gap' && event.gapSeconds >= 0.05) {
        console.log('[MOLAB GAP] ' + JSON.stringify(event));
      } else if (event.stage === 'scheduler') {
        debugLog('[MOLAB LADDER]', JSON.stringify(event));
      } else if (event.stage !== 'scheduler') {
        console.log('[MOLAB VOICE] ' + event.stage + ' ' +
          event.index + '/' + event.total);
      }
    },
  });
  console.log('[MOLAB LADDER] ' + JSON.stringify({
    sentences: result.sentences, batches: result.batches, played: result.played,
    maxActiveJobs: result.maxActiveJobs,
    groupedInferenceCalls: result.groupedInferenceCalls,
    singleInferenceCalls: result.singleInferenceCalls,
    rollingRtf: result.rtf,
    playbackGapSeconds: result.gapSeconds,
    totalMs: Date.now() - startedAt,
  }));
  if (result.played > 0) voiceAnalytics.replyCompleted();
  return result.played > 0;
}

async function processWithLLM(userId, text, validTurn = () => true, contextTurns = []) {
  try {
    if (!validTurn()) return false;
    text = String(text || '').trim();
    if (!text) return false;
    if (text.length > 4000) text = text.slice(0, 4000);
    console.log(`[LLM] Processing request with TTS provider=${CONFIG.TTS_PROVIDER}...`);

    if (CONFIG.TTS_PROVIDER === 'off') {
      console.log('[TTS] Voice route is intentionally disabled in controls.');
      if (focusedUserId === userId) focusedUserId = null;
      return false;
    }

    if (CONFIG.TTS_PROVIDER === 'molab') {
      if (!await molabIsReady()) {
        console.warn('[TTS] Selected Molab endpoint is unavailable; no fallback is allowed.');
        reportVoiceStatus('voice', false);
        if (focusedUserId === userId) focusedUserId = null;
        return false;
      }
      if (!validTurn()) return false;
      const played = await processMolabVoiceTurn(userId, text, validTurn, contextTurns);
      reportVoiceStatus('voice', played);
      if (focusedUserId === userId) focusedUserId = null;
      return played;
    }

    if (CONFIG.TTS_PROVIDER !== 'colab') {
      throw new Error('Invalid TSUKI_TTS_PROVIDER: ' + CONFIG.TTS_PROVIDER);
    }
    if (!await colabIsReady()) {
      console.warn('[TTS] Selected Colab endpoint is unavailable; no fallback is allowed.');
      reportVoiceStatus('voice', false);
      if (focusedUserId === userId) focusedUserId = null;
      return false;
    }

    const response = await axios.post(`${CONFIG.CSHARP_API_URL}/api/voice/process-binary`, {
      userId: userId.toString(),
      text: text,
      contextTurns,
      memoryScope: 'discord'
    }, {
      timeout: 180000, // 3 minutes timeout for LLM processing
      responseType: 'arraybuffer',
      validateStatus: (status) => status >= 200 && status < 300
    });

    if (!validTurn()) return false;
    const llmResponse = response.headers['x-tsuki-text'] || '';
    if (llmResponse) {
      console.log('[LLM] Reply received (chars=' + llmResponse.length + ')');
    }

    const audioBytes = Buffer.isBuffer(response.data)
      ? response.data.length
      : (response.data?.byteLength || 0);

    if (response.status === 204 || !response.data || audioBytes === 0) {
      console.log('[LLM] No audio returned');
      reportVoiceStatus('voice', false);
    } else {
      console.log(`[LLM] Response received (${audioBytes} bytes), queueing audio...`);
      const audioBuffer = Buffer.isBuffer(response.data) ? response.data : Buffer.from(response.data);
      await enqueuePlayback('tsuki-reply', audioBuffer, { priority: 1, validTurn });
      voiceAnalytics.replyCompleted();
      reportVoiceStatus('voice', true);
    }
    
    // Release focus after processing complete
    if (focusedUserId === userId) {
      focusedUserId = null;
      console.log(`[FOCUS] Released (processing complete)`);
    }
    return response.status !== 204 && audioBytes > 0;
  } catch (error) {
    if (error.code === 'VOICE_TURN_INVALIDATED') return false;
    console.error('[LLM] Error:', error.message);
    if (!error.llmFailed) reportVoiceStatus('voice', false);
    // Log detailed error response if available
    if (error.response) {
      console.error('[LLM DEBUG] Status:', error.response.status);
      const decoded = decodeErrorPayload(error.response.data);
      console.error('[LLM DEBUG] Response data:', decoded);
    }
    // Release focus on error
    if (focusedUserId === userId) {
      focusedUserId = null;
      console.log(`[FOCUS] Released (LLM error)`);
    }
    return false;
  }
}

function enqueuePlayback(source, audioBuffer, options = {}) {
  return new Promise((resolve, reject) => {
    playbackQueue.push({
      id: ++playbackSequence,
      source,
      audioBuffer,
      priority: options.priority ?? 1,
      validTurn: options.validTurn,
      resolve,
      reject,
    });
    playbackQueue.sort((a, b) => a.priority - b.priority || a.id - b.id);
    console.log(`[TTS Queue] queued source=${source} pending=${playbackQueue.length}`);
    void drainPlaybackQueue();
  });
}

async function drainPlaybackQueue() {
  if (playbackWorkerActive) {
    return;
  }

  playbackWorkerActive = true;
  try {
    while (playbackQueue.length > 0) {
      const next = playbackQueue.shift();
      if (!next) {
        break;
      }

      try {
        if (next.validTurn && !next.validTurn()) throw invalidVoiceTurn();
        console.log(`[TTS Queue] playing source=${next.source} remaining=${playbackQueue.length}`);
        await playTTSAudio(next.audioBuffer);
        next.resolve();
      } catch (error) {
        next.reject(error);
      }
    }
  } finally {
    playbackWorkerActive = false;
  }
}

async function waitForVoiceConnectionReady(connection) {
  if (connection.state.status === VoiceConnectionStatus.Destroyed) {
    throw new Error('Voice connection is destroyed');
  }
  if (connection.state.status !== VoiceConnectionStatus.Ready) {
    await entersState(connection, VoiceConnectionStatus.Ready, 15000);
  }
}

function subscribeAudioPlayer(connection) {
  if (audioPlayerConnection === connection) return;

  const subscription = connection.subscribe(audioPlayer);
  if (!subscription) {
    throw new Error(`Unable to subscribe audio player while connection is ${connection.state.status}`);
  }
  audioPlayerConnection = connection;
  console.log(`[TTS] Audio player subscribed (connection=${connection.state.status})`);
}

function formatVoicePing(ping) {
  if (ping === null || ping === undefined) return 'n/a';
  if (typeof ping === 'number') return `${ping}ms`;
  try {
    return JSON.stringify(ping);
  } catch {
    return String(ping);
  }
}

/**
 * Play TTS audio in Discord voice channel
 */
async function playTTSAudio(audioBuffer) {
  const connection = currentConnection;
  if (!connection) {
    throw new Error('No active voice connection');
  }

  try {
    const stats = inspectDiscordPcm(audioBuffer, {
      sampleRate: CONFIG.SAMPLE_RATE,
      channels: CONFIG.CHANNELS,
    });
    await waitForVoiceConnectionReady(connection);
    subscribeAudioPlayer(connection);

    console.log(
      `[TTS] Playing audio in voice channel (bytes=${stats.bytes}, duration=${stats.durationSecs.toFixed(2)}s, ` +
      `rms=${stats.rms.toFixed(2)}, peak=${stats.peak}, connection=${connection.state.status})...`,
    );

    const audioStream = createDiscordPcmStream(audioBuffer, { tailPaddingMs: 200 });

    // Create audio resource from stream
    // The buffer should be PCM 48kHz stereo
    const resource = createAudioResource(audioStream, {
      inputType: StreamType.Raw,
      inlineVolume: true
    });

    // Set volume to 50%
    if (resource.volume) {
      resource.volume.setVolume(0.5);
    }

    // Install listeners before play() so a fast/short resource cannot finish
    // before the completion promise is observing the player.
    await new Promise((resolve, reject) => {
      let settled = false;
      const cleanup = () => {
        clearTimeout(timeout);
        audioPlayer.removeListener(AudioPlayerStatus.Idle, onIdle);
        audioPlayer.removeListener('error', onError);
      };
      const onIdle = () => {
        if (settled) return;
        settled = true;
        cleanup();
        resolve();
      };
      const onError = (error) => {
        if (settled) return;
        settled = true;
        cleanup();
        reject(error);
      };
      const timeout = setTimeout(() => {
        onError(new Error('Discord audio playback timed out'));
      }, Math.max(15000, stats.durationSecs * 4000));

      audioPlayer.on(AudioPlayerStatus.Idle, onIdle);
      audioPlayer.on('error', onError);
      try {
        // The connection must already be subscribed before the player starts;
        // otherwise the default voice-player state can consume/pause audio
        // before Discord has a playable subscriber.
        audioPlayer.play(resource);
      } catch (error) {
        onError(error);
      }
    });

    if (currentConnection !== connection ||
        connection.state.status !== VoiceConnectionStatus.Ready) {
      throw new Error('Playback interrupted by voice disconnect');
    }
    console.log(
      `[TTS] Playback complete (player=${audioPlayer.state.status}, connection=${connection.state.status}, ` +
      `ping=${formatVoicePing(connection.ping)})`,
    );
  } catch (error) {
    console.error('[TTS] Playback error:', error.message);
    throw error;
  }
}

/**
 * Container-loopback HTTP: C# direct speech and authenticated controls'
 * live voice status / leave / manual TTS (forwarded via Docker exec).
 * POST /play-tts body: { text: "...", guild_id?: "..." }
 */
function startBridgeHttpServer() {
  const server = http.createServer(async (req, res) => {
    res.setHeader('Content-Type', 'application/json');
    if (req.method === 'GET' && req.url === '/analytics') {
      res.writeHead(200);
      res.end(JSON.stringify({ ...voiceAnalytics.snapshot(), engagement: engagementMetrics }));
      return;
    }
    if (req.method === 'GET' && req.url === '/focus') {
      res.writeHead(200);
      res.end(JSON.stringify(focusSnapshot()));
      return;
    }
    if (req.method === 'GET' && req.url === '/voice-session') {
      res.writeHead(200);
      res.end(JSON.stringify(voiceSession(currentConnection, CONFIG.GUILD_ID, VoiceConnectionStatus.Ready)));
      return;
    }
    if (req.method !== 'POST' || !['/play-tts', '/leave-voice', '/focus'].includes(req.url)) {
      res.writeHead(404);
      res.end(JSON.stringify({ error: 'Not found' }));
      return;
    }
    let body = '';
    let bodyBytes = 0;
    let requestTooLarge = false;
    req.on('data', (chunk) => {
      bodyBytes += chunk.length;
      if (bodyBytes > 64 * 1024) {
        requestTooLarge = true;
        return;
      }
      body += chunk;
    });
    req.on('end', async () => {
      try {
        if (requestTooLarge) {
          res.writeHead(413);
          res.end(JSON.stringify({ error: 'Request body is too large' }));
          return;
        }
        const data = JSON.parse(body || '{}');
        const session = voiceSession(currentConnection, CONFIG.GUILD_ID, VoiceConnectionStatus.Ready);
        if (req.url === '/focus') {
          const action = data.action;
          const userId = data.user_id;
          if (data.guild_id !== CONFIG.GUILD_ID ||
              !['add', 'remove', 'clear'].includes(action) ||
              (action === 'clear' && (userId !== undefined ||
                Object.keys(data).length !== 2)) ||
              (action !== 'clear' && (!isValidSnowflake(userId) ||
                Object.keys(data).length !== 3))) {
            res.writeHead(400);
            res.end(JSON.stringify({ error: 'Invalid focus command or server' }));
            return;
          }
          if (action === 'add') {
            const guild = client.guilds.cache.get(CONFIG.GUILD_ID);
            const member = await guild?.members.fetch(userId).catch(() => null);
            if (!member || member.user?.bot) {
              res.writeHead(404);
              res.end(JSON.stringify({ error: 'Human user not found in configured server' }));
              return;
            }
            manualFocusList.add(userId);
          } else if (action === 'remove') manualFocusList.delete(userId);
          else manualFocusList.clear();
          resetEngagement();
          syncAssemblyRealtimePresence('dashboard focus changed');
          res.writeHead(200);
          res.end(JSON.stringify(focusSnapshot()));
          return;
        }
        if (req.url === '/leave-voice') {
          const invalid = validateVoiceTarget(data, session);
          if (invalid) {
            res.writeHead(invalid.status);
            res.end(JSON.stringify({ error: invalid.error }));
            return;
          }
          leaveVoice(CONFIG.GUILD_ID);
          focusedUserId = null;
          turnGateClosed = false;
          userState.clear();
          audioPlayer.stop(true);
          res.writeHead(200);
          res.end(JSON.stringify({ ok: true, left_channel_id: session.channel_id }));
          return;
        }
        if (data.guild_id !== undefined) {
          const invalid = validateVoiceTarget(data, session);
          if (invalid) {
            res.writeHead(invalid.status);
            res.end(JSON.stringify({ error: invalid.error }));
            return;
          }
        }
        const speechConnection = currentConnection;
        const text = typeof data.text === 'string' ? data.text.trim() : '';
        if (!text) {
          res.writeHead(400);
          res.end(JSON.stringify({ error: 'Missing or empty "text" in body' }));
          return;
        }
        if (text.length > 4000) {
          res.writeHead(413);
          res.end(JSON.stringify({ error: 'Text is limited to 4000 characters' }));
          return;
        }
        if (!currentConnection) {
          reportVoiceStatus('voice', false);
          res.writeHead(503);
          res.end(JSON.stringify({ error: 'Not in a voice channel. Start the bridge and join first.' }));
          return;
        }
        if (CONFIG.TTS_PROVIDER === 'off') {
          res.writeHead(503);
          res.end(JSON.stringify({ error: 'Voice disabled in controls' }));
          return;
        }
        // Synthesize the whole request before playback. Queuing each segment
        // separately lets concurrent manual/LLM requests interleave sentences;
        // a later synthesis failure would also leave an incomplete reply spoken.
        const speech = await synthesizeSelectedSpeech(text);
        if (currentConnection !== speechConnection ||
            currentConnection?.state?.status !== VoiceConnectionStatus.Ready) {
          res.writeHead(409);
          res.end(JSON.stringify({ error: 'Voice connection changed before playback.' }));
          return;
        }
        await enqueuePlayback('manual-tts', speech.pcm, { priority: 0 });
        reportVoiceStatus('voice', true);
        res.writeHead(200);
        res.end(JSON.stringify({ ok: true, played: true,
          segments_played: speech.segments, queued: playbackQueue.length }));
      } catch (err) {
        console.error('[BRIDGE HTTP] Error:', err.message);
        const status = err instanceof SyntaxError ? 400 : err instanceof RangeError ? 413 : 502;
        if (status === 502) reportVoiceStatus('voice', false);
        res.writeHead(status);
        res.end(JSON.stringify({ error: status === 400 ? 'Invalid JSON' :
          status === 413 ? err.message : 'Play TTS failed' }));
      }
    });
  });
  server.on('error', (err) => {
    if (err.code === 'EADDRINUSE') {
      console.warn(`[BRIDGE] Port ${BRIDGE_HTTP_PORT} already in use — bridge HTTP server skipped. Previous process may still be running.`);
    } else {
      console.error('[BRIDGE] HTTP server error:', err.message);
    }
  });
  server.listen(BRIDGE_HTTP_PORT, '127.0.0.1', () => {
    console.log(`[BRIDGE] HTTP server listening on http://127.0.0.1:${BRIDGE_HTTP_PORT}/play-tts (C# can send TTS here to play in Discord)`);
  });
}

async function processRealtimeTranscript(userId, rawText, provider = 'assemblyai') {
  const text = String(rawText || '').trim();
  if (manualFocusList.size && !manualFocusList.has(userId)) {
    voiceAnalytics.drop('focus');
    return;
  }
  const voiceChannelId = currentConnection?.joinConfig?.channelId;
  if (!voiceChannelId) return;
  const sessionConnection = currentConnection;
  const revision = voiceEngagement.snapshot().revision;
  const input = {
    guildId: CONFIG.GUILD_ID, channelId: voiceChannelId, transport: 'voice',
    userId, text, focusAllowed: !manualFocusList.size || manualFocusList.has(userId),
  };
  const gate = voiceEngagement.decide(input);
  const engagement = gate.decision === 'reply' ? gate :
    await resolveEngagement(voiceEngagement, gate, input, 'voice', () =>
      currentConnection === sessionConnection &&
      (!manualFocusList.size || manualFocusList.has(userId)));
  measureEngagement('voice', engagement.reason);
  if (engagement.decision !== 'reply' ||
      currentConnection !== sessionConnection ||
      voiceEngagement.snapshot().revision !== revision ||
      (manualFocusList.size && !manualFocusList.has(userId))) return;
  const contextTurns = selectReplyContext(voiceEngagement, input, engagement);
  // Only an addressed and eligible final transcript can atomically claim
  // the shared reply lock; unrelated speakers never stop other captures.
  const claim = realtimeTurnArbiter.claim(userId, text);
  if (!claim.accepted) {
    voiceAnalytics.drop(claim.reason);
    return;
  }
  if (claim.owner) stopNonOwnerAssemblyRealtimeCaptures(userId);
  const state = getUserState(userId);
  if (state.processing) {
    const queue = realtimePendingTranscripts.get(userId) || [];
    if (queue.length >= 2) {
      voiceAnalytics.drop('busy');
      console.warn('[STT] Realtime turn queue full; at most two owner turns can wait');
      return;
    }
    queue.push({ text, provider, contextTurns });
    realtimePendingTranscripts.set(userId, queue);
    return;
  }

  state.processing = true;
  turnGateClosed = true;
  let next = { text, provider, contextTurns };
  try {
    while (next && realtimeTurnArbiter.owns(userId, claim.token)) {
      if (!currentConnection) {
        voiceAnalytics.drop('other');
        break;
      }
      if (manualFocusList.size && !manualFocusList.has(userId)) {
        voiceAnalytics.drop('focus');
      } else {
        console.log('[STT] Final addressed realtime transcript accepted');
        if (next.provider === 'assemblyai') reportVoiceStatus('assemblyai', true);
        voiceAnalytics.record('stt'); // Realtime STT elapsed time not available.
        reportVoiceStatus('stt', true);
        const replied = await processWithLLM(userId, next.text, () =>
          currentConnection === sessionConnection &&
          voiceEngagement.snapshot().revision === revision &&
          realtimeTurnArbiter.owns(userId, claim.token) &&
          (!manualFocusList.size || manualFocusList.has(userId)), next.contextTurns);
        if (replied && currentConnection === sessionConnection &&
            voiceEngagement.snapshot().revision === revision &&
            (!manualFocusList.size || manualFocusList.has(userId)) &&
            realtimeTurnArbiter.owns(userId, claim.token)) {
          voiceEngagement.completed({ guildId: CONFIG.GUILD_ID,
            channelId: voiceChannelId, transport: 'voice', userId });
        }
        state.lastResponseAt = Date.now();
      }
      const queue = realtimePendingTranscripts.get(userId) || [];
      next = queue.shift() || null;
      if (!queue.length) realtimePendingTranscripts.delete(userId);
    }
  } finally {
    realtimePendingTranscripts.delete(userId);
    state.processing = false;
    if (realtimeTurnArbiter.owns(userId, claim.token)) {
      turnGateClosed = false;
      realtimeTurnArbiter.release(userId, claim.token);
    }
  }
}

/**
 * Stream one Discord speech capture to AssemblyAI v3 in realtime.
 * Discord's receiver emits Opus; prism decodes it to 48 kHz stereo PCM and the
 * converter sends 16 kHz mono PCM16 frames to the WebSocket.
 */
function handleAssemblyRealtimeUserAudio(userId, audioStream) {
  if (!assemblyRealtimeEnabled || !realtimeTurnArbiter.canCapture(userId) ||
      audioPlayer.state.status === AudioPlayerStatus.Playing) {
    audioStream.destroy();
    activeAudioCaptures.delete(userId);
    return;
  }

  const convertToAssemblyPcm = createPcm48StereoTo16Mono();
  const rawAudioChunks = [];
  let rawAudioBytes = 0;
  let finalHandled = false;
  let finishPromise = null;
  let capture = null;

  const session = new AssemblyRealtimeSession({
    keyPool: ASSEMBLY_KEY_POOL,
    connectTimeoutMs: boundedInt(process.env.ASSEMBLYAI_CONNECT_TIMEOUT_MS, 12000, 1000, 60000),
    closeTimeoutMs: boundedInt(process.env.ASSEMBLYAI_CLOSE_TIMEOUT_MS, 8000, 1000, 60000),
    onPartial: (event) => {
      debugLog('[STT] AssemblyAI partial received');
    },
    onFinal: (event) => {
      if (finalHandled || capture?.aborted) return;
      finalHandled = true;
      debugLog('[STT] AssemblyAI final received');
      void processRealtimeTranscript(userId, event.transcript);
    },
    onError: (error) => {
      if (capture && !capture.connectionError) capture.connectionError = error;
      reportVoiceStatus('assemblyai', false);
    },
  });

  capture = {
    audioStream,
    decoder: null,
    session,
    connectionError: null,
    audioSendFailed: false,
    aborted: false,
    finish: null,
  };
  activeAssemblyCaptures.set(userId, capture);
  activeAudioCaptures.add(userId);

  const connectPromise = session.connect().catch((error) => {
    capture.connectionError = error;
    console.error('[STT] AssemblyAI realtime connection failed; rotating keys was exhausted:', error.message);
    reportVoiceStatus('assemblyai', false);
    return null;
  });

  const finish = async () => {
    if (finishPromise) return finishPromise;

    finishPromise = (async () => {
      await connectPromise;
      let result = null;
      try {
        result = await session.close();
      } catch (error) {
        capture.connectionError ||= error;
        console.error('[STT] AssemblyAI realtime close failed:', error.message);
      }

      if (!capture.aborted && !finalHandled && result?.text) {
        finalHandled = true;
        await processRealtimeTranscript(userId, result.text);
      } else if (!capture.aborted && capture.connectionError && fallbackTranscriber && rawAudioBytes > 0) {
        try {
          console.warn(`[STT] AssemblyAI realtime failed; trying ${fallbackModeName}`);
          const fallbackResult = await fallbackTranscriber(
            fallbackApiKey,
            Buffer.concat(rawAudioChunks),
            CONFIG.SAMPLE_RATE,
            CONFIG.STT_LANGUAGE,
          );
          if (fallbackResult?.text) {
            finalHandled = true;
            await processRealtimeTranscript(userId, fallbackResult.text, 'fallback');
          }
        } catch (error) {
          console.error('[STT] Fallback transcription failed:', error.message);
        }
      }
      if (!capture.aborted && capture.connectionError && !finalHandled) {
        await reportUnheardVoiceTurn();
      }
    })().finally(() => {
      activeAssemblyCaptures.delete(userId);
      activeAudioCaptures.delete(userId);
    });

    return finishPromise;
  };
  capture.finish = finish;

  const decoder = new prism.opus.Decoder({
    rate: CONFIG.SAMPLE_RATE,
    channels: CONFIG.CHANNELS,
    frameSize: CONFIG.FRAME_SIZE,
  });
  capture.decoder = decoder;
  audioStream.setMaxListeners(20);
  decoder.setMaxListeners(20);

  const maxFallbackBytes = Math.floor(VAD.MAX_TURN_SEC * CONFIG.SAMPLE_RATE * CONFIG.CHANNELS * 2);
  audioStream.on('error', (error) => {
    capture.connectionError ||= error;
    void finish();
  });
  decoder.on('error', (error) => {
    capture.connectionError ||= error;
    void finish();
  });
  audioStream.on('close', () => {
    void finish();
  });

  audioStream
    .pipe(decoder)
    .on('data', (chunk) => {
      if (rawAudioBytes < maxFallbackBytes) {
        const remaining = maxFallbackBytes - rawAudioBytes;
        const copy = Buffer.from(chunk.subarray(0, remaining));
        rawAudioChunks.push(copy);
        rawAudioBytes += copy.length;
      }

      if (capture.aborted || !assemblyRealtimeEnabled || capture.audioSendFailed) return;
      try {
        const pcm = convertToAssemblyPcm(chunk);
        if (pcm.length > 0) session.sendAudio(pcm);
      } catch (error) {
        capture.audioSendFailed = true;
        capture.connectionError ||= error;
        console.error('[STT] AssemblyAI audio send failed:', error.message);
        reportVoiceStatus('assemblyai', false);
      }
    })
    .on('end', () => {
      void finish();
    });
}

/**
 * Handle user speaking in voice channel.
 * AssemblyAI uses its own realtime endpointing; other providers retain the
 * existing local VAD/batch path.
 */
function handleUserAudio(userId, audioStream) {
  if (assemblyRealtimeRequested) {
    handleAssemblyRealtimeUserAudio(userId, audioStream);
    return;
  }

  // Skip if bot is currently playing back (don't listen to ourselves)
  if (audioPlayer.state.status === AudioPlayerStatus.Playing) {
    audioStream.destroy(); // Clean up immediately
    return;
  }

  debugLog(`[AUDIO] User ${userId} started speaking`);

  const decoder = new prism.opus.Decoder({
    rate: CONFIG.SAMPLE_RATE,
    channels: CONFIG.CHANNELS,
    frameSize: CONFIG.FRAME_SIZE,
  });

  // Increase max listeners to avoid warning (Discord can create many streams)
  audioStream.setMaxListeners(20);
  decoder.setMaxListeners(20);

  // Per-stream segment state
  const segmentChunks = [];      // PCM chunks for current segment
  let segmentBytes = 0;          // total bytes in current segment
  let silentFrameCount = 0;      // consecutive silent frames
  let speechDetected = false;    // have we seen any speech in this segment?
  let hadSpeechInStream = false; // whether this stream ever crossed speech threshold
  let batchingFailed = false;
  const frameBatch = [];

  // Bytes per second for 48 kHz stereo 16-bit = 48000 * 2 * 2 = 192000
  const BYTES_PER_SEC = CONFIG.SAMPLE_RATE * CONFIG.CHANNELS * 2;
  const MAX_SEGMENT_BYTES = VAD.MAX_SEGMENT_SEC * BYTES_PER_SEC;

  function processChunk(chunk, effectiveFrameCount = 1) {
    const rms = calcRMS(chunk);
    const isSpeech = rms >= VAD.RMS_SPEECH_THRESHOLD;

    if (isSpeech) {
      speechDetected = true;
      hadSpeechInStream = true;
      silentFrameCount = 0;
      segmentChunks.push(chunk);
      segmentBytes += chunk.length;
    } else {
      silentFrameCount += effectiveFrameCount;

      // Still push audio during short silence (keeps natural pauses)
      if (speechDetected) {
        segmentChunks.push(chunk);
        segmentBytes += chunk.length;
      }

      // Silence cutoff: finalize segment
      if (speechDetected && silentFrameCount >= VAD.SILENCE_FRAMES_CUTOFF) {
        finalizeSegment();
      }
    }

    // Hard max segment length
    if (segmentBytes >= MAX_SEGMENT_BYTES) {
      debugLog(`[VAD] User ${userId}: max segment length reached (${VAD.MAX_SEGMENT_SEC}s)`);
      finalizeSegment();
    }
  }

  function processWithBatching(chunk) {
    if (!VAD_BATCHING_ENABLED || batchingFailed) {
      processChunk(chunk, 1);
      return;
    }

    try {
      frameBatch.push(chunk);
      if (frameBatch.length < VAD_FRAME_BATCH_SIZE) {
        return;
      }

      const combined = Buffer.concat(frameBatch);
      const effectiveFrames = frameBatch.length;
      frameBatch.length = 0;
      processChunk(combined, effectiveFrames);
    } catch (error) {
      batchingFailed = true;
      console.error(`[VAD] Batching failed for user ${userId}, falling back to per-frame processing:`, error.message || error);
      processChunk(chunk, 1);
    }
  }

  function flushFrameBatch() {
    if (frameBatch.length === 0) return;
    const combined = Buffer.concat(frameBatch);
    const effectiveFrames = frameBatch.length;
    frameBatch.length = 0;
    processChunk(combined, effectiveFrames);
  }

  function finalizeSegment() {
    if (segmentChunks.length === 0) return;

    const segmentBuffer = Buffer.concat(segmentChunks);
    segmentChunks.length = 0;
    segmentBytes = 0;
    silentFrameCount = 0;
    speechDetected = false;

    if (segmentBuffer.length < VAD.MIN_SEGMENT_BYTES) {
      debugLog(`[VAD] User ${userId}: segment too small (${segmentBuffer.length} bytes), discarding`);
      return;
    }

    const durationMs = (segmentBuffer.length / BYTES_PER_SEC * 1000).toFixed(0);
    debugLog(`[VAD] User ${userId}: segment complete (${segmentBuffer.length} bytes, ~${durationMs}ms)`);

    // Add to turn (turn manager handles end-of-turn timing + sending to STT)
    addSegmentToTurn(userId, segmentBuffer);
  }

  function cleanup() {
    try {
      decoder.removeAllListeners();
      decoder.destroy();
      audioStream.removeAllListeners();
      audioStream.destroy();
    } catch (err) {
      // Ignore cleanup errors
    }
    activeAudioCaptures.delete(userId);
  }

  // Handle stream errors before piping to prevent crashes
  audioStream.on('error', (error) => {
    // Silently handle common errors that don't affect functionality
    const errorMsg = error.message || '';
    if (!errorMsg.includes('decrypt') && !errorMsg.includes('corrupted')) {
      console.error(`[AUDIO] Stream error for user ${userId}:`, errorMsg);
    }
    cleanup();
  });

  decoder.on('error', (error) => {
    // Silently handle corrupted data errors - they're common during connection setup/teardown
    const errorMsg = error.message || '';
    if (!errorMsg.includes('corrupted') && !errorMsg.includes('decrypt')) {
      console.error(`[AUDIO] Decoder error for user ${userId}:`, errorMsg);
    }
    cleanup();
  });

  audioStream
    .pipe(decoder)
    .on('data', (chunk) => {
      processWithBatching(chunk);
    })
    .on('end', () => {
      flushFrameBatch();

      // Stream ended (Discord detected silence via EndBehaviorType.AfterSilence)
      if (segmentChunks.length > 0 && speechDetected) {
        finalizeSegment();
      }

      // If this capture produced nothing actionable, release focus so the next turn can start cleanly.
      const state = getUserState(userId);
      const hasPendingTurn = state.turnSegments.length > 0 || state.processing || !!state.endOfTurnTimer;
      if (focusedUserId === userId && !hasPendingTurn) {
        focusedUserId = null;
        const reason = hadSpeechInStream ? 'no pending turn after stream end' : 'no usable speech captured';
        console.log(`[FOCUS] Released (${reason})`);
      }

      cleanup();
    });
}

/**
 * Join voice channel and start listening
 */
async function joinVoice(guildId, channelId) {
  try {
    console.log(`[VOICE] Joining channel ${channelId} in guild ${guildId}`);

    const guild = client.guilds.cache.get(guildId);
    if (!guild) {
      throw new Error(`Guild ${guildId} is not available to this bot`);
    }

    // Invalid/deleted channels must fail the join itself. Previously this was
    // only a warning and joinVoiceChannel silently remained disconnected.
    const voiceChat = await guild.channels.fetch(channelId).catch(() => null);
    if (!voiceChat || voiceChat.type !== ChannelType.GuildVoice) {
      throw new Error("Selected voice channel is unavailable. Choose a current voice channel in controls.");
    }
    const joinPermissions = voiceChat.permissionsFor(client.user);
    if (!joinPermissions?.has(PermissionsBitField.Flags.Connect)) {
      throw new Error("Tsuki lacks permission to connect to the selected voice channel.");
    }

    try {
      const permissions = voiceChat.permissionsFor(client.user);
      const needed = ['ViewChannel', 'SendMessages', 'ReadMessageHistory', 'AttachFiles'];
      const missing = needed.filter(flag => !permissions?.has(PermissionsBitField.Flags[flag]));
      if (voiceChat?.type !== ChannelType.GuildVoice || typeof voiceChat.send !== 'function') {
        console.warn('[VOICE CHAT] This channel does not expose attached text chat');
      } else if (missing.length) {
        console.warn('[VOICE CHAT] Missing channel permissions: ' + missing.join(', '));
      } else {
        console.log('[VOICE CHAT] Attached text chat available for channel ' + channelId);
      }
    } catch (error) {
      console.warn('[VOICE CHAT] Permission check unavailable:', error.message);
    }

    const connection = joinVoiceChannel({
      channelId,
      guildId,
      adapterCreator: guild.voiceAdapterCreator,
      selfDeaf: false, // Must be false to receive audio
      selfMute: false,
      // Explicitly enable encryption mode to handle Discord's encrypted voice packets
      debug: false,
    });

    if (currentConnection && currentConnection !== connection) {
      realtimePendingTranscripts.clear();
      realtimeTurnArbiter.reset();
      resetEngagement();
    }
    currentConnection = connection;
    subscribeAudioPlayer(connection);

    connection.on(VoiceConnectionStatus.Ready, () => {
      console.log('[VOICE] ✅ Connected and ready!');
    });

    syncAssemblyRealtimePresence('voice connection ready');
    connection.on(VoiceConnectionStatus.Disconnected, () => {
      console.log('[VOICE] Disconnected');
      if (currentConnection === connection) {
        currentConnection = null;
      }
      if (audioPlayerConnection === connection) {
        audioPlayerConnection = null;
      }
      audioPlayer.stop(true);
      assemblyRealtimeEnabled = false;
      if (!currentConnection || currentConnection === connection) {
        stopAssemblyRealtimeCaptures('voice connection disconnected');
        realtimeTurnArbiter.reset();
        resetEngagement();
      }
    });

    connection.on('error', (error) => {
      console.error('[VOICE] Connection error:', error.message);
      // Don't crash on decryption errors - just log and continue
      if (error.message && error.message.includes('decrypt')) {
        console.error('[VOICE] Decryption error - this is usually temporary');
      }
    });

    // Listen to users speaking
    connection.receiver.speaking.on('start', async (userId) => {
      if (assemblyRealtimeRequested && !assemblyRealtimeEnabled) {
        return;
      }

      // Check cache first - if we already know this is a bot, skip immediately
      if (knownBots.has(userId)) {
        return;
      }

      if (!shouldAcceptVoiceStart({
        userId,
        manualFocusList,
        assemblyRealtimeRequested,
        focusedUserId,
        turnGateClosed,
      }) || (assemblyRealtimeRequested && !realtimeTurnArbiter.canCapture(userId))) {
        return;
      }

      // If we already have an active capture for this user, skip duplicate start events.
      if (activeAudioCaptures.has(userId)) {
        return;
      }

      // Check if this is a known human - skip bot check entirely
      if (!knownHumans.has(userId)) {
        // First time seeing this user - check if they're a bot
        try {
          const guild = client.guilds.cache.get(CONFIG.GUILD_ID);
          if (guild) {
            const member = await guild.members.fetch(userId).catch(() => null);
            if (member) {
              if (member.user.bot) {
                knownBots.add(userId); // Cache this bot ID
                console.log(`[VOICE] Ignoring bot user ${userId} (${member.user.tag})`);
                return;
              } else {
                knownHumans.add(userId); // Cache this human ID
              }
            }
          }
        } catch (error) {
          console.error(`[VOICE] Error checking if user ${userId} is a bot:`, error.message);
        }
      }

      // A member lookup may have awaited Discord after another speaker won.
      if (assemblyRealtimeRequested && !realtimeTurnArbiter.canCapture(userId)) return;
      console.log(`[VOICE] User ${userId} started speaking`);

      // Legacy STT is single-speaker. AssemblyAI realtime creates one
      // independent session per allowed user and therefore does not take this
      // global focus lock.
      if (!assemblyRealtimeRequested && !focusedUserId) {
        focusedUserId = userId;
        console.log(`[FOCUS] Locked onto user ${userId}`);
      }

      try {
        activeAudioCaptures.add(userId);
        const audioStream = connection.receiver.subscribe(userId, {
          end: {
            behavior: EndBehaviorType.AfterSilence,
            duration: 1000, // 1 second of silence
          },
        });

        handleUserAudio(userId, audioStream);
      } catch (error) {
        // Catch subscription errors (including decryption issues)
        if (!error.message || !error.message.includes('decrypt')) {
          console.error(`[VOICE] Error subscribing to user ${userId}:`, error.message);
        }
      }
    });

    syncAssemblyRealtimePresence('joined voice channel');
    return connection;
  } catch (error) {
    console.error('[VOICE] Join error:', error.message);
    throw error;
  }
}

/**
 * Leave voice channel
 */
function leaveVoice(guildId) {
  realtimePendingTranscripts.clear();
  realtimeTurnArbiter.reset();
  resetEngagement();
  assemblyRealtimeEnabled = false;
  stopAssemblyRealtimeCaptures('voice channel left');
  const connection = getVoiceConnection(guildId) ||
    (currentConnection?.joinConfig?.guildId === guildId ? currentConnection : null);
  if (connection) {
    currentConnection = null;
    audioPlayer.stop(true);
    for (const pending of playbackQueue.splice(0)) {
      pending.reject(new Error('Voice channel left before playback'));
    }
    if (connection.state.status !== VoiceConnectionStatus.Destroyed) connection.destroy();
    if (audioPlayerConnection === connection) {
      audioPlayerConnection = null;
    }
    console.log('[VOICE] Left voice channel');
  }
}

// Discord client events
client.once('clientReady', async () => {
  console.log(`[BOT] Logged in as ${client.user.tag}`);
  if (hasCSharpIntegration) {
    console.log('[BOT] Mode: voice pipeline enabled (STT -> LLM -> TTS)');
  } else {
    console.log('[BOT] Mode: voice join only (C# integration disabled)');
  }
  if (assemblyRealtimeRequested) {
    console.log('[BOT] AssemblyAI realtime waits for a human participant before opening a session');
  }

  // Auto-join only when a real default channel was configured. The slash
  // command remains available for deployments that choose the channel later.
  if (isValidSnowflake(CONFIG.VOICE_CHANNEL_ID)) {
    try {
      await joinVoice(CONFIG.GUILD_ID, CONFIG.VOICE_CHANNEL_ID);
    } catch (error) {
      console.error('[BOT] Failed to auto-join voice:', error.message);
    }
  } else {
    console.log('[BOT] No default voice channel configured; waiting for /tjoin.');
  }
  startBridgeHttpServer();

  // Register guild slash commands (visible instantly, no global propagation wait)
  try {
    const guild = client.guilds.cache.get(CONFIG.GUILD_ID);
    if (guild) {
      await guild.commands.set([
        {
          name: 'tjoin',
          description: 'Join the configured voice channel, or provide a channel ID',
          options: [{ name: 'channel_id', description: 'Optional voice channel ID', type: 3, required: false }],
        },
        {
          name: 'tleave',
          description: 'Leave the current voice channel',
          options: [{ name: 'channel_id', description: 'Optional channel ID to leave', type: 3, required: false }],
        },
        {
          name: 'tfocus',
          description: 'Only listen to this user',
          options: [{ name: 'user_id', description: 'User ID', type: 3, required: true }],
        },
        {
          name: 'tunfocus',
          description: 'Stop focusing on this user',
          options: [{ name: 'user_id', description: 'User ID', type: 3, required: true }],
        },
        {
          name: 'tfocuslist',
          description: 'Show which users Tsuki is focused on',
        },
        {
          name: 'tsayvc',
          description: 'Speak a short message in the connected voice channel',
          options: [{ name: 'text', description: 'What Tsuki should say in voice chat',
                      type: 3, required: true, max_length: DIRECT_TTS_MAX_CHARS }],
        },
        {
          name: 'tsayc',
          description: 'Send a short voice message in this chat channel',
          options: [{ name: 'text', description: 'What Tsuki should say in this chat',
                      type: 3, required: true, max_length: DIRECT_TTS_MAX_CHARS }],
        },
      ]);
      console.log('[BOT] Slash commands registered (/tjoin, /tleave, /tfocus, /tunfocus, /tfocuslist, /tsayvc, /tsayc)');
    }
  } catch (error) {
    console.error('[BOT] Slash command registration failed:', error.message);
  }
});

function isValidSnowflake(id) {
  return /^\d{17,20}$/.test(id);
}

function validateStartupConfig() {
  const missing = [];
  if (!CONFIG.DISCORD_TOKEN || CONFIG.DISCORD_TOKEN === 'YOUR_BOT_TOKEN') {
    missing.push('DISCORD_TOKEN');
  }
  if (!isValidSnowflake(CONFIG.GUILD_ID)) {
    missing.push('GUILD_ID');
  }
  if (CONFIG.STT_MODE === 'assemblyai' && ASSEMBLY_KEY_POOL.size === 0) {
    missing.push('ASSEMBLYAI_KEYS_FILE or ASSEMBLYAI_API_KEY');
  }
  if (!['molab', 'colab', 'off'].includes(CONFIG.TTS_PROVIDER)) {
    missing.push('TSUKI_TTS_PROVIDER must be molab, colab, or off');
  }
  if (hasCSharpIntegration) {
    try {
      const url = new URL(CONFIG.CSHARP_API_URL);
      if (!['http:', 'https:'].includes(url.protocol)) {
        missing.push('CSHARP_API_URL');
      }
    } catch {
      missing.push('CSHARP_API_URL');
    }
  }
  return missing;
}

async function resolveUserName(guild, userId) {
  try {
    const member = await guild.members.fetch(userId);
    return member.displayName || member.user.username;
  } catch {
    return `unknown (${userId})`;
  }
}

client.on('interactionCreate', async (interaction) => {
  if (!interaction.isChatInputCommand()) return;

  // GUARDRAIL: only members with Manage Channels can control Tsuki.
  if (!interaction.memberPermissions?.has(PermissionsBitField.Flags.ManageChannels)) {
    await interaction.reply({ content: 'You need the **Manage Channels** permission to control Tsuki.', ephemeral: true });
    return;
  }

  const guild = interaction.guild;
  if (!guild) return;

  const isSayVc = interaction.commandName === 'tsayvc';
  const isSayChat = interaction.commandName === 'tsayc';
  if (isSayVc || isSayChat) {
    try {
      // Required slash-command text is typed inline; do not show a modal.
      const text = String(interaction.options.getString('text', true)).replace(/\s+/g, ' ').trim();
      if (!text) {
        await interaction.reply({ content: 'Text is empty after validation.', ephemeral: true });
        return;
      }
      if (isSayVc && !currentConnection) {
        await interaction.reply({ content: 'Tsuki is not in a voice channel. Use `/tjoin` first.', ephemeral: true });
        return;
      }

      await interaction.deferReply({ ephemeral: true });
      const speech = await synthesizeSelectedSpeech(text);
      if (isSayVc) {
        await enqueuePlayback('slash-tsayvc', speech.pcm, { priority: 0 });
        reportVoiceStatus('voice', true);
        await interaction.editReply('Spoken in the connected voice channel.');
      } else {
        await sendVoiceMessage(interaction.channelId, pcmToWav(speech.pcm), 'Voice message from Tsuki');
        reportVoiceStatus('chatVoice', true);
        await interaction.editReply('Voice message sent in this chat.');
      }
    } catch (error) {
      console.error('[SLASH] Direct speech failed:', error.message);
      if (CONFIG.TTS_PROVIDER !== 'off') {
        reportVoiceStatus(isSayVc ? 'voice' : 'chatVoice', false);
      }
      const failure = CONFIG.TTS_PROVIDER === 'off'
        ? 'Voice is disabled in controls. Select Molab or Colab first.'
        : isSayVc
          ? 'Could not play speech in the voice channel. Check the voice connection and selected TTS provider.'
          : 'Could not send a voice message. Check the selected TTS provider.';
      if (interaction.deferred || interaction.replied) {
        await interaction.editReply({ content: failure }).catch(() => {});
      } else {
        await interaction.reply({ content: failure, ephemeral: true }).catch(() => {});
      }
    }
    return;
  }

  const arg = interaction.options.getString('channel_id') || interaction.options.getString('user_id') || '';

  try {
    switch (interaction.commandName) {
      case 'tjoin': {
        const channelId = arg || CONFIG.VOICE_CHANNEL_ID;
        if (!isValidSnowflake(channelId)) {
          await interaction.reply({ content: 'That does not look like a valid channel ID (numbers only).', ephemeral: true });
          return;
        }
        if (currentConnection) {
          leaveVoice(CONFIG.GUILD_ID);
        }
        // Reset in-progress turns and focus when switching channels.
        focusedUserId = null;
        turnGateClosed = false;
        userState.clear();
        try {
          await joinVoice(CONFIG.GUILD_ID, channelId);
          await interaction.reply(`Joined <#${channelId}>. I'm listening.`);
        } catch (joinError) {
          await interaction.reply(`Failed to join <#${channelId}>: ${joinError.message}`);
        }
        return;
      }
      case 'tleave': {
        const channelId = arg || currentConnection?.joinConfig?.channelId || '';
        if (!isValidSnowflake(channelId)) {
          await interaction.reply({ content: 'I am not in a voice channel right now.', ephemeral: true });
          return;
        }
        const voice = currentConnection?.joinConfig?.channelId;
        if (voice !== channelId) {
          await interaction.reply({ content: `I'm not in <#${channelId}> right now.`, ephemeral: true });
          return;
        }
        leaveVoice(CONFIG.GUILD_ID);
        focusedUserId = null;
        turnGateClosed = false;
        userState.clear();
        await interaction.reply(`Left <#${channelId}>. See you later!`);
        return;
      }
      case 'tfocus': {
        if (!isValidSnowflake(arg)) {
          await interaction.reply({ content: 'That does not look like a valid user ID.', ephemeral: true });
          return;
        }
        const member = await guild.members.fetch(arg).catch(() => null);
        if (!member) {
          await interaction.reply({ content: `No user with ID \`${arg}\` in this server.`, ephemeral: true });
          return;
        }
        manualFocusList.add(arg);
        resetEngagement();
        syncAssemblyRealtimePresence('manual focus list changed');
        const names = await Promise.all([...manualFocusList].map((id) => resolveUserName(guild, id)));
        await interaction.reply(`Now only listening to: **${names.join(', ')}**`);
        return;
      }
      case 'tunfocus': {
        if (!manualFocusList.delete(arg)) {
          await interaction.reply({ content: `\`${arg}\` was not on the focus list.`, ephemeral: true });
          return;
        }
        resetEngagement();
        syncAssemblyRealtimePresence('manual focus list changed');
        const names = manualFocusList.size
          ? await Promise.all([...manualFocusList].map((id) => resolveUserName(guild, id)))
          : [];
        await interaction.reply(
          manualFocusList.size
            ? `Removed. Still listening to: **${names.join(', ')}**`
            : 'Removed. Manual focus is off — I listen to everyone again.',
        );
        return;
      }
      case 'tfocuslist': {
        if (manualFocusList.size === 0) {
          await interaction.reply('Manual focus is off — I listen to everyone. Use `/tfocus user_id` to restrict it.');
          return;
        }
        const names = await Promise.all([...manualFocusList].map((id) => resolveUserName(guild, id)));
        await interaction.reply(`Listening only to: **${names.join(', ')}**`);
        return;
      }
    }
  } catch (error) {
    console.error('[SLASH] Command failed:', error.message);
    if (interaction.deferred || interaction.replied) {
      await interaction.editReply({ content: 'Something went wrong running that command.' }).catch(() => {});
    } else {
      await interaction.reply({ content: 'Something went wrong running that command.', ephemeral: true }).catch(() => {});
    }
  }
});

client.on('error', (error) => {
  console.error('[BOT] Client error:', error.message);
});

// Accept the configured text channel AND the attached text chat of Tsuki's
// joined/configured voice channel, never unrelated channels or other guilds.
// TEXT_REPLY_MODE='any' widens this to every non-bot message there.
// Guardrails: one turn at a time globally, per-user cooldown, short error reply.
const TEXT_USER_COOLDOWN_MS = boundedInt(process.env.TEXT_USER_COOLDOWN_MS, 3000, 1000, 60000);
// Automatic voice replies in text chat are keywords-only or off.
// Normalize removed legacy 'all' to keywords so upgrades never speak every reply.
const TEXT_VOICE_MODE = (process.env.TEXT_VOICE_MODE || 'keywords').trim().toLowerCase() === 'off'
  ? 'off' : 'keywords';
const TEXT_VOICE_KEYWORDS = parseTextVoiceKeywords(
  process.env.TEXT_VOICE_KEYWORDS || DEFAULT_TEXT_VOICE_KEYWORDS,
);

function wantsVoiceReply(text) {
  return wantsTextVoiceReply(text, TEXT_VOICE_MODE, TEXT_VOICE_KEYWORDS);
}

const textTurnQueue = createTextTurnQueue({ capacity: 8 });
const lastTextTurnAt = new Map(); // userId -> epoch ms

// Convert the synthesized WAV to ogg/opus for Discord voice messages. The WAV
// goes in raw (ffmpeg probes it and resamples 24k->48k mono with high quality).
function wavToOggOpus(wavBuffer) {
  return new Promise((resolve, reject) => {
    if (!Buffer.isBuffer(wavBuffer) || wavBuffer.length === 0 || wavBuffer.length > MAX_AUDIO_BYTES) {
      reject(new Error('Invalid WAV payload'));
      return;
    }

    // FFMPEG_PATH preferred: the mwader/static-ffmpeg build is used in Docker
    // because ffmpeg-static's opus encoder produced full-static output.
    const bin = process.env.FFMPEG_PATH || ffmpegPath;
    if (!bin) {
      reject(new Error("ffmpeg binary not found"));
      return;
    }

    const proc = spawnProcess(bin, [
      "-loglevel", "error",
      "-i", "pipe:0",
      "-af", DISCORD_VOICE_AUDIO_FILTER,
      "-c:a", "libopus", "-b:a", "64k", "-application", "voip",
      "-ar", "48000", "-ac", "1",
      "-f", "ogg", "pipe:1",
    ]);

    const chunks = [];
    proc.stdout.on("data", (c) => chunks.push(c));
    let stderr = "";
    let settled = false;
    const timer = setTimeout(() => {
      proc.kill('SIGKILL');
      fail(new Error('ffmpeg timed out'));
    }, 30000);
    const fail = (error) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(error);
    };
    proc.stderr.on("data", (d) => { stderr = (stderr + d.toString()).slice(-2000); });
    proc.on("error", fail);
    proc.on("close", (code) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      if (code === 0) {
        resolve(Buffer.concat(chunks));
      } else {
        reject(new Error(`ffmpeg exited ${code}: ${stderr.slice(0, 200)}`));
      }
    });
    proc.stdin.on("error", fail);
    proc.stdin.write(wavBuffer);
    proc.stdin.end();
  });
}

// Molab returns 24-kHz mono WAV; Discord voice playback needs 48-kHz
// stereo signed PCM. Leave chat-channel OGG voice messages untouched.
function wavToDiscordPcm(wavBuffer) {
  return new Promise((resolve, reject) => {
    const bin = process.env.FFMPEG_PATH || ffmpegPath;
    if (!bin) return reject(new Error('ffmpeg binary unavailable'));
    const child = spawnProcess(bin, [
      '-hide_banner', '-loglevel', 'error', '-i', 'pipe:0',
      '-f', 's16le', '-acodec', 'pcm_s16le', '-ar', '48000', '-ac', '2',
      'pipe:1',
    ]);
    const chunks = [];
    let totalBytes = 0;
    let errorText = '';
    let settled = false;
    const finish = (error, pcm) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      if (error) reject(error);
      else resolve(pcm);
    };
    const timer = setTimeout(() => {
      child.kill('SIGKILL');
      finish(new Error('Molab WAV conversion timed out'));
    }, 45000);
    child.stdout.on('data', (chunk) => {
      totalBytes += chunk.length;
      if (totalBytes > 32 * 1024 * 1024) {
        child.kill('SIGKILL');
        finish(new Error('Molab PCM exceeds playback size limit'));
        return;
      }
      chunks.push(chunk);
    });
    child.stderr.on('data', (chunk) => {
      errorText = (errorText + chunk.toString()).slice(-1000);
    });
    child.on('error', finish);
    child.stdin.on('error', finish);
    child.on('close', code => {
      if (settled) return;
      if (code !== 0) return finish(new Error('Molab WAV conversion failed: ' + errorText));
      const pcm = Buffer.concat(chunks);
      if (!pcm.length || pcm.length % 4 !== 0) {
        return finish(new Error('Molab WAV conversion produced invalid PCM'));
      }
      finish(null, pcm);
    });
    child.stdin.end(wavBuffer);
  });
}

const DIRECT_TTS_MAX_CHARS = 280;
const MAX_AUDIO_BYTES = 16 * 1024 * 1024;

function decodeBase64Audio(value) {
  if (typeof value !== 'string' || value.length === 0 || value.length > Math.ceil(MAX_AUDIO_BYTES / 3) * 4) {
    throw new Error('Invalid audio payload');
  }
  if (value.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(value)) {
    throw new Error('Invalid audio payload');
  }
  const audio = Buffer.from(value, 'base64');
  if (audio.length === 0 || audio.length > MAX_AUDIO_BYTES) {
    throw new Error('Invalid audio payload');
  }
  return audio;
}

function pcmToWav(pcmBuffer, sampleRate = 48000, channels = 2) {
  const pcm = Buffer.from(pcmBuffer);
  const bytesPerSample = 2;
  const blockAlign = channels * bytesPerSample;
  const header = Buffer.alloc(44);
  header.write('RIFF', 0);
  header.writeUInt32LE(36 + pcm.length, 4);
  header.write('WAVE', 8);
  header.write('fmt ', 12);
  header.writeUInt32LE(16, 16);
  header.writeUInt16LE(1, 20);
  header.writeUInt16LE(channels, 22);
  header.writeUInt32LE(sampleRate, 24);
  header.writeUInt32LE(sampleRate * blockAlign, 28);
  header.writeUInt16LE(blockAlign, 32);
  header.writeUInt16LE(bytesPerSample * 8, 34);
  header.write('data', 36);
  header.writeUInt32LE(pcm.length, 40);
  return Buffer.concat([header, pcm]);
}

async function requestDirectTts(text) {
  const response = await axios.post(`${CONFIG.CSHARP_API_URL}/api/voice/test-tts`, { text }, {
    timeout: 180000,
    validateStatus: (status) => status >= 200 && status < 300,
  });
  const audioBase64 = response.data?.audio;
  if (!audioBase64) {
    throw new Error('C# API did not return TTS audio');
  }
  return decodeBase64Audio(audioBase64);
}

// All Discord TTS surfaces use the selected runtime; text is still returned
// when voice is off or unavailable. Never silently send Molab turns to Colab.
async function requestSelectedVoiceWav(text) {
  const requestStartedAt = Date.now();
  // A structurally valid WAV can still contain only the first few words.
  // Retry a clearly under-duration utterance once, then fail explicitly rather
  // than claiming complete playback of text the model did not likely speak.
  for (let attempt = 1; attempt <= 2; attempt += 1) {
    const wav = await requestSelectedVoiceWavOnce(text);
    const duration = readWavDurationSeconds(wav);
    console.log('[TTS COVERAGE] ' + JSON.stringify({
      chars: text.length, durationSeconds: duration, attempt,
      incomplete: speechDurationLooksIncomplete(text, duration),
    }));
    if (!speechDurationLooksIncomplete(text, duration)) {
      const elapsed = Date.now() - requestStartedAt;
      voiceAnalytics.record('tts', { durationMs: elapsed, requestMs: elapsed, audioSeconds: duration });
      return wav;
    }
    if (attempt === 2) {
      voiceAnalytics.record('tts', { success: false });
      throw new Error('TTS audio appears incomplete after retry; playback was not claimed as complete');
    }
  }
}

async function requestSelectedVoiceWavOnce(text) {
  if (CONFIG.TTS_PROVIDER === 'off') {
    throw new Error('Voice disabled in controls');
  }
  if (CONFIG.TTS_PROVIDER === 'colab') {
    if (!await colabIsReady()) throw new Error('Selected Colab notebook unavailable');
    return pcmToWav(await requestDirectTts(text));
  }
  if (CONFIG.TTS_PROVIDER === 'molab') {
    if (!await molabIsReady()) throw new Error('Selected Molab notebook unavailable');
    const language = /[\u3040-\u30ff\u3400-\u9fff]/u.test(text) ? 'Japanese' : 'English';
    const response = await axios.post(
      'https://' + CONFIG.MOLAB_CUSTOM_DOMAIN + '/tts',
      { text, language },
      { responseType: 'arraybuffer', timeout: 180000,
        maxContentLength: 16 * 1024 * 1024 },
    );
    const wav = Buffer.from(response.data);
    if (wav.length < 44 || wav.length > 16 * 1024 * 1024 ||
        wav.toString('ascii', 0, 4) !== 'RIFF' ||
        wav.toString('ascii', 8, 12) !== 'WAVE') {
      throw new Error('Selected Molab notebook returned invalid WAV');
    }
    return wav;
  }
  throw new Error('Invalid TTS provider');
}

async function synthesizeSelectedSpeech(text) {
  const segments = planTtsUtterances(text);
  const chunks = [];
  for (const segment of segments) {
    const wav = await requestSelectedVoiceWav(segment);
    chunks.push(await wavToDiscordPcm(wav));
  }
  const pcm = assemblePcmUtterances(chunks, { maxAudioBytes: MAX_AUDIO_BYTES });
  console.log('[TTS COVERAGE] ' + JSON.stringify({
    plannedCharacters: text.length, utterances: segments.length,
    audioSeconds: +(pcm.length / (48000 * 2 * 2)).toFixed(2),
  }));
  return { pcm, segments: segments.length };
}

async function sendVoiceMessage(channelId, wavBuffer, description = 'Voice message from Tsuki') {
  const meta = voiceMetadataFromWav(wavBuffer, 1);
  const ogg = await wavToOggOpus(wavBuffer);
  if (ogg.length === 0) {
    throw new Error('FFmpeg returned an empty voice message');
  }

  await client.rest.post(Routes.channelMessages(channelId), {
    body: {
      flags: MessageFlags.IsVoiceMessage,
      attachments: [{
        id: 0,
        filename: 'voice-message.ogg',
        description,
        duration_secs: meta.durationSecs,
        waveform: meta.waveform,
      }],
    },
    files: [{ name: 'voice-message.ogg', data: ogg, contentType: 'audio/ogg' }],
    auth: true,
  });
}

// Provider readiness is visible in controls.tsukihime.dpdns.org.
// Do not send startup text announcements or synthetic voice samples to Discord.
client.on('messageCreate', async (message) => {
  let ownsTextTurn = false;
  try {
    if (!acceptsDiscordChatChannel({
      channelId: message.channel.id,
      channelType: message.channel.type,
      guildId: message.guildId,
      configuredGuildId: CONFIG.GUILD_ID,
      configuredTextId: CONFIG.TEXT_CHANNEL_ID,
      activeVoiceId: currentConnection?.joinConfig?.channelId,
      configuredVoiceId: CONFIG.VOICE_CHANNEL_ID,
      guildVoiceType: ChannelType.GuildVoice,
    })) return;
    if (message.author.bot ||
        (manualFocusList.size && !manualFocusList.has(message.author.id))) return;

    const mentioned = message.mentions.has(client.user);
    let repliedToBot = false;
    if (message.reference?.messageId) {
      // Missing Read Message History permission must not invent a reply target.
      const referenced = await message.fetchReference().catch(() => null);
      repliedToBot = referenced?.author?.id === client.user.id;
    }
    // Strip mention markup only after gathering Discord's trusted metadata.
    const text = message.content.replace(/<@!?(\d+)>/g, ' ').replace(/\s+/g, ' ').trim();
    if (!text) return;
    // Strict mention mode stays strict, regardless of an audio keyword.
    if (CONFIG.TEXT_REPLY_MODE !== 'any' && !mentioned && !repliedToBot) return;
    const addressedOther = !mentioned && !repliedToBot &&
      message.mentions.users.some(user => user.id !== client.user.id);
    const input = {
      guildId: message.guildId, channelId: message.channel.id, transport: 'chat',
      userId: message.author.id, text, mentioned, repliedToBot, addressedOther,
      focusAllowed: !manualFocusList.size || manualFocusList.has(message.author.id),
    };
    const revision = chatEngagement.snapshot().revision;
    const sessionValid = () =>
      chatEngagement.snapshot().revision === revision &&
      (!manualFocusList.size || manualFocusList.has(message.author.id)) &&
      acceptsDiscordChatChannel({
        channelId: message.channel.id, channelType: message.channel.type,
        guildId: message.guildId, configuredGuildId: CONFIG.GUILD_ID,
        configuredTextId: CONFIG.TEXT_CHANNEL_ID,
        activeVoiceId: currentConnection?.joinConfig?.channelId,
        configuredVoiceId: CONFIG.VOICE_CHANNEL_ID,
        guildVoiceType: ChannelType.GuildVoice,
      });
    const gate = chatEngagement.decide(input);
    const engagement = gate.decision === 'reply' ? gate :
      await resolveEngagement(chatEngagement, gate, input, 'chat', sessionValid);
    measureEngagement('chat', engagement.reason);
    if (engagement.decision !== 'reply' || !sessionValid()) return;
    // Text-voice keywords only choose delivery AFTER engagement authorization.
    const triggeredVoice = wantsVoiceReply(text) && CONFIG.TTS_PROVIDER !== 'off';

    const now = Date.now();
    const lastAt = lastTextTurnAt.get(message.author.id) || 0;
    if (now - lastAt < TEXT_USER_COOLDOWN_MS) {
      voiceAnalytics.drop('cooldown');
      debugLog(`[TEXT] cooldown drop for ${message.author.tag} (${now - lastAt}ms < ${TEXT_USER_COOLDOWN_MS}ms)`);
      return;
    }
    if (!await textTurnQueue.claim()) {
      voiceAnalytics.drop('busy');
      debugLog('[TEXT] bounded turn queue full');
      return;
    }
    ownsTextTurn = true;
    if (!sessionValid()) {
      textTurnQueue.release();
      ownsTextTurn = false;
      return;
    }
    const admittedAt = Date.now();
    if (admittedAt - (lastTextTurnAt.get(message.author.id) || 0) < TEXT_USER_COOLDOWN_MS) {
      voiceAnalytics.drop('cooldown');
      textTurnQueue.release();
      ownsTextTurn = false;
      return;
    }
    // Keep cooldown records bounded independently of the lifetime of Discord.
    if (lastTextTurnAt.size > 4096) {
      for (const [id, timestamp] of lastTextTurnAt) {
        if (admittedAt - timestamp > TEXT_USER_COOLDOWN_MS)
          lastTextTurnAt.delete(id);
      }
    }
    lastTextTurnAt.set(message.author.id, admittedAt);
    console.log('[TEXT] Authorized engagement, reason=' + engagement.reason);

    try {
      if (typeof message.channel.sendTyping === 'function') {
        await message.channel.sendTyping().catch(() => {});
      }
      if (!sessionValid()) return;

      // Per-user memory endpoint: userId keys Tsuki's memory for this person,
      // userName lets her address them by name; voice per TEXT_VOICE_MODE.
      const wantVoice = triggeredVoice;
      const contextTurns = selectReplyContext(chatEngagement, input, engagement);
      const response = await axios.post(`${CONFIG.CSHARP_API_URL}/api/chat/discord`, {
        userId: message.author.id,
        userName: message.member?.displayName || message.author.globalName || message.author.username,
        text,
        contextTurns,
        voice: false, // C# owns chat/memory, selected notebook owns all TTS.
      }, { timeout: 180000 });
      if (!sessionValid()) return;

      const reply = response?.data?.text;
      reportVoiceStatus('llm', true);
      if (reply) {
        // Voice replies replace the text entirely (user preference); text is
        // the fallback when synthesis/conversion fails.
        let voiceSent = false;
        if (wantVoice) {
          try {
            const speech = await synthesizeSelectedSpeech(String(reply));
            const wav = pcmToWav(speech.pcm);
            // Some engines (Kokoro) return WAV variants the API's analyzer
            // can't parse — compute metadata from the audio itself as fallback.
            let durationSecs = 0;
            let waveform;
            if (!durationSecs || !waveform) {
              const meta = voiceMetadataFromWav(wav, 1);
              if (!durationSecs) durationSecs = meta.durationSecs;
              if (!waveform) waveform = meta.waveform;
            }
            const ogg = await wavToOggOpus(wav);
            if (!sessionValid()) return;
            if (ogg.length > 0) {
              // Raw REST: discord.js cannot send waveform/duration metadata.
              // NOTE: @discordjs/rest expects the buffer under `data`
              // (older `attachment`/`file` keys left the upload as the string
              // "undefined", producing a 9-byte unplayable file).
              await client.rest.post(Routes.channelMessages(message.channel.id), {
                body: {
                  flags: MessageFlags.IsVoiceMessage,
                  attachments: [
                    {
                      id: 0,
                      filename: 'voice-message.ogg',
                      description: 'Voice message from Tsuki',
                      duration_secs: durationSecs,
                      waveform,
                    },
                  ],
                },
                files: [{ name: 'voice-message.ogg', data: ogg, contentType: 'audio/ogg' }],
                auth: true,
              });
              voiceSent = true;
              chatEngagement.completed({ guildId: message.guildId,
                channelId: message.channel.id, transport: 'chat', userId: message.author.id });
              reportVoiceStatus('chatVoice', true);
              console.log(`[TEXT] Sent voice message (${ogg.length} bytes, ${durationSecs}s)`);
            }
          } catch (voiceError) {
            console.error('[TEXT] Voice message failed:', voiceError?.response?.status, voiceError?.message);
            reportVoiceStatus('chatVoice', false);
          }
        }

        if (wantVoice && !voiceSent) reportVoiceStatus('chatVoice', false);
        if (!voiceSent) {
          if (!sessionValid()) return;
          // Discord hard-caps messages at 2000 chars.
          await message.reply(String(reply).slice(0, 1900));
          chatEngagement.completed({ guildId: message.guildId,
            channelId: message.channel.id, transport: 'chat', userId: message.author.id });
          console.log(`[TEXT] Replied with text (${reply.length} chars)`);
        }
      } else {
        console.log('[TEXT] Empty response, not replying');
      }
    } catch (turnError) {
      console.error('[TEXT] Turn failed:', turnError?.message);
      // An inference HTTP 502 is not evidence that Molab/Colab TTS is down.
      reportVoiceStatus('llm', false);
      if (sessionValid())
        await message.reply('I received your message, but my AI reply service is unavailable right now.').catch(() => {});
    } finally {
      textTurnQueue.release();
      ownsTextTurn = false;
    }
  } catch (error) {
    if (ownsTextTurn) textTurnQueue.release();
    console.error('[TEXT] Failed to handle message:', error?.message);
  }
});

// Handle process termination. Docker sends SIGTERM during a normal stop.
function shutdown(signal) {
  console.log(`[BOT] Shutting down (${signal})...`);
  if (currentConnection) {
    leaveVoice(CONFIG.GUILD_ID);
  }
  client.destroy();
  process.exit(0);
}

process.once('SIGINT', () => shutdown('SIGINT'));
process.once('SIGTERM', () => shutdown('SIGTERM'));

// Login to Discord (token already trimmed in CONFIG)
const startupErrors = validateStartupConfig();
if (startupErrors.length > 0) {
  console.error(`[BOT] Invalid configuration. Missing or invalid: ${startupErrors.join(', ')}`);
  process.exitCode = 78;
} else {
  console.log('[BOT] Starting Discord voice bridge...');
  client.login(CONFIG.DISCORD_TOKEN).catch((error) => {
    const code = error?.code ? ` (${error.code})` : '';
    console.error(`[BOT] Discord login failed${code}. Check DISCORD_TOKEN and bot status.`);
    process.exitCode = 78;
  });
}
