/**
 * Groq Whisper API integration for fast, free speech-to-text
 * Uses the same Groq API key as the LLM service
 */

import axios from 'axios';
import FormData from 'form-data';
import { GroqKeyPool } from './groq-key-pool.js';

/**
 * Convert PCM audio to WAV format
 */
function pcmToWav(pcmBuffer, sampleRate, numChannels, bitDepth) {
  const dataLength = pcmBuffer.length;
  const headerLength = 44;
  const buffer = Buffer.alloc(headerLength + dataLength);

  // RIFF header
  buffer.write('RIFF', 0);
  buffer.writeUInt32LE(headerLength + dataLength - 8, 4);
  buffer.write('WAVE', 8);

  // fmt chunk
  buffer.write('fmt ', 12);
  buffer.writeUInt32LE(16, 16); // fmt chunk size
  buffer.writeUInt16LE(1, 20); // PCM format
  buffer.writeUInt16LE(numChannels, 22);
  buffer.writeUInt32LE(sampleRate, 24);
  buffer.writeUInt32LE(sampleRate * numChannels * (bitDepth / 8), 28); // byte rate
  buffer.writeUInt16LE(numChannels * (bitDepth / 8), 32); // block align
  buffer.writeUInt16LE(bitDepth, 34);

  // data chunk
  buffer.write('data', 36);
  buffer.writeUInt32LE(dataLength, 40);
  pcmBuffer.copy(buffer, headerLength);

  return buffer;
}

/**
 * Transcribe audio using Groq Whisper API
 * @param {string|string[]|GroqKeyPool} apiKeys - Groq API key(s)
 * @param {Buffer} audioBuffer - PCM audio buffer (48kHz stereo)
 * @param {number} sampleRate - Sample rate of the audio
 * @param {string} sttLanguage - Language code (e.g. en, ja) or "auto"
 * @returns {Promise<{text: string, language: string, confidence: number}>}
 */
async function transcribeAudio(apiKeys, audioBuffer, sampleRate = 48000, sttLanguage = 'auto', httpClient = axios) {
  const keyPool = apiKeys instanceof GroqKeyPool ? apiKeys : new GroqKeyPool(apiKeys);
  const candidates = keyPool.candidates();
  if (candidates.length === 0) {
    throw new Error('No Groq API keys configured');
  }

  // Convert PCM to WAV format once; each attempt gets a fresh multipart body.
  const wavBuffer = pcmToWav(audioBuffer, sampleRate, 2, 16);
  console.log(`[GroqWhisper] Converted ${audioBuffer.length} bytes PCM to ${wavBuffer.length} bytes WAV`);
  const languageCode = (sttLanguage || 'auto').trim().toLowerCase();
  let lastError = null;

  for (let index = 0; index < candidates.length; index++) {
    const apiKey = candidates[index];
    const form = new FormData();
    form.append('file', wavBuffer, {
      filename: 'audio.wav',
      contentType: 'audio/wav'
    });
    form.append('model', 'whisper-large-v3');
    form.append('response_format', 'verbose_json');
    if (languageCode !== 'auto') {
      form.append('language', languageCode);
    }

    try {
      const response = await httpClient.post(
        'https://api.groq.com/openai/v1/audio/transcriptions',
        form,
        {
          headers: {
            ...form.getHeaders(),
            'Authorization': `Bearer ${apiKey}`
          },
          maxContentLength: Infinity,
          maxBodyLength: Infinity
        }
      );

      const text = response.data.text || '';
      const language = response.data.language || 'en';
      const confidence = text.length > 10 ? 0.90 : 0.75;
      keyPool.markSuccess(apiKey);
      console.log(`[GroqWhisper] Transcription completed (key ${index + 1}/${candidates.length})`);

      return {
        text: text.trim(),
        language,
        confidence
      };
    } catch (error) {
      lastError = error;
      const status = Number(error?.response?.status || 0);
      keyPool.markFailure(apiKey, status);
      if (!shouldRotate(error) || index === candidates.length - 1) break;
      console.warn(`[GroqWhisper] key ${index + 1}/${candidates.length} failed (status=${status || 'network'}); rotating`);
    }
  }

  throw lastError || new Error('Groq transcription failed');
}

function shouldRotate(error) {
  const status = Number(error?.response?.status || 0);
  return status === 0 || status === 401 || status === 403 || status === 408 || status === 429 || status >= 500;
}

export { transcribeAudio };
