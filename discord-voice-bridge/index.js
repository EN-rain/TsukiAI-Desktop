import 'dotenv/config';
import http from 'http';
import https from 'https';
import { Client, GatewayIntentBits, MessageFlags, Routes, PermissionsBitField } from 'discord.js';
import {
  joinVoiceChannel,
  createAudioPlayer,
  createAudioResource,
  AudioPlayerStatus,
  VoiceConnectionStatus,
  EndBehaviorType,
  getVoiceConnection
} from '@discordjs/voice';
import axios from 'axios';
import { spawn as spawnProcess } from 'child_process';
import ffmpegPath from 'ffmpeg-static';
import axiosRetry from 'axios-retry';
import prism from 'prism-media';
import { GroqKeyPool, loadGroqApiKeys } from './groq-key-pool.js';
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
import { DISCORD_VOICE_AUDIO_FILTER } from './audio-encoding.js';

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
  CSHARP_API_URL: (process.env.CSHARP_API_URL || 'http://localhost:5000').trim(),
  CSHARP_API_KEY: (process.env.CSHARP_API_KEY || '').trim(),
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
let sttModeName = 'Local (C# Whisper)';

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
  console.log('[INFO] 🏠 Local STT enabled (C# Whisper)');
  sttModeName = 'Local (C# Whisper)';
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
const audioPlayer = createAudioPlayer();
const playbackQueue = [];
let playbackWorkerActive = false;
let playbackSequence = 0;

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
// Legacy input gate for the single-speaker STT path. Realtime captures are
// admitted per user and reject new audio at the capture layer while playback
// is active.
let turnGateClosed = false;
// Track users with an active audio capture to avoid duplicate subscriptions.
const activeAudioCaptures = new Set();

// Per-user state: cooldown timestamps and active turn data
const userState = new Map();

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
    debugLog(`[TURN] User ${userId}: discarding tiny turn (${combined.length} bytes)`);
    return;
  }

  // Check cooldown
  const now = Date.now();
  if (now - state.lastResponseAt < VAD.USER_COOLDOWN_MS) {
    debugLog(`[TURN] User ${userId}: cooldown active, skipping`);
    return;
  }

  if (state.processing) {
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
      // Use C# Whisper for STT
      console.log('[STT] Using C# Whisper...');
      const audioBase64 = audioBuffer.toString('base64');
      const response = await axios.post(`${CONFIG.CSHARP_API_URL}/api/voice/stt`, {
        userId: userId.toString(),
        audioData: audioBase64
      }, { timeout: 30000 });

      text = response.data.text;
      language = response.data.language;
      confidence = response.data.confidence;
    }

    // Note: Transcription is logged by C# API to UI
    console.log(`[STT] Transcription complete (${language}, ${(confidence ?? 0).toFixed(2)})`);

    if (text && text.trim().length > 0) {
      await processWithLLM(userId, text);
    } else {
      // No text detected, release focus
      if (focusedUserId === userId) {
        focusedUserId = null;
        console.log(`[FOCUS] Released (no text detected)`);
      }
    }
  } catch (error) {
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
async function processWithLLM(userId, text) {
  try {
    text = String(text || '').trim();
    if (!text) return;
    if (text.length > 4000) text = text.slice(0, 4000);
    // Note: Text and response are logged by C# API to UI
    console.log(`[LLM] Processing request...`);

    const response = await axios.post(`${CONFIG.CSHARP_API_URL}/api/voice/process-binary`, {
      userId: userId.toString(),
      text: text,
      memoryScope: 'discord'
    }, {
      timeout: 180000, // 3 minutes timeout for LLM processing
      responseType: 'arraybuffer',
      validateStatus: (status) => status >= 200 && status < 300
    });

    const llmResponse = response.headers['x-tsuki-text'] || '';
    if (llmResponse) {
      console.log('[LLM] Response text:', llmResponse);
    }

    const audioBytes = Buffer.isBuffer(response.data)
      ? response.data.length
      : (response.data?.byteLength || 0);

    if (response.status === 204 || !response.data || audioBytes === 0) {
      console.log('[LLM] No audio returned');
    } else {
      console.log(`[LLM] Response received (${audioBytes} bytes), queueing audio...`);
      const audioBuffer = Buffer.isBuffer(response.data) ? response.data : Buffer.from(response.data);
      await enqueuePlayback('tsuki-reply', audioBuffer, { priority: 1 });
    }
    
    // Release focus after processing complete
    if (focusedUserId === userId) {
      focusedUserId = null;
      console.log(`[FOCUS] Released (processing complete)`);
    }
  } catch (error) {
    console.error('[LLM] Error:', error.message);
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
  }
}

function enqueuePlayback(source, audioBuffer, options = {}) {
  return new Promise((resolve, reject) => {
    playbackQueue.push({
      id: ++playbackSequence,
      source,
      audioBuffer,
      priority: options.priority ?? 1,
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

/**
 * Play TTS audio in Discord voice channel
 */
async function playTTSAudio(audioBuffer) {
  const connection = currentConnection;
  if (!connection) {
    throw new Error('No active voice connection');
  }

  try {
    console.log('[TTS] Playing audio in voice channel...');

    // Create a readable stream from the buffer
    const { Readable } = await import('stream');
    const audioStream = Readable.from(audioBuffer);

    // Create audio resource from stream
    // The buffer should be PCM 48kHz stereo
    const resource = createAudioResource(audioStream, {
      inputType: 'raw', // Raw PCM audio
      inlineVolume: true
    });

    // Set volume to 50%
    if (resource.volume) {
      resource.volume.setVolume(0.5);
    }

    audioPlayer.play(resource);
    connection.subscribe(audioPlayer);

    // Wait for playback to finish
    await new Promise((resolve, reject) => {
      const onIdle = () => {
        audioPlayer.removeListener('error', onError);
        resolve();
      };
      const onError = (error) => {
        audioPlayer.removeListener(AudioPlayerStatus.Idle, onIdle);
        reject(error);
      };
      audioPlayer.once(AudioPlayerStatus.Idle, onIdle);
      audioPlayer.once('error', onError);
    });

    console.log('[TTS] Playback complete');
  } catch (error) {
    console.error('[TTS] Playback error:', error.message);
    throw error;
  }
}

/**
 * Start HTTP server so C# app can send "play TTS in Discord" requests
 * POST /play-tts body: { text: "..." }
 */
function startBridgeHttpServer() {
  const server = http.createServer(async (req, res) => {
    res.setHeader('Content-Type', 'application/json');
    if (req.method !== 'POST' || req.url !== '/play-tts') {
      res.writeHead(404);
      res.end(JSON.stringify({ error: 'Not found. Use POST /play-tts with body { text: \"...\" }' }));
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
          res.writeHead(503);
          res.end(JSON.stringify({ error: 'Not in a voice channel. Start the bridge and join first.' }));
          return;
        }
        const response = await axios.post(`${CONFIG.CSHARP_API_URL}/api/voice/test-tts`, { text });
        const audioBase64 = response.data?.audio;
        if (typeof audioBase64 !== 'string' || audioBase64.length === 0) {
          res.writeHead(502);
          res.end(JSON.stringify({ error: 'C# API did not return audio' }));
          return;
        }
        const audioBuffer = decodeBase64Audio(audioBase64);
        await enqueuePlayback('manual-tts', audioBuffer, { priority: 0 });
        res.writeHead(200);
        res.end(JSON.stringify({ ok: true, played: true, queued: playbackQueue.length }));
      } catch (err) {
        console.error('[BRIDGE HTTP] Error:', err.message);
        const status = err instanceof SyntaxError ? 400 : 502;
        res.writeHead(status);
        res.end(JSON.stringify({ error: status === 400 ? 'Invalid JSON' : 'Play TTS failed' }));
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

async function processRealtimeTranscript(userId, rawText) {
  const text = String(rawText || '').trim();
  if (!text) return;

  const state = getUserState(userId);
  if (state.processing) return;

  state.processing = true;
  turnGateClosed = true;
  try {
    console.log(`[STT] Final AssemblyAI transcript received for user ${userId}`);
    await processWithLLM(userId, text);
    state.lastResponseAt = Date.now();
  } finally {
    state.processing = false;
    turnGateClosed = false;
  }
}

/**
 * Stream one Discord speech capture to AssemblyAI v3 in realtime.
 * Discord's receiver emits Opus; prism decodes it to 48 kHz stereo PCM and the
 * converter sends 16 kHz mono PCM16 frames to the WebSocket.
 */
function handleAssemblyRealtimeUserAudio(userId, audioStream) {
  if (!assemblyRealtimeEnabled || audioPlayer.state.status === AudioPlayerStatus.Playing) {
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
      debugLog(`[STT] AssemblyAI partial user=${userId}: ${event.transcript}`);
    },
    onFinal: (event) => {
      if (finalHandled || capture?.aborted) return;
      finalHandled = true;
      debugLog(`[STT] AssemblyAI final user=${userId}: ${event.transcript}`);
      void processRealtimeTranscript(userId, event.transcript);
    },
    onError: (error) => {
      if (capture && !capture.connectionError) capture.connectionError = error;
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
            await processRealtimeTranscript(userId, fallbackResult.text);
          }
        } catch (error) {
          console.error('[STT] Fallback transcription failed:', error.message);
        }
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

    const connection = joinVoiceChannel({
      channelId,
      guildId,
      adapterCreator: guild.voiceAdapterCreator,
      selfDeaf: false, // Must be false to receive audio
      selfMute: false,
      // Explicitly enable encryption mode to handle Discord's encrypted voice packets
      debug: false,
    });

    currentConnection = connection;

    connection.on(VoiceConnectionStatus.Ready, () => {
      console.log('[VOICE] ✅ Connected and ready!');
    });

    syncAssemblyRealtimePresence('voice connection ready');
    connection.on(VoiceConnectionStatus.Disconnected, () => {
      console.log('[VOICE] Disconnected');
      if (currentConnection === connection) {
        currentConnection = null;
      }
      audioPlayer.stop(true);
      assemblyRealtimeEnabled = false;
      stopAssemblyRealtimeCaptures('voice connection disconnected');
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
      })) {
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
  assemblyRealtimeEnabled = false;
  stopAssemblyRealtimeCaptures('voice channel left');
  const connection = getVoiceConnection(guildId);
  if (connection) {
    connection.destroy();
    currentConnection = null;
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
    console.log('[BOT] No default voice channel configured; waiting for /tsuki join.');
  }
  startBridgeHttpServer();

  // Register guild slash commands (visible instantly, no global propagation wait)
  try {
    const guild = client.guilds.cache.get(CONFIG.GUILD_ID);
    if (guild) {
      await guild.commands.set([
        {
          name: 'tsuki',
          description: 'Control Tsuki voice chat and direct speech',
          options: [
            {
              name: 'join',
              description: 'Join the configured voice channel, or provide a channel ID',
              type: 1,
              options: [{ name: 'channel_id', description: 'Optional voice channel ID', type: 3, required: false }],
            },
            {
              name: 'leave',
              description: 'Leave the current voice channel',
              type: 1,
              options: [{ name: 'channel_id', description: 'Optional channel ID to leave', type: 3, required: false }],
            },
            {
              name: 'focus',
              description: 'Only listen to this user',
              type: 1,
              options: [{ name: 'user_id', description: 'User ID', type: 3, required: true }],
            },
            {
              name: 'unfocus',
              description: 'Stop focusing on this user',
              type: 1,
              options: [{ name: 'user_id', description: 'User ID', type: 3, required: true }],
            },
            {
              name: 'focuslist',
              description: 'Show which users Tsuki is focused on',
              type: 1,
            },
            {
              name: 'say',
              description: 'Make Tsuki speak text directly',
              type: 1,
              options: [
                {
                  name: 'destination',
                  description: 'Where to send the voice',
                  type: 3,
                  required: true,
                  choices: [
                    { name: 'Voice channel', value: 'vc' },
                    { name: 'Chat voice message', value: 'c' },
                  ],
                },
                { name: 'text', description: 'Text to synthesize', type: 3, required: true },
              ],
            },
          ],
        },
      ]);
      console.log('[BOT] Slash commands registered');
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
  if (!guild || interaction.commandName !== 'tsuki') return;

  const subcommand = interaction.options.getSubcommand();
  const arg = interaction.options.getString('channel_id') || interaction.options.getString('user_id') || '';

  try {
    switch (subcommand) {
      case 'join': {
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
      case 'leave': {
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
      case 'focus': {
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
        syncAssemblyRealtimePresence('manual focus list changed');
        const names = await Promise.all([...manualFocusList].map((id) => resolveUserName(guild, id)));
        await interaction.reply(`Now only listening to: **${names.join(', ')}**`);
        return;
      }
      case 'unfocus': {
        if (!manualFocusList.delete(arg)) {
          await interaction.reply({ content: `\`${arg}\` was not on the focus list.`, ephemeral: true });
          return;
        }
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
      case 'focuslist': {
        if (manualFocusList.size === 0) {
          await interaction.reply('Manual focus is off — I listen to everyone. Use `/tsuki focus user_id` to restrict it.');
          return;
        }
        const names = await Promise.all([...manualFocusList].map((id) => resolveUserName(guild, id)));
        await interaction.reply(`Listening only to: **${names.join(', ')}**`);
        return;
      }
      case 'say': {
        const destination = interaction.options.getString('destination', true);
        const rawText = interaction.options.getString('text', true);
        const text = limitDirectTtsText(rawText);
        if (!text) {
          await interaction.reply({ content: 'Text is empty after validation.', ephemeral: true });
          return;
        }

        if (destination === 'vc' && !currentConnection) {
          await interaction.reply({ content: 'I am not in a voice channel. Use `/tsuki join` first.', ephemeral: true });
          return;
        }

        await interaction.deferReply({ ephemeral: true });
        const pcm = await requestDirectTts(text);
        if (destination === 'vc') {
          await enqueuePlayback('slash-say-vc', pcm, { priority: 0 });
          await interaction.editReply(`Speaking in <#${currentConnection.joinConfig.channelId}>.`);
        } else {
          await sendVoiceMessage(interaction.channelId, pcmToWav(pcm), 'Voice message from Tsuki');
          await interaction.editReply('Voice message sent.');
        }
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

// Text mentions: when Tsuki is @-mentioned in the configured text channel,
// run the message through the same LLM pipeline as voice turns and reply in text.
// TEXT_REPLY_MODE='any' widens this to every non-bot message in the channel.
// Guardrails: one turn at a time globally, per-user cooldown, short error reply.
const TEXT_USER_COOLDOWN_MS = boundedInt(process.env.TEXT_USER_COOLDOWN_MS, 3000, 1000, 60000);
// Voice replies in text chat: 'all' = every reply gets a voice message,
// 'keywords' = only when the user's message contains a keyword (TEXT_VOICE_KEYWORDS),
// 'off' = text only. Keywords are comma-separated, case-insensitive substrings.
const TEXT_VOICE_MODE = (process.env.TEXT_VOICE_MODE || 'all').trim().toLowerCase();
const TEXT_VOICE_KEYWORDS = (process.env.TEXT_VOICE_KEYWORDS || '')
  .split(',')
  .map((k) => k.trim().toLowerCase())
  .filter(Boolean);

function wantsVoiceReply(text) {
  if (TEXT_VOICE_MODE === 'off') return false;
  if (TEXT_VOICE_MODE === 'keywords') {
    if (TEXT_VOICE_KEYWORDS.length === 0) return false;
    const hay = text.toLowerCase();
    return TEXT_VOICE_KEYWORDS.some((k) => hay.includes(k));
  }
  return true;
}

let textTurnInFlight = false;
const lastTextTurnAt = new Map(); // userId -> epoch ms

// Discord voice-message attachments require duration + waveform metadata
// (base64 amplitude samples). The API computes both from the synthesized WAV.
function voiceMetadataFromWav(wavBuffer, fallbackDuration) {
  let duration = fallbackDuration || 1;
  let sampleRate = 24000;
  try {
    duration = wavBuffer.readDoubleLE ? duration : duration; // noop guard
    sampleRate = wavBuffer.readUInt32LE(24);
    const byteRate = wavBuffer.readUInt32LE(28);
    // locate the data chunk (standard 44-byte layout, scan to be safe)
    let pos = 12;
    while (pos + 8 <= wavBuffer.length) {
      const id = wavBuffer.toString("ascii", pos, pos + 4);
      const size = wavBuffer.readUInt32LE(pos + 4);
      if (id === "data") {
        duration = Math.max(0.5, Math.round((size / byteRate) * 10) / 10);
        break;
      }
      pos += 8 + size + (size % 2);
    }
  } catch { /* keep fallback */ }

  // waveform: peak amplitude per bin over 16-bit samples (skip 44-byte header)
  const bins = 64;
  const dataStart = 44;
  const bytesPerSample = 2;
  const sampleCount = Math.floor((wavBuffer.length - dataStart) / bytesPerSample);
  const step = Math.max(1, Math.floor(sampleCount / bins));
  const amps = [];
  let max = 1;
  for (let b = 0; b < bins; b++) {
    let peak = 0;
    const s0 = dataStart + b * step * bytesPerSample;
    for (let i = 0; i < step; i++) {
      const off = s0 + i * bytesPerSample;
      if (off + 1 >= wavBuffer.length) break;
      const v = Math.abs(wavBuffer.readInt16LE(off));
      if (v > peak) peak = v;
    }
    amps.push(peak);
    if (peak > max) max = peak;
  }
  const waveform = Buffer.from(amps.map((a) => Math.round((a / max) * 255))).toString("base64");
  return { durationSecs: duration, waveform };
}

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

function limitDirectTtsText(value) {
  const text = String(value || '').replace(/\s+/g, ' ').trim();
  if (text.length <= DIRECT_TTS_MAX_CHARS) return text;
  const suffix = '...';
  const contentLimit = DIRECT_TTS_MAX_CHARS - suffix.length;
  const cut = text.lastIndexOf(' ', contentLimit);
  return (cut > 0 ? text.slice(0, cut) : text.slice(0, contentLimit)).trim() + suffix;
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

client.on('messageCreate', async (message) => {
  try {
    if (!CONFIG.TEXT_CHANNEL_ID || message.channel.id !== CONFIG.TEXT_CHANNEL_ID) return;
    if (message.author.bot) return;

    const mentioned = message.mentions.has(client.user);
    if (CONFIG.TEXT_REPLY_MODE !== 'any' && !mentioned) return;

    // Strip the mention itself so Tsuki doesn't read "@Tsuki ..." as content.
    const text = message.content.replace(/<@!?(\d+)>/g, ' ').replace(/\s+/g, ' ').trim();
    if (!text) return;

    const now = Date.now();
    const lastAt = lastTextTurnAt.get(message.author.id) || 0;
    if (now - lastAt < TEXT_USER_COOLDOWN_MS) {
      debugLog(`[TEXT] cooldown drop for ${message.author.tag} (${now - lastAt}ms < ${TEXT_USER_COOLDOWN_MS}ms)`);
      return;
    }
    if (textTurnInFlight) {
      debugLog('[TEXT] drop: another text turn already in flight');
      return;
    }

    lastTextTurnAt.set(message.author.id, now);
    textTurnInFlight = true;
    console.log(`[TEXT] ${CONFIG.TEXT_REPLY_MODE === 'any' ? 'Message' : 'Mention'} from ${message.author.tag}: ${text}`);

    try {
      await message.channel.sendTyping().catch(() => {});

      // Per-user memory endpoint: userId keys Tsuki's memory for this person,
      // userName lets her address them by name; voice per TEXT_VOICE_MODE.
      const wantVoice = wantsVoiceReply(text);
      const response = await axios.post(`${CONFIG.CSHARP_API_URL}/api/chat/discord`, {
        userId: message.author.id,
        userName: message.member?.displayName || message.author.globalName || message.author.username,
        text,
        voice: wantVoice,
      }, { timeout: 180000 });

      const reply = response?.data?.text;
      if (reply) {
        // Voice replies replace the text entirely (user preference); text is
        // the fallback when synthesis/conversion fails.
        let voiceSent = false;
        if (wantVoice && response?.data?.audio) {
          try {
            const wav = decodeBase64Audio(response.data.audio);
            // Some engines (Kokoro) return WAV variants the API's analyzer
            // can't parse — compute metadata from the audio itself as fallback.
            let durationSecs = response.data.duration_secs || 0;
            let waveform = response.data.waveform;
            if (!durationSecs || !waveform) {
              const meta = voiceMetadataFromWav(wav, 1);
              if (!durationSecs) durationSecs = meta.durationSecs;
              if (!waveform) waveform = meta.waveform;
            }
            const ogg = await wavToOggOpus(wav);
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
              console.log(`[TEXT] Sent voice message (${ogg.length} bytes, ${durationSecs}s)`);
            }
          } catch (voiceError) {
            console.error('[TEXT] Voice message failed:', voiceError?.response?.status, voiceError?.message);
          }
        }

        if (!voiceSent) {
          // Discord hard-caps messages at 2000 chars.
          await message.reply(String(reply).slice(0, 1900));
          console.log(`[TEXT] Replied to ${message.author.tag} with text (${reply.length} chars)`);
        }
      } else {
        console.log('[TEXT] Empty response, not replying');
      }
    } catch (turnError) {
      console.error('[TEXT] Turn failed:', turnError?.message);
      await message.reply('my brain hiccuped — try again in a moment ✨').catch(() => {});
    } finally {
      textTurnInFlight = false;
    }
  } catch (error) {
    textTurnInFlight = false;
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
