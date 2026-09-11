import { AssemblyAI } from 'assemblyai';
import { AssemblyKeyPool, assemblyFailureStatus } from './assembly-key-pool.js';

const DEFAULT_SAMPLE_RATE = 16000;
const DEFAULT_CONNECT_TIMEOUT_MS = 12000;
const DEFAULT_CLOSE_TIMEOUT_MS = 8000;
const DEFAULT_MAX_PENDING_AUDIO_BYTES = 4 * 1024 * 1024;

function withTimeout(promise, timeoutMs, message) {
  let timer;
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(message)), timeoutMs);
  });

  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}

function defaultTranscriberFactory(apiKey, options) {
  const client = new AssemblyAI({ apiKey });
  return client.streaming.transcriber(options);
}

function safeInvoke(callback, value) {
  try {
    callback?.(value);
  } catch {
    // Event listeners must not break the WebSocket lifecycle.
  }
}

function safeInvokeError(callback, error) {
  try {
    callback?.(error);
  } catch {
    // Event listeners must not break the WebSocket lifecycle.
  }
}

/**
 * One AssemblyAI v3 streaming session for one Discord speech capture.
 * Connection failures rotate through the configured key pool before failing.
 */
export class AssemblyRealtimeSession {
  #keyPool;
  #transcriberFactory;
  #options;
  #connectTimeoutMs;
  #closeTimeoutMs;
  #maxPendingAudioBytes;
  #onPartial;
  #onFinal;
  #onError;
  #transcriber = null;
  #activeKey = null;
  #activeFailureReported = false;
  #connectPromise = null;
  #connected = false;
  #closing = false;
  #fatalError = null;
  #pendingAudio = [];
  #pendingAudioBytes = 0;
  #finalTurns = new Map();
  #notifiedFinalTurns = new Set();
  #language = '';
  #confidence = 0;

  constructor({
    keyPool,
    transcriberFactory = defaultTranscriberFactory,
    sampleRate = DEFAULT_SAMPLE_RATE,
    speechModel = 'universal-streaming-multilingual',
    formatTurns = true,
    languageDetection = true,
    maxTurnSilence,
    minEndOfTurnSilenceWhenConfident,
    connectTimeoutMs = DEFAULT_CONNECT_TIMEOUT_MS,
    closeTimeoutMs = DEFAULT_CLOSE_TIMEOUT_MS,
    maxPendingAudioBytes = DEFAULT_MAX_PENDING_AUDIO_BYTES,
    onPartial,
    onFinal,
    onError,
  } = {}) {
    if (!(keyPool instanceof AssemblyKeyPool)) {
      throw new TypeError('keyPool must be an AssemblyKeyPool');
    }

    this.#keyPool = keyPool;
    this.#transcriberFactory = transcriberFactory;
    this.#options = {
      sampleRate,
      speechModel,
      formatTurns,
      languageDetection,
      ...(maxTurnSilence === undefined ? {} : { maxTurnSilence }),
      ...(minEndOfTurnSilenceWhenConfident === undefined ? {} : { minEndOfTurnSilenceWhenConfident }),
    };
    this.#connectTimeoutMs = connectTimeoutMs;
    this.#closeTimeoutMs = closeTimeoutMs;
    this.#maxPendingAudioBytes = maxPendingAudioBytes;
    this.#onPartial = onPartial;
    this.#onFinal = onFinal;
    this.#onError = onError;
  }

  async connect() {
    if (this.#connected) return { connected: true };
    if (this.#connectPromise) return this.#connectPromise;

    this.#connectPromise = this.#connectWithRotation();
    try {
      return await this.#connectPromise;
    } finally {
      this.#connectPromise = null;
    }
  }

  sendAudio(audio) {
    if (!audio || this.#closing) return;
    if (this.#fatalError) throw this.#fatalError;

    const chunk = Buffer.from(audio);
    if (chunk.length === 0) return;

    if (!this.#connected || !this.#transcriber) {
      if (this.#pendingAudioBytes + chunk.length > this.#maxPendingAudioBytes) {
        throw new Error('AssemblyAI connection is not ready and its audio buffer is full');
      }
      this.#pendingAudio.push(chunk);
      this.#pendingAudioBytes += chunk.length;
      return;
    }

    try {
      this.#transcriber.sendAudio(chunk);
    } catch (error) {
      this.#fatalError = error;
      this.#recordActiveFailure(error);
      safeInvokeError(this.#onError, error);
      throw error;
    }
  }

  async close() {
    const transcriber = this.#transcriber;
    this.#closing = true;

    try {
      if (transcriber) {
        if (this.#fatalError) {
          await this.#safeClose(transcriber, false);
        } else {
          try {
            await withTimeout(
              transcriber.close(true),
              this.#closeTimeoutMs,
              'AssemblyAI termination timed out',
            );
          } catch {
            await this.#safeClose(transcriber, false);
          }
        }
      }
    } finally {
      this.#connected = false;
      this.#transcriber = null;
      this.#activeKey = null;
      this.#activeFailureReported = false;
      this.#pendingAudio = [];
      this.#pendingAudioBytes = 0;
    }

    return this.result();
  }

  result() {
    const text = [...this.#finalTurns.entries()]
      .sort(([a], [b]) => a - b)
      .map(([, event]) => event.transcript)
      .join(' ')
      .trim();

    return {
      text,
      language: this.#language || 'en',
      confidence: this.#confidence,
    };
  }

  async #connectWithRotation() {
    const candidates = this.#keyPool.candidates();
    if (candidates.length === 0) {
      throw new Error('No AssemblyAI API keys configured');
    }

    let lastError = null;
    for (const apiKey of candidates) {
      let transcriber = null;
      try {
        this.#fatalError = null;
        transcriber = this.#transcriberFactory(apiKey, this.#options);
        this.#transcriber = transcriber;
        const begin = await this.#connectAttempt(transcriber);
        this.#connected = true;
        this.#activeKey = apiKey;
        this.#activeFailureReported = false;
        this.#keyPool.markSuccess(apiKey);
        this.#flushPendingAudio();
        return begin;
      } catch (error) {
        lastError = error;
        this.#keyPool.markFailure(apiKey, assemblyFailureStatus(error));
        await this.#safeClose(transcriber, false);
        if (this.#transcriber === transcriber) this.#transcriber = null;
      }
    }

    this.#connected = false;
    this.#transcriber = null;
    this.#activeKey = null;
    throw lastError || new Error('AssemblyAI realtime connection failed');
  }

  async #connectAttempt(transcriber) {
    let opened = false;
    let rejectBeforeOpen;
    const beforeOpenFailure = new Promise((_, reject) => {
      rejectBeforeOpen = reject;
    });

    transcriber.on('turn', (event) => this.#handleTurn(event));
    transcriber.on('error', (error) => {
      this.#fatalError = error;
      if (!opened) {
        rejectBeforeOpen(error);
      } else {
        this.#recordActiveFailure(error);
        safeInvokeError(this.#onError, error);
      }
    });
    transcriber.on('close', (code, reason) => {
      if (!opened) {
        const suffix = reason ? `: ${String(reason)}` : '';
        rejectBeforeOpen(new Error(`AssemblyAI socket closed before Begin (${code ?? 'unknown'})${suffix}`));
      } else if (!this.#closing && !this.#fatalError) {
        const error = new Error(`AssemblyAI socket closed unexpectedly (${code ?? 'unknown'})`);
        this.#fatalError = error;
        this.#recordActiveFailure(error);
        safeInvokeError(this.#onError, error);
      }
    });

    const connectPromise = Promise.resolve().then(() => transcriber.connect());
    // The SDK's connect promise and its event callbacks can race. Mark the
    // promise handled even when an error event wins the race.
    connectPromise.catch(() => {});

    const begin = await withTimeout(
      Promise.race([connectPromise, beforeOpenFailure]),
      this.#connectTimeoutMs,
      'AssemblyAI connection timed out',
    );
    opened = true;
    return begin;
  }

  #flushPendingAudio() {
    const pending = this.#pendingAudio;
    this.#pendingAudio = [];
    this.#pendingAudioBytes = 0;
    for (const chunk of pending) {
      this.#transcriber.sendAudio(chunk);
    }
  }

  #handleTurn(event) {
    const transcript = String(event?.transcript ?? '').trim();
    if (!transcript) return;

    if (event.language_code) this.#language = String(event.language_code);
    if (Number.isFinite(Number(event.end_of_turn_confidence))) {
      this.#confidence = Number(event.end_of_turn_confidence);
    }

    const order = Number.isFinite(Number(event.turn_order))
      ? Number(event.turn_order)
      : this.#finalTurns.size;
    const normalized = { ...event, transcript };

    if (event.end_of_turn) {
      this.#finalTurns.set(order, normalized);
      if (!this.#notifiedFinalTurns.has(order)) {
        this.#notifiedFinalTurns.add(order);
        safeInvoke(this.#onFinal, normalized);
      }
    } else {
      safeInvoke(this.#onPartial, normalized);
    }
  }

  #recordActiveFailure(error) {
    if (this.#activeFailureReported || !this.#activeKey) return;
    this.#activeFailureReported = true;
    this.#keyPool.markFailure(this.#activeKey, assemblyFailureStatus(error));
  }

  async #safeClose(transcriber, waitForTermination) {
    if (!transcriber?.close) return;
    try {
      await transcriber.close(waitForTermination);
    } catch {
      // The connection is already being discarded; preserve the original error.
    }
  }
}

/**
 * Discord supplies 48 kHz stereo PCM. AssemblyAI streaming expects 16 kHz
 * mono PCM16. Averaging each three-frame window also provides a small amount
 * of anti-aliasing before decimation and keeps this path CPU-only.
 */
export function createPcm48StereoTo16Mono() {
  let remainder = Buffer.alloc(0);

  return (chunk) => {
    const input = remainder.length > 0
      ? Buffer.concat([remainder, Buffer.from(chunk)])
      : Buffer.from(chunk);
    const completeBytes = input.length - (input.length % 4);
    const completeFrames = completeBytes / 4;
    const outputFrames = Math.floor(completeFrames / 3);
    const output = Buffer.alloc(outputFrames * 2);

    for (let outputIndex = 0; outputIndex < outputFrames; outputIndex++) {
      let sum = 0;
      const firstFrame = outputIndex * 3;
      for (let frameIndex = 0; frameIndex < 3; frameIndex++) {
        const offset = (firstFrame + frameIndex) * 4;
        const left = input.readInt16LE(offset);
        const right = input.readInt16LE(offset + 2);
        sum += (left + right) / 2;
      }
      output.writeInt16LE(Math.max(-32768, Math.min(32767, Math.round(sum / 3))), outputIndex * 2);
    }

    remainder = input.subarray(outputFrames * 3 * 4);
    return output;
  };
}

/**
 * Batch compatibility path for the bridge's optional non-realtime fallback.
 * It still uses the v3 WebSocket and the same rotating key pool.
 */
export async function transcribeAudio(apiKeys, audioBuffer, sampleRate = 48000) {
  const keyPool = apiKeys instanceof AssemblyKeyPool ? apiKeys : new AssemblyKeyPool(apiKeys);
  const session = new AssemblyRealtimeSession({ keyPool });
  const convert = createPcm48StereoTo16Mono();

  await session.connect();
  const pcm = convert(audioBuffer);
  if (pcm.length === 0) throw new Error('Audio is too short for AssemblyAI realtime transcription');
  session.sendAudio(pcm);
  return session.close();
}
