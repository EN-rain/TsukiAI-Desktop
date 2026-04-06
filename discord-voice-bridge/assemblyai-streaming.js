import axios from 'axios';

/**
 * Convert PCM audio to WAV format
 * @param {Buffer} pcmBuffer - Raw PCM audio data
 * @param {number} sampleRate - Sample rate (e.g., 48000)
 * @param {number} channels - Number of channels (1 = mono, 2 = stereo)
 * @param {number} bitsPerSample - Bits per sample (16 for 16-bit PCM)
 * @returns {Buffer} WAV formatted audio
 */
function pcmToWav(pcmBuffer, sampleRate = 48000, channels = 2, bitsPerSample = 16) {
  const blockAlign = channels * (bitsPerSample / 8);
  const byteRate = sampleRate * blockAlign;
  const dataSize = pcmBuffer.length;
  const headerSize = 44;
  const fileSize = headerSize + dataSize - 8;

  const header = Buffer.alloc(headerSize);
  let offset = 0;

  // RIFF chunk descriptor
  header.write('RIFF', offset); offset += 4;
  header.writeUInt32LE(fileSize, offset); offset += 4;
  header.write('WAVE', offset); offset += 4;

  // fmt sub-chunk
  header.write('fmt ', offset); offset += 4;
  header.writeUInt32LE(16, offset); offset += 4; // Subchunk1Size (16 for PCM)
  header.writeUInt16LE(1, offset); offset += 2; // AudioFormat (1 for PCM)
  header.writeUInt16LE(channels, offset); offset += 2;
  header.writeUInt32LE(sampleRate, offset); offset += 4;
  header.writeUInt32LE(byteRate, offset); offset += 4;
  header.writeUInt16LE(blockAlign, offset); offset += 2;
  header.writeUInt16LE(bitsPerSample, offset); offset += 2;

  // data sub-chunk
  header.write('data', offset); offset += 4;
  header.writeUInt32LE(dataSize, offset);

  return Buffer.concat([header, pcmBuffer]);
}

/**
 * Transcribe audio using AssemblyAI API
 * @param {string} apiKey - AssemblyAI API key
 * @param {Buffer} audioBuffer - PCM audio buffer (48kHz stereo)
 * @param {number} sampleRate - Sample rate of the audio
 * @returns {Promise<{text: string, language: string, confidence: number}>}
 */
export async function transcribeAudio(apiKey, audioBuffer, sampleRate = 48000) {
  try {
    // Convert PCM to WAV format
    const wavBuffer = pcmToWav(audioBuffer, sampleRate, 2, 16);
    console.log(`[AssemblyAI] Converted ${audioBuffer.length} bytes PCM to ${wavBuffer.length} bytes WAV`);

    // Step 1: Upload audio to AssemblyAI
    const uploadResponse = await axios.post(
      'https://api.assemblyai.com/v2/upload',
      wavBuffer,
      {
        headers: {
          'authorization': apiKey,
          'content-type': 'application/octet-stream',
        },
        timeout: 30000,
      }
    );

    const uploadUrl = uploadResponse.data.upload_url;
    console.log('[AssemblyAI] Audio uploaded:', uploadUrl);

    // Step 2: Request transcription
    const transcriptResponse = await axios.post(
      'https://api.assemblyai.com/v2/transcript',
      {
        audio_url: uploadUrl,
        language_detection: true,
        speech_models: ['universal-2'], // Use universal-2 model
      },
      {
        headers: {
          'authorization': apiKey,
          'content-type': 'application/json',
        },
        timeout: 10000,
      }
    );

    const transcriptId = transcriptResponse.data.id;
    console.log('[AssemblyAI] Transcription requested:', transcriptId);

    // Step 3: Poll for completion
    let transcript = null;
    let attempts = 0;
    const maxAttempts = 60; // 60 seconds max wait

    while (attempts < maxAttempts) {
      await new Promise(resolve => setTimeout(resolve, 1000)); // Wait 1 second

      const statusResponse = await axios.get(
        `https://api.assemblyai.com/v2/transcript/${transcriptId}`,
        {
          headers: {
            'authorization': apiKey,
          },
          timeout: 10000,
        }
      );

      transcript = statusResponse.data;

      if (transcript.status === 'completed') {
        console.log('[AssemblyAI] Transcription completed');
        break;
      } else if (transcript.status === 'error') {
        throw new Error(`AssemblyAI transcription failed: ${transcript.error}`);
      }

      attempts++;
    }

    if (!transcript || transcript.status !== 'completed') {
      throw new Error('AssemblyAI transcription timed out');
    }

    return {
      text: transcript.text || '',
      language: transcript.language_code || 'en',
      confidence: transcript.confidence || 0.0,
    };
  } catch (error) {
    console.error('[AssemblyAI] Error:', error.message);
    if (error.response) {
      console.error('[AssemblyAI] Response status:', error.response.status);
      console.error('[AssemblyAI] Response data:', JSON.stringify(error.response.data, null, 2));
    }
    throw error;
  }
}
