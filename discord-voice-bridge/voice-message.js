function readWavDataChunk(wavBuffer) {
  if (!Buffer.isBuffer(wavBuffer) || wavBuffer.length < 12 ||
      wavBuffer.toString('ascii', 0, 4) !== 'RIFF' ||
      wavBuffer.toString('ascii', 8, 12) !== 'WAVE') {
    return null;
  }

  let byteRate = 0;
  let bitsPerSample = 16;
  let position = 12;
  let data = null;

  while (position + 8 <= wavBuffer.length) {
    const chunkSize = wavBuffer.readUInt32LE(position + 4);
    const payloadStart = position + 8;
    const payloadEnd = payloadStart + chunkSize;
    if (payloadEnd > wavBuffer.length || payloadEnd < payloadStart) return null;

    const chunkId = wavBuffer.toString('ascii', position, position + 4);
    if (chunkId === 'fmt ' && chunkSize >= 16) {
      byteRate = wavBuffer.readUInt32LE(payloadStart + 8);
      bitsPerSample = wavBuffer.readUInt16LE(payloadStart + 14);
    } else if (chunkId === 'data' && data === null) {
      data = {
        start: payloadStart,
        size: chunkSize,
      };
    }

    const nextPosition = payloadEnd + (chunkSize % 2);
    if (nextPosition <= position || nextPosition > wavBuffer.length) return null;
    position = nextPosition;
  }

  if (!data || byteRate <= 0 || bitsPerSample !== 16) return null;
  return { ...data, byteRate };
}

export function voiceMetadataFromWav(wavBuffer, fallbackDuration = 1) {
  const chunk = readWavDataChunk(wavBuffer);
  const duration = chunk
    ? Math.max(0.5, Math.round((chunk.size / chunk.byteRate) * 10) / 10)
    : fallbackDuration || 1;

  const bins = 64;
  const dataStart = chunk?.start ?? 44;
  const dataSize = chunk?.size ?? Math.max(0, wavBuffer.length - dataStart);
  const sampleCount = Math.floor(dataSize / 2);
  const amps = [];
  let max = 1;

  for (let bin = 0; bin < bins; bin += 1) {
    const startSample = Math.floor((bin * sampleCount) / bins);
    const endSample = Math.max(startSample + 1, Math.floor(((bin + 1) * sampleCount) / bins));
    let peak = 0;
    for (let sample = startSample; sample < endSample; sample += 1) {
      const offset = dataStart + sample * 2;
      if (offset + 1 >= wavBuffer.length || offset >= dataStart + dataSize) break;
      peak = Math.max(peak, Math.abs(wavBuffer.readInt16LE(offset)));
    }
    amps.push(peak);
    max = Math.max(max, peak);
  }

  return {
    durationSecs: duration,
    waveform: Buffer.from(amps.map((amp) => Math.round((amp / max) * 255))).toString('base64'),
  };
}
