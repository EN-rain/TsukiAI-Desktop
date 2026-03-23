/**
 * Groq Whisper API integration for fast, free speech-to-text
 * Uses the same Groq API key as the LLM service
 */

import axios from 'axios';
import FormData from 'form-data';

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
 * @param {string} apiKey - Groq API key (same as LLM key)
 * @param {Buffer} audioBuffer - PCM audio buffer (48kHz stereo)
 * @param {number} sampleRate - Sample rate of the audio
 * @param {string} sttLanguage - Language code (e.g. en, ja) or "auto"
 * @returns {Promise<{text: string, language: string, confidence: number}>}
 */
async function transcribeAudio(apiKey, audioBuffer, sampleRate = 48000, sttLanguage = 'auto') {
  try {
    // Convert PCM to WAV format
    const wavBuffer = pcmToWav(audioBuffer, sampleRate, 2, 16);
    console.log(`[GroqWhisper] Converted ${audioBuffer.length} bytes PCM to ${wavBuffer.length} bytes WAV`);

    // Create form data
    const form = new FormData();
    form.append('file', wavBuffer, {
      filename: 'audio.wav',
      contentType: 'audio/wav'
    });
    form.append('model', 'whisper-large-v3');
    form.append('response_format', 'verbose_json'); // Get language and confidence
    const languageCode = (sttLanguage || 'auto').trim().toLowerCase();
    if (languageCode !== 'auto') {
      form.append('language', languageCode);
    }

    // Send to Groq Whisper API
    const response = await axios.post(
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

    console.log('[GroqWhisper] Transcription completed');

    // Extract results
    const text = response.data.text || '';
    const language = response.data.language || 'en';
    
    // Groq doesn't provide confidence, but we can estimate from duration
    // Longer transcriptions with clear text usually indicate good quality
    const confidence = text.length > 10 ? 0.90 : 0.75;

    return {
      text: text.trim(),
      language: language,
      confidence: confidence
    };
  } catch (error) {
    console.error('[GroqWhisper] Error:', error.message);
    if (error.response) {
      console.error('[GroqWhisper] Response status:', error.response.status);
      console.error('[GroqWhisper] Response data:', JSON.stringify(error.response.data, null, 2));
    }
    throw error;
  }
}

export { transcribeAudio };
