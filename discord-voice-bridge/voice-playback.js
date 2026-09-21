import { Readable } from 'node:stream';

export function inspectDiscordPcm(
  audioBuffer,
  { sampleRate = 48000, channels = 2, bytesPerSample = 2 } = {},
) {
  if (!Buffer.isBuffer(audioBuffer)) {
    throw new TypeError('Discord playback audio must be a Buffer');
  }
  if (audioBuffer.length === 0) {
    throw new Error('Discord playback audio is empty');
  }

  const bytesPerFrame = channels * bytesPerSample;
  if (audioBuffer.length % bytesPerFrame !== 0) {
    throw new Error(`Discord PCM is not frame-aligned (${audioBuffer.length} bytes)`);
  }

  let sumSquares = 0;
  let peak = 0;
  const sampleCount = audioBuffer.length / bytesPerSample;
  for (let offset = 0; offset < audioBuffer.length; offset += bytesPerSample) {
    const sample = audioBuffer.readInt16LE(offset);
    const absolute = Math.abs(sample);
    sumSquares += sample * sample;
    if (absolute > peak) peak = absolute;
  }

  if (peak === 0) {
    throw new Error('Discord playback audio is silent');
  }

  const frameCount = audioBuffer.length / bytesPerFrame;
  return {
    bytes: audioBuffer.length,
    frames: frameCount,
    durationSecs: frameCount / sampleRate,
    rms: Math.sqrt(sumSquares / sampleCount),
    peak,
  };
}

export function createDiscordPcmStream(audioBuffer, { tailPaddingMs = 0 } = {}) {
  // Keep the binary input as a Buffer, not an iterable of individual bytes.
  // A little trailing silence gives Discord's RTP/Opus packetizer time to
  // deliver the last voiced frame before a following utterance starts.
  if (!Number.isInteger(tailPaddingMs) || tailPaddingMs < 0 || tailPaddingMs > 500) {
    throw new RangeError('Invalid Discord playback tail padding');
  }
  const bytesPerFrame = 2 * 2; // PCM 48 kHz stereo, 16-bit samples
  const tailBytes = Math.round(48000 * tailPaddingMs / 1000) * bytesPerFrame;
  return Readable.from(tailBytes ? [audioBuffer, Buffer.alloc(tailBytes)] : [audioBuffer]);
}
