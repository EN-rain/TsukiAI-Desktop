import 'dotenv/config';
import http from 'http';
import { Client, GatewayIntentBits } from 'discord.js';
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
import prism from 'prism-media';

// Configuration (trim token - copy/paste often adds newlines or spaces)
const BRIDGE_HTTP_PORT = parseInt(process.env.BRIDGE_HTTP_PORT || '3001', 10);
const CONFIG = {
  DISCORD_TOKEN: (process.env.DISCORD_TOKEN || 'YOUR_BOT_TOKEN').trim(),
  GUILD_ID: process.env.GUILD_ID || 'YOUR_GUILD_ID',
  VOICE_CHANNEL_ID: process.env.VOICE_CHANNEL_ID || 'YOUR_VOICE_CHANNEL_ID',
  CSHARP_API_URL: (process.env.CSHARP_API_URL || 'http://localhost:5000').trim(),
  ASSEMBLYAI_API_KEY: (process.env.ASSEMBLYAI_API_KEY || '').trim(),
  USE_CLOUD_STT: (process.env.USE_CLOUD_STT || 'false').toLowerCase() === 'true',
  SAMPLE_RATE: 48000, // Discord voice sample rate
  CHANNELS: 2, // Stereo
  FRAME_SIZE: 960, // 20ms at 48kHz
};

// Check STT mode and conditionally import AssemblyAI
const USE_ASSEMBLYAI = CONFIG.USE_CLOUD_STT && CONFIG.ASSEMBLYAI_API_KEY && CONFIG.ASSEMBLYAI_API_KEY.length > 10;
let transcribeAudio = null;

if (USE_ASSEMBLYAI) {
  console.log('[INFO] ✅ Cloud STT enabled (AssemblyAI)');
  // Dynamically import AssemblyAI module only if needed
  try {
    const assemblyModule = await import('./assemblyai-streaming.js');
    transcribeAudio = assemblyModule.transcribeAudio;
  } catch (error) {
    console.error('[ERROR] Failed to load AssemblyAI module:', error.message);
    console.error('[ERROR] Please create assemblyai-streaming.js or disable cloud STT');
    process.exit(1);
  }
} else {
  console.log('[INFO] 🏠 Local STT enabled (C# Whisper)');
}

// ── VAD & chunking settings ────────────────────────────────────────────
const VAD = {
  // RMS threshold to consider a frame as speech (0–32767 scale for 16-bit PCM)
  RMS_SPEECH_THRESHOLD: parseInt(process.env.VAD_RMS_THRESHOLD || '300', 10),
  // How many consecutive silent frames (20 ms each) before we finalize a segment
  // 45 frames * 20 ms = 900 ms silence cutoff
  SILENCE_FRAMES_CUTOFF: parseInt(process.env.VAD_SILENCE_FRAMES || '45', 10),
  // Hard max for a single audio segment in seconds
  MAX_SEGMENT_SEC: parseFloat(process.env.VAD_MAX_SEGMENT_SEC || '12'),
  // Max total turn length in seconds (across all segments before sending to LLM)
  MAX_TURN_SEC: parseFloat(process.env.VAD_MAX_TURN_SEC || '30'),
  // End-of-turn silence: if no new speech for this many ms, finalize the whole turn
  END_OF_TURN_MS: parseInt(process.env.VAD_END_OF_TURN_MS || '1800', 10),
  // Per-user cooldown in ms (prevent rapid-fire triggers)
  USER_COOLDOWN_MS: parseInt(process.env.VAD_USER_COOLDOWN_MS || '3000', 10),
  // Minimum segment size in bytes to bother sending for STT (avoids tiny pops)
  MIN_SEGMENT_BYTES: parseInt(process.env.VAD_MIN_SEGMENT_BYTES || '9600', 10), // ~50 ms stereo 48 kHz
};

const isStandaloneMode = !process.env.CSHARP_API_URL || process.env.CSHARP_API_URL === 'http://localhost:5000';

if (isStandaloneMode) {
  console.log('[INFO] C# API URL configured:', CONFIG.CSHARP_API_URL);
  console.log('[INFO] Full STT->LLM->TTS pipeline enabled');
} else {
  console.log('[INFO] Running in standalone mode - C# integration disabled');
  console.log('[INFO] This bot will join voice but not process audio yet');
}

console.log('[VAD] Config:', JSON.stringify(VAD, null, 2));

// Create Discord client
const client = new Client({
  intents: [
    GatewayIntentBits.Guilds,
    GatewayIntentBits.GuildVoiceStates,
    GatewayIntentBits.GuildMessages,
  ],
});

// Audio player for TTS playback
const audioPlayer = createAudioPlayer();

// Track active voice connections
let currentConnection = null;
// Focus mode: track which user is currently being processed (ignore all others)
let focusedUserId = null;
// Track users with an active audio capture to avoid duplicate subscriptions.
const activeAudioCaptures = new Set();

// Per-user state: cooldown timestamps and active turn data
const userState = new Map();

// Cache of known bot user IDs to avoid repeated lookups and spam logs
const knownBots = new Set();

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
    console.log(`[TURN] User ${userId}: discarding tiny turn (${combined.length} bytes)`);
    return;
  }

  // Check cooldown
  const now = Date.now();
  if (now - state.lastResponseAt < VAD.USER_COOLDOWN_MS) {
    console.log(`[TURN] User ${userId}: cooldown active, skipping`);
    return;
  }

  if (state.processing) {
    console.log(`[TURN] User ${userId}: already processing, skipping`);
    return;
  }

  state.processing = true;
  try {
    console.log(`[TURN] User ${userId}: finalizing turn (${segments.length} segment(s), ${combined.length} bytes)`);
    await sendAudioForSTT(userId, combined);
    state.lastResponseAt = Date.now();
  } finally {
    state.processing = false;
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
    console.log(`[VAD] User ${userId}: max turn duration reached (${turnDurationSec.toFixed(1)}s), forcing finalize`);
    finalizeTurn(userId);
    return;
  }

  // Reset end-of-turn timer: wait for more segments or finalize after END_OF_TURN_MS
  if (state.endOfTurnTimer) {
    clearTimeout(state.endOfTurnTimer);
  }
  state.endOfTurnTimer = setTimeout(() => {
    console.log(`[VAD] User ${userId}: end-of-turn silence (${VAD.END_OF_TURN_MS}ms), finalizing turn`);
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
 * Send audio to STT service (AssemblyAI or C# Whisper)
 */
async function sendAudioForSTT(userId, audioBuffer) {
  try {
    console.log(`[STT] Transcribing ${audioBuffer.length} bytes from user ${userId}`);

    let text, language, confidence;

    if (USE_ASSEMBLYAI) {
      // Use AssemblyAI for STT
      console.log('[STT] Using AssemblyAI...');
      const result = await transcribeAudio(CONFIG.ASSEMBLYAI_API_KEY, audioBuffer, CONFIG.SAMPLE_RATE);
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
    // Note: Text and response are logged by C# API to UI
    console.log(`[LLM] Processing request...`);

    const response = await axios.post(`${CONFIG.CSHARP_API_URL}/api/voice/process-binary`, {
      userId: userId.toString(),
      text: text
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
      console.log(`[LLM] Response received (${audioBytes} bytes), playing audio...`);
      const audioBuffer = Buffer.isBuffer(response.data) ? response.data : Buffer.from(response.data);
      await playTTSAudio(audioBuffer);
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

/**
 * Play TTS audio in Discord voice channel
 */
async function playTTSAudio(audioBuffer) {
  if (!currentConnection) {
    console.error('[TTS] No active voice connection');
    return;
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
    currentConnection.subscribe(audioPlayer);

    // Wait for playback to finish
    await new Promise((resolve) => {
      audioPlayer.once(AudioPlayerStatus.Idle, resolve);
    });

    console.log('[TTS] Playback complete');
  } catch (error) {
    console.error('[TTS] Playback error:', error.message);
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
    req.on('data', (chunk) => { body += chunk; });
    req.on('end', async () => {
      try {
        const data = JSON.parse(body || '{}');
        const text = (data.text || '').trim();
        if (!text) {
          res.writeHead(400);
          res.end(JSON.stringify({ error: 'Missing or empty "text" in body' }));
          return;
        }
        if (!currentConnection) {
          res.writeHead(503);
          res.end(JSON.stringify({ error: 'Not in a voice channel. Start the bridge and join first.' }));
          return;
        }
        const response = await axios.post(`${CONFIG.CSHARP_API_URL}/api/voice/test-tts`, { text });
        const audioBase64 = response.data?.audio;
        if (!audioBase64) {
          res.writeHead(502);
          res.end(JSON.stringify({ error: 'C# API did not return audio' }));
          return;
        }
        const audioBuffer = Buffer.from(audioBase64, 'base64');
        await playTTSAudio(audioBuffer);
        res.writeHead(200);
        res.end(JSON.stringify({ ok: true, played: true }));
      } catch (err) {
        console.error('[BRIDGE HTTP] Error:', err.message);
        res.writeHead(500);
        res.end(JSON.stringify({ error: err.message || 'Play TTS failed' }));
      }
    });
  });
  server.listen(BRIDGE_HTTP_PORT, '127.0.0.1', () => {
    console.log(`[BRIDGE] HTTP server listening on http://127.0.0.1:${BRIDGE_HTTP_PORT}/play-tts (C# can send TTS here to play in Discord)`);
  });
}

/**
 * Handle user speaking in voice channel.
 * Implements RMS-based VAD with silence cutoff, hard max segment length, and turn management.
 */
function handleUserAudio(userId, audioStream) {
  // Skip if bot is currently playing back (don't listen to ourselves)
  if (audioPlayer.state.status === AudioPlayerStatus.Playing) {
    audioStream.destroy(); // Clean up immediately
    return;
  }

  console.log(`[AUDIO] User ${userId} started speaking`);

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

  // Bytes per second for 48 kHz stereo 16-bit = 48000 * 2 * 2 = 192000
  const BYTES_PER_SEC = CONFIG.SAMPLE_RATE * CONFIG.CHANNELS * 2;
  const MAX_SEGMENT_BYTES = VAD.MAX_SEGMENT_SEC * BYTES_PER_SEC;

  function finalizeSegment() {
    if (segmentChunks.length === 0) return;

    const segmentBuffer = Buffer.concat(segmentChunks);
    segmentChunks.length = 0;
    segmentBytes = 0;
    silentFrameCount = 0;
    speechDetected = false;

    if (segmentBuffer.length < VAD.MIN_SEGMENT_BYTES) {
      console.log(`[VAD] User ${userId}: segment too small (${segmentBuffer.length} bytes), discarding`);
      return;
    }

    const durationMs = (segmentBuffer.length / BYTES_PER_SEC * 1000).toFixed(0);
    console.log(`[VAD] User ${userId}: segment complete (${segmentBuffer.length} bytes, ~${durationMs}ms)`);

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
      const rms = calcRMS(chunk);
      const isSpeech = rms >= VAD.RMS_SPEECH_THRESHOLD;

      if (isSpeech) {
        speechDetected = true;
        hadSpeechInStream = true;
        silentFrameCount = 0;
        segmentChunks.push(chunk);
        segmentBytes += chunk.length;
      } else {
        silentFrameCount++;

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
        console.log(`[VAD] User ${userId}: max segment length reached (${VAD.MAX_SEGMENT_SEC}s)`);
        finalizeSegment();
      }
    })
    .on('end', () => {
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

    const connection = joinVoiceChannel({
      channelId,
      guildId,
      adapterCreator: client.guilds.cache.get(guildId).voiceAdapterCreator,
      selfDeaf: false, // Must be false to receive audio
      selfMute: false,
      // Explicitly enable encryption mode to handle Discord's encrypted voice packets
      debug: false,
    });

    currentConnection = connection;

    connection.on(VoiceConnectionStatus.Ready, () => {
      console.log('[VOICE] ✅ Connected and ready!');
    });

    connection.on(VoiceConnectionStatus.Disconnected, () => {
      console.log('[VOICE] Disconnected');
      currentConnection = null;
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
      // Check cache first - if we already know this is a bot, skip immediately
      if (knownBots.has(userId)) {
        return;
      }

      // FOCUS MODE: If we're focused on another user, ignore everyone else
      if (focusedUserId && focusedUserId !== userId) {
        return; // Silently ignore other users while focused
      }

      // If we already have an active capture for this user, skip duplicate start events.
      if (activeAudioCaptures.has(userId)) {
        return;
      }

      // Ignore bots (including music bots)
      try {
        const guild = client.guilds.cache.get(CONFIG.GUILD_ID);
        if (guild) {
          const member = await guild.members.fetch(userId).catch(() => null);
          if (member && member.user.bot) {
            knownBots.add(userId); // Cache this bot ID
            console.log(`[VOICE] Ignoring bot user ${userId} (${member.user.tag})`);
            return;
          }
        }
      } catch (error) {
        console.error(`[VOICE] Error checking if user ${userId} is a bot:`, error.message);
      }

      console.log(`[VOICE] User ${userId} started speaking`);

      // Set focus on this user if no one is focused
      if (!focusedUserId) {
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
  if (isStandaloneMode) {
    console.log('[BOT] Mode: voice pipeline enabled (STT -> LLM -> TTS)');
  } else {
    console.log('[BOT] Mode: voice join only (C# integration disabled)');
  }

  // Auto-join voice channel on startup
  try {
    await joinVoice(CONFIG.GUILD_ID, CONFIG.VOICE_CHANNEL_ID);
    startBridgeHttpServer();
  } catch (error) {
    console.error('[BOT] Failed to auto-join voice:', error.message);
  }
});

client.on('error', (error) => {
  console.error('[BOT] Client error:', error.message);
});

// Handle process termination
process.on('SIGINT', () => {
  console.log('[BOT] Shutting down...');
  if (currentConnection) {
    leaveVoice(CONFIG.GUILD_ID);
  }
  client.destroy();
  process.exit(0);
});

// Login to Discord (token already trimmed in CONFIG)
console.log('[BOT] Starting Discord voice bridge...');
client.login(CONFIG.DISCORD_TOKEN);
