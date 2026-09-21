import { batchMolabVoiceSentences, countMolabWords } from './molab-voice-pipeline.js';

// Keep single-model requests short enough to avoid skipped tails, without
// silently cutting off caller text. Preserve the original sentence order.
export const TTS_UTTERANCE_MAX_CHARS = 140;
export const DIRECT_TTS_TOTAL_MAX_CHARS = 1200;

export function planTtsUtterances(
  value, { maxChars = TTS_UTTERANCE_MAX_CHARS, maxTotalChars = DIRECT_TTS_TOTAL_MAX_CHARS } = {},
) {
  const text = String(value ?? '').replace(/\s+/gu, ' ').trim();
  if (!text) throw new Error('TTS text is empty');
  if (text.length > maxTotalChars) {
    throw new RangeError('TTS text exceeds ' + maxTotalChars + ' characters; no text was dropped');
  }
  const parts = batchMolabVoiceSentences(text, maxChars);
  if (!parts.length || parts.some(part => !part || part.length > maxChars)) {
    throw new Error('TTS sentence planning failed');
  }
  return parts;
}

// A WAV can be valid and still represent an obviously incomplete utterance.
// This is a conservative warning/retry guard, not a speech transcription.
export function speechDurationLooksIncomplete(text, durationSeconds) {
  if (!Number.isFinite(durationSeconds) || durationSeconds <= 0) return true;
  // This only detects *severely* short output. It is not ASR and must not
  // reject a fully spoken sentence simply because the speaker talks quickly.
  const words = countMolabWords(text);
  if (words >= 12 && durationSeconds < words / 10) return true;
  const cjk = [...String(text)].filter(char => /[\u3040-\u30ff\u3400-\u9fff]/u.test(char)).length;
  return cjk >= 18 && durationSeconds < cjk / 23;
}

export function assemblePcmUtterances(parts, {
  pauseMs = 100, maxAudioBytes = 16 * 1024 * 1024,
} = {}) {
  if (!Array.isArray(parts) || parts.length === 0) {
    throw new Error('No speech audio was generated');
  }
  if (!Number.isInteger(pauseMs) || pauseMs < 0 || pauseMs > 500) {
    throw new RangeError('Invalid pause between spoken utterances');
  }
  const pause = Buffer.alloc(Math.round(48000 * pauseMs / 1000) * 4);
  const output = [];
  let bytes = 0;
  for (let index = 0; index < parts.length; index += 1) {
    const pcm = parts[index];
    if (!Buffer.isBuffer(pcm) || pcm.length === 0 || pcm.length % 4 !== 0) {
      throw new Error('Invalid PCM for utterance ' + (index + 1));
    }
    const gap = index < parts.length - 1 ? pause : null;
    bytes += pcm.length + (gap?.length || 0);
    if (bytes + 44 > maxAudioBytes) {
      throw new RangeError('Speech audio exceeds the voice-message size limit; text was not truncated');
    }
    output.push(pcm);
    if (gap) output.push(gap);
  }
  return Buffer.concat(output, bytes);
}
