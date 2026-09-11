import axios from 'axios';

const TARGET_SAMPLE_RATE = 16000;
const BYTES_PER_SAMPLE = 2;

const LOCALE_ALIASES = new Map([
  ['en', 'en-US'],
  ['english', 'en-US'],
  ['ja', 'ja-JP'],
  ['jp', 'ja-JP'],
  ['japanese', 'ja-JP'],
]);

/**
 * Resolve the bridge's short language names to an Azure Speech locale.
 * Azure's single-shot REST endpoint needs one locale per request; "auto"
 * therefore means the configured AZURE_STT_LANGUAGE, not silent guessing.
 */
function normalizeAzureLocale(language = 'auto', configuredLanguage = 'en-US') {
  let value = String(language ?? '').trim();
  if (!value || value.toLowerCase() === 'auto') {
    value = String(configuredLanguage ?? '').trim() || 'en-US';
  }
  if (value.toLowerCase() === 'auto') {
    value = 'en-US';
  }

  const alias = LOCALE_ALIASES.get(value.toLowerCase());
  if (alias) return alias;

  const match = /^([a-z]{2})-([a-z]{2})$/i.exec(value);
  if (match) {
    return `${match[1].toLowerCase()}-${match[2].toUpperCase()}`;
  }

  throw new Error(
    `Unsupported Azure STT language "${value}". Use en, ja, en-US, ja-JP, or another Azure locale.`,
  );
}

function clampInt16(value) {
  return Math.max(-32768, Math.min(32767, Math.round(value)));
}

/**
 * Discord gives us signed 16-bit little-endian PCM at 48 kHz stereo. Azure's
 * REST recognizer accepts a PCM WAV, so downsample deterministically to 16 kHz
 * mono and add a standard 44-byte WAV header.
 */
function pcmToWav16kMono(pcmBuffer, sourceSampleRate = 48000, sourceChannels = 2) {
  if (!Buffer.isBuffer(pcmBuffer)) {
    throw new TypeError('Azure STT audio must be a Buffer');
  }
  if (!Number.isInteger(sourceSampleRate) || sourceSampleRate <= 0 || sourceSampleRate % TARGET_SAMPLE_RATE !== 0) {
    throw new Error(`Azure STT requires a source sample rate divisible by ${TARGET_SAMPLE_RATE} Hz`);
  }
  if (!Number.isInteger(sourceChannels) || sourceChannels < 1) {
    throw new Error('Azure STT source channel count must be a positive integer');
  }

  const bytesPerFrame = sourceChannels * BYTES_PER_SAMPLE;
  const sourceFrameCount = Math.floor(pcmBuffer.length / bytesPerFrame);
  const decimation = sourceSampleRate / TARGET_SAMPLE_RATE;
  const outputFrameCount = Math.floor(sourceFrameCount / decimation);
  const pcm16 = Buffer.alloc(outputFrameCount * BYTES_PER_SAMPLE);

  for (let outputFrame = 0; outputFrame < outputFrameCount; outputFrame++) {
    const firstSourceFrame = outputFrame * decimation;
    let total = 0;
    for (let sourceOffset = 0; sourceOffset < decimation; sourceOffset++) {
      const sourceFrame = firstSourceFrame + sourceOffset;
      const frameOffset = sourceFrame * bytesPerFrame;
      for (let channel = 0; channel < sourceChannels; channel++) {
        total += pcmBuffer.readInt16LE(frameOffset + channel * BYTES_PER_SAMPLE);
      }
    }

    const divisor = decimation * sourceChannels;
    pcm16.writeInt16LE(clampInt16(total / divisor), outputFrame * BYTES_PER_SAMPLE);
  }

  const wav = Buffer.alloc(44 + pcm16.length);
  wav.write('RIFF', 0);
  wav.writeUInt32LE(36 + pcm16.length, 4);
  wav.write('WAVE', 8);
  wav.write('fmt ', 12);
  wav.writeUInt32LE(16, 16);
  wav.writeUInt16LE(1, 20); // PCM
  wav.writeUInt16LE(1, 22); // mono
  wav.writeUInt32LE(TARGET_SAMPLE_RATE, 24);
  wav.writeUInt32LE(TARGET_SAMPLE_RATE * BYTES_PER_SAMPLE, 28);
  wav.writeUInt16LE(BYTES_PER_SAMPLE, 32);
  wav.writeUInt16LE(16, 34);
  wav.write('data', 36);
  wav.writeUInt32LE(pcm16.length, 40);
  pcm16.copy(wav, 44);
  return wav;
}

function parseAzureResponse(data, locale) {
  const status = String(data?.RecognitionStatus || '').toLowerCase();
  const best = Array.isArray(data?.NBest) ? data.NBest[0] : null;
  const text = String(data?.DisplayText || best?.Display || '').trim();
  const confidence = Number.isFinite(Number(best?.Confidence))
    ? Math.max(0, Math.min(1, Number(best.Confidence)))
    : 0;

  if (status !== 'success' || !text) {
    return { text: '', language: locale, confidence: 0 };
  }

  return { text, language: locale, confidence };
}

function speechEndpoint(region) {
  const configured = (process.env.AZURE_SPEECH_ENDPOINT || '').trim();
  if (configured) {
    const url = new URL(configured);
    if (url.protocol !== 'https:') {
      throw new Error('AZURE_SPEECH_ENDPOINT must use HTTPS');
    }
    return url;
  }

  const normalizedRegion = String(region || '').trim().toLowerCase();
  if (!normalizedRegion) {
    throw new Error('AZURE_SPEECH_REGION is required when STT_MODE=azure');
  }
  return new URL(`https://${normalizedRegion}.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1`);
}

/**
 * Transcribe one Discord voice turn through Azure Speech-to-Text.
 */
async function transcribeAudio(apiKey, audioBuffer, sampleRate = 48000, sttLanguage = 'auto') {
  const key = String(apiKey || '').trim();
  if (!key) throw new Error('AZURE_SPEECH_KEY is required when STT_MODE=azure');

  const locale = normalizeAzureLocale(
    sttLanguage,
    process.env.AZURE_STT_LANGUAGE || 'en-US',
  );
  const endpoint = speechEndpoint(process.env.AZURE_SPEECH_REGION);
  endpoint.searchParams.set('language', locale);
  endpoint.searchParams.set('format', 'detailed');
  endpoint.searchParams.set('profanity', 'raw');

  const wavBuffer = pcmToWav16kMono(audioBuffer, sampleRate, 2);
  const response = await axios.post(endpoint.toString(), wavBuffer, {
    headers: {
      'Ocp-Apim-Subscription-Key': key,
      'Content-Type': 'audio/wav; codecs=audio/pcm; samplerate=16000',
      Accept: 'application/json',
    },
    timeout: 30000,
    maxContentLength: Infinity,
    maxBodyLength: Infinity,
  });

  return parseAzureResponse(response.data, locale);
}

export {
  normalizeAzureLocale,
  pcmToWav16kMono,
  parseAzureResponse,
  transcribeAudio,
};
