import { splitMolabVoiceSentences } from './molab-voice-pipeline.js';

// A1 is always its own request. Thereafter choose two or three ordered
// utterances per single GPU batch; a one-item tail is unavoidable for A1+A2.
export function planMolabHybridGroups(text, maxChars = 140) {
  const sentences = splitMolabVoiceSentences(text, maxChars);
  if (!sentences.length) return [];
  const groups = [[sentences[0]]];
  for (let index = 1; index < sentences.length;) {
    const left = sentences.length - index;
    const count = left === 4 ? 2 : Math.min(3, left);
    groups.push(sentences.slice(index, index + count));
    index += count;
  }
  return groups;
}

function ensureAudio(item, where) {
  if (!item || !Buffer.isBuffer(item.audio) || !item.audio.length ||
      !Number.isFinite(item.audioDurationSeconds) || item.audioDurationSeconds <= 0) {
    throw new Error('Molab returned invalid or empty audio for ' + where);
  }
  return item;
}

// Play A1 without waiting on the batch of A2/A3/A4. Only one future GPU
// request is prefetched at once, during playback; outputs retain source order.
// On a batch endpoint/model failure retry those utterances one by one, never
// claiming the group was GPU-batched. A failed single propagates to the caller.
export async function playMolabHybridPipeline({
  text, synthesize, synthesizeBatch, play,
  maxChars = 140, onProgress = () => {}, now = () => Date.now(),
}) {
  if (typeof synthesize !== 'function' || typeof synthesizeBatch !== 'function' ||
      typeof play !== 'function') throw new TypeError('Synthesis and playback callbacks required');
  const groups = planMolabHybridGroups(text, maxChars);
  const sentences = groups.reduce((n, group) => n + group.length, 0);
  if (!groups.length) {
    return { sentences: 0, batches: 0, played: 0, maxActiveJobs: 0,
      groupedInferenceCalls: 0, singleInferenceCalls: 0,
      rtf: null, gapSeconds: 0 };
  }
  let played = 0;
  let singleInferenceCalls = 0;
  let groupedInferenceCalls = 0;
  let measuredMs = 0;
  let measuredAudioSeconds = 0;
  let gapSeconds = 0;
  let cursor = 0;

  function record({ generationMs, requestMs, audioSeconds, size, stage }) {
    const validMs = Number.isFinite(generationMs) && generationMs > 0
      ? generationMs : null;
    if (validMs != null && audioSeconds > 0) {
      measuredMs += validMs;
      measuredAudioSeconds += audioSeconds;
    }
    onProgress({
      stage, size, generationMs: validMs,
      requestMs: Number.isFinite(requestMs) && requestMs > 0 ? requestMs : null,
      audioDurationSeconds: +audioSeconds.toFixed(3),
      rtf: validMs == null ? null : +(validMs / (audioSeconds * 1000)).toFixed(3),
    });
  }

  async function generate(group, groupIndex, startIndex) {
    if (group.length === 1) {
      singleInferenceCalls += 1;
      const item = ensureAudio(await synthesize(group[0], startIndex),
        'sentence ' + (startIndex + 1));
      record({ generationMs: item.generationMs, requestMs: item.requestMs,
        audioSeconds: item.audioDurationSeconds, size: 1, stage: 'synthesized' });
      return [item];
    }
    groupedInferenceCalls += 1;
    try {
      const generated = await synthesizeBatch(group, startIndex);
      if (!Array.isArray(generated?.items) || generated.items.length !== group.length) {
        throw new Error('Molab batch returned the wrong number of utterances');
      }
      const items = generated.items.map((item, index) =>
        ensureAudio(item, 'batch sentence ' + (startIndex + index + 1)));
      record({ generationMs: generated.generationMs, requestMs: generated.requestMs,
        audioSeconds: items.reduce((sum, item) => sum + item.audioDurationSeconds, 0),
        size: group.length, stage: 'batch_synthesized' });
      return items;
    } catch (error) {
      onProgress({ stage: 'batch_fallback', size: group.length,
        reason: error?.message || 'Batch generation failed' });
      // Failed full-batch attempts are not counted as successful TTS samples.
      const recovered = [];
      for (let index = 0; index < group.length; index += 1) {
        recovered.push(...await generate([group[index]], groupIndex,
          startIndex + index));
      }
      return recovered;
    }
  }

  // A1 generation and its first playback take priority over every other GPU job.
  const first = (await generate(groups[0], 0, cursor))[0];
  cursor += 1;
  const firstPlayback = play(first.audio, 0);
  onProgress({ stage: 'playing', index: 1, total: sentences,
    audioDurationSeconds: first.audioDurationSeconds });
  let nextIndex = 1;
  function prefetch() {
    if (nextIndex >= groups.length) return null;
    const groupIndex = nextIndex++;
    const startIndex = groups.slice(0, groupIndex)
      .reduce((sum, group) => sum + group.length, 0);
    // Always settle; if playback fails there is no unhandled prefetch rejection.
    return generate(groups[groupIndex], groupIndex, startIndex)
      .then(items => ({ items, groupIndex, startIndex }),
        error => ({ error, groupIndex, startIndex }));
  }
  let upcoming = prefetch();
  try {
    await firstPlayback;
    played += 1;
    onProgress({ stage: 'played', index: 1, total: sentences });
    while (upcoming) {
      const waitingSince = now();
      const ready = await upcoming;
      if (ready.error) throw ready.error;
      if (now() > waitingSince) {
        const gap = (now() - waitingSince) / 1000;
        gapSeconds += gap;
        onProgress({ stage: 'gap', gapSeconds: +gap.toFixed(3),
          index: ready.startIndex + 1, total: sentences });
      }
      upcoming = prefetch();
      for (let i = 0; i < ready.items.length; i += 1) {
        const index = ready.startIndex + i;
        const item = ready.items[i];
        onProgress({ stage: 'playing', index: index + 1, total: sentences,
          audioDurationSeconds: item.audioDurationSeconds });
        await play(item.audio, index);
        played += 1;
        onProgress({ stage: 'played', index: index + 1, total: sentences });
      }
    }
  } finally {
    // Already dispatched inference may finish, but prefetch() handles rejection.
  }
  return {
    sentences, batches: groups.length, played, maxActiveJobs: 1,
    groupedInferenceCalls, singleInferenceCalls,
    rtf: measuredAudioSeconds > 0
      ? +(measuredMs / (measuredAudioSeconds * 1000)).toFixed(3) : null,
    gapSeconds: +gapSeconds.toFixed(3),
  };
}
