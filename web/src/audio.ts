// The backend TTS endpoints return raw PCM (48 kHz stereo s16le) as base64.
// Browsers can't play raw PCM directly, so wrap it in a minimal WAV header
// and decode it through an AudioContext.

export function base64ToBytes(base64: string): Uint8Array {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

export function pcmToWavBlob(pcm: Uint8Array, sampleRate = 48000, channels = 2): Blob {
  const bitsPerSample = 16;
  const blockAlign = (channels * bitsPerSample) / 8;
  const byteRate = sampleRate * blockAlign;
  const header = 44;
  const buffer = new ArrayBuffer(header + pcm.length);
  const view = new DataView(buffer);

  const writeAscii = (offset: number, text: string) => {
    for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i));
  };

  writeAscii(0, "RIFF");
  view.setUint32(4, 36 + pcm.length, true);
  writeAscii(8, "WAVE");
  writeAscii(12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true); // PCM
  view.setUint16(22, channels, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, byteRate, true);
  view.setUint16(32, blockAlign, true);
  view.setUint16(34, bitsPerSample, true);
  writeAscii(36, "data");
  view.setUint32(40, pcm.length, true);

  new Uint8Array(buffer, header).set(pcm);
  return new Blob([buffer], { type: "audio/wav" });
}

let audioContext: AudioContext | null = null;

function getContext(): AudioContext {
  if (!audioContext) {
    audioContext = new AudioContext();
  }
  return audioContext;
}

/** Plays base64-encoded 48 kHz stereo PCM; resolves when playback finishes. */
export async function playPcm48k(base64: string): Promise<void> {
  const bytes = base64ToBytes(base64);
  if (bytes.length === 0) return;

  const ctx = getContext();
  if (ctx.state === "suspended") await ctx.resume();

  const blob = pcmToWavBlob(bytes);
  const arrayBuffer = await blob.arrayBuffer();
  const audioBuffer = await ctx.decodeAudioData(arrayBuffer);

  return new Promise((resolve) => {
    const source = ctx.createBufferSource();
    source.buffer = audioBuffer;
    source.connect(ctx.destination);
    source.onended = () => resolve();
    source.start();
  });
}
